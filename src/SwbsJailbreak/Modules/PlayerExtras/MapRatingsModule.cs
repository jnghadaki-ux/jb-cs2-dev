using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>Section "MapRatings" (SMPlugins MapRatings.sp v1.0).</summary>
public sealed class MapRatingsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>SECONDS_IN_SERVER_TO_RATE: seconds a player must have been on the server before rating.</summary>
    public int SecondsBeforeRating { get; set; } = 480;

    /// <summary>Show the rating menu to everyone at the end of the map (cs_intermission), like the original.</summary>
    public bool PromptAtMapEnd { get; set; } = true;

    /// <summary>!topmaps: ratings a map needs to be listed.</summary>
    public int TopMapsMinRatings { get; set; } = 3;
}

/// <summary>
/// Port of MapRatings.sp: players rate the current map 1-3 stars ("I don't care" removes the rating), at the end of the
/// map or with !ratemap. Stored per map in data/map_ratings.json (the original used MySQL). !maprating [map] and
/// !topmaps show the results in game (the original showed them on the website).
/// </summary>
public sealed class MapRatingsModule : JbModule
{
    private const string Ns = "map_ratings";

    private MapRatingsConfig Cfg => Section<MapRatingsConfig>("MapRatings");

    public override string Name => "MapRatings";
    public override int Order => 47;
    public override bool Enabled => Cfg.Enabled;

    private string _map = "";

    /// <summary>Engine time each player joined (OnClientPutInServer also runs again on every map change).</summary>
    private readonly double[] _joined = new double[Players.MaxSlots];

    public override void OnLoad(bool hotReload)
    {
        if (hotReload) _map = (Server.MapName ?? "").ToLowerInvariant();
    }

    public override void OnMapStart(string mapName) => _map = (mapName ?? "").ToLowerInvariant();

    public override void OnPlayerConnected(JbPlayer player) => _joined[player.Slot] = Server.EngineTime;

    [GameEventHandler]
    public HookResult OnIntermission(EventCsIntermission ev, GameEventInfo info)
    {
        if (!Cfg.PromptAtMapEnd || _map.Length == 0) return HookResult.Continue;
        foreach (var c in Players.Humans()) ShowMenu(c, prompt: true);
        return HookResult.Continue;
    }

    private static Dictionary<string, int> Ratings(string map) =>
        Storage.Get<Dictionary<string, int>>(Ns, map) ?? new Dictionary<string, int>();

    /// <summary>Engine-free (unit-tested): average stars and count, ignoring anything outside 1-3.</summary>
    internal static (double Average, int Count) Summary(IEnumerable<int> stars)
    {
        var valid = stars.Where(s => s is >= 1 and <= 3).ToList();
        return valid.Count == 0 ? (0, 0) : (Math.Round(valid.Average(), 2), valid.Count);
    }

    private bool CanRate(CCSPlayerController c, out int secondsLeft)
    {
        var p = c.Jb();
        var inServer = p == null || _joined[c.Slot] <= 0 ? 0 : Server.EngineTime - _joined[c.Slot];
        secondsLeft = (int)Math.Ceiling(Math.Max(0, Cfg.SecondsBeforeRating - inServer));
        return p != null && !p.IsBot && p.SteamId64 != 0 && secondsLeft == 0;
    }

    private void ShowMenu(CCSPlayerController c, bool prompt)
    {
        if (_map.Length == 0) return;
        if (!CanRate(c, out var left))
        {
            if (!prompt) Chat.Tell(c, "maprate.too_soon", (left + 59) / 60);
            return;
        }

        var key = c.SteamID.ToString();
        var current = Ratings(_map).TryGetValue(key, out var stars) ? stars : 0;
        var m = new JbMenu(Lang.T(current > 0 ? "maprate.title_rerate" : "maprate.title", Hud.Escape(_map)))
        {
            Subtitle = current > 0 ? Lang.T("maprate.current", current) : null
        };
        for (var i = 3; i >= 0; i--)
        {
            var value = i;
            m.Add(Lang.T("maprate.option" + i), p => Rate(p, value));
        }

        m.Open(c);
    }

    private void Rate(CCSPlayerController c, int stars)
    {
        if (_map.Length == 0 || !CanRate(c, out _)) return;
        var all = Ratings(_map);
        var key = c.SteamID.ToString();
        if (stars == 0) all.Remove(key);
        else all[key] = Math.Clamp(stars, 1, 3);
        Storage.Set(Ns, _map, all);
        Chat.Tell(c, stars == 0 ? "maprate.cleared" : "maprate.thanks", stars, _map);
    }

    [ConsoleCommand("css_ratemap", "sm_ratemap - rate the current map")]
    [ConsoleCommand("css_rm", "sm_rm - rate the current map")]
    public void CmdRateMap(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        ShowMenu(caller, prompt: false);
    }

    [ConsoleCommand("css_maprating", "sm_maprating [map] - show a map's rating")]
    public void CmdMapRating(CCSPlayerController? caller, CommandInfo info)
    {
        var map = info.ArgCount > 1 ? info.GetArg(1).Trim().ToLowerInvariant() : _map;
        if (map.Length == 0) return;
        var (avg, count) = Summary(Ratings(map).Values);
        if (count == 0) { Chat.ReplyT(caller, info, "maprate.none", map); return; }
        Chat.ReplyT(caller, info, "maprate.summary", map, avg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), count);
    }

    [ConsoleCommand("css_topmaps", "sm_topmaps - best rated maps")]
    public void CmdTopMaps(CCSPlayerController? caller, CommandInfo info)
    {
        var top = Storage.All<Dictionary<string, int>>(Ns)
            .Select(kv => (Map: kv.Key, S: Summary(kv.Value.Values)))
            .Where(x => x.S.Count >= Math.Max(1, Cfg.TopMapsMinRatings))
            .OrderByDescending(x => x.S.Average).ThenByDescending(x => x.S.Count)
            .Take(5)
            .ToList();
        if (top.Count == 0) { Chat.ReplyT(caller, info, "maprate.top_empty", Cfg.TopMapsMinRatings); return; }
        Chat.ReplyT(caller, info, "maprate.top_title");
        for (var i = 0; i < top.Count; i++)
            Chat.ReplyT(caller, info, "maprate.top_line", i + 1, top[i].Map,
                top[i].S.Average.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), top[i].S.Count);
    }
}
