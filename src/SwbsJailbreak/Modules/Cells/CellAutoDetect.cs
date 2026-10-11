using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Cells;

internal sealed class AutoDetectResult
{
    public List<CellDoorEntry> Doors { get; } = new();
    public List<CellButtonEntry> Buttons { get; } = new();
    public List<string> Table { get; } = new();
    public string Method { get; set; } = "none";
    public int Candidates { get; set; }
    public int Passed { get; set; }
    public int TSpawns { get; set; }
    public int ButtonsOnMap { get; set; }
}

/// <summary>
/// Heuristic cell door finder (CS2-only feature, spec 02 §1.2.7). Used only when a map has neither a saved JSON nor
/// matching original name lists. Scores every supported door entity by button wiring (read from the buttons' real
/// entity IO connections), targetname stems taken from the 231 original names, proximity to prisoner spawns,
/// grouping and class; the result is shown to admins and is never saved automatically.
/// </summary>
internal static class CellAutoDetect
{
    public static readonly Regex CellRegex = new("(cell|zelle|jail|prison|cela|celda|cely|dvere|hucre|cdoor|jaildoor)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static readonly Regex ExcludeRegex = new("(vent|armou?ry|gun|weapon|secret|lr_|warden)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> OpenInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Open", "Toggle", "Break", "Disable", "Kill", "KillHierarchy", "StartForward", "Unlock"
    };

    private sealed class Door
    {
        public required uint Raw;
        public required CBaseEntity Ent;
        public required string Class;
        public required string Name;
        public required DoorKind Kind;
        public Vector? Center;
        public int Score;
        public readonly List<string> Reasons = new();
    }

    public static bool NameLooksLikeCell(string name) => name.Length > 0 && CellRegex.IsMatch(name) && !ExcludeRegex.IsMatch(name);

    public static AutoDetectResult Run(int threshold)
    {
        var result = new AutoDetectResult();
        var doors = new List<Door>();
        var buttons = new List<CBaseButton>();
        var byName = new Dictionary<string, List<CEntityInstance>>(StringComparer.Ordinal);
        var tSpawns = new List<Vector>();
        var ctSpawns = new List<Vector>();

        foreach (var ent in Utilities.GetAllEntities())
        {
            if (ent == null || !ent.IsValid) continue;
            var dn = ent.DesignerName;
            if (dn == null) continue;
            var name = ent.Entity?.Name ?? "";
            if (name.Length > 0)
            {
                if (!byName.TryGetValue(name, out var list)) byName[name] = list = new List<CEntityInstance>();
                list.Add(ent);
            }

            if (DoorClasses.TryKind(dn, out var kind))
            {
                var be = ent.As<CBaseEntity>();
                doors.Add(new Door { Raw = ent.EntityHandle.Raw, Ent = be, Class = dn, Name = name, Kind = kind, Center = EntityBounds.Center(be) });
            }
            else if (DoorClasses.IsButton(dn))
            {
                buttons.Add(ent.As<CBaseButton>());
            }
            else if (dn == "info_player_terrorist")
            {
                if (ent.As<CBaseEntity>().AbsOrigin is { } o) tSpawns.Add(new Vector(o.X, o.Y, o.Z));
            }
            else if (dn == "info_player_counterterrorist")
            {
                if (ent.As<CBaseEntity>().AbsOrigin is { } o) ctSpawns.Add(new Vector(o.X, o.Y, o.Z));
            }
        }

        result.Candidates = doors.Count;
        result.TSpawns = tSpawns.Count;
        result.ButtonsOnMap = buttons.Count;
        if (doors.Count == 0) return result;

        var doorByRaw = doors.ToDictionary(d => d.Raw);

        // ---- button wiring ----
        CBaseButton? bestButton = null;
        var bestButtonLinks = 0;
        var linkedByCellButton = new HashSet<uint>();
        var buttonLinks = new Dictionary<CBaseButton, HashSet<uint>>();
        foreach (var b in buttons)
        {
            HashSet<uint> links;
            try
            {
                links = LinkedDoors(b, byName, doorByRaw);
            }
            catch
            {
                continue;
            }

            buttonLinks[b] = links;
            var bName = b.Entity?.Name ?? "";
            var bTarget = SafeTarget(b);
            if (links.Count > 0 && (CellRegex.IsMatch(bName) || CellRegex.IsMatch(bTarget)))
            {
                linkedByCellButton.UnionWith(links);
                if (links.Count > bestButtonLinks)
                {
                    bestButton = b;
                    bestButtonLinks = links.Count;
                }
            }
        }

        // Closest button to the CT spawn centroid that links to >= 2 doors (eGo-style fallback).
        HashSet<uint>? closestLinks = null;
        CBaseButton? closestButton = null;
        if (ctSpawns.Count > 0)
        {
            var centroid = new Vector(ctSpawns.Average(v => v.X), ctSpawns.Average(v => v.Y), ctSpawns.Average(v => v.Z));
            var best = float.MaxValue;
            foreach (var (b, links) in buttonLinks)
            {
                if (links.Count < 2) continue;
                var bName = b.Entity?.Name ?? "";
                if (CellRegex.IsMatch(bName)) continue;
                var c = EntityBounds.Center(b);
                if (c == null) continue;
                var d = VectorUtil.Distance(c, centroid);
                if (d < best)
                {
                    best = d;
                    closestButton = b;
                    closestLinks = links;
                }
            }
        }

        // ---- scoring ----
        var nameCounts = doors.Where(d => d.Name.Length > 0).GroupBy(d => d.Name).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var d in doors)
        {
            if (linkedByCellButton.Contains(d.Raw)) Add(d, 3, "cell-button");
            if (closestLinks != null && closestLinks.Contains(d.Raw)) Add(d, 2, "ct-button");
            if (NameLooksLikeCell(d.Name)) Add(d, 2, "name");
            if (d.Center != null && tSpawns.Count > 0)
            {
                var near = tSpawns.Any(t => VectorUtil.Distance2D(t, d.Center) <= 384f && MathF.Abs(t.Z - d.Center.Z) <= 192f);
                if (near) Add(d, 2, "near-t-spawn");
                var minDist = tSpawns.Min(t => VectorUtil.Distance(t, d.Center));
                if (minDist > 2048f) Add(d, -3, "far");
            }

            if (d.Name.Length > 0 && nameCounts.TryGetValue(d.Name, out var cnt) && cnt >= 3) Add(d, 1, "group");
            if (DoorClasses.IsMoving(d.Kind)) Add(d, 1, "moving");
        }

        var passed = doors.Where(d => d.Score >= threshold).ToList();
        var names = new HashSet<string>(passed.Where(d => d.Name.Length > 0).Select(d => d.Name), StringComparer.Ordinal);

        foreach (var d in doors.OrderByDescending(d => d.Score))
        {
            var hid = SafeHammerId(d.Ent);
            var accepted = d.Score >= threshold || (d.Name.Length > 0 && names.Contains(d.Name));
            result.Table.Add($"[CellDoors] autodetect: {d.Class} \"{d.Name}\" hid={hid} score={d.Score} reasons={string.Join(",", d.Reasons)}{(accepted ? " ACCEPTED" : "")}");
        }

        foreach (var n in names) result.Doors.Add(new CellDoorEntry { Name = n });
        foreach (var d in passed.Where(d => d.Name.Length == 0))
        {
            result.Doors.Add(new CellDoorEntry
            {
                Name = "",
                HammerId = NullIfEmpty(SafeHammerId(d.Ent)),
                Class = d.Class,
                Origin = d.Ent.AbsOrigin is { } o ? CellDoorStore.ToArray(o) : null
            });
        }

        result.Passed = passed.Count;
        var linkButton = bestButton ?? (closestLinks != null && passed.Any(d => closestLinks.Contains(d.Raw)) ? closestButton : null);
        if (linkButton != null && linkButton.IsValid)
        {
            result.Buttons.Add(new CellButtonEntry
            {
                Name = linkButton.Entity?.Name ?? "",
                HammerId = NullIfEmpty(SafeHammerId(linkButton)),
                Class = linkButton.DesignerName,
                Origin = linkButton.AbsOrigin is { } bo ? CellDoorStore.ToArray(bo) : null
            });
        }

        var methods = new List<string>();
        if (passed.Any(d => d.Reasons.Contains("cell-button") || d.Reasons.Contains("ct-button"))) methods.Add("button wiring");
        if (passed.Any(d => d.Reasons.Contains("name"))) methods.Add("names");
        if (passed.Any(d => d.Reasons.Contains("near-t-spawn"))) methods.Add("prisoner spawns");
        result.Method = methods.Count == 0 ? "none" : string.Join(" + ", methods);
        return result;
    }

    private static void Add(Door d, int score, string reason)
    {
        d.Score += score;
        d.Reasons.Add(score >= 0 ? $"{reason}+{score}" : $"{reason}{score}");
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    internal static string SafeHammerId(CBaseEntity e)
    {
        try { return e.UniqueHammerID ?? ""; }
        catch { return ""; }
    }

    private static string SafeTarget(CBaseEntity e)
    {
        try { return e.Target ?? ""; }
        catch { return ""; }
    }

    /// <summary>Doors reached from a button's OnPressed/OnIn/OnDamaged through at most 3 relay hops.</summary>
    private static HashSet<uint> LinkedDoors(CBaseButton button, Dictionary<string, List<CEntityInstance>> byName, Dictionary<uint, Door> doors)
    {
        var found = new HashSet<uint>();
        var visited = new HashSet<uint> { button.EntityHandle.Raw };
        var queue = new Queue<(CEntityIOOutput Output, int Depth)>();
        queue.Enqueue((button.OnPressed, 0));
        queue.Enqueue((button.OnIn, 0));
        queue.Enqueue((button.OnDamaged, 0));

        var guard = 0;
        while (queue.Count > 0 && guard++ < 256)
        {
            var (output, depth) = queue.Dequeue();
            var conn = output.Connections;
            var hops = 0;
            while (conn != null && hops++ < 64)
            {
                if (!conn.MarkedForRemoval)
                {
                    var target = conn.TargetDesc ?? "";
                    var input = conn.TargetInput ?? "";
                    if (target.Length > 0 && !target.StartsWith('!'))
                    {
                        foreach (var ent in Resolve(target, byName))
                        {
                            var raw = ent.EntityHandle.Raw;
                            if (doors.ContainsKey(raw))
                            {
                                if (OpenInputs.Contains(input)) found.Add(raw);
                                continue;
                            }

                            if (depth >= 3 || !visited.Add(raw)) continue;
                            foreach (var next in RelayOutputs(ent))
                                queue.Enqueue((next, depth + 1));
                        }
                    }
                }

                conn = conn.Next;
            }
        }

        return found;
    }

    private static IEnumerable<CEntityInstance> Resolve(string target, Dictionary<string, List<CEntityInstance>> byName)
    {
        if (target.EndsWith('*'))
        {
            var prefix = target[..^1];
            foreach (var (n, list) in byName)
                if (n.StartsWith(prefix, StringComparison.Ordinal))
                    foreach (var e in list)
                        yield return e;
            yield break;
        }

        if (byName.TryGetValue(target, out var exact))
            foreach (var e in exact)
                yield return e;
    }

    private static IEnumerable<CEntityIOOutput> RelayOutputs(CEntityInstance ent)
    {
        switch (ent.DesignerName)
        {
            case "logic_relay":
                yield return ent.As<CLogicRelay>().OnTrigger;
                break;
            case "logic_branch":
                var br = ent.As<CLogicBranch>();
                yield return br.OnTrue;
                yield return br.OnFalse;
                break;
            case "math_counter":
                yield return ent.As<CMathCounter>().OnHitMax;
                break;
            case "trigger_multiple":
            case "trigger_once":
                var tr = ent.As<CTriggerMultiple>();
                yield return tr.OnTrigger;
                yield return tr.OnStartTouch;
                break;
        }
    }
}
