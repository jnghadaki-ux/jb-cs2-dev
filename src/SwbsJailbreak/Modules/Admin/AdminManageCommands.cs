using System.Text.Json;
using System.Text.Json.Nodes;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Admin;

/// <summary>
/// !addadmin / !removeadmin / !adminlist edit CounterStrikeSharp's configs/admins.json in game (and reload it), so an admin
/// can be added without touching the server's files. !reloadconfig re-reads this plugin's config (the plugin restarts
/// itself) plus the admin files.
/// </summary>
public sealed partial class AdminModule
{
    /// <summary>configs/admins.json of CounterStrikeSharp (two folders above the plugin's config folder).</summary>
    private string AdminsPath => Path.GetFullPath(Path.Combine(Plugin.ConfigDirectory, "..", "..", "admins.json"));

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>"level2", "l2", "2", "lead", "root", "#group" or "@flag" → the admins.json groups / flags and immunity.</summary>
    internal static (string[] Groups, string[] Flags, int Immunity, string Label)? ParseAdminRole(string role)
    {
        var r = role.Trim().ToLowerInvariant();
        if (r is "root" or "@css/root") return (Array.Empty<string>(), new[] { "@css/root" }, 100, "root");
        if (r.StartsWith('@')) return (Array.Empty<string>(), new[] { r }, 0, r);
        if (r.StartsWith('#'))
        {
            var known = AdminLevels.All.FirstOrDefault(l => l.Group.Equals(r, StringComparison.OrdinalIgnoreCase));
            return (new[] { known?.Group ?? r }, Array.Empty<string>(), known?.Immunity ?? 0, known?.Name ?? r);
        }

        var digits = new string(r.Where(char.IsDigit).ToArray());
        var level = r switch
        {
            "lead" or "l4" or "level4" or "4" => AdminLevels.All.FirstOrDefault(l => l.Group.EndsWith("/lead", StringComparison.OrdinalIgnoreCase)),
            _ when (r.StartsWith("level") || r.StartsWith('l') || r.Length == 1) && digits.Length == 1 =>
                AdminLevels.All.FirstOrDefault(l => l.Group.Equals("#swbs/level" + digits, StringComparison.OrdinalIgnoreCase)),
            _ => null
        };
        return level == null ? null : (new[] { level.Group }, Array.Empty<string>(), level.Immunity, level.Name);
    }

    internal static bool IsSteamId64(string s) => s.Length == 17 && s.StartsWith("7656119") && s.All(char.IsDigit);

    private JsonObject ReadAdmins()
    {
        try
        {
            var path = AdminsPath;
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o)
                return o;
        }
        catch
        {
            // a broken file is replaced by a valid one with the new entry (the old text is kept as a backup below)
        }

        return new JsonObject();
    }

    private void WriteAdmins(JsonObject doc)
    {
        var path = AdminsPath;
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.WriteAllText(path, doc.ToJsonString(Indented));
        Server.ExecuteCommand("css_groups_reload");
        Server.ExecuteCommand("css_admins_reload");
    }

    [ConsoleCommand("css_addadmin", "sm_addadmin <steamid64|#userid|name> <level1|level2|level3|lead|root|#group|@flag> [name] - (root) add or change an admin")]
    public void CmdAddAdmin(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "root")) return;
        if (info.ArgCount < 3)
        {
            Chat.ReplyT(caller, info, "core.usage", "!addadmin <steamid64|#userid|name> <level1|level2|level3|lead|root|#group|@flag> [name]");
            Chat.ReplyT(caller, info, "admin.manage.roles", string.Join(", ", AdminLevels.All.Select(l => $"{l.Name} ({l.Group})")));
            return;
        }

        var who = info.GetArg(1).Trim();
        string steamId;
        string name;
        if (IsSteamId64(who))
        {
            steamId = who;
            name = Utilities.GetPlayers().FirstOrDefault(p => p.SteamID.ToString() == who)?.PlayerName ?? who;
        }
        else
        {
            var t = Targets.FindOrReply(caller, info, who, singleOnly: true, respectImmunity: false);
            if (t == null) return;
            var target = t.Players[0];
            if (target.IsBot || target.SteamID == 0) { Chat.ReplyT(caller, info, "admin.manage.no_steamid", target.PlayerName); return; }
            steamId = target.SteamID.ToString();
            name = target.PlayerName;
        }

        if (info.ArgCount > 3) name = string.Join(" ", Enumerable.Range(3, info.ArgCount - 3).Select(info.GetArg)).Trim();
        if (ParseAdminRole(info.GetArg(2)) is not { } role)
        {
            Chat.ReplyT(caller, info, "admin.manage.bad_role", info.GetArg(2));
            Chat.ReplyT(caller, info, "admin.manage.roles", string.Join(", ", AdminLevels.All.Select(l => $"{l.Name} ({l.Group})")));
            return;
        }

        try
        {
            var doc = ReadAdmins();
            foreach (var key in doc.Where(kv => kv.Value is JsonObject o && o["identity"]?.ToString() == steamId).Select(kv => kv.Key).ToList())
                doc.Remove(key);
            var entry = new JsonObject
            {
                ["identity"] = steamId,
                ["immunity"] = role.Immunity,
                ["flags"] = new JsonArray(role.Flags.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                ["groups"] = new JsonArray(role.Groups.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray())
            };
            var key2 = string.IsNullOrWhiteSpace(name) ? steamId : name;
            if (doc.ContainsKey(key2)) key2 = $"{key2} ({steamId})";
            doc[key2] = entry;
            WriteAdmins(doc);
            Chat.ReplyT(caller, info, "admin.manage.added", key2, steamId, role.Label);
            LogActivity(caller, $"added admin {key2} ({steamId}) as {role.Label}");
        }
        catch (Exception ex)
        {
            Chat.ReplyT(caller, info, "admin.manage.failed", ex.Message);
        }
    }

    [ConsoleCommand("css_removeadmin", "sm_removeadmin <steamid64|#userid|name> - (root) remove an admin")]
    [ConsoleCommand("css_deladmin", "sm_deladmin <steamid64|#userid|name> - (root) remove an admin")]
    public void CmdRemoveAdmin(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "root")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!removeadmin <steamid64|#userid|name>"); return; }
        var who = info.GetArg(1).Trim();
        var steamId = who;
        if (!IsSteamId64(who))
        {
            var online = Targets.Find(caller, who, singleOnly: true, respectImmunity: false);
            if (online.Ok) steamId = online.Players[0].SteamID.ToString();
        }

        try
        {
            var doc = ReadAdmins();
            var keys = doc.Where(kv => kv.Value is JsonObject o && o["identity"]?.ToString() == steamId || kv.Key.Equals(who, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).ToList();
            if (keys.Count == 0) { Chat.ReplyT(caller, info, "admin.manage.not_found", who); return; }
            foreach (var k in keys) doc.Remove(k);
            WriteAdmins(doc);
            Chat.ReplyT(caller, info, "admin.manage.removed", string.Join(", ", keys));
            LogActivity(caller, $"removed admin {string.Join(", ", keys)}");
        }
        catch (Exception ex)
        {
            Chat.ReplyT(caller, info, "admin.manage.failed", ex.Message);
        }
    }

    [ConsoleCommand("css_adminlist", "sm_adminlist - (root) the admins in admins.json")]
    public void CmdAdminList(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "root")) return;
        var doc = ReadAdmins();
        if (doc.Count == 0) { Chat.ReplyT(caller, info, "admin.manage.none"); return; }
        Chat.ReplyT(caller, info, "admin.manage.list_title", doc.Count);
        foreach (var (key, value) in doc)
        {
            if (value is not JsonObject o) continue;
            var groups = (o["groups"] as JsonArray)?.Select(g => g?.ToString()).Where(g => !string.IsNullOrEmpty(g)) ?? Enumerable.Empty<string?>();
            var flags = (o["flags"] as JsonArray)?.Select(g => g?.ToString()).Where(g => !string.IsNullOrEmpty(g)) ?? Enumerable.Empty<string?>();
            Chat.ReplyT(caller, info, "admin.manage.list_line", key, o["identity"]?.ToString() ?? "?", string.Join(" ", groups.Concat(flags)));
        }
    }

    [ConsoleCommand("css_reloadconfig", "sm_reloadconfig - (config) re-read this plugin's config and the admin files")]
    [ConsoleCommand("css_reloadplugin", "sm_reloadplugin - (config) re-read this plugin's config and the admin files")]
    public void CmdReloadConfig(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "config")) return;
        var name = Plugin.GetType().Assembly.GetName().Name ?? Plugin.ModuleName;
        Chat.ReplyT(caller, info, "admin.manage.reloading", name);
        LogActivity(caller, "reloaded the plugin config");
        Server.ExecuteCommand("css_groups_reload");
        Server.ExecuteCommand("css_admins_reload");
        // The plugin restarts itself (config, lang files, modules): after this command has returned.
        Server.NextFrame(() => Server.ExecuteCommand($"css_plugins reload {name}"));
    }
}
