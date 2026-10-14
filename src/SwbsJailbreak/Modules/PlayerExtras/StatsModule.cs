using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>Section "Stats": SWBS CS2 addition, persistent per-player jailbreak statistics (!stats).</summary>
public sealed class StatsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Rounds need at least this many human players on prisoners + guards to count.</summary>
    public int MinPlayers { get; set; } = 2;

    /// <summary>!rank: rounds a player needs before they appear on the leaderboard, and lines shown per board.</summary>
    public int RankMinRounds { get; set; } = 10;
    public int RankTopCount { get; set; } = 5;

    /// <summary>!rank overall score = survived x1 + warden rounds x3 + guards killed x2 + prisoners killed x1 + LR duels x3.</summary>
    public RankWeights RankWeights { get; set; } = new();
}

/// <summary>One player's totals, stored in data/stats.json keyed by SteamID64.</summary>
public sealed class PlayerStats
{
    public string Name { get; set; } = "";
    public int RoundsPrisoner { get; set; }
    public int RoundsGuard { get; set; }
    public int RoundsSurvived { get; set; }
    public int WardenRounds { get; set; }
    public int GuardsKilled { get; set; }
    public int PrisonersKilled { get; set; }
    public int Deaths { get; set; }
    public int LastRequestsPlayed { get; set; }
    public int LastRequestDuelsWon { get; set; }
}

/// <summary>
/// Counts rounds per team, rounds survived, rounds as warden, kills of guards (as a prisoner) and of prisoners (as a
/// guard), deaths, last requests played and LR duels won. Bots are not tracked. !stats [player] shows them.
/// </summary>
public sealed class StatsModule : JbModule
{
    private const string Ns = "stats";

    private StatsConfig Cfg => Section<StatsConfig>("Stats");

    public override string Name => "Stats";

    /// <summary>Before the LR core (9) so an LR duel is still active when the death is counted.</summary>
    public override int Order => 7;

    public override bool Enabled => Cfg.Enabled;

    private readonly HashSet<ulong> _wardenThisRound = new();

    public override void OnAllModulesLoaded(bool hotReload)
    {
        JbEvents.WardenSelected += OnWardenSelected;
        JbEvents.LastRequestStarted += OnLastRequestStarted;
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.WardenSelected -= OnWardenSelected;
        JbEvents.LastRequestStarted -= OnLastRequestStarted;
    }

    // ================================================================= counting

    private bool Counting => Players.Humans().Count(c => c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist) >= Math.Max(1, Cfg.MinPlayers);

    private static bool Tracked(JbPlayer? p) => p is { Connected: true, IsBot: false } && p.SteamId64 != 0;

    private static void Update(JbPlayer p, Action<PlayerStats> change)
    {
        var key = p.SteamId64.ToString();
        var s = Storage.Get<PlayerStats>(Ns, key) ?? new PlayerStats();
        s.Name = p.Name;
        change(s);
        Storage.Set(Ns, key, s);
    }

    public override void OnRoundStart() => _wardenThisRound.Clear();

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        if (!Counting) return;
        foreach (var c in Players.Humans().Where(c => c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist).ToList())
        {
            var p = c.Jb();
            if (!Tracked(p)) continue;
            var alive = c.IsAliveP();
            var guard = c.Team == CsTeam.CounterTerrorist;
            Update(p!, s =>
            {
                if (guard) s.RoundsGuard++;
                else s.RoundsPrisoner++;
                if (alive) s.RoundsSurvived++;
            });
        }
    }

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        if (!Counting) return;
        if (Tracked(victim)) Update(victim, s => s.Deaths++);
        if (attacker == null || ReferenceEquals(attacker, victim) || !Tracked(attacker)) return;

        var duelWon = Jb.LastRequests.IsInLastRequest(victim) && ReferenceEquals(Jb.LastRequests.GetOpponent(victim), attacker);
        var killedGuard = victim.IsGuard && attacker.Team == CsTeam.Terrorist;
        var killedPrisoner = victim.Team == CsTeam.Terrorist && attacker.IsGuard;
        if (!duelWon && !killedGuard && !killedPrisoner) return;
        Update(attacker, s =>
        {
            if (killedGuard) s.GuardsKilled++;
            if (killedPrisoner) s.PrisonersKilled++;
            if (duelWon) s.LastRequestDuelsWon++;
        });
    }

    private void OnWardenSelected(JbPlayer warden)
    {
        if (!Tracked(warden) || !Counting || !_wardenThisRound.Add(warden.SteamId64)) return;
        Update(warden, s => s.WardenRounds++);
    }

    private void OnLastRequestStarted(JbPlayer prisoner, LrFlags flags)
    {
        if (Tracked(prisoner) && Counting) Update(prisoner, s => s.LastRequestsPlayed++);
    }

    // ================================================================= !stats

    // ================================================================= !rank

    [ConsoleCommand("css_rank", "sm_rank [category|player] - jailbreak leaderboard and your position")]
    [ConsoleCommand("css_top", "sm_top [category] - jailbreak leaderboard")]
    public void CmdRank(CCSPlayerController? caller, CommandInfo info)
    {
        var category = RankCategory.Score;
        CCSPlayerController? subject = caller;
        if (info.ArgCount > 1)
        {
            var arg = info.GetArg(1);
            if (Leaderboard.Aliases.TryGetValue(arg, out var c)) category = c;
            else
            {
                var t = Targets.FindOrReply(caller, info, arg, singleOnly: true, respectImmunity: false);
                if (t == null) { Chat.ReplyT(caller, info, "rank.categories"); return; }
                subject = t.Players[0];
            }
        }

        // Unsaved rounds from this map are in memory; Storage.All reads the same in-memory copy.
        var board = Leaderboard.Build(Storage.All<PlayerStats>(Ns), category, Cfg.RankWeights, Cfg.RankMinRounds);
        var title = Lang.T("rank.cat." + category.ToString().ToLowerInvariant());
        if (board.Count == 0) { Chat.ReplyT(caller, info, "rank.empty", title, Cfg.RankMinRounds); return; }

        Chat.ReplyT(caller, info, "rank.title", title, board.Count);
        foreach (var e in board.Take(Math.Clamp(Cfg.RankTopCount, 1, 15)))
            Chat.ReplyT(caller, info, "rank.line", e.Position, e.Name, Format(e.Value));

        var p = subject.Jb();
        if (subject == null || !Tracked(p)) return;
        var mine = board.FirstOrDefault(e => e.SteamId == p!.SteamId64.ToString());
        if (mine != null)
        {
            Chat.ReplyT(caller, info, ReferenceEquals(subject, caller) ? "rank.you" : "rank.them", subject.PlayerName, mine.Position, board.Count, Format(mine.Value));
            return;
        }

        var s = Storage.Get<PlayerStats>(Ns, p!.SteamId64.ToString()) ?? new PlayerStats();
        var left = Math.Max(0, Cfg.RankMinRounds - Leaderboard.Rounds(s));
        if (left > 0) Chat.ReplyT(caller, info, "rank.unranked", subject.PlayerName, left);
        else Chat.ReplyT(caller, info, "rank.not_on_board", subject.PlayerName);
    }

    private static string Format(double v) =>
        v % 1 == 0 ? ((long)v).ToString() : v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    [ConsoleCommand("css_stats", "sm_stats [player] - show jailbreak statistics")]
    [ConsoleCommand("css_jbstats", "sm_jbstats [player] - show jailbreak statistics")]
    public void CmdStats(CCSPlayerController? caller, CommandInfo info)
    {
        CCSPlayerController? target = caller;
        if (info.ArgCount > 1)
        {
            var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true, respectImmunity: false);
            if (t == null) return;
            target = t.Players[0];
        }

        if (target == null) { Chat.ReplyT(caller, info, "core.usage", "css_stats <player>"); return; }
        var p = target.Jb();
        if (!Tracked(p)) { Chat.ReplyT(caller, info, "stats.none", target.PlayerName); return; }

        var s = Storage.Get<PlayerStats>(Ns, p!.SteamId64.ToString()) ?? new PlayerStats();
        var minutes = Storage.Get<long>("playtime", p.SteamId64.ToString()) / 60;
        var rounds = s.RoundsPrisoner + s.RoundsGuard;
        var survival = rounds == 0 ? 0 : (int)Math.Round(100.0 * s.RoundsSurvived / rounds);

        Chat.ReplyT(caller, info, "stats.title", target.PlayerName);
        Chat.ReplyT(caller, info, "stats.rounds", rounds, s.RoundsPrisoner, s.RoundsGuard, survival);
        Chat.ReplyT(caller, info, "stats.warden", s.WardenRounds);
        Chat.ReplyT(caller, info, "stats.kills", s.GuardsKilled, s.PrisonersKilled, s.Deaths);
        Chat.ReplyT(caller, info, "stats.lr", s.LastRequestsPlayed, s.LastRequestDuelsWon);
        Chat.ReplyT(caller, info, "stats.playtime", minutes / 60, minutes % 60);
    }
}
