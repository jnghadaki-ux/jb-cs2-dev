using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>Section "Pools": prisoners pool points behind a warden game, map game or day; the guards share a chosen pot.</summary>
public sealed class PoolsConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>New pools can only be started this many seconds into the round (after freeze time). Joining stays open.</summary>
    public int StartSeconds { get; set; } = 60;
    /// <summary>Pools one player may start per round.</summary>
    public int StartsPerPlayer { get; set; } = 1;
    /// <summary>Open pools per category (warden games, map games, days).</summary>
    public int MaxPoolsPerCategory { get; set; } = 3;
    public int MinAmount { get; set; } = 50;
    public int MaxAmount { get; set; } = 1000;
    /// <summary>Amounts offered in the !pool menu.</summary>
    public List<int> Amounts { get; set; } = new() { 50, 100, 250, 500, 1000 };
    /// <summary>Who may put points in: prisoners (alive or dead). Guards are paid from pools, so they cannot fund them.</summary>
    public bool AllowSpectators { get; set; } = false;
    public bool WardenGames { get; set; } = true;
    public bool MapGames { get; set; } = true;
    public bool Days { get; set; } = true;
    /// <summary>Show the open pools to the warden and guards on the centre HUD.</summary>
    public bool ShowHud { get; set; } = true;
}

internal enum PoolCategory { Game, MapGame, Day }

internal sealed class Pool
{
    public required string Key;
    public required PoolCategory Category;
    public required string Label;
    public ulong StarterSteamId;
    public readonly Dictionary<ulong, int> Contributions = new(); // SteamID64 -> points
    public int Total => Contributions.Values.Sum();
}

/// <summary>Pool rules that need no engine (unit-tested).</summary>
internal static class PoolRules
{
    /// <summary>Why a new pool may not be started (null = allowed).</summary>
    public static string? CannotStart(float elapsed, int startSeconds, int startedByPlayer, int startsPerPlayer, int poolsInCategory, int maxPerCategory)
    {
        if (elapsed > startSeconds) return "pools.err.start_window";
        if (startedByPlayer >= Math.Max(1, startsPerPlayer)) return "pools.err.started_already";
        if (poolsInCategory >= Math.Max(1, maxPerCategory)) return "pools.err.category_full";
        return null;
    }

    /// <summary>Split a pot evenly; the first <c>total % n</c> shares get one point more (nothing is lost).</summary>
    public static int[] Split(int total, int n)
    {
        if (n <= 0) return Array.Empty<int>();
        var each = total / n;
        var rest = total % n;
        return Enumerable.Range(0, n).Select(i => each + (i < rest ? 1 : 0)).ToArray();
    }

    public static string Key(PoolCategory c, string id) => (c switch { PoolCategory.Game => "game:", PoolCategory.MapGame => "map:", _ => "day:" }) + id.Trim().ToLowerInvariant();
}

/// <summary>
/// Point pools (!pool): prisoners put points behind a warden game, a map game or a day they want. Pools can only be started
/// in the first minute of a round (once per player, a few per category); others can join until something is chosen.
/// The warden sees the pots in !games / !day. When the warden starts a pooled option, its pot is shared out among the
/// current guards; every other pool is refunded (at round end, when a day or last requests make them moot).
/// </summary>
public sealed class PoolsModule : JbModule
{
    private PoolsConfig Cfg => Section<PoolsConfig>("Pools");
    public override string Name => "Pools";
    public override int Order => 92;
    public override bool Enabled => Cfg.Enabled;

    internal static PoolsModule? Instance { get; private set; }

    private readonly Dictionary<string, Pool> _pools = new();
    private readonly Dictionary<ulong, int> _started = new(); // SteamID64 -> pools started this round
    private float _nextHud;

    public override void OnLoad(bool hotReload)
    {
        Instance = this;
        JbEvents.DayStarted += OnDayStarted;
        JbEvents.LastRequestStarted += OnLastRequestStarted;
        Plugin.RegisterListener<Listeners.OnTick>(HudTick);
    }

    public override void OnUnload(bool hotReload)
    {
        RefundAll("pools.refunded_unload");
        JbEvents.DayStarted -= OnDayStarted;
        JbEvents.LastRequestStarted -= OnLastRequestStarted;
        if (Instance == this) Instance = null;
    }

    public override void OnRoundStart()
    {
        RefundAll(null); // anything left over from a round that ended oddly
        _started.Clear();
    }

    public override void OnRoundEnd(CsTeam winner, int reason) => RefundAll("pools.refunded_round_end");

    // ================================================================= hooks from games / days

    /// <summary>"[pool 650]" for menus (empty when there is no pool).</summary>
    internal static string Tag(PoolCategory category, string id)
    {
        if (Instance == null || !Instance._pools.TryGetValue(PoolRules.Key(category, id), out var p) || p.Total <= 0) return "";
        var top = Instance._pools.Values.Where(x => x.Category == category).Max(x => x.Total) == p.Total;
        return Lang.T(top ? "pools.tag_top" : "pools.tag", p.Total);
    }

    /// <summary>The warden started a warden game or a map game.</summary>
    internal static void GameStarted(PoolCategory category, string id) => Instance?.Chosen(PoolRules.Key(category, id));

    private void OnDayStarted(JbPlayer? starter, DayType type, bool ffa)
    {
        if (Jb.Days.Current is { } day) Chosen(PoolRules.Key(PoolCategory.Day, day.Name));
        RefundAll("pools.refunded_day"); // no warden games during a day
    }

    private void OnLastRequestStarted(JbPlayer p, LrFlags flags) => RefundAll("pools.refunded_lr");

    private void Chosen(string key)
    {
        if (!_pools.Remove(key, out var pool) || pool.Total <= 0) return;
        var guards = Players.Guards().Where(c => !c.IsBot && c.Jb() != null).OrderByDescending(c => Jb.Warden.IsWarden(c)).ToList();
        if (guards.Count == 0) { Refund(pool); Chat.TellAll("pools.no_guards", pool.Label); return; }
        var shares = PoolRules.Split(pool.Total, guards.Count); // the warden is first, so gets any odd point
        for (var i = 0; i < guards.Count; i++) Jb.Points.AddPoints(guards[i].Jb()!, shares[i], "pool");
        Chat.TellAll("pools.paid", pool.Label, pool.Total, guards.Count, shares[^1]);
        Logger.LogInformation("[Pools] {Pool} chosen: {Total} points shared by {Guards} guards", pool.Label, pool.Total, guards.Count);
    }

    private void Refund(Pool pool)
    {
        foreach (var (steamId, amount) in pool.Contributions) Jb.Points.AddPoints(steamId, amount);
    }

    private void RefundAll(string? messageKey)
    {
        if (_pools.Count == 0) return;
        var total = _pools.Values.Sum(p => p.Total);
        foreach (var p in _pools.Values) Refund(p);
        _pools.Clear();
        if (messageKey != null && total > 0) Chat.TellAll(messageKey, total);
    }

    // ================================================================= !pool

    [ConsoleCommand("css_pool", "sm_pool - put points behind a warden game, map game or day you want (pools start in the first minute)")]
    [ConsoleCommand("css_pools", "sm_pools - see and join point pools")]
    [ConsoleCommand("css_fund", "sm_fund - see and join point pools")]
    [OpenCommand]
    public void CmdPool(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid || caller.IsBot) return;
        if (Why(caller) is { } why) { Chat.Tell(caller, why); return; }
        var m = new JbMenu(Lang.T("pools.title")) { Subtitle = Hud.Escape(Summary()) };
        if (Cfg.WardenGames) m.Add(Lang.T("pools.cat.game"), p => OpenCategory(p, PoolCategory.Game));
        if (Cfg.MapGames && MapGameLabels().Count > 0) m.Add(Lang.T("pools.cat.map"), p => OpenCategory(p, PoolCategory.MapGame));
        if (Cfg.Days) m.Add(Lang.T("pools.cat.day"), p => OpenCategory(p, PoolCategory.Day));
        m.Open(caller);
    }

    /// <summary>Why this player cannot use pools right now (null = can).</summary>
    private string? Why(CCSPlayerController c)
    {
        if (Jb.Points is NullServices) return "pools.err.no_points";
        if (!Round.IsActive) return "pools.err.round";
        if (Jb.Days.IsInProgress) return "pools.err.day";
        if (Jb.LastRequests.NumStartedIgnore(LrFlags.None) > 0) return "pools.err.lr";
        if (c.Team == CsTeam.CounterTerrorist) return "pools.err.guard";
        if (c.Team != CsTeam.Terrorist && !Cfg.AllowSpectators) return "pools.err.team";
        return null;
    }

    private void OpenCategory(CCSPlayerController c, PoolCategory cat)
    {
        var steamId = c.SteamID;
        var mine = _pools.Values.FirstOrDefault(p => p.Category == cat && p.Contributions.ContainsKey(steamId));
        var m = new JbMenu(Lang.T("pools.cat_title." + cat.ToString().ToLowerInvariant()));
        if (mine != null) m.Subtitle = Hud.Escape(Lang.T("pools.yours", mine.Contributions[steamId], mine.Label));
        foreach (var (id, label) in Options(cat))
        {
            var key = PoolRules.Key(cat, id);
            var exists = _pools.TryGetValue(key, out var pool);
            var text = exists ? Lang.T("pools.option_join", label, pool!.Total, pool.Contributions.Count) : Lang.T("pools.option_start", label);
            var lbl = label;
            var i = id;
            m.Add(Hud.Escape(text), p => OpenAmounts(p, cat, i, lbl), mine == null);
        }

        m.Open(c);
    }

    private List<(string Id, string Label)> Options(PoolCategory cat) => cat switch
    {
        PoolCategory.Game => Plugin.GetModule<Games.WardenGamesModule>()?.Games
            .Where(g => g is not Games.MapGame).Select(g => (g.Key, Lang.T(g.NameKey))).ToList() ?? new(),
        PoolCategory.MapGame => MapGameLabels().Select(l => (l, l)).ToList(),
        _ => Jb.Days.All.Where(d => d.Enabled).Select(d => (d.Name, d.Name)).ToList()
    };

    private List<string> MapGameLabels() =>
        Plugin.GetModule<Games.WardenGamesModule>()?.Buttons.Select(b => b.Label).Where(l => l.Length > 0).Distinct().ToList() ?? new();

    private void OpenAmounts(CCSPlayerController c, PoolCategory cat, string id, string label)
    {
        var have = c.Jb() is { } jp ? Jb.Points.GetPoints(jp) : 0;
        var m = new JbMenu(Lang.T("pools.amount_title", label)) { Subtitle = Hud.Escape(Lang.T("pools.you_have", have)) };
        foreach (var a in Cfg.Amounts.Where(a => a >= Cfg.MinAmount && a <= Cfg.MaxAmount).Distinct().OrderBy(a => a))
        {
            var amount = a;
            m.Add(amount.ToString(), p => Contribute(p, cat, id, label, amount), amount <= have);
        }

        m.Open(c);
    }

    private void Contribute(CCSPlayerController c, PoolCategory cat, string id, string label, int amount)
    {
        if (Why(c) is { } why) { Chat.Tell(c, why); return; }
        var jp = c.Jb();
        if (jp == null) return;
        var steamId = c.SteamID;
        if (_pools.Values.Any(p => p.Category == cat && p.Contributions.ContainsKey(steamId))) { Chat.Tell(c, "pools.err.one_per_category"); return; }
        if (amount < Cfg.MinAmount || amount > Cfg.MaxAmount) { Chat.Tell(c, "pools.err.limits", Cfg.MinAmount, Cfg.MaxAmount); return; }
        if (Jb.Points.GetPoints(jp) < amount) { Chat.Tell(c, "pools.err.points", Jb.Points.GetPoints(jp)); return; }

        var key = PoolRules.Key(cat, id);
        var isNew = !_pools.TryGetValue(key, out var pool);
        if (isNew)
        {
            var cannot = PoolRules.CannotStart(Round.Elapsed, Cfg.StartSeconds, _started.GetValueOrDefault(steamId),
                Cfg.StartsPerPlayer, _pools.Values.Count(p => p.Category == cat), Cfg.MaxPoolsPerCategory);
            if (cannot != null) { Chat.Tell(c, cannot, Cfg.StartSeconds); return; }
            pool = new Pool { Key = key, Category = cat, Label = label, StarterSteamId = steamId };
            _pools[key] = pool;
            _started[steamId] = _started.GetValueOrDefault(steamId) + 1;
        }

        Jb.Points.AddPoints(jp, -amount, "pool");
        pool!.Contributions[steamId] = amount;
        // One chat line per pool (when it starts); joins are only confirmed to the player and show on the HUD.
        if (isNew) Chat.TellAll("pools.started", Chat.Name(c), label, amount);
        else Chat.Tell(c, "pools.joined", amount, label, pool.Total);
        _nextHud = 0;
    }

    // ================================================================= HUD

    private string Summary()
    {
        if (_pools.Count == 0) return Lang.T("pools.none");
        return Lang.T("pools.summary", string.Join(" · ", _pools.Values.OrderByDescending(p => p.Total).Take(4).Select(p => $"{p.Label} {p.Total}")));
    }

    private void HudTick()
    {
        if (!Cfg.ShowHud || _pools.Count == 0) return;
        var now = Server.CurrentTime;
        if (now < _nextHud) return;
        _nextHud = now + 2f;
        var html = Hud.Color(Hud.Escape(Summary()), "#FFD24A");
        foreach (var g in Players.Guards().Where(c => !c.IsBot)) Hud.Set(g, "pools", html, 5, 2.5f);
    }
}
