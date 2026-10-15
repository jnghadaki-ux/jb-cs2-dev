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
    private static readonly string[] Lead = L3.Concat(new[] { "@css/config", "@css/cvar" }).ToArray();

    public static readonly AdminLevel[] All =
    {
        new("#swbs/level1", "Level 1", 10, L1,
            "admin menu and chat, jail logs, warden control, open cells, start/stop days, abort LRs, warden games, mute/gag/silence, slay/slap/beacon/freeze"),
        new("#swbs/level2", "Level 2", 20, L2,
            "+ kick, move/swap teams, 1up/xup, timed CT bans and bans, crew admin"),
        new("#swbs/level3", "Level 3", 30, L3,
            "+ permanent CT bans and unbans, day/LR editing, respawn, noclip/god/give and other fun commands, map change"),
        new("#swbs/lead", "Lead", 50, Lead,
            "+ zone editor and server cvars (everything except root-only: rcon, cell door editor, points, server setup)")
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

    /// <summary>
    /// Engine-free: add the SWBS tiers to an admin_groups.json document, leaving every existing group (including one with
    /// the same name an owner edited) untouched. Returns the new JSON text, or null when nothing had to be added.
    /// </summary>
    public static string? MergeInto(string? existingJson)
    {
        JsonObject doc;
        try { doc = string.IsNullOrWhiteSpace(existingJson) ? new JsonObject() : JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return null; } // never overwrite a file we cannot read
        var added = false;
        foreach (var l in All)
        {
            if (doc.ContainsKey(l.Group)) continue;
            doc[l.Group] = new JsonObject
            {
                ["flags"] = new JsonArray(l.Flags.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
                ["immunity"] = l.Immunity
            };
            added = true;
        }

        return added ? doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : null;
    }
}
