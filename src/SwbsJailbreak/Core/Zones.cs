using System.Numerics;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace SwbsJailbreak.Core;

/// <summary>
/// One saved zone, the same fields as the SMPlugins plugin_zonemanager_data row (origin, mins, maxs, angles, type,
/// 2 data ints, 5 data strings, imported trigger name) plus an optional admin label. Stored per map in
/// configs/plugins/SwbsJailbreak/maps/zones/&lt;map&gt;.json.
/// </summary>
public sealed class ZoneRecord
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = ZoneTypeKeys.NotSet;
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("origin")] public float[] Origin { get; set; } = new float[3];
    [JsonPropertyName("mins")] public float[] Mins { get; set; } = { -100f, -100f, 2f };
    [JsonPropertyName("maxs")] public float[] Maxs { get; set; } = { 100f, 100f, 100f };
    [JsonPropertyName("angles")] public float[] Angles { get; set; } = new float[3];
    [JsonPropertyName("ints")] public int[] Ints { get; set; } = new int[2];
    [JsonPropertyName("strings")] public string[] Strings { get; set; } = { "", "", "", "", "" };
    [JsonPropertyName("imported")] public bool Imported { get; set; }
    [JsonPropertyName("importedName")] public string ImportedName { get; set; } = "";

    /// <summary>Repair arrays from hand-edited files so every accessor is safe.</summary>
    public void Normalize()
    {
        Origin = Fix(Origin, 3, 0f);
        Mins = Fix(Mins, 3, 0f);
        Maxs = Fix(Maxs, 3, 0f);
        Angles = Fix(Angles, 3, 0f);
        Ints = Fix(Ints, 2, 0);
        var s = Fix(Strings, 5, "");
        for (var i = 0; i < s.Length; i++) s[i] ??= "";
        Strings = s;
        Type = string.IsNullOrWhiteSpace(Type) ? ZoneTypeKeys.NotSet : Type.Trim().ToLowerInvariant();
        Label ??= "";
        ImportedName ??= "";
        for (var i = 0; i < 3; i++)
        {
            if (Mins[i] > Maxs[i]) (Mins[i], Maxs[i]) = (Maxs[i], Mins[i]);
            if (Maxs[i] - Mins[i] < 1f) Maxs[i] = Mins[i] + 1f;
        }
    }

    private static T[] Fix<T>(T[]? a, int len, T fill)
    {
        if (a != null && a.Length == len) return a;
        var r = new T[len];
        for (var i = 0; i < len; i++) r[i] = a != null && i < a.Length ? a[i] : fill;
        return r;
    }

    public ZoneRecord Clone() => new()
    {
        Id = Id, Type = Type, Label = Label, Origin = (float[])Origin.Clone(), Mins = (float[])Mins.Clone(),
        Maxs = (float[])Maxs.Clone(), Angles = (float[])Angles.Clone(), Ints = (int[])Ints.Clone(),
        Strings = (string[])Strings.Clone(), Imported = Imported, ImportedName = ImportedName
    };
}

/// <summary>The per-map file.</summary>
public sealed class ZoneMapDoc
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("zones")] public List<ZoneRecord> Zones { get; set; } = new();
}

/// <summary>Built-in type keys. Ids in brackets are the original ZONE_TYPE_* numbers ("do not change or reuse").</summary>
public static class ZoneTypeKeys
{
    public const string NotSet = "not_set";                       // 0
    public const string Kill = "kill";                            // 2
    public const string Push = "push";                            // 3
    public const string Blockade = "blockade";                    // 7
    public const string Respawn = "respawn";                      // 8
    public const string Teleport = "teleport";                    // 10
    public const string TeleportDestination = "teleport_destination"; // 11
    public const string WeaponStrip = "weapon_strip";             // 13
    public const string DamageBlocker = "damage_blocker";         // 14
    public const string Named = "named";                          // 15
    public const string Notify = "notify";                        // new
    public const string CellBlock = "cell_block";                 // new (used by Crews turf)
    public const string Spawn = "spawn";                          // new: custom round-start spawn area
    public const string MapInput = "map_input";                   // new: fire an input on map entities (fix broken buttons)
}

/// <summary>Live zone: the saved record plus its resolved type.</summary>
public sealed class Zone
{
    internal Zone(ZoneRecord record)
    {
        Record = record;
        Refresh();
    }

    public ZoneRecord Record { get; }
    public int Id => Record.Id;
    public string TypeKey => Record.Type;

    /// <summary>Null when no module registered this type key (the original's "type plugin is not loaded").</summary>
    public ZoneType? Type { get; internal set; }

    /// <summary>Cached geometry; call <see cref="Refresh"/> after editing the record's origin / mins / maxs.</summary>
    public ZoneBox Box { get; private set; }

    internal void Refresh() => Box = new ZoneBox(V(Record.Origin), V(Record.Mins), V(Record.Maxs));

    /// <summary>Bumped on every edit (editor redraw detection).</summary>
    internal int Version { get; set; }
    public Vector3 Angles => V(Record.Angles);

    /// <summary>Data string 1: the name used by Named / Teleport / Teleport Destination / Cell Block zones.</summary>
    public string Name => Record.Strings[0];

    public int GetInt(int number) => number is 1 or 2 ? Record.Ints[number - 1] : 0;
    public string GetString(int number) => number is >= 1 and <= 5 ? Record.Strings[number - 1] : "";

    internal void SetInt(int number, int value)
    {
        if (number is 1 or 2) Record.Ints[number - 1] = value;
    }

    internal void SetString(int number, string value)
    {
        if (number is >= 1 and <= 5) Record.Strings[number - 1] = (value ?? "").Trim();
    }

    public string Title => Record.Label.Length > 0 ? Record.Label : Name.Length > 0 ? Name : $"#{Id}";

    internal static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
    internal static float[] A(Vector3 v) => new[] { v.X, v.Y, v.Z };
}

/// <summary>How a type's data is edited in the zone manager (replaces each SourcePawn type's own data menu).</summary>
public enum ZoneFieldKind
{
    Text,     // data string, typed in chat
    Seconds,  // data int, seconds after round start (0 = not set)
    Number,   // data int
    Flags,    // data int bitmask, one toggle per flag
    Choice    // data int, cycles through Choices
}

public sealed record ZoneField(string Label, ZoneFieldKind Kind, int Index, string Help = "")
{
    public (string Name, int Value)[] Options { get; init; } = Array.Empty<(string, int)>();
    public int Min { get; init; } = 0;
    public int Max { get; init; } = 100000;
}

/// <summary>
/// A zone type (the original ZoneManager_RegisterZoneType). Callbacks get the zone and the player; enter/exit/touch are
/// driven by position polling because CSS has no SDKHooks touch. Touch runs on every check while inside (the original
/// OnTouch fired every touching frame).
/// </summary>
public abstract class ZoneType
{
    public abstract string Key { get; }
    public abstract string DisplayName { get; }

    /// <summary>Original ZONE_TYPE_* number, -1 for types added by this port.</summary>
    public virtual int LegacyId => -1;

    public virtual string Description => "";
    public virtual IReadOnlyList<ZoneField> Fields => Array.Empty<ZoneField>();

    /// <summary>True for types that must be checked every tick (Blockade), false = normal polling rate.</summary>
    public virtual bool EveryTick => false;

    public virtual void OnEnter(Zone zone, CCSPlayerController player) { }
    public virtual void OnExit(Zone zone, CCSPlayerController player) { }
    public virtual void OnTouch(Zone zone, CCSPlayerController player) { }

    /// <summary>The zone got this type, or zones were loaded (Forward_TypeAssigned).</summary>
    public virtual void OnAssigned(Zone zone) { }

    /// <summary>The zone lost this type or was removed (Forward_TypeUnassigned).</summary>
    public virtual void OnUnassigned(Zone zone) { }

    public virtual void OnRoundStart() { }
    public virtual void OnTick(IReadOnlyList<Zone> zonesOfThisType) { }

    /// <summary>Short data summary for menus ("2 -- 0" in the original).</summary>
    public virtual string Summary(Zone zone) => $"{zone.GetInt(1)} -- {zone.GetInt(2)}";
}

/// <summary>Zone Manager service (`Jb.Zones`). The null object answers "no zones" so modules work without it.</summary>
public interface IZoneService
{
    bool Available { get; }
    IReadOnlyList<Zone> All { get; }
    IEnumerable<Zone> OfType(string typeKey);
    Zone? Find(int id);

    /// <summary>First zone of the type whose data string 1 equals the name (case-insensitive).</summary>
    Zone? FindByName(string typeKey, string name);

    bool IsInside(CCSPlayerController player, Zone zone);
    IEnumerable<Zone> ZonesOf(CCSPlayerController player);
    IEnumerable<Zone> ZonesAt(Vector3 point);

    /// <summary>Teleport to a destination zone's floor centre facing its angles (ZoneTypeTeleport_TryToTeleport).</summary>
    bool TeleportTo(Zone destination, CCSPlayerController player);

    /// <summary>Find the Teleport Destination zone with this name and teleport there; false if it does not exist.</summary>
    bool TeleportToDestination(string name, CCSPlayerController player);

    void RegisterType(ZoneType type);
    IReadOnlyList<ZoneType> Types { get; }

    /// <summary>Seconds since the current round started (zone timers).</summary>
    float RoundElapsed { get; }

    event Action<Zone, CCSPlayerController>? Entered;
    event Action<Zone, CCSPlayerController>? Exited;
    event Action? Loaded;
}

public sealed class NullZoneService : IZoneService
{
    public static readonly NullZoneService Instance = new();
    public bool Available => false;
    public IReadOnlyList<Zone> All => Array.Empty<Zone>();
    public IEnumerable<Zone> OfType(string typeKey) => Array.Empty<Zone>();
    public Zone? Find(int id) => null;
    public Zone? FindByName(string typeKey, string name) => null;
    public bool IsInside(CCSPlayerController player, Zone zone) => false;
    public IEnumerable<Zone> ZonesOf(CCSPlayerController player) => Array.Empty<Zone>();
    public IEnumerable<Zone> ZonesAt(Vector3 point) => Array.Empty<Zone>();
    public bool TeleportTo(Zone destination, CCSPlayerController player) => false;
    public bool TeleportToDestination(string name, CCSPlayerController player) => false;
    public void RegisterType(ZoneType type) { }
    public IReadOnlyList<ZoneType> Types => Array.Empty<ZoneType>();
    public float RoundElapsed => 0f;
#pragma warning disable CS0067 // events of the null object are never raised
    public event Action<Zone, CCSPlayerController>? Entered;
    public event Action<Zone, CCSPlayerController>? Exited;
    public event Action? Loaded;
#pragma warning restore CS0067
}
