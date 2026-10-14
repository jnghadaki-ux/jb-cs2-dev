using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>Section "Betting": SWBS CS2 addition, betting points on which prisoner gets a last request.</summary>
public sealed class BettingConfig
{
    public bool Enabled { get; set; } = true;

    public int MinBet { get; set; } = 10;
    public int MaxBet { get; set; } = 500;

    /// <summary>Betting closes when this few prisoners are alive (and at the first LR).</summary>
    public int CloseAtPrisoners { get; set; } = 2;

    /// <summary>Payout multiplier = alive prisoners when the bet was placed x OddsPerPrisoner, between MinMultiplier and
    /// MaxMultiplier. The stake is returned inside the payout.</summary>
    public double OddsPerPrisoner { get; set; } = 0.8;
    public double MinMultiplier { get; set; } = 1.5;
    public double MaxMultiplier { get; set; } = 5;

    /// <summary>Only dead players and spectators may bet, so nobody can steer the result with their own play.</summary>
    public bool DeadOnly { get; set; } = true;

    /// <summary>Bet on who wins a running LR duel: !bet &lt;prisoner or guard in the duel&gt; &lt;points&gt;.</summary>
    public bool DuelBetsEnabled { get; set; } = true;

    /// <summary>A winning duel bet pays stake x this (the stake is inside the payout).</summary>
    public double DuelMultiplier { get; set; } = 1.9;

    /// <summary>Duel bets close this many seconds after the LR was picked, so nobody bets on a duel that is nearly over.</summary>
    public int DuelBetSeconds { get; set; } = 20;
}

/// <summary>
/// !bet &lt;prisoner&gt; &lt;points|all&gt;: dead players and spectators bet jailbreak points on which alive prisoner will get a
/// last request this round. The stake is taken at once; at round end every bet on a prisoner who got an LR pays the
/// stake x the multiplier fixed when it was placed. Rounds without an LR (days, guards wiped out) refund every bet.
/// </summary>
public sealed class BettingModule : JbModule
{
    private sealed record Bet(ulong Bettor, string BettorName, ulong Target, string TargetName, int Stake, double Multiplier);

    /// <summary>A bet on the winner of the LR duel of the prisoner in <see cref="PrisonerSlot"/>.</summary>
    private sealed record DuelBet(ulong Bettor, string BettorName, int PrisonerSlot, int TargetSlot, string TargetName, int Stake, double Multiplier);

    private BettingConfig Cfg => Section<BettingConfig>("Betting");

    public override string Name => "Betting";
    public override int Order => 45; // after Points (40)
    public override bool Enabled => Cfg.Enabled;

    private readonly List<Bet> _bets = new();
    private readonly HashSet<ulong> _gotLr = new();
    private readonly List<DuelBet> _duelBets = new();
    private readonly Dictionary<int, float> _lrStartedAt = new();
    private bool _lrStarted;

    public override void OnAllModulesLoaded(bool hotReload)
    {
        JbEvents.LastRequestInitialized += OnLastRequestInitialized;
        JbEvents.LastRequestStarted += OnLastRequestStarted;
        JbEvents.LastRequestDecided += OnLastRequestDecided;
        JbEvents.LastRequestEnded += OnLastRequestEnded;
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.LastRequestInitialized -= OnLastRequestInitialized;
        JbEvents.LastRequestStarted -= OnLastRequestStarted;
        JbEvents.LastRequestDecided -= OnLastRequestDecided;
        JbEvents.LastRequestEnded -= OnLastRequestEnded;
        RefundAll("unload");
        RefundDuels(_ => true);
    }

    // ================================================================= duel bets

    private void OnLastRequestStarted(JbPlayer prisoner, LrFlags flags) => _lrStartedAt[prisoner.Slot] = CounterStrikeSharp.API.Server.CurrentTime;

    private void OnLastRequestDecided(JbPlayer winner, JbPlayer loser, JbPlayer prisoner, LastRequestDefinition lr)
    {
        var mine = _duelBets.Where(b => b.PrisonerSlot == prisoner.Slot).ToList();
        if (mine.Count == 0) return;
        _duelBets.RemoveAll(b => b.PrisonerSlot == prisoner.Slot);
        var winners = new List<string>();
        foreach (var b in mine)
        {
            if (b.TargetSlot != winner.Slot) continue;
            var payout = Payout(b.Stake, b.Multiplier);
            Jb.Points.AddPoints(b.Bettor, payout);
            winners.Add(Lang.T("bet.winner_entry", b.BettorName, payout, b.TargetName));
        }

        Chat.TellAll(winners.Count == 0 ? "bet.duel_no_winners" : "bet.duel_winners", winner.Name, string.Join(", ", winners));
    }

    /// <summary>An LR that ended without a decision (round end, abort, both slain) refunds its duel bets.</summary>
    private void OnLastRequestEnded(JbPlayer prisoner, JbPlayer? opponent)
    {
        _lrStartedAt.Remove(prisoner.Slot);
        RefundDuels(b => b.PrisonerSlot == prisoner.Slot);
    }

    private void RefundDuels(Func<DuelBet, bool> which)
    {
        var refund = _duelBets.Where(which).ToList();
        foreach (var b in refund) Jb.Points.AddPoints(b.Bettor, b.Stake);
        _duelBets.RemoveAll(b => which(b));
    }

    /// <summary>The prisoner of the running LR duel this player is in, or null.</summary>
    private static JbPlayer? DuelOf(CCSPlayerController c)
    {
        var p = c.Jb();
        if (p == null) return null;
        var prisoner = c.Team == CsTeam.Terrorist ? p : Jb.LastRequests.GetOpponent(p);
        if (prisoner == null || !Jb.LastRequests.HasStartedLastRequest(prisoner) || Jb.LastRequests.GetOpponent(prisoner) == null) return null;
        return (Jb.LastRequests.GetFlags(prisoner) & LrFlags.Freeday) != 0 ? null : prisoner;
    }

    /// <summary>Engine-free (unit-tested): duel bets are open for <paramref name="window"/> seconds after the LR was picked.</summary>
    internal static bool DuelBetOpen(float startedAt, float now, int window) => startedAt > 0 && now - startedAt <= Math.Max(0, window);

    private void PlaceDuelBet(CCSPlayerController caller, JbPlayer me, CommandInfo info)
    {
        if (Cfg.DeadOnly && caller.IsAliveP() && caller.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist) { Chat.Tell(caller, "bet.dead_only"); return; }
        if (caller.IsBot || caller.SteamID == 0) return;
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true, singleOnly: true, respectImmunity: false);
        if (t == null) return;
        var target = t.Players[0];
        var prisoner = DuelOf(target);
        if (prisoner == null) { Chat.Tell(caller, "bet.not_in_duel", target.PlayerName); return; }
        if (!_lrStartedAt.TryGetValue(prisoner.Slot, out var at) || !DuelBetOpen(at, CounterStrikeSharp.API.Server.CurrentTime, Cfg.DuelBetSeconds))
        {
            Chat.Tell(caller, "bet.duel_closed", Cfg.DuelBetSeconds);
            return;
        }

        if (_duelBets.Any(b => b.Bettor == caller.SteamID && b.PrisonerSlot == prisoner.Slot)) { Chat.Tell(caller, "bet.duel_already"); return; }

        var balance = Jb.Points.GetPoints(me);
        var arg = info.GetArg(2).Trim();
        int stake;
        if (arg.Equals("all", StringComparison.OrdinalIgnoreCase)) stake = Math.Min(balance, Cfg.MaxBet);
        else if (!int.TryParse(arg, out stake)) { Chat.Tell(caller, "core.usage", "!bet <player in the duel> <points|all>"); return; }
        if (stake < Cfg.MinBet || stake > Cfg.MaxBet) { Chat.Tell(caller, "bet.limits", Cfg.MinBet, Cfg.MaxBet); return; }
        if (stake > balance) { Chat.Tell(caller, "bet.not_enough", balance); return; }

        var mult = Math.Max(1.0, Cfg.DuelMultiplier);
        Jb.Points.AddPoints(me, -stake, "duel bet");
        _duelBets.Add(new DuelBet(caller.SteamID, caller.PlayerName, prisoner.Slot, target.Slot, target.PlayerName, stake, mult));
        Chat.TellAll("bet.duel_placed", caller.PlayerName, stake, target.PlayerName,
            mult.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture), Payout(stake, mult));
        Jb.Log.LogEvent($"{caller.PlayerName} bet {stake} points on {target.PlayerName} winning their LR duel (x{mult:0.##})", me, target.Jb(), LogType.Any);
    }

    public override void OnRoundStart()
    {
        // A restart (mp_restartgame, !rr) can skip round_end: bets still open were never settled, so give them back.
        RefundAll("round restarted");
        RefundDuels(_ => true);
        _lrStartedAt.Clear();
        _gotLr.Clear();
        _lrStarted = false;
    }

    public override void OnMapEnd()
    {
        RefundAll("map change");
        RefundDuels(_ => true);
    }

    private void OnLastRequestInitialized(JbPlayer prisoner)
    {
        _lrStarted = true;
        _gotLr.Add(Key(prisoner.SteamId64, prisoner.Slot));
    }

    /// <summary>SteamID64, or a slot-based key for bots (SteamID 0) so bets on bots work in tests.</summary>
    private static ulong Key(ulong steamId, int slot) => steamId != 0 ? steamId : 1000UL + (ulong)slot;

    private static ulong KeyOf(CCSPlayerController c) => Key(c.SteamID, c.Slot);

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        RefundDuels(_ => true);
        if (_bets.Count == 0) return;
        if (_gotLr.Count == 0)
        {
            RefundAll("no LR");
            Chat.TellAll("bet.refunded_all");
            return;
        }

        var winners = new List<string>();
        foreach (var b in _bets)
        {
            if (!_gotLr.Contains(b.Target)) continue;
            var payout = Payout(b.Stake, b.Multiplier);
            Jb.Points.AddPoints(b.Bettor, payout);
            winners.Add(Lang.T("bet.winner_entry", b.BettorName, payout, b.TargetName));
        }

        Chat.TellAll(winners.Count == 0 ? "bet.no_winners" : "bet.winners", string.Join(", ", winners));
        _bets.Clear();
    }

    private void RefundAll(string why)
    {
        foreach (var b in _bets) Jb.Points.AddPoints(b.Bettor, b.Stake);
        if (_bets.Count > 0) Logger.LogInformation("[Betting] refunded {Count} bets ({Why})", _bets.Count, why);
        _bets.Clear();
    }

    // ================================================================= !bet

    [ConsoleCommand("css_bet", "sm_bet <player> <points|all> - (dead) bet on who gets a last request, or during LRs on who wins a duel")]
    public void CmdBet(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        var me = caller.Jb();
        if (me == null) return;
        if (Jb.Points is NullServices) { Chat.Tell(caller, "bet.no_points_system"); return; }

        var alive = Players.AlivePrisoners().ToList();
        var mine = _bets.FirstOrDefault(b => b.Bettor == caller.SteamID);
        var open = IsOpen(alive.Count, out var closedKey);

        if (info.ArgCount < 3)
        {
            if (mine != null) Chat.Tell(caller, "bet.yours", mine.Stake, mine.TargetName, Payout(mine.Stake, mine.Multiplier));
            if (open) Chat.Tell(caller, "bet.open", Multiplier(alive.Count).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture), Cfg.MinBet, Cfg.MaxBet);
            else if (closedKey == "bet.closed_lr" && Cfg.DuelBetsEnabled) Chat.Tell(caller, "bet.duel_how", Cfg.DuelBetSeconds);
            else Chat.Tell(caller, closedKey);
            if (mine == null) Chat.Tell(caller, "core.usage", "!bet <prisoner> <points|all>");
            return;
        }

        if (!open)
        {
            // Once LRs run, !bet <player> <points> bets on who wins a running LR duel instead.
            if (closedKey == "bet.closed_lr" && Cfg.DuelBetsEnabled) { PlaceDuelBet(caller, me, info); return; }
            Chat.Tell(caller, closedKey);
            return;
        }
        if (Cfg.DeadOnly && caller.IsAliveP() && caller.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist) { Chat.Tell(caller, "bet.dead_only"); return; }
        if (caller.IsBot || caller.SteamID == 0) return;
        if (mine != null) { Chat.Tell(caller, "bet.already", mine.Stake, mine.TargetName); return; }

        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true, singleOnly: true, respectImmunity: false);
        if (t == null) return;
        var target = t.Players[0];
        if (target.Team != CsTeam.Terrorist) { Chat.Tell(caller, "bet.not_prisoner", target.PlayerName); return; }
        if (target.Slot == caller.Slot) { Chat.Tell(caller, "bet.self"); return; }

        var balance = Jb.Points.GetPoints(me);
        var arg = info.GetArg(2).Trim();
        int stake;
        if (arg.Equals("all", StringComparison.OrdinalIgnoreCase)) stake = Math.Min(balance, Cfg.MaxBet);
        else if (!int.TryParse(arg, out stake)) { Chat.Tell(caller, "core.usage", "!bet <prisoner> <points|all>"); return; }

        if (stake < Cfg.MinBet || stake > Cfg.MaxBet) { Chat.Tell(caller, "bet.limits", Cfg.MinBet, Cfg.MaxBet); return; }
        if (stake > balance) { Chat.Tell(caller, "bet.not_enough", balance); return; }

        var mult = Multiplier(alive.Count);
        Jb.Points.AddPoints(me, -stake, "bet");
        _bets.Add(new Bet(caller.SteamID, caller.PlayerName, KeyOf(target), target.PlayerName, stake, mult));
        Chat.TellAll("bet.placed", caller.PlayerName, stake, target.PlayerName,
            mult.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture), Payout(stake, mult));
        Jb.Log.LogEvent($"{caller.PlayerName} bet {stake} points on {target.PlayerName} getting LR (x{mult:0.##})", me, target.Jb(), LogType.Any);
    }

    private bool IsOpen(int alivePrisoners, out string closedKey)
    {
        closedKey = "";
        if (!Round.InProgress) { closedKey = "bet.closed_round"; return false; }
        if (Jb.Days.IsInProgress) { closedKey = "bet.closed_day"; return false; }
        if (_lrStarted || Jb.LastRequests.NumInitialized > 0) { closedKey = "bet.closed_lr"; return false; }
        if (alivePrisoners <= Math.Max(1, Cfg.CloseAtPrisoners)) { closedKey = "bet.closed_few"; return false; }
        return true;
    }

    private double Multiplier(int alivePrisoners) =>
        MultiplierFor(alivePrisoners, Cfg.OddsPerPrisoner, Cfg.MinMultiplier, Cfg.MaxMultiplier);

    /// <summary>Engine-free odds (unit-tested): alive x odds, clamped to [min, max], two decimals.</summary>
    internal static double MultiplierFor(int alivePrisoners, double oddsPerPrisoner, double min, double max)
    {
        var lo = Math.Max(1.0, min);
        var hi = Math.Max(lo, max);
        return Math.Round(Math.Clamp(Math.Max(0, alivePrisoners) * oddsPerPrisoner, lo, hi), 2);
    }

    internal static int Payout(int stake, double multiplier) => (int)Math.Floor(stake * multiplier);
}
