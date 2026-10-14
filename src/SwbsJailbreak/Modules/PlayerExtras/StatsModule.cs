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

    /// <summary>!ratewarden: prisoners rate the round's warden 1-5. The warden must have served this many seconds;
    /// prisoners get the menu at round end when RateWardenPrompt is on.</summary>
    public bool RateWardenEnabled { get; set; } = true;
    public float RateWardenMinSeconds { get; set; } = 60f;
    public bool RateWardenPrompt { get; set; } = true;
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
    public int LastRequestDuelsLost { get; set; }

    /// <summary>Per LR name: [wins, losses] (LRs with an opponent decided by a death).</summary>
    public Dictionary<string, int[]> LrRecord { get; set; } = new();

    /// <summary>Warden ratings received from prisoners (1-5 each).</summary>
    public int WardenRatingSum { get; set; }
    public int WardenRatingCount { get; set; }

    /// <summary>Contraband (!search / !snitch).</summary>
    public int SearchesMade { get; set; }
    public int ContrabandFound { get; set; }
    public int TimesCaught { get; set; }
    public int SnitchTips { get; set; }
    public int SnitchCorrect { get; set; }
}

/// <summary>
/// Counts rounds per team, rounds survived, rounds as warden, kills of guards (as a prisoner) and of prisoners (as a
/// guard), deaths, last requests played and LR duels won. Bots are not tracked. !stats [player] shows them.
/// </summary>
public sealed class StatsModule : JbModule
{
    internal const string Ns = "stats";

    private StatsConfig Cfg => Section<StatsConfig>("Stats");

    public override string Name => "Stats";

    /// <summary>Before the LR core (9) so an LR duel is still active when the death is counted.</summary>
    public override int Order => 7;

    public override bool Enabled => Cfg.Enabled;

    private readonly HashSet<ulong> _wardenThisRound = new();

    // !ratewarden: seconds each warden served this round, and the warden open for rating (last finished round).
    private readonly Dictionary<ulong, (string Name, float Seconds)> _served = new();
    private ulong _currentWarden;
    private float _currentWardenSince;
    private ulong _rateable;
    private string _rateableName = "";
    private readonly HashSet<ulong> _ratedBy = new();

    public override void OnAllModulesLoaded(bool hotReload)
    {
        JbEvents.WardenSelected += OnWardenSelected;
        JbEvents.WardenRemoved += OnWardenRemoved;
        JbEvents.LastRequestStarted += OnLastRequestStarted;
        JbEvents.LastRequestDecided += OnLastRequestDecided;
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.WardenSelected -= OnWardenSelected;
        JbEvents.WardenRemoved -= OnWardenRemoved;
        JbEvents.LastRequestStarted -= OnLastRequestStarted;
        JbEvents.LastRequestDecided -= OnLastRequestDecided;
    }

    public override void OnMapStart(string mapName)
    {
        _served.Clear();
        _currentWarden = 0;
        _rateable = 0;
        _rateableName = "";
        _ratedBy.Clear();
    }

    // ================================================================= counting

    private bool Counting => Players.Humans().Count(c => c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist) >= Math.Max(1, Cfg.MinPlayers);

    internal static bool Tracked(JbPlayer? p) => p is { Connected: true, IsBot: false } && p.SteamId64 != 0;

    /// <summary>Change one player's stored stats (other modules use this too, e.g. contraband).</summary>
    internal static void Update(JbPlayer p, Action<PlayerStats> change)
    {
        var key = p.SteamId64.ToString();
        var s = Storage.Get<PlayerStats>(Ns, key) ?? new PlayerStats();
        s.Name = p.Name;
        change(s);
        Storage.Set(Ns, key, s);
    }

    public override void OnRoundStart()
    {
        _wardenThisRound.Clear();
        _served.Clear();
        _currentWarden = 0;
    }

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        OpenRating();
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

        // LR duels are counted from JbEvents.LastRequestDecided (the survivor wins, also for slays and contests).
        var killedGuard = victim.IsGuard && attacker.Team == CsTeam.Terrorist;
        var killedPrisoner = victim.Team == CsTeam.Terrorist && attacker.IsGuard;
        if (!killedGuard && !killedPrisoner) return;
        Update(attacker, s =>
        {
            if (killedGuard) s.GuardsKilled++;
            if (killedPrisoner) s.PrisonersKilled++;
        });
    }

    private void OnLastRequestDecided(JbPlayer winner, JbPlayer loser, JbPlayer prisoner, LastRequestDefinition lr)
    {
        if (!Counting) return;
        if (Tracked(winner)) Update(winner, s => RecordDuel(s, lr.Name, true));
        if (Tracked(loser)) Update(loser, s => RecordDuel(s, lr.Name, false));
    }

    /// <summary>Engine-free (unit-tested): count one decided LR duel.</summary>
    internal static void RecordDuel(PlayerStats s, string lrName, bool won)
    {
        s.LrRecord ??= new Dictionary<string, int[]>();
        if (!s.LrRecord.TryGetValue(lrName, out var r) || r is not { Length: 2 }) s.LrRecord[lrName] = r = new int[2];
        if (won)
        {
            s.LastRequestDuelsWon++;
            r[0]++;
        }
        else
        {
            s.LastRequestDuelsLost++;
            r[1]++;
        }
    }

    private void OnWardenSelected(JbPlayer warden)
    {
        CloseServedTime();
        if (Tracked(warden))
        {
            _currentWarden = warden.SteamId64;
            _currentWardenSince = (float)CounterStrikeSharp.API.Server.EngineTime;
            if (!_served.ContainsKey(_currentWarden)) _served[_currentWarden] = (warden.Name, 0f);
        }

        if (!Tracked(warden) || !Counting || !_wardenThisRound.Add(warden.SteamId64)) return;
        Update(warden, s => s.WardenRounds++);
    }

    private void OnWardenRemoved(JbPlayer warden) => CloseServedTime();

    /// <summary>Add the current warden's time to <see cref="_served"/>.</summary>
    private void CloseServedTime()
    {
        if (_currentWarden == 0) return;
        var add = Math.Max(0f, (float)CounterStrikeSharp.API.Server.EngineTime - _currentWardenSince);
        if (_served.TryGetValue(_currentWarden, out var v)) _served[_currentWarden] = (v.Name, v.Seconds + add);
        _currentWarden = 0;
    }

    // ================================================================= !ratewarden

    /// <summary>Round end: the warden who served longest (at least RateWardenMinSeconds) becomes rateable.</summary>
    private void OpenRating()
    {
        CloseServedTime();
        if (!Cfg.RateWardenEnabled) return;
        var best = _served.OrderByDescending(kv => kv.Value.Seconds).FirstOrDefault();
        if (best.Key == 0 || best.Value.Seconds < Cfg.RateWardenMinSeconds) return;

        _rateable = best.Key;
        _rateableName = best.Value.Name;
        _ratedBy.Clear();
        if (!Cfg.RateWardenPrompt) return;

        var voters = Players.Humans().Where(c => c.Team == CsTeam.Terrorist && c.SteamID != _rateable).Select(c => c.Slot).ToList();
        // Shortly after the round_end handlers, so the menu is not replaced by round-end cleanup.
        Plugin.AddTimer(1f, () =>
        {
            foreach (var slot in voters)
            {
                var c = Players.Get(slot).Controller;
                if (c != null && c.IsValid && !c.IsBot) ShowRatingMenu(c, prompt: true);
            }
        }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
    }

    [ConsoleCommand("css_ratewarden", "sm_ratewarden - rate the last round's warden from 1 to 5")]
    [ConsoleCommand("css_rate", "sm_rate - rate the last round's warden from 1 to 5")]
    public void CmdRateWarden(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        if (!Cfg.RateWardenEnabled) { Chat.Tell(caller, "rate.disabled"); return; }
        ShowRatingMenu(caller, prompt: false);
    }

    private void ShowRatingMenu(CCSPlayerController c, bool prompt)
    {
        if (_rateable == 0) { if (!prompt) Chat.Tell(c, "rate.none"); return; }
        if (c.SteamID == _rateable) { if (!prompt) Chat.Tell(c, "rate.self"); return; }
        if (c.Team != CsTeam.Terrorist) { if (!prompt) Chat.Tell(c, "rate.only_prisoners"); return; }
        if (_ratedBy.Contains(c.SteamID)) { if (!prompt) Chat.Tell(c, "rate.already", _rateableName); return; }

        var target = _rateable;
        var m = new JbMenu(Lang.T("rate.title", Hud.Escape(_rateableName))) { Subtitle = Lang.T("rate.subtitle") };
        for (var stars = 5; stars >= 1; stars--)
        {
            var value = stars;
            m.Add(Lang.T("rate.option" + stars), p => Rate(p, target, value));
        }

        m.Open(c);
    }

    private void Rate(CCSPlayerController voter, ulong warden, int stars)
    {
        if (warden != _rateable || warden == 0 || voter.SteamID == warden || !_ratedBy.Add(voter.SteamID)) return;
        var key = warden.ToString();
        var s = Storage.Get<PlayerStats>(Ns, key) ?? new PlayerStats { Name = _rateableName };
        s.WardenRatingSum += Math.Clamp(stars, 1, 5);
        s.WardenRatingCount++;
        Storage.Set(Ns, key, s);
        Chat.Tell(voter, "rate.thanks", stars, _rateableName);
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
        Chat.ReplyT(caller, info, "stats.lr", s.LastRequestsPlayed, s.LastRequestDuelsWon, s.LastRequestDuelsLost,
            Leaderboard.DuelWinRate(s).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        if (s.SearchesMade > 0 || s.TimesCaught > 0 || s.SnitchTips > 0)
            Chat.ReplyT(caller, info, "stats.contraband", s.ContrabandFound, s.SearchesMade, s.TimesCaught, s.SnitchCorrect, s.SnitchTips);
        if (s.WardenRatingCount > 0)
            Chat.ReplyT(caller, info, "stats.rating", Leaderboard.AverageRating(s).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), s.WardenRatingCount);
        Chat.ReplyT(caller, info, "stats.playtime", minutes / 60, minutes % 60);
    }

    // ================================================================= !lrstats

    [ConsoleCommand("css_lrstats", "sm_lrstats [player] - last request duel wins and losses per LR")]
    public void CmdLrStats(CCSPlayerController? caller, CommandInfo info)
    {
        var target = caller;
        if (info.ArgCount > 1)
        {
            var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true, respectImmunity: false);
            if (t == null) return;
            target = t.Players[0];
        }

        if (target == null) { Chat.ReplyT(caller, info, "core.usage", "css_lrstats <player>"); return; }
        var p = target.Jb();
        if (!Tracked(p)) { Chat.ReplyT(caller, info, "stats.none", target.PlayerName); return; }
        var s = Storage.Get<PlayerStats>(Ns, p!.SteamId64.ToString()) ?? new PlayerStats();
        Chat.ReplyT(caller, info, "lrstats.title", target.PlayerName, s.LastRequestDuelsWon, s.LastRequestDuelsLost,
            Leaderboard.DuelWinRate(s).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        var rows = TopLrs(s, 6);
        if (rows.Count == 0) { Chat.ReplyT(caller, info, "lrstats.none"); return; }
        foreach (var (name, won, lost) in rows) Chat.ReplyT(caller, info, "lrstats.line", name, won, lost);
    }

    /// <summary>Engine-free (unit-tested): the most played LRs, then by wins, then by name.</summary>
    internal static List<(string Name, int Won, int Lost)> TopLrs(PlayerStats s, int count) =>
        (s.LrRecord ?? new Dictionary<string, int[]>())
        .Where(kv => kv.Value is { Length: 2 } && kv.Value[0] + kv.Value[1] > 0)
        .Select(kv => (Name: kv.Key, Won: kv.Value[0], Lost: kv.Value[1]))
        .OrderByDescending(r => r.Won + r.Lost).ThenByDescending(r => r.Won).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .Take(Math.Max(1, count))
        .ToList();
}
