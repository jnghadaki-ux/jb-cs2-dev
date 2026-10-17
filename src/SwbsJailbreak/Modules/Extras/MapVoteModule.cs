using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Extras;

/// <summary>Section "MapVote": player map commands (!rtv, !nominate, !nextmap, !timeleft, !maplist) for Workshop maps.</summary>
public sealed class MapVoteConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The vote pool: Workshop maps as "name|workshop id" (e.g. "jb_summer|3070212801"), or just the id. Empty = the admin
    /// map list (Admin.MapList). Names are only for display; the plugin learns a map's real name the first time it loads.
    /// </summary>
    public List<string> Maps { get; set; } = new();

    /// <summary>
    /// Share of the players that must type !rtv to start a map vote (0.6 = 60%). Only humans on a team count (spectators,
    /// e.g. AFK players moved there, only when they voted themselves).
    /// </summary>
    public double RtvRatio { get; set; } = 0.6;
    /// <summary>At least this many !rtv votes (never more than there are players). 0 = only RtvRatio.</summary>
    public int RtvMinVotes { get; set; } = 0;
    /// <summary>Count spectators who did not vote too (the old behaviour: every connected human).</summary>
    public bool RtvCountSpectators { get; set; } = false;
    /// <summary>Seconds after the map starts before !rtv is allowed.</summary>
    public int RtvDelaySeconds { get; set; } = 120;
    public int VoteSeconds { get; set; } = 20;
    /// <summary>Maps on the ballot (nominations first, the rest random from the pool).</summary>
    public int VoteOptions { get; set; } = 5;
    /// <summary>
    /// A won !rtv vote ends the map at the end of the current round (the time limit is cut to now; see
    /// MapEndMaxWaitSeconds). Off = !rtv only picks the next map.
    /// </summary>
    public bool RtvEndsGame { get; set; } = true;
    /// <summary>
    /// The map lasts this many minutes (mp_timelimit is set on every map start). When the time is up the map changes at the
    /// end of the round, or after MapEndMaxWaitSeconds when the round does not end. 0 = keep the server's mp_timelimit and
    /// let the game end the match by itself (an admin's !setmapend still ends the map).
    /// </summary>
    public int MapTimeLimitMinutes { get; set; } = 30;
    /// <summary>
    /// When the time is up, wait at most this many seconds for the round to end before changing the map anyway (modes whose
    /// rounds never end, a long last round). 0 = change as soon as the time is up.
    /// </summary>
    public int MapEndMaxWaitSeconds { get; set; } = 120;
    /// <summary>
    /// Seconds to wait for a map change before trying the next way to load it (a Workshop map that is not on the server yet
    /// downloads first; the last try repeats the first command).
    /// </summary>
    public float ChangeCheckSeconds { get; set; } = 25f;
    /// <summary>Seconds of "Changing to ..." countdown at the end of the map (end-of-match scoreboard) before the next map loads.</summary>
    public float ChangeDelaySeconds { get; set; } = 8f;
    /// <summary>Countdown seconds before an admin's !map / !changemap loads the map.</summary>
    public int AdminChangeCountdownSeconds { get; set; } = 5;
    /// <summary>Start a map vote automatically this many minutes before mp_timelimit runs out (0 = off).</summary>
    public int EndVoteMinutesBefore { get; set; } = 3;
    /// <summary>The end-of-map vote offers "Extend" by this many minutes, at most MaxExtends times per map.</summary>
    public int ExtendMinutes { get; set; } = 15;
    public int MaxExtends { get; set; } = 1;
    /// <summary>Maps played this recently (count) are not offered again.</summary>
    public int RecentMapsExcluded { get; set; } = 2;

    // ---- !startjb (admin): from a default map into jailbreak
    /// <summary>
    /// The Workshop collection with your jailbreak maps (the id in its Steam page URL). Its maps join the vote pool and
    /// their names are looked up on Steam, so no map has to be typed in by id. Admins switch maps on/off with !mapsetup.
    /// </summary>
    public string WorkshopCollectionId { get; set; } = "";
    /// <summary>Look up map names (and the collection's maps) on Steam's public Workshop API.</summary>
    public bool FetchNamesFromSteam { get; set; } = true;
    /// <summary>With MultiAddonManager loaded: !mapadd also downloads the map to the server (mm_download_addon) ahead of time.</summary>
    public bool PreDownloadMaps { get; set; } = true;
    /// <summary>Game mode for jailbreak, set before the map loads (casual: game_type 0, game_mode 0).</summary>
    public int GameType { get; set; } = 0;
    public int GameMode { get; set; } = 0;
    /// <summary>Server commands !startjb runs first (no bots, no team balancing, warmup off).</summary>
    public List<string> StartCommands { get; set; } = new()
    {
        "bot_kick", "bot_quota 0", "mp_autoteambalance 0", "mp_limitteams 0", "mp_warmuptime 0", "mp_warmup_end"
    };
    /// <summary>Map name prefixes that count as jailbreak maps (on one, !startjb just restarts the game in jailbreak mode).</summary>
    public List<string> JailbreakMapPrefixes { get; set; } = new() { "jb_", "ba_jail", "jail_", "ba_jb" };
}

/// <summary>One map of the vote pool (engine-free, unit-tested).</summary>
internal readonly record struct VoteMap(string Name, ulong WorkshopId)
{
    public string Label => Name.Length > 0 ? Name : "workshop " + WorkshopId;

    /// <summary>"name|id", "name:id", "workshop/id", "id" or a plain map name; null for an empty line.</summary>
    public static VoteMap? Parse(string? entry)
    {
        var s = (entry ?? "").Trim();
        if (s.Length == 0) return null;
        var sep = s.IndexOfAny(new[] { '|', ':' });
        string name = "", idText = s;
        if (sep >= 0) { name = s[..sep].Trim(); idText = s[(sep + 1)..].Trim(); }
        if (idText.StartsWith("workshop/", StringComparison.OrdinalIgnoreCase)) idText = idText[9..];
        if (ulong.TryParse(idText, out var id)) return new VoteMap(name, id);
        return new VoteMap(sep >= 0 ? name : s, 0); // a map by name only (stock or subscribed workshop map)
    }
}

/// <summary>Map vote rules that need no engine (unit-tested).</summary>
internal static class MapVoteRules
{
    /// <summary>
    /// Votes needed for !rtv: the ratio of the eligible players rounded up, at least one, at least
    /// <paramref name="minVotes"/> but never more than there are players.
    /// </summary>
    public static int RtvNeeded(int players, double ratio, int minVotes = 0)
    {
        players = Math.Max(0, players);
        var needed = (int)Math.Ceiling(players * Math.Clamp(ratio, 0.01, 1.0) - 1e-9);
        needed = Math.Max(needed, Math.Min(Math.Max(0, minVotes), players));
        return Math.Max(1, needed);
    }

    /// <summary>
    /// Players !rtv counts: the humans on a team plus spectators who voted themselves (AFK players moved to spectators do
    /// not hold the vote back); everyone when nobody is on a team.
    /// </summary>
    public static int RtvEligible(int onTeam, int spectatorVoters, int humans) =>
        onTeam > 0 ? Math.Min(humans, onTeam + spectatorVoters) : Math.Max(0, humans);

    /// <summary>The winning option index (most votes; ties broken by <paramref name="pick"/>); -1 with no votes.</summary>
    public static int Winner(IReadOnlyCollection<int> votes, int options, Func<int, int> pick)
    {
        if (votes.Count == 0 || options <= 0) return -1;
        var counts = new int[options];
        foreach (var v in votes) if (v >= 0 && v < options) counts[v]++;
        var max = counts.Max();
        if (max == 0) return -1;
        var tied = Enumerable.Range(0, options).Where(i => counts[i] == max).ToList();
        return tied[pick(tied.Count)];
    }

    /// <summary>Ballot: nominations first (in order, no duplicates), then random pool maps, never the current or recent ones.</summary>
    public static List<VoteMap> Ballot(IReadOnlyList<VoteMap> pool, IReadOnlyList<VoteMap> nominations, Func<VoteMap, bool> excluded, int size, Random rng)
    {
        var list = new List<VoteMap>();
        foreach (var n in nominations)
            if (list.Count < size && !excluded(n) && !list.Contains(n)) list.Add(n);
        foreach (var m in pool.Where(m => !excluded(m) && !list.Contains(m)).OrderBy(_ => rng.Next()))
        {
            if (list.Count >= size) break;
            list.Add(m);
        }

        return list;
    }

    /// <summary>Workshop item ids in a GetCollectionDetails response (maps only, sub-collections skipped).</summary>
    public static List<ulong> ParseCollection(string json)
    {
        var ids = new List<ulong>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var resp) || !resp.TryGetProperty("collectiondetails", out var cds)) return ids;
        foreach (var cd in cds.EnumerateArray())
        {
            if (!cd.TryGetProperty("children", out var children)) continue;
            foreach (var ch in children.EnumerateArray())
            {
                if (ch.TryGetProperty("filetype", out var ft) && ft.ValueKind == JsonValueKind.Number && ft.GetInt32() != 0) continue;
                if (ch.TryGetProperty("publishedfileid", out var id) && ulong.TryParse(id.ToString(), out var v) && !ids.Contains(v)) ids.Add(v);
            }
        }

        return ids;
    }

    /// <summary>id -> title from a GetPublishedFileDetails response (items Steam could not find are left out).</summary>
    public static Dictionary<ulong, string> ParseTitles(string json)
    {
        var titles = new Dictionary<ulong, string>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var resp) || !resp.TryGetProperty("publishedfiledetails", out var items)) return titles;
        foreach (var it in items.EnumerateArray())
        {
            if (it.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Number && res.GetInt32() != 1) continue;
            if (!it.TryGetProperty("publishedfileid", out var id) || !ulong.TryParse(id.ToString(), out var v)) continue;
            var title = it.TryGetProperty("title", out var t) ? (t.GetString() ?? "").Trim() : "";
            if (title.Length > 0) titles[v] = title;
        }

        return titles;
    }

    /// <summary>
    /// Commands to load a map, in the order to try them: host_workshop_map &lt;id&gt; for a Workshop map (then
    /// ds_workshop_changelevel &lt;name&gt; when its bsp name is known); changelevel for a stock map, ds_workshop_changelevel
    /// for a map of the server's Workshop collection by name. With a single way the command is tried twice. Names that are
    /// not plain map names are never put into a command.
    /// </summary>
    public static List<string> ChangeCommands(VoteMap m, string knownName, Func<string, bool> isStockMap)
    {
        var list = new List<string>();
        if (m.WorkshopId != 0)
        {
            list.Add($"host_workshop_map {m.WorkshopId}");
            if (IsMapName(knownName)) list.Add($"ds_workshop_changelevel {knownName}");
        }
        else if (IsMapName(m.Name))
        {
            if (isStockMap(m.Name)) list.Add($"changelevel {m.Name}");
            list.Add($"ds_workshop_changelevel {m.Name}");
        }

        if (list.Count == 1) list.Add(list[0]);
        return list;
    }

    /// <summary>A bsp name that is safe in a server command (letters, digits, _ - .).</summary>
    public static bool IsMapName(string? name) =>
        name is { Length: > 0 and <= 64 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') && !ulong.TryParse(name, out _);

    /// <summary>Seconds left: game start + mp_timelimit minutes - now; null without a time limit.</summary>
    public static float? TimeLeft(float limitMinutes, float gameStart, float now) =>
        limitMinutes > 0f ? gameStart + limitMinutes * 60f - now : null;

    /// <summary>The mp_timelimit (minutes) that ends the map <paramref name="minutesFromNow"/> from now.</summary>
    public static float TimeLimitFor(float elapsedSeconds, float minutesFromNow) =>
        MathF.Max(0.01f, MathF.Max(0f, elapsedSeconds) / 60f + MathF.Max(0f, minutesFromNow));

    /// <summary>Minutes for admin !setmapend: 0 (end of this round) to <paramref name="max"/>; null for a bad argument.</summary>
    public static int? ParseMinutes(string? arg, int max = 600) =>
        int.TryParse((arg ?? "").Trim(), out var n) && n >= 0 && n <= max ? n : null;

    /// <summary>
    /// Countdown before the next map loads at the end of the match: the wanted seconds, but done before the game's own
    /// mp_match_restart_delay runs out (it would load the map group's next map instead).
    /// </summary>
    public static float ChangeDelay(float wanted, float restartDelay)
    {
        wanted = Math.Max(1f, wanted);
        return restartDelay > 0f ? Math.Min(wanted, Math.Max(1f, restartDelay - 2f)) : wanted;
    }

    /// <summary>Time is up and the round did not end for <paramref name="maxWait"/> seconds: end the map anyway.</summary>
    public static bool ForceEnd(float secondsSinceTimeUp, int maxWait) => secondsSinceTimeUp >= Math.Max(0, maxWait);

    /// <summary>A cvar value in minutes ("45.5", never "45,5").</summary>
    public static string Minutes(float minutes) => minutes.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Minutes for admin !extend: the argument (1-180), the default without one; null for a bad argument.</summary>
    public static int? ExtendBy(string? arg, int defaultMinutes)
    {
        if (string.IsNullOrWhiteSpace(arg)) return Math.Max(1, defaultMinutes);
        return int.TryParse(arg.Trim(), out var n) && n >= 1 && n <= 180 ? n : null;
    }

    public static string FormatTime(float seconds)
    {
        var s = Math.Max(0, (int)seconds);
        return $"{s / 60}:{s % 60:00}";
    }
}

public sealed class MapVoteModule : JbModule
{
    private MapVoteConfig Cfg => Section<MapVoteConfig>("MapVote");
    private const string Ns = "mapvote";
    private const string HudKey = "mapvote.change";

    public override string Name => "MapVote";
    public override int Order => 95;
    public override bool Enabled => Cfg.Enabled;

    private enum Choice { Map, Extend, Stay }

    private readonly HashSet<int> _rtv = new();
    private readonly Dictionary<int, VoteMap> _nominations = new(); // slot -> map
    private readonly Dictionary<int, int> _votes = new();           // slot -> option
    private List<(Choice Kind, VoteMap Map)> _ballot = new();
    private bool _voting, _voted, _fromRtv;
    private VoteMap? _next;
    private bool _changing;
    private int _extends;
    private float _mapStart;
    private ulong _loadingId;
    private int _mapSerial, _changeId;
    /// <summary>An admin set the map end (!setmapend / !extend) or a won !rtv cut it: end the map even with MapTimeLimitMinutes 0.</summary>
    private bool _endForced;
    private float? _timeUpAt;
    private float _retryEndAt;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _voteTimer;

    public override void OnMapStart(string mapName)
    {
        _mapSerial++;
        _changeId++;
        // Learn the real name of the workshop map we asked for (shown in !maplist and the ballot from now on).
        if (_loadingId != 0)
        {
            Storage.Set(Ns, _loadingId.ToString(), mapName);
            Storage.Flush();
            _loadingId = 0;
        }

        var recent = Storage.Get<List<string>>(Ns, "recent") ?? new List<string>();
        recent.Remove(mapName);
        recent.Insert(0, mapName);
        Storage.Set(Ns, "recent", recent.Take(10).ToList());
        _rtv.Clear();
        _nominations.Clear();
        _votes.Clear();
        _voting = _voted = _fromRtv = _changing = _endForced = false;
        _timeUpAt = null;
        _retryEndAt = 0;
        if (Cfg.MapTimeLimitMinutes > 0) Plugin.AddTimer(1f, () => Server.ExecuteCommand($"mp_timelimit {Cfg.MapTimeLimitMinutes}"));
        _next = null;
        _extends = 0;
        _mapStart = Server.CurrentTime;
        RefreshFromSteam(null);
        if (!_warnedNoMam && !MultiAddonManager.Loaded)
        {
            _warnedNoMam = true;
            Logger.LogWarning("[MapVote] MultiAddonManager is not loaded: it is required to mount the SWBS Workshop packs (models, sounds, hats) and pre-download vote maps. https://github.com/Source2ZE/MultiAddonManager/releases");
        }

        _voteTimer?.Kill();
        _voteTimer = null;
    }

    public override void OnLoad(bool hotReload)
    {
        // _mapStart stays 0 until the first map start (no engine reads while the plugin loads).
        Plugin.AddTimer(1f, Tick, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT); // runs on every map
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        _rtv.Remove(player.Slot);
        _nominations.Remove(player.Slot);
        _votes.Remove(player.Slot);
        // The players who wanted a change may now be enough.
        if (!_voting && !_changing && _rtv.Count > 0 && _rtv.Count >= RtvNeeded) RockTheVote();
    }

    /// <summary>Admin map actions go to the admin log (daily files, Discord) like the admin suite's own.</summary>
    private static void AdminLog(CCSPlayerController? admin, string text) => Jb.Log.LogEvent(text, admin?.Jb(), null, LogType.Admin);

    // ================================================================= pool

    /// <summary>Every known map: the configured list (MapVote.Maps, else Admin.MapList) plus the Workshop collection's maps.</summary>
    private List<VoteMap> AllMaps()
    {
        var entries = Cfg.Maps.Count > 0 ? Cfg.Maps : Section<Admin.AdminConfig>("Admin").MapList;
        var list = entries.Select(VoteMap.Parse).Where(m => m != null).Select(m => m!.Value).ToList();
        foreach (var id in Storage.Get<List<ulong>>(Ns, "collection") ?? new List<ulong>())
            if (!list.Any(m => m.WorkshopId == id)) list.Add(new VoteMap("", id));
        return list.Distinct().ToList();
    }

    private static HashSet<ulong> Disabled() => (Storage.Get<List<ulong>>(Ns, "disabled") ?? new List<ulong>()).ToHashSet();

    /// <summary>The maps players can vote for: every known map an admin has not switched off in !mapsetup.</summary>
    private List<VoteMap> Pool()
    {
        var off = Disabled();
        return AllMaps().Where(m => m.WorkshopId == 0 || !off.Contains(m.WorkshopId)).ToList();
    }

    /// <summary>The name to show: the configured one, the Workshop title from Steam, the name it had when it loaded, its id.</summary>
    private static string Label(VoteMap m)
    {
        if (m.Name.Length > 0) return m.Name;
        if (m.WorkshopId == 0) return m.Label;
        if (Storage.Get<string>(Ns, "title:" + m.WorkshopId) is { Length: > 0 } title) return title;
        return Storage.Get<string>(Ns, m.WorkshopId.ToString()) is { Length: > 0 } known ? known : m.Label;
    }

    private bool IsCurrentOrRecent(VoteMap m)
    {
        var recent = (Storage.Get<List<string>>(Ns, "recent") ?? new List<string>()).Take(Math.Max(1, Cfg.RecentMapsExcluded + 1));
        var names = new[] { Label(m), m.Name, m.WorkshopId != 0 ? Storage.Get<string>(Ns, m.WorkshopId.ToString()) ?? "" : "" }
            .Where(n => n.Length > 0).ToList();
        return recent.Any(r => names.Any(n => string.Equals(r, n, StringComparison.OrdinalIgnoreCase)));
    }

    private VoteMap? Find(string text)
    {
        text = text.Trim();
        var pool = Pool();
        if (ulong.TryParse(text.Replace("workshop/", ""), out var id)) return pool.FirstOrDefault(m => m.WorkshopId == id) is { WorkshopId: > 0 } byId ? byId : null;
        var exact = pool.Where(m => string.Equals(Label(m), text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        var partial = pool.Where(m => Label(m).Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }


    private int RtvPlayers
    {
        get
        {
            var humans = Players.Humans().ToList();
            if (Cfg.RtvCountSpectators) return humans.Count;
            var onTeam = humans.Count(c => c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist);
            var spectatorVoters = humans.Count(c => c.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist) && _rtv.Contains(c.Slot));
            return MapVoteRules.RtvEligible(onTeam, spectatorVoters, humans.Count);
        }
    }

    private int RtvNeeded => MapVoteRules.RtvNeeded(RtvPlayers, Cfg.RtvRatio, Cfg.RtvMinVotes);

    // ================================================================= commands

    [ConsoleCommand("css_rtv", "sm_rtv - rock the vote: vote to change the map")]
    [ConsoleCommand("css_rockthevote", "sm_rockthevote - vote to change the map")]
    [ConsoleCommand("css_votechange", "sm_votechange - vote to change the map")]
    [OpenCommand]
    public void CmdRtv(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid || caller.IsBot) return;
        if (Pool().Count == 0) { Chat.Tell(caller, "mapvote.no_maps"); return; }
        if (_changing) { Chat.Tell(caller, "mapvote.change_pending"); return; }
        if (_voting) { Chat.Tell(caller, "mapvote.in_progress"); return; }
        // The next map is known: !rtv can still end this map early (SourceMod's sm_rtv_postvoteaction 0).
        if (_voted && (_next == null || !Cfg.RtvEndsGame)) { Chat.Tell(caller, "mapvote.already_decided", _next is { } n ? Label(n) : Lang.T("mapvote.extend")); return; }
        var wait = Cfg.RtvDelaySeconds - (Server.CurrentTime - _mapStart);
        if (wait > 0) { Chat.Tell(caller, "mapvote.rtv_wait", (int)Math.Ceiling(wait)); return; }
        if (!_rtv.Add(caller.Slot)) { Chat.Tell(caller, "mapvote.rtv_already", _rtv.Count, RtvNeeded); return; }
        Chat.TellAll("mapvote.rtv_added", Chat.Name(caller), _rtv.Count, RtvNeeded);
        if (_rtv.Count >= RtvNeeded) RockTheVote();
    }

    private void RockTheVote()
    {
        if (_voted && _next is { } next)
        {
            _rtv.Clear();
            Chat.TellAll("mapvote.rtv_next", Label(next));
            EndMapSoon();
            return;
        }

        if (!_voted) StartVote(true);
    }

    [ConsoleCommand("css_nominate", "sm_nominate [map] - put a map on the next map vote")]
    [ConsoleCommand("css_nom", "sm_nom [map] - put a map on the next map vote")]
    [OpenCommand]
    public void CmdNominate(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid || caller.IsBot) return;
        if (_voting || _voted) { Chat.Tell(caller, "mapvote.nominate_closed"); return; }
        var pool = Pool();
        if (pool.Count == 0) { Chat.Tell(caller, "mapvote.no_maps"); return; }
        if (info.ArgCount >= 2)
        {
            var m = Find(info.ArgString);
            if (m == null) { Chat.Tell(caller, "mapvote.not_found", info.ArgString.Trim()); return; }
            Nominate(caller, m.Value);
            return;
        }

        var menu = new JbMenu(Lang.T("mapvote.nominate_title"));
        foreach (var map in pool.OrderBy(Label, StringComparer.OrdinalIgnoreCase))
        {
            var m = map;
            menu.Add(Hud.Escape(Label(m)), p => Nominate(p, m), !IsCurrentOrRecent(m) && !_nominations.ContainsValue(m));
        }

        menu.Open(caller);
    }

    private int NominationLimit => Math.Clamp(Cfg.VoteOptions, 2, 6);

    private void Nominate(CCSPlayerController c, VoteMap m)
    {
        if (_voting || _voted) { Chat.Tell(c, "mapvote.nominate_closed"); return; }
        if (IsCurrentOrRecent(m)) { Chat.Tell(c, "mapvote.recent", Label(m)); return; }
        if (_nominations.Any(kv => kv.Key != c.Slot && kv.Value.Equals(m))) { Chat.Tell(c, "mapvote.already_nominated", Label(m)); return; }
        var replaced = _nominations.TryGetValue(c.Slot, out var old);
        if (!replaced && _nominations.Count >= NominationLimit) { Chat.Tell(c, "mapvote.nominations_full", NominationLimit); return; }
        _nominations[c.Slot] = m;
        if (replaced && !old.Equals(m)) Chat.TellAll("mapvote.nomination_changed", Chat.Name(c), Label(old), Label(m));
        else Chat.TellAll("mapvote.nominated", Chat.Name(c), Label(m));
    }

    [ConsoleCommand("css_nextmap", "sm_nextmap - show the next map")]
    [OpenCommand]
    public void CmdNextMap(CCSPlayerController? caller, CommandInfo info)
    {
        if (_voted) Chat.ReplyT(caller, info, "mapvote.next", _next is { } n ? Label(n) : Lang.T("mapvote.extend"));
        else Chat.ReplyT(caller, info, "mapvote.next_pending");
    }

    [ConsoleCommand("css_timeleft", "sm_timeleft - time left on this map")]
    [OpenCommand]
    public void CmdTimeLeft(CCSPlayerController? caller, CommandInfo info)
    {
        var left = TimeLeft();
        if (_changing) Chat.ReplyT(caller, info, "mapvote.change_pending");
        else if (left == null) Chat.ReplyT(caller, info, "mapvote.no_timelimit");
        else if (left.Value <= 0f) Chat.ReplyT(caller, info, "mapvote.timeleft_round");
        else Chat.ReplyT(caller, info, "mapvote.timeleft", MapVoteRules.FormatTime(left.Value));
    }

    [ConsoleCommand("css_currentmap", "sm_currentmap - show the current map")]
    [OpenCommand]
    public void CmdCurrentMap(CCSPlayerController? caller, CommandInfo info) => Chat.ReplyT(caller, info, "mapvote.current", Server.MapName);

    [ConsoleCommand("css_maplist", "sm_maplist - list the maps you can vote for")]
    [ConsoleCommand("css_maps", "sm_maps - list the maps you can vote for")]
    [OpenCommand]
    public void CmdMapList(CCSPlayerController? caller, CommandInfo info)
    {
        var pool = Pool();
        if (pool.Count == 0) { Chat.ReplyT(caller, info, "mapvote.no_maps"); return; }
        Chat.ReplyT(caller, info, "mapvote.list", string.Join(", ", pool.Select(Label).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
    }

    [ConsoleCommand("css_revote", "sm_revote - change your map vote while the vote is open")]
    [OpenCommand]
    public void CmdRevote(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid || caller.IsBot) return;
        if (!_voting) { Chat.Tell(caller, "mapvote.no_vote_open"); return; }
        ShowBallot(caller);
    }

    // ================================================================= !extend, !setmapend, !setnextmap (admin)

    // !extend is the warden's round extend on jailbreak (WardenTools): the map extend is !extendmap only.
    [ConsoleCommand("css_extendmap", "sm_extendmap [minutes] - (admin) extend the map")]
    public void CmdExtend(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "map")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        var minutes = MapVoteRules.ExtendBy(info.ArgCount >= 2 ? info.GetArg(1) : null, Cfg.ExtendMinutes);
        if (minutes == null) { Chat.ReplyT(caller, info, "core.usage", "!extendmap [minutes]"); return; }
        if (TimeLeft() == null) { Chat.ReplyT(caller, info, "mapvote.no_timelimit"); return; }
        if (_extends >= Cfg.MaxExtends) { Chat.ReplyT(caller, info, "mapvote.extend_max", Cfg.MaxExtends); return; }
        _extends++;
        ExtendBy(minutes.Value);
        var who = caller?.PlayerName ?? "Console";
        AdminLog(caller, $"{who} extended the map by {minutes.Value} minutes");
        Chat.TellAll("mapvote.extended_admin", who, minutes.Value);
    }

    [ConsoleCommand("css_setmapend", "sm_setmapend <minutes> - (admin) end the map this many minutes from now (0 = at the end of this round)")]
    [ConsoleCommand("css_settimeleft", "sm_settimeleft <minutes> - (admin) end the map this many minutes from now")]
    public void CmdSetMapEnd(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "map")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        var minutes = MapVoteRules.ParseMinutes(info.ArgCount >= 2 ? info.GetArg(1) : null);
        if (minutes == null) { Chat.ReplyT(caller, info, "core.usage", "!setmapend <minutes, 0-600>"); return; }
        var rules = Round.GameRules;
        if (rules == null) return;
        var limit = MapVoteRules.TimeLimitFor(Server.CurrentTime - rules.GameStartTime, minutes.Value);
        Server.ExecuteCommand($"mp_timelimit {MapVoteRules.Minutes(limit)}");
        _endForced = true;
        _timeUpAt = null;
        _retryEndAt = 0;
        var who = caller?.PlayerName ?? "Console";
        AdminLog(caller, $"{who} set the map to end in {minutes.Value} minutes");
        if (minutes.Value == 0) Chat.TellAll("mapvote.mapend_round", who);
        else Chat.TellAll("mapvote.mapend_set", who, minutes.Value);
    }

    [ConsoleCommand("css_setnextmap", "sm_setnextmap <map|workshop id|link> - (admin) set the next map")]
    public void CmdSetNextMap(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "map")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!setnextmap <map|workshop id>"); return; }
        var m = Resolve(info.ArgString, false);
        if (m == null) { Chat.ReplyT(caller, info, "mapvote.not_found", info.ArgString.Trim()); return; }
        // A running vote is cancelled: the admin's map is the next one, loaded when the map ends.
        _voteTimer?.Kill();
        _voteTimer = null;
        _voting = false;
        SetNext(m.Value);
        var who = caller?.PlayerName ?? "Console";
        AdminLog(caller, $"{who} set the next map to {Label(m.Value)}");
        Chat.TellAll("mapvote.next_set", who, Label(m.Value));
    }

    /// <summary>The next map is decided: shown by !nextmap, set as nextlevel for a stock map, downloaded ahead for a Workshop map.</summary>
    private void SetNext(VoteMap m)
    {
        _voted = true;
        _next = m;
        if (m.WorkshopId == 0 && MapVoteRules.IsMapName(m.Name) && Server.IsMapValid(m.Name)) Server.ExecuteCommand($"nextlevel {m.Name}");
        else if (m.WorkshopId != 0 && Cfg.PreDownloadMaps && MultiAddonManager.Loaded) Server.ExecuteCommand($"mm_download_addon {m.WorkshopId}");
    }

    private void ExtendBy(int minutes)
    {
        var limit = ConVar.Find("mp_timelimit")?.GetPrimitiveValue<float>() ?? 0f;
        // Time already up (waiting for the round to end): extend from now.
        if (TimeLeft() is <= 0f && Round.GameRules is { } rules)
            limit = MapVoteRules.TimeLimitFor(Server.CurrentTime - rules.GameStartTime, 0f);
        Server.ExecuteCommand($"mp_timelimit {MapVoteRules.Minutes(limit + Math.Max(1, minutes))}");
        _timeUpAt = null;
    }

    // ================================================================= Steam names, !mapsetup, !mapadd, !mapremove

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private bool _refreshing;
    private bool _warnedNoMam;

    /// <summary>
    /// Fetch the collection's maps and every unnamed map's Workshop title from Steam (background thread), then store them
    /// on the main thread. <paramref name="admin"/> gets a summary when done.
    /// </summary>
    private void RefreshFromSteam(CCSPlayerController? admin)
    {
        if (!Cfg.FetchNamesFromSteam || _refreshing) return;
        var collection = ulong.TryParse(Cfg.WorkshopCollectionId.Trim(), out var cid) ? cid : 0;
        var known = AllMaps().Where(m => m.WorkshopId != 0).Select(m => m.WorkshopId).ToList();
        if (collection == 0 && known.Count == 0) return;
        _refreshing = true;
        var adminSlot = admin?.Slot ?? -1;
        Task.Run(async () =>
        {
            List<ulong>? items = null;
            var titles = new Dictionary<ulong, string>();
            string? error = null;
            try
            {
                if (collection != 0)
                {
                    var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["collectioncount"] = "1", ["publishedfileids[0]"] = collection.ToString() });
                    var res = await Http.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/", form);
                    items = MapVoteRules.ParseCollection(await res.Content.ReadAsStringAsync());
                }

                var ids = known.Concat(items ?? new List<ulong>()).Distinct().ToList();
                for (var i = 0; i < ids.Count; i += 100)
                {
                    var batch = ids.Skip(i).Take(100).ToList();
                    var fields = new Dictionary<string, string> { ["itemcount"] = batch.Count.ToString() };
                    for (var k = 0; k < batch.Count; k++) fields[$"publishedfileids[{k}]"] = batch[k].ToString();
                    var res = await Http.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", new FormUrlEncodedContent(fields));
                    foreach (var kv in MapVoteRules.ParseTitles(await res.Content.ReadAsStringAsync())) titles[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            Server.NextFrame(() =>
            {
                _refreshing = false;
                if (items != null) Storage.Set(Ns, "collection", items);
                foreach (var kv in titles) Storage.Set(Ns, "title:" + kv.Key, kv.Value);
                Storage.Flush();
                if (error != null) Logger.LogWarning("[MapVote] Steam lookup failed: {Error}", error);
                else Logger.LogInformation("[MapVote] Steam: {Maps} collection maps, {Titles} map names", items?.Count ?? 0, titles.Count);
                if (adminSlot >= 0 && Players.Get(adminSlot).Controller is { IsValid: true } a)
                {
                    if (error != null) Chat.Tell(a, "mapvote.steam_failed", error);
                    else Chat.Tell(a, "mapvote.steam_done", items?.Count ?? 0, titles.Count);
                }
            });
        });
    }

    [ConsoleCommand("css_mapsetup", "sm_mapsetup - (admin) switch maps on/off for voting, refresh map names from Steam")]
    public void CmdMapSetup(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "map")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (caller == null)
        {
            var off = Disabled();
            foreach (var m in AllMaps()) info.ReplyToCommand($"{(off.Contains(m.WorkshopId) ? "[off]" : "[on] ")} {Label(m)} {(m.WorkshopId != 0 ? m.WorkshopId.ToString() : "")}");
            return;
        }

        OpenMapSetup(caller);
    }

    private void OpenMapSetup(CCSPlayerController admin, int page = 0)
    {
        var off = Disabled();
        var all = AllMaps().OrderBy(Label, StringComparer.OrdinalIgnoreCase).ToList();
        var m = new JbMenu(Lang.T("mapvote.setup_title")) { Subtitle = Hud.Escape(Lang.T("mapvote.setup_sub", all.Count - all.Count(x => off.Contains(x.WorkshopId)), all.Count)) };
        m.Add(Lang.T(_refreshing ? "mapvote.setup_refreshing" : "mapvote.setup_refresh"), a => { RefreshFromSteam(a); OpenMapSetup(a); }, !_refreshing && Cfg.FetchNamesFromSteam);
        foreach (var map in all)
        {
            var v = map;
            var on = v.WorkshopId == 0 || !off.Contains(v.WorkshopId);
            m.Add($"{(on ? "[ON] " : "[OFF] ")}{Hud.Escape(Label(v))}", a =>
            {
                var set = Disabled();
                if (!set.Remove(v.WorkshopId)) set.Add(v.WorkshopId);
                Storage.Set(Ns, "disabled", set.ToList());
                Storage.Flush();
                Jb.Log.LogEvent($"{a.PlayerName} switched map {Label(v)} {(set.Contains(v.WorkshopId) ? "off" : "on")} for voting", a.Jb(), null, LogType.Admin);
                OpenMapSetup(a);
            }, v.WorkshopId != 0);
        }

        m.Open(admin);
    }

    [ConsoleCommand("css_mapadd", "sm_mapadd <workshop id|url> - (admin) add a Workshop map to the vote list (its name comes from Steam)")]
    public void CmdMapAdd(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "map")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        var id = info.ArgCount >= 2 ? WorkshopIdFrom(info.ArgString) : 0;
        if (id == 0) { Chat.ReplyT(caller, info, "core.usage", "!mapadd <workshop id or link>"); return; }
        if (AllMaps().Any(m => m.WorkshopId == id))
        {
            var set = Disabled();
            set.Remove(id);
            Storage.Set(Ns, "disabled", set.ToList());
        }
        else Config.Update<MapVoteConfig>("MapVote", c => { if (c.Maps.Count == 0) c.Maps.AddRange(Section<Admin.AdminConfig>("Admin").MapList); c.Maps.Add(id.ToString()); });

        Config.SaveIfDirty(Logger);
        Storage.Flush();
        Chat.ReplyT(caller, info, "mapvote.added", id);
        if (Cfg.PreDownloadMaps && MultiAddonManager.Loaded) Server.ExecuteCommand($"mm_download_addon {id}");
        RefreshFromSteam(caller);
    }

    [ConsoleCommand("css_mapremove", "sm_mapremove <workshop id|name> - (admin) take a map off the vote list")]
    public void CmdMapRemove(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "map")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!mapremove <workshop id or name>"); return; }
        var id = WorkshopIdFrom(info.ArgString);
        var m = id != 0 ? AllMaps().FirstOrDefault(x => x.WorkshopId == id) : FindIn(AllMaps(), info.ArgString);
        if (m is not { WorkshopId: > 0 } map) { Chat.ReplyT(caller, info, "mapvote.not_found", info.ArgString.Trim()); return; }
        // Collection maps cannot be deleted from the collection here: switch them off (same as !mapsetup).
        var set = Disabled();
        set.Add(map.WorkshopId);
        Storage.Set(Ns, "disabled", set.ToList());
        Storage.Flush();
        Chat.ReplyT(caller, info, "mapvote.removed", Label(map));
    }

    [ConsoleCommand("css_addon", "sm_addon <add|remove|list> [workshop id|link] - (admin) mount Workshop addons with MultiAddonManager")]
    [ConsoleCommand("css_addons", "sm_addons - (admin) list the Workshop addons MultiAddonManager mounts")]
    public void CmdAddon(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "config")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (!MultiAddonManager.Loaded) { Chat.ReplyT(caller, info, "mapvote.mam_missing"); return; }
        var sub = info.ArgCount >= 2 ? info.GetArg(1).ToLowerInvariant() : "list";
        var id = info.ArgCount >= 3 ? WorkshopIdFrom(info.GetArg(2)) : 0;
        switch (sub)
        {
            case "add" or "remove" when id == 0:
                Chat.ReplyT(caller, info, "core.usage", $"!addon {sub} <workshop id or link>");
                return;
            case "add":
                Server.ExecuteCommand($"mm_add_addon {id}");
                Chat.ReplyT(caller, info, "mapvote.addon_added", Label(new VoteMap("", id)), id);
                RefreshTitle(id);
                break;
            case "remove":
                Server.ExecuteCommand($"mm_remove_addon {id}");
                Chat.ReplyT(caller, info, "mapvote.addon_removed", Label(new VoteMap("", id)), id);
                break;
            default:
                var ids = MultiAddonManager.Addons();
                if (ids.Count == 0) { Chat.ReplyT(caller, info, "mapvote.addon_none"); return; }
                foreach (var a in ids) Chat.ReplyT(caller, info, "mapvote.addon_line", Label(new VoteMap("", a)), a);
                return;
        }

        Jb.Log.LogEvent($"{caller?.PlayerName ?? "Console"} {sub} Workshop addon {id}", caller?.Jb(), null, LogType.Admin);
    }

    /// <summary>Look up one Workshop title (for !addon) without touching the vote pool.</summary>
    private void RefreshTitle(ulong id)
    {
        if (!Cfg.FetchNamesFromSteam) return;
        Task.Run(async () =>
        {
            try
            {
                var res = await Http.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/",
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["itemcount"] = "1", ["publishedfileids[0]"] = id.ToString() }));
                var titles = MapVoteRules.ParseTitles(await res.Content.ReadAsStringAsync());
                Server.NextFrame(() =>
                {
                    foreach (var kv in titles) Storage.Set(Ns, "title:" + kv.Key, kv.Value);
                    Storage.Flush();
                });
            }
            catch
            {
                // name stays the id
            }
        });
    }

    /// <summary>A Workshop id from "3070212801", "workshop/3070212801" or a Steam link (…?id=3070212801).</summary>
    internal static ulong WorkshopIdFrom(string text)
    {
        text = text.Trim().Trim('"');
        var q = text.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
        if (q >= 0) text = new string(text[(q + 3)..].TakeWhile(char.IsDigit).ToArray());
        if (text.StartsWith("workshop/", StringComparison.OrdinalIgnoreCase)) text = text[9..];
        return ulong.TryParse(text, out var id) ? id : 0;
    }

    private static VoteMap? FindIn(List<VoteMap> maps, string text)
    {
        text = text.Trim();
        var exact = maps.Where(m => string.Equals(Label(m), text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        var partial = maps.Where(m => Label(m).Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    // ================================================================= !startjb

    [ConsoleCommand("css_startjb", "sm_startjb [collection id|map] - (admin) start jailbreak: from a default map, load the Workshop jailbreak maps")]
    [ConsoleCommand("css_jbstart", "sm_jbstart - (admin) start jailbreak")]
    [ConsoleCommand("css_startmode", "sm_startmode - (admin) start this server's game mode (jailbreak)")]
    public void CmdStartJb(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "map")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        var arg = info.ArgCount >= 2 ? info.ArgString.Trim().Trim('"') : "";
        foreach (var cmd in Cfg.StartCommands.Where(c => !string.IsNullOrWhiteSpace(c))) Server.ExecuteCommand(cmd);
        var who = caller?.PlayerName ?? "Console";
        Jb.Log.LogEvent($"{who} started jailbreak (!startjb {arg})", caller?.Jb(), null, LogType.Admin);

        if (arg.Length == 0 && IsJailbreakMap(Server.MapName))
        {
            // Already on a jailbreak map (e.g. stuck in warmup or a competitive config): restart the game in jailbreak mode.
            Server.ExecuteCommand($"game_type {Cfg.GameType}");
            Server.ExecuteCommand($"game_mode {Cfg.GameMode}");
            Server.ExecuteCommand("mp_restartgame 1");
            Chat.TellAll("mapvote.startjb_restart", who);
            return;
        }

        // From a default map: casual mode, then the Workshop collection (or a map from the pool) loads.
        Server.ExecuteCommand($"game_type {Cfg.GameType}");
        Server.ExecuteCommand($"game_mode {Cfg.GameMode}");
        var collection = ulong.TryParse(arg, out var a) ? a : ulong.TryParse(Cfg.WorkshopCollectionId.Trim(), out var c) ? c : 0;
        if (collection != 0 && (arg.Length == 0 || Find(arg) == null))
        {
            Chat.TellAll("mapvote.startjb_collection", who, collection);
            Plugin.AddTimer(3f, () => Server.ExecuteCommand($"host_workshop_collection {collection}"), CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        var target = arg.Length > 0 ? Find(arg) : null;
        if (target == null)
        {
            var pool = Pool().Where(m => !IsCurrentOrRecent(m)).ToList();
            if (pool.Count == 0) pool = Pool();
            if (pool.Count == 0) { Chat.ReplyT(caller, info, "mapvote.startjb_nothing"); return; }
            target = pool[Random.Shared.Next(pool.Count)];
        }

        Chat.TellAll("mapvote.startjb_map", who, Label(target.Value));
        ChangeTo(target.Value, Math.Max(1, Cfg.AdminChangeCountdownSeconds), caller);
    }

    private bool IsJailbreakMap(string map) =>
        Cfg.JailbreakMapPrefixes.Any(p => p.Length > 0 && map.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || Pool().Any(m => string.Equals(Label(m), map, StringComparison.OrdinalIgnoreCase));


    // ================================================================= vote

    private static float? TimeLeft()
    {
        try
        {
            var limit = ConVar.Find("mp_timelimit")?.GetPrimitiveValue<float>() ?? 0f;
            var rules = Round.GameRules;
            if (rules == null) return null;
            return MapVoteRules.TimeLeft(limit, rules.GameStartTime, Server.CurrentTime);
        }
        catch
        {
            return null;
        }
    }

    private static bool Warmup
    {
        get
        {
            try { return Round.GameRules?.WarmupPeriod ?? false; }
            catch { return false; }
        }
    }

    /// <summary>The module itself ends the map when the time is up (else the game ends the match at a round end, if ever).</summary>
    private bool EndsMapItself => Cfg.MapTimeLimitMinutes > 0 || _endForced;

    private DateTime _nextNameCheck = DateTime.MinValue;
    private DateTime _nextNameScan = DateTime.MinValue;

    /// <summary>
    /// Every 10 s (also right after a mid-map plugin load, which gets no map start): look up maps that still show as
    /// "workshop &lt;id&gt;", at most every 10 minutes while some are unnamed.
    /// </summary>
    private void NameCheck()
    {
        if (!Cfg.FetchNamesFromSteam || _refreshing || DateTime.UtcNow < _nextNameCheck || DateTime.UtcNow < _nextNameScan) return;
        _nextNameScan = DateTime.UtcNow.AddSeconds(10);
        var collection = ulong.TryParse(Cfg.WorkshopCollectionId.Trim(), out var cid) ? cid : 0;
        var collectionMissing = collection != 0 && Storage.Get<List<ulong>>(Ns, "collection") == null;
        var unnamed = AllMaps().Any(m => m.WorkshopId != 0 && m.Name.Length == 0 && Storage.Get<string>(Ns, "title:" + m.WorkshopId) is not { Length: > 0 });
        if (!collectionMissing && !unnamed) return;
        _nextNameCheck = DateTime.UtcNow.AddMinutes(10);
        RefreshFromSteam(null);
    }

    /// <summary>
    /// Every second: the end-of-map vote EndVoteMinutesBefore the end, and the map end itself. mp_timelimit alone only ends
    /// the match when a round ends (never on deathmatch or movement maps), so when the time is up the map changes at the end
    /// of the round, or after MapEndMaxWaitSeconds.
    /// </summary>
    private void Tick()
    {
        NameCheck();
        if (_mapStart <= 0f) _mapStart = Server.CurrentTime; // loaded mid-map
        if (_changing) return;
        var left = TimeLeft();
        if (left == null || Warmup) { _timeUpAt = null; return; }
        if (!_voting && !_voted && Cfg.EndVoteMinutesBefore > 0 && left.Value <= Cfg.EndVoteMinutesBefore * 60f && Pool().Count > 0) StartVote(false);
        if (left.Value > 0f || !EndsMapItself) { _timeUpAt = null; return; }
        var now = Server.CurrentTime;
        if (_timeUpAt == null)
        {
            _timeUpAt = now;
            if (Cfg.MapEndMaxWaitSeconds > 0) Chat.TellAll("mapvote.time_up");
        }

        if (_voting || now < _retryEndAt) return;
        if (MapVoteRules.ForceEnd(now - _timeUpAt.Value, Cfg.MapEndMaxWaitSeconds)) EndMap();
    }

    /// <summary>A won !rtv: the time is up now, so the map ends at the end of this round (or after MapEndMaxWaitSeconds).</summary>
    private void EndMapSoon()
    {
        if (!Cfg.RtvEndsGame) return;
        var rules = Round.GameRules;
        if (rules != null) Server.ExecuteCommand($"mp_timelimit {MapVoteRules.Minutes(MapVoteRules.TimeLimitFor(Server.CurrentTime - rules.GameStartTime, 0f))}");
        _endForced = true;
        _retryEndAt = 0;
        if (rules == null || Cfg.MapEndMaxWaitSeconds <= 0) EndMap();
    }

    private void StartVote(bool fromRtv)
    {
        if (_voting || _voted) return;
        var pool = Pool();
        var ballot = MapVoteRules.Ballot(pool, _nominations.Values.ToList(), IsCurrentOrRecent, NominationLimit, Random.Shared);
        if (ballot.Count == 0) { Chat.TellAll("mapvote.no_maps"); return; }
        _ballot = ballot.Select(m => (Choice.Map, m)).ToList();
        if (fromRtv) _ballot.Add((Choice.Stay, default)); // "Don't change the map"
        else if (_extends < Cfg.MaxExtends && TimeLeft() != null) _ballot.Add((Choice.Extend, default));
        _voting = true;
        _fromRtv = fromRtv;
        _votes.Clear();
        Chat.TellAll("mapvote.started", Cfg.VoteSeconds);
        foreach (var c in Players.Humans()) ShowBallot(c);
        _voteTimer = Plugin.AddTimer(Math.Clamp(Cfg.VoteSeconds, 5, 120), EndVote, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
    }

    private string BallotLabel((Choice Kind, VoteMap Map) item) => item.Kind switch
    {
        Choice.Extend => Lang.T("mapvote.extend_item", Cfg.ExtendMinutes),
        Choice.Stay => Lang.T("mapvote.dont_change"),
        _ => Label(item.Map)
    };

    private void ShowBallot(CCSPlayerController c)
    {
        var m = new JbMenu(Lang.T("mapvote.title")) { Timeout = Math.Clamp(Cfg.VoteSeconds, 5, 120) };
        for (var i = 0; i < _ballot.Count; i++)
        {
            var idx = i;
            var label = BallotLabel(_ballot[i]);
            m.Add(Hud.Escape(label), p =>
            {
                if (!_voting) return;
                _votes[p.Slot] = idx;
                Chat.Tell(p, "mapvote.voted", label);
            });
        }

        m.Open(c);
    }

    private void EndVote()
    {
        _voteTimer = null;
        if (!_voting) return;
        _voting = false;
        var win = MapVoteRules.Winner(_votes.Values.ToList(), _ballot.Count, n => Random.Shared.Next(n));
        if (win < 0)
        {
            // Nobody voted: an !rtv vote fails, the end-of-map vote picks a random map so the server still moves on.
            if (_fromRtv) { _rtv.Clear(); Chat.TellAll("mapvote.no_votes"); return; }
            win = Enumerable.Range(0, _ballot.Count).Where(i => _ballot[i].Kind == Choice.Map).OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
        }

        var choice = _ballot[win];
        var count = _votes.Values.Count(v => v == win);
        switch (choice.Kind)
        {
            case Choice.Stay:
                _rtv.Clear();
                Chat.TellAll("mapvote.stays", count);
                return;
            case Choice.Extend:
                _extends++;
                ExtendBy(Cfg.ExtendMinutes);
                _rtv.Clear();
                _nominations.Clear();
                Chat.TellAll("mapvote.extended", Cfg.ExtendMinutes, count);
                return;
        }

        SetNext(choice.Map);
        Logger.LogInformation("[MapVote] next map {Map} ({Votes} votes)", Label(choice.Map), count);
        if (_fromRtv && Cfg.RtvEndsGame)
        {
            Chat.TellAll("mapvote.won_rtv", Label(choice.Map), count);
            EndMapSoon();
        }
        else Chat.TellAll("mapvote.won_map_end", Label(choice.Map), count);
    }

    /// <summary>The round ended after the map's time ran out: the map changes now (also when the game itself did not end the match).</summary>
    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd ev, GameEventInfo info)
    {
        if (!_changing && !_voting && EndsMapItself && !Warmup && TimeLeft() is <= 0f && Server.CurrentTime >= _retryEndAt) EndMap();
        return HookResult.Continue;
    }

    /// <summary>The game is over (time limit, max rounds, a finished match): load the voted map, or a random one if no vote happened.</summary>
    [GameEventHandler]
    public HookResult OnMatchEnd(EventCsWinPanelMatch ev, GameEventInfo info)
    {
        if (!_changing) EndMap();
        return HookResult.Continue;
    }

    /// <summary>
    /// End the map: the vote winner, the admin's !setnextmap or a random pool map. The game's own match end would load the
    /// map group's next map after mp_match_restart_delay (CS2 ignores nextlevel for Workshop maps), so it is told to restart
    /// instead and the plugin loads the map before that.
    /// </summary>
    private void EndMap()
    {
        if (_changing) return;
        var next = _next ?? RandomMap();
        if (next == null) return; // nothing configured: the server's own map cycle decides
        var restartDelay = ConVar.Find("mp_match_restart_delay")?.GetPrimitiveValue<float>() ?? 0f;
        if (next.Value.WorkshopId != 0) Cvars.SetTemp("mp_match_end_restart", "1");
        ChangeTo(next.Value, MapVoteRules.ChangeDelay(Cfg.ChangeDelaySeconds, restartDelay), null);
    }

    private VoteMap? RandomMap()
    {
        var pool = Pool().Where(m => !IsCurrentOrRecent(m)).ToList();
        if (pool.Count == 0) pool = Pool().Where(m => !string.Equals(KnownName(m), Server.MapName, StringComparison.OrdinalIgnoreCase)).ToList();
        return pool.Count == 0 ? null : pool[Random.Shared.Next(pool.Count)];
    }

    /// <summary>
    /// The one way a map is loaded (vote, map end, !rtv, !setnextmap, !map, !changemap, !startmode): "Changing to X in N"
    /// countdown (chat + HUD), then host_workshop_map &lt;id&gt; for a Workshop map or changelevel for a stock map. If the
    /// map has not changed ChangeCheckSeconds later the next command is tried (the last try repeats the first), and the
    /// admin is told when none worked. A newer change (an admin's) replaces a pending one.
    /// </summary>
    private void ChangeTo(VoteMap m, float delay, CCSPlayerController? admin)
    {
        var attempts = MapVoteRules.ChangeCommands(m, KnownName(m), Server.IsMapValid);
        var label = Label(m);
        var adminSlot = admin?.Slot ?? -1;
        if (attempts.Count == 0)
        {
            Logger.LogError("[MapVote] no way to load {Map}", label);
            if (admin != null) Chat.Tell(admin, "mapvote.change_failed", label);
            return;
        }

        _voteTimer?.Kill();
        _voteTimer = null;
        _voting = false;
        _changing = true;
        _voted = true;
        _next = m;
        var id = ++_changeId;
        var serial = _mapSerial;
        bool Stale() => id != _changeId || serial != _mapSerial;
        var left = Math.Max(0, (int)Math.Ceiling(delay));
        Chat.TellAll("mapvote.changing_in", label, left);

        void Attempt(int i)
        {
            if (Stale()) return;
            _loadingId = m.WorkshopId;
            Logger.LogInformation("[MapVote] changing map to {Map}: {Command}", label, attempts[i]);
            Server.ExecuteCommand(attempts[i]);
            Plugin.AddTimer(Math.Clamp(Cfg.ChangeCheckSeconds, 10f, 600f), () =>
            {
                if (Stale()) return; // the map changed
                if (i + 1 < attempts.Count)
                {
                    Logger.LogWarning("[MapVote] map did not change after \"{Command}\", trying \"{Next}\"", attempts[i], attempts[i + 1]);
                    Attempt(i + 1);
                    return;
                }

                _changing = false;
                _loadingId = 0;
                _next = null; // the map end picks another map next time
                _voted = false;
                _retryEndAt = Server.CurrentTime + 120f;
                Logger.LogError("[MapVote] could not change map to {Map} (tried: {Commands})", label, string.Join("; ", attempts));
                if (adminSlot >= 0 && Players.Get(adminSlot).Controller is { IsValid: true } a) Chat.Tell(a, "mapvote.change_failed", label);
                else Chat.TellAll("mapvote.change_failed", label);
            }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
        }

        void Count()
        {
            if (Stale()) return;
            if (left <= 0)
            {
                Hud.ClearGlobal(HudKey);
                Chat.TellAll("mapvote.changing", label);
                Attempt(0);
                return;
            }

            Hud.SetGlobal(HudKey, Lang.T("mapvote.hud_changing", Hud.Escape(label), left), 60, 1.5f);
            left--;
            Plugin.AddTimer(1f, Count, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
        }

        Count();
    }

    /// <summary>The map's real (bsp) name if it has loaded here before, for ds_workshop_changelevel.</summary>
    private static string KnownName(VoteMap m) =>
        m.WorkshopId != 0 ? Storage.Get<string>(Ns, m.WorkshopId.ToString()) ?? "" : m.Name;

    // ================================================================= !mapchange (admin, instant)

    [ConsoleCommand("css_mapchange", "sm_mapchange [map name|workshop id|link] - (admin) change the map now (no name = pick from a menu)")]
    [ConsoleCommand("css_changemap", "sm_changemap [map] - (admin) change the map now")]
    public void CmdMapChange(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "map")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (info.ArgCount < 2)
        {
            if (caller == null) { info.ReplyToCommand("Usage: css_mapchange <map name|workshop id|link>"); return; }
            OpenChangeMenu(caller);
            return;
        }

        ChangeNow(caller, info.ArgString, info);
    }

    /// <summary>Change the map right away (after a short countdown). Used by !mapchange, !map and the admin menu.</summary>
    internal bool ChangeNow(CCSPlayerController? admin, string query, CommandInfo? info)
    {
        var m = Resolve(query, true);
        if (m == null)
        {
            if (info != null) Chat.ReplyT(admin, info, "mapvote.not_found", query.Trim());
            else if (admin != null) Chat.Tell(admin, "mapvote.not_found", query.Trim());
            return false;
        }

        var who = admin?.PlayerName ?? "Console";
        AdminLog(admin, $"{who} changed the map to {Label(m.Value)}");
        ChangeTo(m.Value, Math.Max(1, Cfg.AdminChangeCountdownSeconds), admin);
        return true;
    }

    /// <summary>
    /// A Workshop id or link, a map from the list by name (also switched-off ones), or a stock map name. With
    /// <paramref name="anyName"/> also any other plain map name (a map of the server's Workshop collection, loaded with
    /// ds_workshop_changelevel; the change check tells the admin if it does not exist).
    /// </summary>
    private VoteMap? Resolve(string query, bool anyName)
    {
        query = query.Trim().Trim('"');
        if (query.Length == 0) return null;
        var id = WorkshopIdFrom(query);
        var all = AllMaps();
        if (id != 0) return all.FirstOrDefault(x => x.WorkshopId == id) is { WorkshopId: > 0 } known ? known : new VoteMap("", id);
        var byLearned = all.Where(x => x.WorkshopId != 0 && string.Equals(KnownName(x), query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byLearned.Count == 1) return byLearned[0];
        if (FindIn(all, query) is { } found) return found;
        if (Server.IsMapValid(query)) return new VoteMap(query, 0);
        return anyName && MapVoteRules.IsMapName(query) ? new VoteMap(query, 0) : null;
    }

    private void OpenChangeMenu(CCSPlayerController admin)
    {
        var maps = AllMaps().OrderBy(Label, StringComparer.OrdinalIgnoreCase).ToList();
        if (maps.Count == 0) { Chat.Tell(admin, "mapvote.no_maps"); return; }
        var m = new JbMenu(Lang.T("mapvote.change_title")) { Subtitle = Hud.Escape(Lang.T("mapvote.current_short", Server.MapName)) };
        foreach (var map in maps)
        {
            var v = map;
            var current = string.Equals(KnownName(v), Server.MapName, StringComparison.OrdinalIgnoreCase);
            m.Add(Hud.Escape(Label(v)), a => ChangeNow(a, v.WorkshopId != 0 ? v.WorkshopId.ToString() : v.Name, null), !current);
        }

        m.Open(admin);
    }
}

/// <summary>The MultiAddonManager Metamod plugin (mounts Workshop addons): present when its mm_extra_addons cvar exists.</summary>
internal static class MultiAddonManager
{
    public static bool Loaded
    {
        get
        {
            try { return ConVar.Find("mm_extra_addons") != null; }
            catch { return false; }
        }
    }

    /// <summary>The addon ids it mounts (mm_extra_addons, comma separated).</summary>
    public static List<ulong> Addons() => ParseList(Cvars.Get("mm_extra_addons"));

    /// <summary>Engine-free: "123,456, 789" -> ids (bad entries skipped, no duplicates).</summary>
    public static List<ulong> ParseList(string? value) =>
        (value ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => ulong.TryParse(x.Trim(), out var v) ? v : 0).Where(v => v != 0).Distinct().ToList();
}
