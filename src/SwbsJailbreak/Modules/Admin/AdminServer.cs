using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Admin;

/// <summary>SourceMod basecommands (map, rcon, cvar, execcfg, who, reloadadmins) + restart round + admin list.</summary>
public sealed partial class AdminModule
{
    [ConsoleCommand("css_map", "sm_map <map|workshop id> - change map")]
    public void CmdMap(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "map")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!map <map|workshop id>"); return; }
        ChangeMap(caller, info.GetArg(1), info);
    }

    internal void ChangeMap(CCSPlayerController? caller, string map, CommandInfo? info)
    {
        map = map.Trim();
        string command;
        if (map.StartsWith("workshop/", StringComparison.OrdinalIgnoreCase)) map = map[9..];
        if (ulong.TryParse(map, out _)) command = $"host_workshop_map {map}";
        else if (Server.IsMapValid(map)) command = $"changelevel {map}";
        else command = $"ds_workshop_changelevel {map}"; // subscribed workshop map by name
        Activity(caller, "admin.act.map", map);
        Plugin.AddTimer(3.0f, () => Server.ExecuteCommand(command));
    }

    [ConsoleCommand("css_rcon", "sm_rcon <args> - run a server command")]
    public void CmdRcon(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "rcon")) return;
        var cmd = ArgsFrom(info, 1);
        if (cmd.Length == 0) { Chat.ReplyT(caller, info, "core.usage", "!rcon <command>"); return; }
        Server.ExecuteCommand(cmd);
        LogActivity(caller, "rcon: " + cmd);
        Chat.ReplyT(caller, info, "admin.rcon.sent", cmd);
    }

    [ConsoleCommand("css_cvar", "sm_cvar <cvar> [value]")]
    public void CmdCvar(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "cvar")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!cvar <cvar> [value]"); return; }
        var name = info.GetArg(1);
        if (Cfg.ProtectedCvars.Contains(name, StringComparer.OrdinalIgnoreCase) && !Perms.Has(caller, "rcon"))
        {
            Chat.ReplyT(caller, info, "core.no_access");
            return;
        }

        var cv = ConVar.Find(name);
        if (cv == null) { Chat.ReplyT(caller, info, "admin.cvar.unknown", name); return; }
        if (info.ArgCount < 3)
        {
            Chat.ReplyT(caller, info, "admin.cvar.value", name, cv.StringValue);
            return;
        }

        var value = ArgsFrom(info, 2);
        Server.ExecuteCommand($"{name} \"{value.Replace("\"", "")}\"");
        Activity(caller, "admin.act.cvar", name, value);
    }

    [ConsoleCommand("css_execcfg", "sm_execcfg <filename>")]
    public void CmdExecCfg(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "config")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!execcfg <file.cfg>"); return; }
        var file = info.GetArg(1);
        if (file.Contains("..") || file.Contains(';')) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        Server.ExecuteCommand($"exec {file}");
        Activity(caller, "admin.act.execcfg", file);
    }

    [ConsoleCommand("css_reloadadmins", "sm_reloadadmins - reload CSS admins, groups and overrides")]
    public void CmdReloadAdmins(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "ban")) return;
        Server.ExecuteCommand("css_admins_reload");
        Server.ExecuteCommand("css_groups_reload");
        Server.ExecuteCommand("css_overrides_reload");
        Activity(caller, "admin.act.reloadadmins");
    }

    [ConsoleCommand("css_rr", "Restart the round (mp_restartgame 1)")]
    [ConsoleCommand("css_restartround", "Restart the round")]
    public void CmdRestartRound(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "map")) return;
        Server.ExecuteCommand("mp_restartgame 1");
        Activity(caller, "admin.act.rr");
    }

    [ConsoleCommand("css_who", "sm_who [#userid|name] - show players' admin access")]
    public void CmdWho(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "admin")) return;
        IEnumerable<CCSPlayerController> list = Players.All();
        if (info.ArgCount > 1)
        {
            var t = Targets.FindOrReply(caller, info, info.GetArg(1), respectImmunity: false);
            if (t == null) return;
            list = t.Players;
        }

        foreach (var c in list)
        {
            var data = Perms.AdminDataOf(c);
            var flags = data == null ? "-" : string.Join(",", data.GetAllFlags());
            if (!c.IsBot && Perms.IsUnverified(c)) flags += " (Steam ID not verified" + (data == null ? ": needs GSLT, or TrustUnverifiedSteamIds" : "") + ")";
            Chat.PrintToConsole(caller, $"#{c.UserId,-4} {c.PlayerName,-32} {c.SteamID,-18} {c.Team,-16} {flags}");
        }

        Chat.ReplyT(caller, info, "admin.who.console");
    }

    [ConsoleCommand("css_admins", "List online admins")]
    public void CmdAdmins(CCSPlayerController? caller, CommandInfo info)
    {
        var admins = Players.Humans().Where(c => Perms.IsAdmin(c)).Select(c => c.PlayerName).ToList();
        Chat.ReplyT(caller, info, admins.Count == 0 ? "admin.admins.none" : "admin.admins.list", string.Join(", ", admins));
    }
}
