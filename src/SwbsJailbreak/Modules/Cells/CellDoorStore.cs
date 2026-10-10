using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Cells;

/// <summary>Supported cell door classes (exact designer names) - UltJB_API_CellDoors.sp.</summary>
public enum DoorKind
{
    Door,
    RotDoor,
    PropDoor,
    MoveLinear,
    Breakable,
    TrackTrain,
    WallToggle,
    Brush
}

public enum OpenMethod
{
    /// <summary>Send open inputs to the door entities (original behaviour, gives open detection).</summary>
    Doors,

    /// <summary>Press the map's linked cell button(s) (for doors driven by scripts / animated props).</summary>
    Buttons,

    /// <summary>Both (do not use when the button Toggles the doors).</summary>
    Both
}

internal static class DoorClasses
{
    public static readonly IReadOnlyDictionary<string, DoorKind> Supported = new Dictionary<string, DoorKind>(StringComparer.Ordinal)
    {
        ["func_door"] = DoorKind.Door,
        ["func_door_rotating"] = DoorKind.RotDoor,
        ["prop_door_rotating"] = DoorKind.PropDoor,
        ["func_movelinear"] = DoorKind.MoveLinear,
        ["func_breakable"] = DoorKind.Breakable,
        ["func_tracktrain"] = DoorKind.TrackTrain,
        ["func_wall_toggle"] = DoorKind.WallToggle,
        ["func_brush"] = DoorKind.Brush,
    };

    public static bool TryKind(string? designerName, out DoorKind kind)
    {
        kind = default;
        return designerName != null && Supported.TryGetValue(designerName, out kind);
    }

    /// <summary>The editor's "door type" test (classname[5..] in door/brea/wall/trac/brus/move) - also func_wall and func_breakable_surf.</summary>
    public static bool IsEditorDoorType(string? dn) =>
        dn != null && (Supported.ContainsKey(dn) || dn is "func_wall" or "func_breakable_surf" or "func_door_rotating" or "prop_door_rotating");

    public static bool IsButton(string? dn) => dn is "func_button" or "func_rot_button";

    public static bool IsMoving(DoorKind k) => k is DoorKind.Door or DoorKind.RotDoor or DoorKind.MoveLinear or DoorKind.PropDoor;

    /// <summary>Kinds whose open state can only be seen by the entity disappearing / being disabled.</summary>
    public static bool IsRemovalKind(DoorKind k) => k is DoorKind.Breakable or DoorKind.WallToggle or DoorKind.TrackTrain;
}

// ------------------------------------------------------------------------------------------------ JSON model

public sealed class CellDoorEntry
{
    /// <summary>Targetname. Non-empty = every supported door entity with exactly this (case-sensitive) name.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>CS2 m_sUniqueHammerID, used for unnamed entities.</summary>
    [JsonPropertyName("hammerId")] public string? HammerId { get; set; }

    [JsonPropertyName("class")] public string? Class { get; set; }

    /// <summary>Spawn origin (x, y, z), matched within 16 units when the hammer id is missing.</summary>
    [JsonPropertyName("origin")] public float[]? Origin { get; set; }

    /// <summary>auto | open | break | kill | disable | toggle.</summary>
    [JsonPropertyName("openMode")] public string OpenMode { get; set; } = "auto";

    public string Describe() => !string.IsNullOrEmpty(Name) ? Name : $"(unnamed) hid:{HammerId ?? "?"}";
}

public sealed class CellButtonEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("hammerId")] public string? HammerId { get; set; }
    [JsonPropertyName("class")] public string? Class { get; set; }
    [JsonPropertyName("origin")] public float[]? Origin { get; set; }

    public string Describe() => !string.IsNullOrEmpty(Name) ? Name : $"(unnamed) hid:{HammerId ?? "?"}";
}

public sealed class CellDoorFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("map")] public string Map { get; set; } = "";

    /// <summary>manual | auto | legacy (informational).</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "manual";

    /// <summary>doors | buttons | both.</summary>
    [JsonPropertyName("openMethod")] public string OpenMethod { get; set; } = "doors";

    /// <summary>Per-map override of the automatic open time (seconds).</summary>
    [JsonPropertyName("autoOpenSeconds")] public float? AutoOpenSeconds { get; set; }

    [JsonPropertyName("doors")] public List<CellDoorEntry> Doors { get; set; } = new();
    [JsonPropertyName("buttons")] public List<CellButtonEntry> Buttons { get; set; } = new();
    [JsonPropertyName("savedBy")] public string? SavedBy { get; set; }
    [JsonPropertyName("savedAt")] public string? SavedAt { get; set; }
}

// ------------------------------------------------------------------------------------------------ files

/// <summary>
/// Per-map cell door files under the core's map document folder (Storage.MapDirectory/cells):
///   cells/&lt;map&gt;.json         authoritative config written by the editor
///   cells/legacy/&lt;name&gt;.txt   optional original CS:GO name lists (one targetname per line); the 154 shipped
///                                 lists are also compiled into <see cref="LegacyCellDoorData"/>
///   cells/aliases.json           optional { "cs2_map_name": "legacy_list_name" }
/// </summary>
internal static class CellDoorStore
{
    public const string Kind = "cells";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Directory => Path.GetFullPath(Path.Combine(Storage.MapDirectory, Kind));

    /// <summary>Lower-case map name without workshop path parts and with invalid filename characters replaced.</summary>
    public static string SanitizeMap(string map)
    {
        var s = (map ?? "").Trim().ToLowerInvariant();
        var cut = Math.Max(s.LastIndexOf('/'), s.LastIndexOf('\\'));
        if (cut >= 0) s = s[(cut + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        s = string.Concat(s.Select(ch => invalid.Contains(ch) ? '_' : ch));
        return s.Length == 0 ? "unknown" : s;
    }

    public static string JsonPath(string map) => Path.Combine(Directory, map + ".json");

    /// <summary>Null when missing or malformed (malformed files are logged and left untouched).</summary>
    public static CellDoorFile? LoadJson(string map, ILogger logger)
    {
        var path = JsonPath(map);
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CellDoorFile>(File.ReadAllText(path), ReadOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Cells] {Path} is malformed - ignored (file left untouched)", path);
            return null;
        }
    }

    public enum SaveError
    {
        None,
        Directory,
        File
    }

    public static SaveError SaveJson(CellDoorFile doc, ILogger logger)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Cells] Error creating {Dir}", Directory);
            return SaveError.Directory;
        }

        var path = JsonPath(doc.Map);
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, WriteOptions));
            File.Move(tmp, path, true);
            return SaveError.None;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Cells] Error writing {Path}", path);
            return SaveError.File;
        }
    }

    /// <summary>Original reading rules: TrimString each line, skip empty lines, ignore duplicates (first kept).</summary>
    public static string[] ReadLegacyTxt(string path)
    {
        var names = new List<string>();
        foreach (var raw in File.ReadAllText(path).Split('\n'))
        {
            var n = raw.Trim();
            if (n.Length > 0 && !names.Contains(n, StringComparer.Ordinal)) names.Add(n);
        }

        return names.ToArray();
    }

    public static Dictionary<string, string> LoadAliases(ILogger logger)
    {
        var path = Path.Combine(Directory, "aliases.json");
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), ReadOptions);
            return d == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Cells] {Path} is malformed - ignored", path);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>A named legacy list: an override txt in cells/legacy/, else the compiled original data.</summary>
    public static string[]? GetLegacyList(string name)
    {
        var key = SanitizeMap(name);
        var txt = Path.Combine(Directory, "legacy", key + ".txt");
        try
        {
            if (File.Exists(txt)) return ReadLegacyTxt(txt);
        }
        catch
        {
            // fall back to the compiled data
        }

        return LegacyCellDoorData.Maps.TryGetValue(key, out var names) ? names : null;
    }

    /// <summary>"jb_clouds_final4" -&gt; "jb_clouds": prefix + first name token.</summary>
    public static string Stem(string map)
    {
        var parts = SanitizeMap(map).Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[0] + "_" + parts[1] : parts.FirstOrDefault() ?? "";
    }

    /// <summary>
    /// Find the original name list for a map: exact name, aliases.json, else the union of every original list whose
    /// map shares the stem (a renamed CS2 port, e.g. jb_clouds_d -&gt; jb_clouds_*). Returns (description, names, isStem).
    /// </summary>
    public static (string Description, string[] Names, bool IsStem)? FindLegacy(string map, ILogger logger)
    {
        var exact = GetLegacyList(map);
        if (exact is { Length: > 0 }) return ($"legacy/{map}", exact, false);

        var aliases = LoadAliases(logger);
        if (aliases.TryGetValue(map, out var alias) && GetLegacyList(alias) is { Length: > 0 } aliased)
            return ($"legacy/{SanitizeMap(alias)} (alias)", aliased, false);

        var stem = Stem(map);
        if (stem.Length < 4 || !stem.Contains('_')) return null;
        var keys = LegacyCellDoorData.Maps.Keys.Where(k => Stem(k) == stem).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var legacyDir = Path.Combine(Directory, "legacy");
        try
        {
            if (System.IO.Directory.Exists(legacyDir))
                keys.AddRange(System.IO.Directory.GetFiles(legacyDir, "*.txt").Select(Path.GetFileNameWithoutExtension)
                    .Where(k => k != null && Stem(k) == stem && !keys.Contains(k, StringComparer.OrdinalIgnoreCase))!);
        }
        catch
        {
            // ignore unreadable folder
        }

        if (keys.Count == 0) return null;
        var union = new List<string>();
        foreach (var k in keys)
            foreach (var n in GetLegacyList(k) ?? Array.Empty<string>())
                if (!union.Contains(n, StringComparer.Ordinal))
                    union.Add(n);
        return union.Count == 0 ? null : ($"legacy stem '{stem}' ({string.Join(", ", keys)})", union.ToArray(), true);
    }

    public static float[] ToArray(Vector v) => new[] { MathF.Round(v.X, 2), MathF.Round(v.Y, 2), MathF.Round(v.Z, 2) };
}

/// <summary>World-space bounds of an entity: CSS Trace AABB on the net10.0 build, collision OBB + origin otherwise.</summary>
internal static class EntityBounds
{
    public static bool TryGet(CBaseEntity e, out Vector mins, out Vector maxs)
    {
        mins = new Vector(0, 0, 0);
        maxs = new Vector(0, 0, 0);
        if (!e.IsValid) return false;
#if NET10_0_OR_GREATER
        try
        {
            CounterStrikeSharp.API.Modules.Utils.Trace.GetEntityWorldSpaceAABB(e, out var mn, out var mx);
            if (mx.X - mn.X > 0.01f || mx.Y - mn.Y > 0.01f || mx.Z - mn.Z > 0.01f)
            {
                mins = mn;
                maxs = mx;
                return true;
            }
        }
        catch
        {
            // fall back to the collision property
        }
#endif
        var o = e.AbsOrigin;
        var col = e.Collision;
        if (o == null || col == null) return false;
        mins = new Vector(o.X + col.Mins.X, o.Y + col.Mins.Y, o.Z + col.Mins.Z);
        maxs = new Vector(o.X + col.Maxs.X, o.Y + col.Maxs.Y, o.Z + col.Maxs.Z);
        return true;
    }

    public static Vector? Center(CBaseEntity e)
    {
        if (!TryGet(e, out var mn, out var mx)) return e.AbsOrigin is { } o ? new Vector(o.X, o.Y, o.Z) : null;
        return new Vector((mn.X + mx.X) / 2f, (mn.Y + mx.Y) / 2f, (mn.Z + mx.Z) / 2f);
    }

    /// <summary>Distance from a point to the box (0 inside).</summary>
    public static float DistanceToBox(Vector p, Vector mn, Vector mx)
    {
        var dx = Math.Max(Math.Max(mn.X - p.X, 0), p.X - mx.X);
        var dy = Math.Max(Math.Max(mn.Y - p.Y, 0), p.Y - mx.Y);
        var dz = Math.Max(Math.Max(mn.Z - p.Z, 0), p.Z - mx.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
