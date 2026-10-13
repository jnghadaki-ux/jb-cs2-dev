using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.Extras;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Crews;

/// <summary>Section "Crews".</summary>
public sealed class CrewsConfig
{
    public bool Enabled { get; set; } = true;

    // ---- roster ----
    public int BaseSlots { get; set; } = 4;
    public int MaxSlots { get; set; } = 8;
    public int MaxCrews { get; set; } = 60;
    public int InviteSeconds { get; set; } = 60;
    public int CreateCooldownMinutes { get; set; } = 60;
    public int LeaveCooldownMinutes { get; set; } = 10;
    public string[] BannedWords { get; set; } = { "admin", "staff", "swbs", "owner", "nazi", "hitler" };

    // ---- respect ----
    /// <summary>No respect is earned with fewer human players online (no farming on an empty server).</summary>
    public int MinPlayersForRespect { get; set; } = 6;
    public int RespectSurvive { get; set; } = 2;
    public int RespectRoundWin { get; set; } = 3;
    public int RespectLrWin { get; set; } = 5;
    public int RespectTurfHeld { get; set; } = 1;
    public int RespectTurfTaken { get; set; } = 5;
    public int RespectWarWin { get; set; } = 25;
    public int RespectWarSurvivor { get; set; } = 5;
    public int RespectRivalBonus { get; set; } = 10;
    public int RoundCapPerMember { get; set; } = 10;
    public double DecayPercentPerWeek { get; set; } = 10;

    // ---- identity ----
    public bool ScoreboardTags { get; set; } = true;
    public bool LogCrewChat { get; set; } = true;

    // ---- turf ----
    public bool TurfEnabled { get; set; } = true;
    public int TurfMinMembers { get; set; } = 2;
    public bool TurfBanners { get; set; } = true;
    public bool HomeSpawns { get; set; } = true;

    // ---- favors / extras ----
    public bool FavorsEnabled { get; set; } = true;
    public bool CrewPingMarkers { get; set; } = true;
    public bool ShakedownEnabled { get; set; } = true;
    public float ShakedownSeconds { get; set; } = 10f;

    // ---- crew war ----
    public bool CrewWarEnabled { get; set; } = true;
    public int CrewWarMinCrews { get; set; } = 2;
    public int CrewWarMinMembers { get; set; } = 2;
}

/// <summary>Per-map turf holders: cell block key → crew id.</summary>
public sealed class CrewTurfDoc
{
    public Dictionary<string, string> Holders { get; set; } = new();
}

internal sealed class CrewTagState
{
    public string Applied = "";
    public string Original = "";
    public bool HasOriginal;
}

/// <summary>
/// Crews: persistent prisoner gangs built on this plugin's own systems (design: docs/design/CREWS.md). Identity (tag, colour,
/// crew chat, roster with ranks), respect earned by clean play (no buying), cell-block turf on Zone Manager zones, favors that
/// give information or identity only, a warden shakedown, crew pings, and the Crew War day. Never changes damage, health or
/// speed outside the Crew War day.
/// </summary>
public sealed partial class CrewsModule : JbModule, ICrewService
{
    internal CrewsConfig Cfg => Section<CrewsConfig>("Crews");

    public override string Name => "Crews";
    public override int Order => 13;
    public override bool Enabled => Cfg.Enabled;

    private const string CrewNs = "crews";
    private const string CooldownNs = "crew_cooldowns";

    private readonly Dictionary<string, Crew> _crews = new();
    private readonly Dictionary<string, string> _memberOf = new(); // steam -> crew id
    private readonly Dictionary<string, int> _earnedRound = new();
    private CrewTurfDoc _turf = new();
    private string _map = "";
    private int _tick;

    /// <summary>Set by the Crew War day: its own rewards replace the normal end-of-round respect.</summary>
    internal bool WarThisRound;

    // ====================================================================== lifecycle

    public override void OnLoad(bool hotReload)
    {
        Jb.Crews = this;
        LoadAll();
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        _map = hotReload ? (Server.MapName ?? "").ToLowerInvariant() : ""; // Cold start: no map yet, and Server.* reads the engine globals natively (crashes the server); OnMapStart covers it.
        LoadTurf();
    }

    public override void OnAllModulesLoaded(bool hotReload)
    {
        JbEvents.CellsOpened += OnCellsOpened;
        JbEvents.LastRequestEnded += OnLastRequestEnded;
        Aim.Pinged += OnPing;
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.CellsOpened -= OnCellsOpened;
        JbEvents.LastRequestEnded -= OnLastRequestEnded;
        Aim.Pinged -= OnPing;
        ClearRoundVisuals();
        RemoveTurfBanners();
        foreach (var p in Players.Connected()) RestoreTag(p);
    }

    public override void OnMapStart(string mapName)
    {
        _map = (mapName ?? "").ToLowerInvariant();
        _invites.Clear();
        _nextPing.Clear();
        LoadTurf();
        var now = Now;
        foreach (var crew in _crews.Values)
        {
            var before = crew.Respect;
            CrewRules.ApplyDecay(crew, now, Cfg.DecayPercentPerWeek);
            if (crew.Respect != before || crew.LastDecayUnix == now) Save(crew);
        }
    }

    public override void OnRoundStart()
    {
        _earnedRound.Clear();
        WarThisRound = false;
        ResetRoundState();
        Round.Timer(0.5f, DrawTurfBanners); // after the engine's round-start cleanup
        Round.Timer(0.3f, MoveCrewsHome);   // after spawn preparation (players spawn before round_start in CS2)
    }

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        if (!WarThisRound) RoundRewards(winner);
        ClearRoundVisuals();
    }

    public override void OnPlayerConnected(JbPlayer player)
    {
        if (player.IsBot || player.SteamId64 == 0) return;
        if (CrewOf(player.SteamId64) is { } crew && crew.Member(player.SteamId64.ToString()) is { } m && m.Name != player.Name)
        {
            m.Name = player.Name;
            Save(crew);
        }
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        _invites.Remove(player.Slot);
        _nextPing.Remove(player.Slot);
        if (player.Slot is >= 0 and < PlayerSlots) _prisonerCrew[player.Slot] = null;
    }

    public override void OnPlayerSpawn(JbPlayer player) => ApplyTag(player);

    public override void OnPlayerTeam(JbPlayer player, CsTeam oldTeam, CsTeam newTeam, bool disconnect)
    {
        if (!disconnect) Server.NextFrame(() => ApplyTag(player));
    }

    private void OnTick()
    {
        _tick++;
        if (_tick % 192 == 0) // ~3 s on 64 tick
            foreach (var p in Players.Connected()) ApplyTag(p);
        if (_tick % 64 == 0) RollCallTick();
        if (_tick % 16 == 0) RefreshPrisonerCrews();
    }

    private const int PlayerSlots = 64;
    private readonly string?[] _prisonerCrew = new string?[PlayerSlots];

    /// <summary>
    /// Crew id of each prisoner by slot, refreshed 4x per second. Per-viewer visibility checks (crew pings, Lookout) run
    /// for every viewer and entity on every tick, so they read this cache instead of SteamIDs and dictionaries.
    /// </summary>
    private void RefreshPrisonerCrews()
    {
        Array.Clear(_prisonerCrew);
        foreach (var c in Players.Humans())
            if (c.Slot is >= 0 and < PlayerSlots && c.Team == CsTeam.Terrorist && _memberOf.TryGetValue(Steam(c), out var id))
                _prisonerCrew[c.Slot] = id;
    }

    /// <summary>True when the viewer is a prisoner in this crew (cached, safe for CheckTransmit).</summary>
    private bool IsPrisonerOf(CCSPlayerController viewer, string crewId) =>
        viewer.Slot is >= 0 and < PlayerSlots && _prisonerCrew[viewer.Slot] == crewId;

    // ====================================================================== storage

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void LoadAll()
    {
        _crews.Clear();
        _memberOf.Clear();
        foreach (var (id, crew) in Storage.All<Crew>(CrewNs))
        {
            crew.Id = id;
            crew.Members ??= new List<CrewMember>();
            crew.Members.RemoveAll(m => m == null || string.IsNullOrEmpty(m.SteamId));
            if (crew.Members.Count == 0) continue;
            if (crew.Boss == null) crew.Members.OrderByDescending(m => m.Rank).ThenByDescending(m => m.Earned).First().Rank = CrewRank.Boss;
            _crews[id] = crew;
            foreach (var m in crew.Members)
            {
                if (_memberOf.TryAdd(m.SteamId, id)) continue;
                Logger.LogWarning("[Crews] {Steam} is listed in two crews; keeping the first", m.SteamId);
            }
        }

        Logger.LogInformation("[Crews] {Count} crews loaded", _crews.Count);
    }

    /// <summary>Saves a live crew. A stale reference (crew disbanded while a menu or prompt was open) is never written back.</summary>
    internal void Save(Crew crew)
    {
        if (IsLive(crew)) Storage.Set(CrewNs, crew.Id, crew);
    }

    internal Crew? CrewById(string id) => _crews.TryGetValue(id, out var crew) ? crew : null;

    private bool IsLive(Crew crew) => _crews.TryGetValue(crew.Id, out var live) && ReferenceEquals(live, crew);

    /// <summary>The crew still exists and the member is still on its roster (menus can outlive both).</summary>
    private bool IsLiveMember(Crew crew, CrewMember member) => IsLive(crew) && crew.Members.Any(m => ReferenceEquals(m, member));

    private void DeleteCrew(Crew crew)
    {
        if (!IsLive(crew)) return;
        foreach (var m in crew.Members)
            if (_memberOf.TryGetValue(m.SteamId, out var id) && id == crew.Id) _memberOf.Remove(m.SteamId);
        _crews.Remove(crew.Id);
        Storage.Delete(CrewNs, crew.Id);
        foreach (var other in _crews.Values.Where(c => c.RivalId == crew.Id).ToList())
        {
            other.RivalId = "";
            Save(other);
        }

        foreach (var key in _turf.Holders.Where(kv => kv.Value == crew.Id).Select(kv => kv.Key).ToList()) _turf.Holders.Remove(key);
        SaveTurf();
        RemoveTurfBanners();
        DrawTurfBanners();
    }

    private long CooldownUntil(ulong steam) => Storage.Get<long>(CooldownNs, steam.ToString());
    private void SetCooldown(ulong steam, int minutes) => Storage.Set(CooldownNs, steam.ToString(), Now + Math.Max(0, minutes) * 60L);

    // ====================================================================== ICrewService

    public bool Available => true;

    public Crew? CrewOf(ulong steamId64) =>
        steamId64 != 0 && _memberOf.TryGetValue(steamId64.ToString(), out var id) && _crews.TryGetValue(id, out var crew) ? crew : null;

    internal Crew? CrewOf(CCSPlayerController? c) => c == null || !c.IsValid || c.IsBot ? null : CrewOf(c.SteamID);
    internal Crew? CrewOf(JbPlayer? p) => p == null || p.IsBot ? null : CrewOf(p.SteamId64);

    public bool SameCrew(CCSPlayerController a, CCSPlayerController b)
    {
        var ca = CrewOf(a);
        return ca != null && ReferenceEquals(ca, CrewOf(b));
    }

    internal static string Steam(CCSPlayerController c) => c.SteamID.ToString();

    internal string Label(Crew crew) => $"{CrewColors.Of(crew.Color).Chat}[{crew.Tag}] {crew.Name}{{default}}";

    internal int LevelOf(Crew crew) => CrewRules.Level(crew.Legacy);

    internal int SlotsOf(Crew crew) => CrewRules.Slots(LevelOf(crew), Cfg.BaseSlots, Cfg.MaxSlots);

    internal IEnumerable<CCSPlayerController> OnlineMembers(Crew crew) =>
        Players.Humans().Where(c => _memberOf.TryGetValue(Steam(c), out var id) && id == crew.Id);

    internal void TellCrew(Crew crew, string key, params object?[] args)
    {
        foreach (var c in OnlineMembers(crew)) Chat.Tell(c, key, args);
    }

    // ====================================================================== scoreboard tag (prisoners only)

    private void ApplyTag(JbPlayer p)
    {
        var c = p.Controller;
        if (c == null || !c.IsValid || c.IsBot) return;
        var crew = Cfg.ScoreboardTags ? CrewOf(p) : null;
        var desired = crew != null && c.Team == CsTeam.Terrorist ? crew.Tag : "";
        var s = p.Data<CrewTagState>();
        var current = c.Clan ?? "";
        if (desired.Length == 0)
        {
            RestoreTag(p);
            return;
        }

        if (current == desired) return;
        if (IsAdminTag(c, current)) return; // the admin tag wins
        if (current != s.Applied)
        {
            s.Original = current;
            s.HasOriginal = current.Length > 0;
        }

        c.SetClanTag(desired);
        s.Applied = desired;
    }

    private void RestoreTag(JbPlayer p)
    {
        var c = p.Controller;
        var s = p.Data<CrewTagState>();
        if (c == null || !c.IsValid || s.Applied.Length == 0) return;
        if ((c.Clan ?? "") == s.Applied) c.SetClanTag(s.HasOriginal ? s.Original : "");
        s.Applied = "";
    }

    private bool IsAdminTag(CCSPlayerController c, string current)
    {
        var tweaks = Section<ServerTweaksConfig>("ServerTweaks");
        return tweaks.AdminTags && current.Length > 0 && current == tweaks.AdminTag && Perms.IsAdmin(c);
    }

    // ====================================================================== respect

    private int HumansOnline => Players.Humans().Count();

    /// <summary>Give respect to the player's crew, under the per-round cap and the minimum player count.</summary>
    internal int Grant(CCSPlayerController c, int amount, string reasonKey, bool ignoreCap = false)
    {
        var crew = CrewOf(c);
        if (crew == null || amount == 0) return 0;
        if (amount > 0 && HumansOnline < Cfg.MinPlayersForRespect) return 0;
        var steam = Steam(c);
        var earned = _earnedRound.TryGetValue(steam, out var e) ? e : 0;
        var g = ignoreCap ? amount : CrewRules.Grantable(earned, amount, Cfg.RoundCapPerMember);
        if (g == 0) return 0;
        var levelBefore = LevelOf(crew);
        crew.Respect = Math.Max(0, crew.Respect + g);
        if (g > 0) crew.Legacy += g;
        if (crew.Member(steam) is { } m) m.Earned += Math.Max(0, g);
        _earnedRound[steam] = earned + Math.Max(0, g);
        Save(crew);
        Chat.Tell(c, "crews.respect.gain", g, Lang.T(reasonKey));
        var levelAfter = LevelOf(crew);
        if (levelAfter > levelBefore) TellCrew(crew, "crews.level_up", Label(crew), levelAfter, SlotsOf(crew));
        return g;
    }

    /// <summary>Respect for the whole crew (Crew War win, turf); not capped per member.</summary>
    internal void GrantCrew(Crew crew, int amount, string reasonKey)
    {
        if (amount <= 0 || HumansOnline < Cfg.MinPlayersForRespect) return;
        var levelBefore = LevelOf(crew);
        crew.Respect += amount;
        crew.Legacy += amount;
        Save(crew);
        TellCrew(crew, "crews.respect.crew_gain", amount, Lang.T(reasonKey));
        if (LevelOf(crew) > levelBefore) TellCrew(crew, "crews.level_up", Label(crew), LevelOf(crew), SlotsOf(crew));
    }

    private void RoundRewards(CsTeam winner)
    {
        foreach (var c in Players.AlivePrisoners().Where(c => !c.IsBot).ToList())
        {
            if (CrewOf(c) == null) continue;
            Grant(c, Cfg.RespectSurvive, "crews.reason.survived");
            if (winner == CsTeam.Terrorist) Grant(c, Cfg.RespectRoundWin, "crews.reason.round_win");
        }

        if (!Cfg.TurfEnabled) return;
        foreach (var crewId in _turf.Holders.Values.Distinct())
        {
            if (!_crews.TryGetValue(crewId, out var crew)) continue;
            foreach (var c in OnlineMembers(crew).Where(c => c.Team == CsTeam.Terrorist).ToList())
                Grant(c, Cfg.RespectTurfHeld, "crews.reason.turf_held");
        }
    }

    private void OnLastRequestEnded(JbPlayer prisoner, JbPlayer? opponent)
    {
        // Won a duel: the prisoner is still alive and the guard is not.
        var c = prisoner.Controller;
        if (c == null || !c.IsValid || !c.IsAliveP() || opponent == null || opponent.IsAlive) return;
        Grant(c, Cfg.RespectLrWin, "crews.reason.lr_win");
    }

    // ====================================================================== turf (cell blocks)

    private static string BlockKey(Zone z) => z.Name.Length > 0 ? z.Name.ToLowerInvariant() : "#" + z.Id;

    private IEnumerable<Zone> Blocks => Jb.Zones.OfType(ZoneTypeKeys.CellBlock);

    private void LoadTurf()
    {
        _turf = _map.Length == 0 ? new CrewTurfDoc() : Storage.LoadMapDoc<CrewTurfDoc>("crew_turf", _map);
        _turf.Holders ??= new Dictionary<string, string>();
    }

    private void SaveTurf()
    {
        if (_map.Length > 0) Storage.SaveMapDoc("crew_turf", _map, _turf);
    }

    internal string? TurfHolder(Zone block) => _turf.Holders.TryGetValue(BlockKey(block), out var id) && _crews.ContainsKey(id) ? id : null;

    internal List<Zone> TurfOf(Crew crew) => Blocks.Where(b => TurfHolder(b) == crew.Id).ToList();

    /// <summary>When the cells open everyone is still in their cell: the crew with most members in a block holds it.</summary>
    private void OnCellsOpened()
    {
        if (!Cfg.TurfEnabled || Jb.Days.IsInProgress || HumansOnline < Cfg.MinPlayersForRespect) return;
        var changed = false;
        foreach (var block in Blocks.ToList())
        {
            var presence = new Dictionary<string, int>();
            foreach (var c in Players.AlivePrisoners())
            {
                var crew = CrewOf(c);
                if (crew == null || !Jb.Zones.IsInside(c, block)) continue;
                presence[crew.Id] = presence.TryGetValue(crew.Id, out var n) ? n + 1 : 1;
            }

            var holder = TurfHolder(block);
            var next = CrewRules.ResolveTurf(presence, holder, Cfg.TurfMinMembers);
            if (next == null || next == holder) continue;
            _turf.Holders[BlockKey(block)] = next;
            changed = true;
            var crewNow = _crews[next];
            var blockName = block.Name.Length > 0 ? block.Name : block.Title;
            if (holder != null && _crews.TryGetValue(holder, out var lost))
                Chat.TellAll("crews.turf.taken_from", Label(crewNow), blockName, Label(lost));
            else
                Chat.TellAll("crews.turf.taken", Label(crewNow), blockName);
            foreach (var c in OnlineMembers(crewNow).Where(c => c.IsAliveP() && c.Team == CsTeam.Terrorist && Jb.Zones.IsInside(c, block)).ToList())
                Grant(c, Cfg.RespectTurfTaken, "crews.reason.turf_taken");
        }

        if (!changed) return;
        SaveTurf();
        RemoveTurfBanners();
        DrawTurfBanners();
    }

    // ---- banners (real beam ring + pillar in the crew colour at each held block) ----

    private readonly List<CBeam> _turfBanners = new();

    private void DrawTurfBanners()
    {
        RemoveTurfBanners();
        if (!Cfg.TurfEnabled || !Cfg.TurfBanners) return;
        foreach (var block in Blocks)
        {
            var holder = TurfHolder(block);
            if (holder == null || !_crews.TryGetValue(holder, out var crew)) continue;
            var color = CrewColors.Of(crew.Color).Rgb;
            var p = block.Box.BottomCenter;
            var radius = Math.Clamp(MathF.Min(block.Box.Size.X, block.Box.Size.Y) / 4f, 16f, 64f);
            var basePos = new CssVector(p.X, p.Y, p.Z + 2f);
            _turfBanners.AddRange(Fx.Ring(basePos, radius, Color.FromArgb(180, color), 2f, 0f, 16));
            var pillar = Fx.Beam(basePos, new CssVector(p.X, p.Y, p.Z + 96f), Color.FromArgb(200, color), 3f);
            if (pillar != null) _turfBanners.Add(pillar);
        }
    }

    private void RemoveTurfBanners()
    {
        foreach (var b in _turfBanners) Fx.SafeRemove(b);
        _turfBanners.Clear();
    }

    // ---- home spawns: at round start, members of a turf holder move to a free spawn point inside their block ----

    private void MoveCrewsHome()
    {
        if (!Cfg.TurfEnabled || !Cfg.HomeSpawns || Jb.Days.IsInProgress || _turf.Holders.Count == 0) return;
        var spawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_terrorist")
            .Where(e => e.AbsOrigin != null)
            .Select(e => (Pos: new Vector3(e.AbsOrigin!.X, e.AbsOrigin.Y, e.AbsOrigin.Z), Yaw: e.AbsRotation?.Y ?? 0f))
            .ToList();
        if (spawns.Count == 0) return;
        var used = new HashSet<int>();
        foreach (var crew in _turf.Holders.Values.Distinct().Select(id => _crews.GetValueOrDefault(id)).Where(c => c != null))
        {
            var blocks = TurfOf(crew!);
            foreach (var c in OnlineMembers(crew!).Where(x => x.IsAliveP() && x.Team == CsTeam.Terrorist).ToList())
            {
                if (blocks.Any(b => Jb.Zones.IsInside(c, b))) continue; // already home
                for (var i = 0; i < spawns.Count; i++)
                {
                    var s = spawns[i];
                    if (used.Contains(i) || !blocks.Any(b => b.Box.OverlapsHull(s.Pos))) continue;
                    // Free spot: nobody else standing on it.
                    if (Players.Alive().Any(o => o.Slot != c.Slot && o.PawnOrNull()?.AbsOrigin is { } oo &&
                                                 Vector3.DistanceSquared(new Vector3(oo.X, oo.Y, oo.Z), s.Pos) < 32f * 32f)) continue;
                    used.Add(i);
                    c.TeleportTo(new CssVector(s.Pos.X, s.Pos.Y, s.Pos.Z), new QAngle(0, s.Yaw, 0), new CssVector(0, 0, 0));
                    break;
                }
            }
        }
    }

    private void ResetRoundState()
    {
        _favorUsed.Clear();
        _shakedownUsed = false;
        _rollCallUntil.Clear();
        ClearRoundVisuals();
    }
}
