using System.Text.Json;
using System.Text.Json.Nodes;
using CounterStrikeSharp.API.Core;

namespace SwbsJailbreak.Core;

/// <summary>One SWBS admin tier: a CounterStrikeSharp admin group (admin_groups.json) with its flags and immunity.</summary>
public sealed record AdminLevel(string Group, string Name, int Immunity, string[] Flags, string Summary);

/// <summary>
/// The four SWBS admin tiers under root. Each tier is a CSS admin group that includes every flag of the tiers below
/// (so the plugin's capability map in General.Permissions works unchanged) and a higher immunity, so a lower tier
/// cannot target a higher one. Put an admin in a tier with "groups": ["#swbs/level2"] in admins.json.
/// </summary>
public static class AdminLevels
{
    private static readonly string[] L1 = { "@css/reservation", "@css/generic", "@css/chat", "@css/slay" };
    private static readonly string[] L2 = L1.Concat(new[] { "@css/kick", "@css/ban" }).ToArray();
    private static readonly string[] L3 = L2.Concat(new[] { "@css/unban", "@css/cheats", "@css/changemap" }).ToArray();
    private static readonly string[] Lead = L3.Concat(new[] { "@css/config", "@css/cvar", "@swbs/unban", "@swbs/noclip", "@swbs/god" }).ToArray();

    /// <summary>
    /// Flag sets earlier plugin versions installed for a group. A group still holding exactly one of them was never
    /// edited by the owner and is brought up to date; an edited group is left alone.
    /// </summary>
    private static readonly Dictionary<string, string[][]> PreviousDefaults = new()
    {
        ["#swbs/lead"] = new[] { L3.Concat(new[] { "@css/config", "@css/cvar" }).ToArray() }
    };

    public static readonly AdminLevel[] All =
    {
        new("#swbs/level1", "Level 1", 10, L1,
            "admin menu and chat, jail logs, warden control, open cells, start/stop days, abort LRs, warden games, mute/gag/silence, slay/slap/beacon/freeze, respawn/1up/xup"),
        new("#swbs/level2", "Level 2", 20, L2,
            "+ kick, move/swap teams, bans and timed CT bans, crew admin"),
        new("#swbs/level3", "Level 3", 30, L3,
            "+ permanent CT bans, day/LR editing, teleport (bring/goto), gravity, bhop logs (!bhc), give/hp/armor/speed and other fun commands (not noclip/god), map change"),
        new("#swbs/lead", "Lead", 50, Lead,
            "+ unban (bans and CT bans), noclip, god, warden pointer/beacon settings, zone editor and server cvars (root-only: rcon, cell door editor, points, server setup)")
    };

    /// <summary>The highest tier a player is in (by group, or by flags for admins set up without groups); null if none.</summary>
    public static string? NameOf(CCSPlayerController? player)
    {
        if (player == null) return "Console";
        if (Perms.HasFlag(player, "@css/root")) return "Root";
        for (var i = All.Length - 1; i >= 0; i--)
        {
            var l = All[i];
            if (Perms.HasFlag(player, l.Group) || l.Flags.All(f => Perms.HasFlag(player, f))) return l.Name;
        }

        return Perms.IsAdmin(player) ? "Admin" : null;
    }

    /// <summary>Index of a tier group in <see cref="All"/> (case-insensitive), -1 if it is not one.</summary>
    public static int IndexOf(string group) => Array.FindIndex(All, l => string.Equals(l.Group, group, StringComparison.OrdinalIgnoreCase));

    /// <summary>Engine-free: tier <paramref name="tier"/> (index in <see cref="All"/>) has this flag, itself or through a lower tier.</summary>
    public static bool TierGrants(int tier, string flag) =>
        tier >= 0 && tier < All.Length && All.Take(tier + 1).Any(l => l.Flags.Contains(flag, StringComparer.OrdinalIgnoreCase));

    /// <summary>Engine-free: a member of tier <paramref name="tier"/> counts as a member of <paramref name="group"/> (that tier or a lower one).</summary>
    public static bool TierImplies(int tier, string group)
    {
        var g = IndexOf(group);
        return tier >= 0 && g >= 0 && g <= tier;
    }

    /// <summary>
    /// The highest SWBS tier a player is in by group membership (-1 if none). <paramref name="inGroup"/> asks CSS.
    /// </summary>
    public static int TierOf(Func<string, bool> inGroup)
    {
        for (var i = All.Length - 1; i >= 0; i--)
            if (inGroup(All[i].Group)) return i;
        return -1;
    }

    /// <summary>Immunity root admins count with (above every tier).</summary>
    public const uint RootImmunity = 99;

    /// <summary>Engine-free: the immunity used for targeting: the configured one, but never below the admin's rank.</summary>
    public static uint EffectiveImmunity(uint configured, bool root, int tier) =>
        Math.Max(configured, root ? RootImmunity : tier >= 0 && tier < All.Length ? (uint)All[tier].Immunity : 0u);

    /// <summary>
    /// The flags that mark a tier for admins set up with flags instead of a tier group: what the tier adds over the one
    /// below it (Level 1: @css/generic, the basic admin flag). An admin with "@css/ban" counts as Level 2 and so also gets
    /// Level 1's commands.
    /// </summary>
    public static string[] Signature(int tier) => tier <= 0
        ? new[] { "@css/generic" }
        : All[tier].Flags.Except(All[tier - 1].Flags, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The highest tier whose signature flags the admin holds (-1 if none). <paramref name="hasFlag"/> asks CSS.</summary>
    public static int TierOfFlags(Func<string, bool> hasFlag)
    {
        for (var i = All.Length - 1; i >= 0; i--)
            if (Signature(i).Any(hasFlag)) return i;
        return -1;
    }

    /// <summary>
    /// Engine-free: add the SWBS tiers to an admin_groups.json document and keep them a ladder: every tier holds every
    /// flag of the tier below it (Level 1 &lt; Level 2 &lt; Level 3 &lt; Lead), including flags an owner added to a lower tier.
    /// Other groups, immunities and flags an owner added are never removed. Returns the new JSON text, or null when
    /// nothing changed.
    /// </summary>
    public static string? MergeInto(string? existingJson)
    {
        JsonObject doc;
        try { doc = string.IsNullOrWhiteSpace(existingJson) ? new JsonObject() : JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return null; } // never overwrite a file we cannot read
        var changed = false;
        var below = new List<string>();
        foreach (var l in All)
        {
            List<string> flags;
            if (doc[l.Group] is JsonObject existing)
            {
                flags = (existing["flags"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(f => f.Length > 0).ToList() ?? new List<string>();
                // An untouched group from an earlier version gets the current flags (immunity is kept).
                if (PreviousDefaults.TryGetValue(l.Group, out var olds) && olds.Any(o => flags.ToHashSet().SetEquals(o)))
                {
                    flags = l.Flags.ToList();
                    changed = true;
                }

                var missing = below.Where(f => !flags.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missing.Count > 0)
                {
                    flags.AddRange(missing);
                    changed = true;
                }

                if (changed) existing["flags"] = new JsonArray(flags.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
            }
            else if (doc.ContainsKey(l.Group))
            {
                continue; // something we cannot read as a group: leave it alone
            }
            else
            {
                flags = l.Flags.Concat(below).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                doc[l.Group] = new JsonObject
                {
                    ["flags"] = new JsonArray(flags.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
                    ["immunity"] = l.Immunity
                };
                changed = true;
            }

            below = flags;
        }

        return changed ? doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : null;
    }
}
