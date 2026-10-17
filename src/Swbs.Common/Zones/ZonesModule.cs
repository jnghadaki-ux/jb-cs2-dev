using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace Swbs.Common;

/// <summary>Section "Zones" (API_ZoneManager.sp constants and cvars).</summary>
public sealed class ZonesConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Polling rate for enter/exit/touch: every N server ticks (1 = every tick, needed for exact timer starts and ends).
    /// Players moving fast enough to cross a zone between two checks still enter and leave it (swept hull test).
    /// </summary>
    public int CheckEveryTicks { get; set; } = CommonHost.ZoneCheckEveryTicksDefault;

    /// <summary>Spawn zones place players on every spawn (deathmatch respawns), not only in the first seconds of a round.</summary>
    public bool SpawnZonesOnEverySpawn { get; set; } = CommonHost.SpawnZonesOnEverySpawnDefault;

    /// <summary>MAX_ZONES.</summary>
    public int MaxZones { get; set; } = 256;

    /// <summary>ZONES_TO_SHOW_IN_RADIUS: nearby zones drawn (red) around the one being edited (green).</summary>
    public int EditorNearbyZones { get; set; } = 5;

    /// <summary>ADDITION: show a solid blockade to everyone as a red beam box (the original blockade was invisible).</summary>
    public bool ShowBlockades { get; set; } = true;

    public int[] BlockadeColor { get; set; } = { 255, 40, 40, 200 };

    /// <summary>Notify zones: seconds before the same player can trigger the same zone's announcement again.</summary>
    public float NotifyCooldown { get; set; } = 5f;

    /// <summary>zm_can_import_from_another_map.</summary>
    public bool CanImportFromAnotherMap { get; set; } = false;

    /// <summary>Seconds between automatic saves of edited zones (also saved when the editor closes and at map end).</summary>
    public float AutoSaveSeconds { get; set; } = 15f;
}

/// <summary>
/// Remake of SMPlugins Libraries/ZoneManager (API_ZoneManager.sp) for CS2, shared by the SWBS game modes (from SWBS Jailbreak). Zones are per-map axis-aligned boxes with a
/// type, 2 data ints and 5 data strings, saved as JSON instead of MySQL. CSS has no SDKHooks touch, and spawning trigger
/// entities from managed code is unverified on CS2, so enter / exit / touch come from polling player hulls against the
/// boxes (exact, cheap, and unit-tested). Types are registered by name like ZoneManager_RegisterZoneType.
/// </summary>
public sealed partial class ZonesModule : SwbsModule, IZoneService
{
    /// <summary>The live zone service (null until the module loaded, or when Zones.Enabled is off).</summary>
    public static ZonesModule? Instance { get; private set; }

    internal ZonesConfig Cfg => Section<ZonesConfig>("Zones");

    public override string Name => "Zones";
    public override int Order => 2;
    public override bool Enabled => Cfg.Enabled;

    private readonly List<Zone> _zones = new();
    private readonly Dictionary<int, Zone> _byId = new();
    private readonly Dictionary<string, ZoneType> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ZoneType> _typeList = new();
    private readonly Dictionary<int, HashSet<int>> _inside = new();
    private string _map = "";
    private bool _dirty;
    private bool _importsSynced;
    private float _roundStart;
    private int _tick;
    private float _nextAutoSave;
    private int _lastId; // highest id handed out on this map: new zones never reuse an id (open editors hold ids)

    public event Action<Zone, CCSPlayerController>? Entered;
    public event Action<Zone, CCSPlayerController>? Exited;
    public event Action? Loaded;

    // ====================================================================== lifecycle

    public override void OnLoad(bool hotReload)
    {
        Instance = this;
        RegisterBuiltInTypes();
        CommonHost.OnZonesLoaded(this);
        Plugin.RegisterListener<Listeners.OnTick>(OwnTick);
        if (!hotReload) return; // Cold start: no map yet, and Server.* reads the engine globals natively (crashes the server); OnMapStart covers it.
        var map = Server.MapName;
        if (!string.IsNullOrEmpty(map)) LoadMap(map);
        _roundStart = Server.CurrentTime;
    }

    public override void OnUnload(bool hotReload)
    {
        CommonHost.OnZonesUnloaded(this);
        if (ReferenceEquals(Instance, this)) Instance = null;
        if (_dirty) Save();
        EndAllEditors();
        foreach (var t in _typeList)
        {
            foreach (var z in _zones.Where(z => ReferenceEquals(z.Type, t)).ToList()) SafeCall(() => t.OnUnassigned(z));
        }

        foreach (var t in _typeList.OfType<DamageBlockerZone>()) t.Stop();
        _inside.Clear();
    }

    public override void OnMapStart(string mapName)
    {
        EndAllEditors();
        LoadMap(mapName);
    }

    public override void OnMapEnd()
    {
        if (_dirty) Save();
        EndAllEditors();
        _inside.Clear();
    }

    public override void OnRoundStart()
    {
        _roundStart = Server.CurrentTime;
        _inside.Clear(); // everyone respawns: enter events fire again on the first check (the original recreated its zone entities)
        if (!_importsSynced)
        {
            _importsSynced = true;
            SyncImportedZones();
        }

        foreach (var t in _typeList) SafeCall(t.OnRoundStart);
    }

    public override void OnPlayerDeath(SwbsPlayer victim, SwbsPlayer? attacker, EventPlayerDeath ev)
    {
        if (victim.Controller is { IsValid: true } c) ExitAll(c);
    }

    public override void OnPlayerSpawn(SwbsPlayer player)
    {
        _lastFeet.Remove(player.Slot);
        if (HostPlacesSpawns) return;
        if (Cfg.SpawnZonesOnEverySpawn || Server.CurrentTime - _roundStart <= 3f) TryPlaceAtSpawnZone(player);
    }

    /// <summary>
    /// Spawn zones: moves the player to a random spot of a random spawn zone of their team. False when the map has none.
    /// </summary>
    /// <summary>
    /// True when the game mode calls <see cref="TryPlaceAtSpawnZone"/> itself (after its own spawn placement), so the
    /// player spawn event does not.
    /// </summary>
    public bool HostPlacesSpawns { get; set; }

    /// <summary>Seconds since the round started (spawn zones only move round-start spawns unless SpawnZonesOnEverySpawn).</summary>
    public bool InRoundStartWindow => Server.CurrentTime - _roundStart <= 3f;

    public bool TryPlaceAtSpawnZone(SwbsPlayer player)
    {
        var c = player.Controller;
        if (c == null || !c.IsValid || !c.IsAliveP()) return false;
        var team = c.Team;
        var zones = _zones.Where(z => z.TypeKey == ZoneTypeKeys.Spawn && SpawnZone.ForTeam(z, team)).ToList();
        if (zones.Count == 0) return false;
        var zone = zones[Random.Shared.Next(zones.Count)];
        var spot = SpawnZone.RandomSpot(zone.Box, Random.Shared);
        c.TeleportTo(new CounterStrikeSharp.API.Modules.Utils.Vector(spot.X, spot.Y, spot.Z + 1f), new QAngle(0, zone.Angles.Y, 0));
        return true;
    }

    public override void OnPlayerDisconnect(SwbsPlayer player)
    {
        if (player.Controller is { IsValid: true } c) ExitAll(c);
        _inside.Remove(player.Slot);
        _lastFeet.Remove(player.Slot);
        EndEditor(player.Slot);
    }

    public override void OnPlayerTeam(SwbsPlayer player, CsTeam oldTeam, CsTeam newTeam, bool disconnect)
    {
        if (newTeam is not (CsTeam.Terrorist or CsTeam.CounterTerrorist) && player.Controller is { IsValid: true } c) ExitAll(c);
    }

    // ====================================================================== load / save

    private void LoadMap(string map)
    {
        foreach (var z in _zones) if (z.Type != null) SafeCall(() => z.Type.OnUnassigned(z));
        _zones.Clear();
        _byId.Clear();
        _inside.Clear();
        _dirty = false;
        _importsSynced = false;
        _lastId = 0;
        // Round-scoped state must not carry over from the previous map (warmup has no round_start, and
        // Server.CurrentTime restarts with each map).
        _roundStart = Server.CurrentTime;
        _nextAutoSave = 0;
        foreach (var t in _typeList) SafeCall(t.OnRoundStart);
        _map = (map ?? "").ToLowerInvariant();
        if (_map.Length == 0) return;

        ZoneMapDoc doc;
        try { doc = Storage.LoadMapDoc<ZoneMapDoc>("zones", _map); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Zones] could not read zones for {Map}", _map);
            doc = new ZoneMapDoc();
        }

        // Every record is kept (MaxZones only limits creating new zones): a missing or duplicate id gets a new one, so the
        // next save never erases zones from the file.
        var renumber = new List<ZoneRecord>();
        foreach (var r in doc.Zones ?? new List<ZoneRecord>())
        {
            if (r == null) continue;
            r.Normalize();
            if (r.Id < 1 || _byId.ContainsKey(r.Id))
            {
                renumber.Add(r);
                continue;
            }

            AddLoaded(new Zone(r));
        }

        _lastId = _byId.Count == 0 ? 0 : _byId.Keys.Max();
        foreach (var r in renumber)
        {
            Logger.LogWarning("[Zones] {Map}: zone with invalid or duplicate id {Id} renumbered to {New}", _map, r.Id, _lastId + 1);
            r.Id = ++_lastId;
            AddLoaded(new Zone(r));
            _dirty = true;
        }

        foreach (var z in _zones) Bind(z);
        Logger.LogInformation("[Zones] {Map}: {Count} zones loaded", _map, _zones.Count);
        try { Loaded?.Invoke(); }
        catch (Exception ex) { Logger.LogError(ex, "[Zones] Loaded handler failed"); }
    }

    private void AddLoaded(Zone z)
    {
        _zones.Add(z);
        _byId[z.Id] = z;
    }

    internal void MarkDirty(Zone? zone = null)
    {
        _dirty = true;
        if (zone != null)
        {
            zone.Refresh();
            zone.Version++;
        }
    }

    internal void Save()
    {
        if (_map.Length == 0) return;
        var doc = new ZoneMapDoc { Zones = _zones.OrderBy(z => z.Id).Select(z => z.Record).ToList() };
        try
        {
            Storage.SaveMapDoc("zones", _map, doc);
            _dirty = false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Zones] could not save zones for {Map}", _map);
        }
    }

    /// <summary>Imported map triggers keep following the real trigger's bounds (the original hooked the map entity).</summary>
    private void SyncImportedZones()
    {
        foreach (var z in _zones.Where(z => z.Record.Imported && z.Record.ImportedName.Length > 0))
        {
            var ent = FindNamedEntities(z.Record.ImportedName)
                .OrderBy(e => e.AbsOrigin == null ? float.MaxValue : Vector3.Distance(ToV3(e.AbsOrigin), z.Box.Center))
                .FirstOrDefault();
            if (ent == null || !EntityBounds.TryGet(ent, out var mn, out var mx))
            {
                Logger.LogWarning("[Zones] {Map}: imported trigger \"{Name}\" (zone #{Id}) not found, keeping saved bounds", _map, z.Record.ImportedName, z.Id);
                continue;
            }

            var origin = ToV3(ent.AbsOrigin!);
            z.Record.Origin = Zone.A(origin);
            z.Record.Mins = Zone.A(ToV3(mn) - origin);
            z.Record.Maxs = Zone.A(ToV3(mx) - origin);
            z.Refresh();
            z.Version++;
        }
    }

    // ====================================================================== types

    public IReadOnlyList<ZoneType> Types => _typeList;

    public void RegisterType(ZoneType type)
    {
        if (_types.TryGetValue(type.Key, out var old))
        {
            _typeList.Remove(old);
            foreach (var z in _zones.Where(z => ReferenceEquals(z.Type, old)).ToList())
            {
                SafeCall(() => old.OnUnassigned(z));
                z.Type = null;
            }
        }

        _types[type.Key] = type;
        _typeList.Add(type);
        _typeList.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase)); // SortZoneTypesByName
        foreach (var z in _zones.Where(z => z.Type == null && string.Equals(z.TypeKey, type.Key, StringComparison.OrdinalIgnoreCase))) Bind(z);
    }

    private void Bind(Zone z)
    {
        z.Type = _types.TryGetValue(z.TypeKey, out var t) ? t : null;
        if (z.Type == null)
        {
            Logger.LogWarning("[Zones] zone #{Id}: type \"{Type}\" is not loaded (zone kept, inactive)", z.Id, z.TypeKey);
            return;
        }

        SafeCall(() => z.Type.OnAssigned(z));
    }

    internal void SetType(Zone z, ZoneType type)
    {
        if (ReferenceEquals(z.Type, type)) return;
        ForceExit(z);
        if (z.Type != null) SafeCall(() => z.Type.OnUnassigned(z));
        z.Record.Type = type.Key;
        z.Type = type;
        SafeCall(() => type.OnAssigned(z));
        MarkDirty(z);
    }

    // ====================================================================== create / remove

    internal Zone? Create(Vector3 origin, Vector3 mins, Vector3 maxs, Vector3 angles, bool imported = false, string importedName = "")
    {
        var max = Math.Max(1, Cfg.MaxZones);
        if (_zones.Count >= max)
        {
            Logger.LogError("[Zones] Max zones ({Max}) already created", max);
            return null;
        }

        var id = ++_lastId;
        var r = new ZoneRecord
        {
            Id = id, Origin = Zone.A(origin), Mins = Zone.A(mins), Maxs = Zone.A(maxs), Angles = Zone.A(angles),
            Imported = imported, ImportedName = importedName ?? ""
        };
        r.Normalize();
        var z = new Zone(r);
        AddLoaded(z);
        Bind(z);
        MarkDirty(z);
        return z;
    }

    internal Zone? Duplicate(Zone source, Vector3 offset)
    {
        var copy = Create(source.Box.Origin + offset, source.Box.Mins, source.Box.Maxs, source.Angles);
        if (copy == null) return null;
        copy.Record.Ints = (int[])source.Record.Ints.Clone();
        copy.Record.Strings = (string[])source.Record.Strings.Clone();
        copy.Record.Label = source.Record.Label;
        if (source.Type != null) SetType(copy, source.Type);
        else SetUnloadedType(copy, source.TypeKey);
        return copy;
    }

    /// <summary>Gives a zone a type key that is not loaded: the zone stays inactive until a plugin registers that type.</summary>
    internal void SetUnloadedType(Zone z, string key)
    {
        ForceExit(z);
        if (z.Type != null) SafeCall(() => z.Type.OnUnassigned(z));
        z.Type = null;
        z.Record.Type = key;
        MarkDirty(z);
    }

    internal bool Remove(Zone z)
    {
        if (!_byId.Remove(z.Id)) return false;
        ForceExit(z);
        if (z.Type != null) SafeCall(() => z.Type.OnUnassigned(z));
        _zones.Remove(z);
        _dirty = true;
        return true;
    }

    /// <summary>Players inside get OnExit (the original's trigger EndTouch when its entity was killed or recreated).</summary>
    private void ForceExit(Zone z)
    {
        foreach (var (slot, set) in _inside)
        {
            if (!set.Remove(z.Id)) continue;
            var c = Players.Get(slot)?.Controller;
            if (c == null || !c.IsValid) continue;
            if (z.Type != null) SafeCall(() => z.Type.OnExit(z, c));
            RaiseExited(z, c);
        }
    }

    // ====================================================================== tracking

    private void OwnTick()
    {
        _tick++;
        if (_zones.Count > 0)
        {
            foreach (var t in _typeList.Where(t => t.EveryTick))
            {
                var list = _zones.Where(z => ReferenceEquals(z.Type, t)).ToList();
                if (list.Count > 0) SafeCall(() => t.OnTick(list));
            }

            if (_tick % Math.Max(1, Cfg.CheckEveryTicks) == 0) Poll();
        }

        EditorTick();

        var now = Server.CurrentTime;
        if (_dirty && now >= _nextAutoSave)
        {
            _nextAutoSave = now + Math.Max(5f, Cfg.AutoSaveSeconds);
            if (!_editors.Values.Any(s => s.Active)) Save();
        }
    }

    /// <summary>Feet position at the previous check (swept enter/exit for players crossing a thin zone between checks).</summary>
    private readonly Dictionary<int, Vector3> _lastFeet = new();

    /// <summary>A move longer than this between two checks is a teleport, not movement: no sweep across the map.</summary>
    private const float MaxSweep = 256f;

    private void Poll()
    {
        foreach (var c in Players.Alive().ToList())
        {
            var feet = Feet(c);
            if (feet == null) continue;
            var now = new HashSet<int>();
            foreach (var z in _zones)
                if (z.Type != null && z.Box.OverlapsHull(feet.Value)) now.Add(z.Id);

            var before = _inside.TryGetValue(c.Slot, out var b) ? b : new HashSet<int>();
            var (entered, exited) = ZoneDiff.Compute(before, now);
            _inside[c.Slot] = now;

            // Passed straight through a zone between two checks (fast bhop / surf through a thin start or end zone): it
            // counts as entering and leaving it, in that order, like the original's trigger touch.
            var passed = new List<Zone>();
            if (_lastFeet.TryGetValue(c.Slot, out var from) && Vector3.DistanceSquared(from, feet.Value) <= MaxSweep * MaxSweep)
            {
                foreach (var z in _zones)
                    if (z.Type != null && !now.Contains(z.Id) && !before.Contains(z.Id) && z.Box.HullSweepHits(from, feet.Value)) passed.Add(z);
            }

            _lastFeet[c.Slot] = feet.Value;
            foreach (var z in passed)
            {
                if (!c.IsValid || !c.IsAliveP()) break;
                SafeCall(() => z.Type!.OnEnter(z, c));
                RaiseEntered(z, c);
                if (!c.IsValid) break;
                SafeCall(() => z.Type!.OnExit(z, c));
                RaiseExited(z, c);
            }

            foreach (var id in exited)
            {
                if (!_byId.TryGetValue(id, out var z) || !c.IsValid) continue;
                if (z.Type != null) SafeCall(() => z.Type.OnExit(z, c));
                RaiseExited(z, c);
            }

            foreach (var id in entered)
            {
                if (!_byId.TryGetValue(id, out var z) || !c.IsValid || !c.IsAliveP()) continue;
                if (z.Type != null) SafeCall(() => z.Type.OnEnter(z, c));
                RaiseEntered(z, c);
            }

            if (!c.IsValid || !c.IsAliveP()) continue;
            foreach (var id in now.ToList())
            {
                if (!_byId.TryGetValue(id, out var z) || z.Type == null) continue;
                SafeCall(() => z.Type.OnTouch(z, c));
                if (!c.IsValid || !c.IsAliveP()) break;
            }
        }
    }

    /// <summary>Forget where the player was (call after teleporting a player so no sweep runs across the jump).</summary>
    public void ForgetPosition(int slot) => _lastFeet.Remove(slot);

    private void ExitAll(CCSPlayerController c)
    {
        _lastFeet.Remove(c.Slot);
        if (!_inside.Remove(c.Slot, out var set)) return;
        foreach (var id in set)
        {
            if (!_byId.TryGetValue(id, out var z)) continue;
            if (z.Type != null) SafeCall(() => z.Type.OnExit(z, c));
            RaiseExited(z, c);
        }
    }

    private void RaiseEntered(Zone z, CCSPlayerController c)
    {
        try { Entered?.Invoke(z, c); }
        catch (Exception ex) { Logger.LogError(ex, "[Zones] Entered handler failed"); }
    }

    private void RaiseExited(Zone z, CCSPlayerController c)
    {
        try { Exited?.Invoke(z, c); }
        catch (Exception ex) { Logger.LogError(ex, "[Zones] Exited handler failed"); }
    }

    // ====================================================================== IZoneService

    public bool Available => true;
    public IReadOnlyList<Zone> All => _zones;
    public IEnumerable<Zone> OfType(string typeKey) => _zones.Where(z => string.Equals(z.TypeKey, typeKey, StringComparison.OrdinalIgnoreCase));
    public Zone? Find(int id) => _byId.TryGetValue(id, out var z) ? z : null;

    public Zone? FindByName(string typeKey, string name) =>
        string.IsNullOrWhiteSpace(name) ? null : OfType(typeKey).FirstOrDefault(z => string.Equals(z.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool IsInside(CCSPlayerController player, Zone zone) => Feet(player) is { } f && zone.Box.OverlapsHull(f);

    public IEnumerable<Zone> ZonesOf(CCSPlayerController player) =>
        Feet(player) is { } f ? _zones.Where(z => z.Box.OverlapsHull(f)).ToList() : Enumerable.Empty<Zone>();

    public IEnumerable<Zone> ZonesAt(Vector3 point) => _zones.Where(z => z.Box.Contains(point)).ToList();

    public float RoundElapsed => Math.Max(0f, Server.CurrentTime - _roundStart);

    public bool TeleportTo(Zone destination, CCSPlayerController player)
    {
        if (!player.IsValid || player.PawnOrNull() == null) return false;
        var p = destination.Box.BottomCenter;
        var a = destination.Angles;
        player.TeleportTo(new CssVector(p.X, p.Y, p.Z + 1f), new QAngle(a.X, a.Y, 0f), new CssVector(0, 0, 0));
        return true;
    }

    public bool TeleportToDestination(string name, CCSPlayerController player)
    {
        var dest = FindByName(ZoneTypeKeys.TeleportDestination, name);
        return dest != null && TeleportTo(dest, player);
    }

    // ====================================================================== helpers

    internal static Vector3? Feet(CCSPlayerController c)
    {
        var o = c.PawnOrNull()?.AbsOrigin;
        return o == null ? null : new Vector3(o.X, o.Y, o.Z);
    }

    internal static Vector3 ToV3(CssVector v) => new(v.X, v.Y, v.Z);
    internal static CssVector ToCss(Vector3 v) => new(v.X, v.Y, v.Z);

    internal IEnumerable<CBaseEntity> FindNamedEntities(string name)
    {
        foreach (var e in Utilities.GetAllEntities())
        {
            if (!e.IsValid) continue;
            string? n;
            try { n = e.Entity?.Name; }
            catch { continue; }
            if (string.IsNullOrEmpty(n) || !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) continue;
            var b = e.As<CBaseEntity>();
            if (b.IsValid) yield return b;
        }
    }

    internal void SafeCall(Action a)
    {
        try { a(); }
        catch (Exception ex) { Logger.LogError(ex, "[Zones] zone type callback failed"); }
    }
}
