using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Admin;

/// <summary>
/// SourceMod playercommands (slap/slay/rename), SMPlugins Respawn, Heal and Teamswitch, plus the owner's SimpleAdmin
/// player commands (hp, armor, give, bring, goto, team, god).
/// </summary>
public sealed partial class AdminModule
{
    private readonly HashSet<int> _switchOnDeath = new();
    private readonly HashSet<int> _switchOnRoundEnd = new();

    [ConsoleCommand("css_slap", "sm_slap <#userid|name> [damage]")]
    public void CmdSlap(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "slay")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!slap <target> [damage]"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        var dmg = 0;
        if (info.ArgCount > 2 && !int.TryParse(info.GetArg(2), out dmg)) { Chat.ReplyT(caller, info, "admin.invalid_amount"); return; }
        dmg = Math.Clamp(dmg, 0, 1000);
        foreach (var c in t.Players)
        {
            c.Slap(dmg);
            Sound.PlayAt(c, Cfg.SlapSound);
            Fx.Shake(c, 8f, 4f, 0.5f);
        }

        Activity(caller, "admin.act.slap", t.Description, dmg);
    }

    [ConsoleCommand("css_slay", "sm_slay <#userid|name>")]
    public void CmdSlay(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "slay")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!slay <target>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        foreach (var c in t.Players) c.Kill();
        Activity(caller, "admin.act.slay", t.Description);
    }

    [ConsoleCommand("css_rename", "sm_rename <#userid|name> <new name>")]
    public void CmdRename(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "slay")) return;
        if (info.ArgCount < 3) { Chat.ReplyT(caller, info, "core.usage", "!rename <target> <new name>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true);
        if (t == null) return;
        var newName = ArgsFrom(info, 2);
        if (newName.Length is 0 or > 64) { Chat.ReplyT(caller, info, "admin.rename.invalid"); return; }
        var target = t.Players[0];
        var old = target.PlayerName;
        target.SetName(newName);
        Activity(caller, "admin.act.rename", old, newName);
    }

    // ------------------------------------------------------------------ respawn / heal / hp / armor / god

    [ConsoleCommand("css_respawn", "sm_respawn <#steamid|#userid|name> - respawn a dead player")]
    public void CmdRespawn(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "respawn")) return;
        if (info.ArgCount < 2) { OpenRespawnMenu(caller); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), deadOnly: true);
        if (t == null) return;
        var n = 0;
        foreach (var c in t.Players.Where(c => c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist))
        {
            c.RespawnSolid();
            n++;
        }

        if (n == 0) { Chat.ReplyT(caller, info, "admin.respawn.none"); return; }
        Activity(caller, "admin.act.respawn", t.Description);
    }

    private void OpenRespawnMenu(CCSPlayerController? caller)
    {
        if (caller == null) return;
        var dead = Players.All().Where(c => !c.IsAliveP() && c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist).ToList();
        if (dead.Count == 0) { Chat.Tell(caller, "admin.respawn.none"); return; }
        var m = new JbMenu(Lang.T("admin.menu.respawn"));
        foreach (var d in dead)
        {
            var slot = d.Slot;
            m.Add(Hud.Escape(d.PlayerName), admin =>
            {
                var target = Players.Get(slot).Controller;
                if (target == null || target.IsAliveP()) { Chat.Tell(admin, "admin.target_gone"); return; }
                target.RespawnSolid();
                Activity(admin, "admin.act.respawn", target.PlayerName);
            });
        }

        m.Open(caller);
    }

    [ConsoleCommand("css_heal", "sm_heal <#steamid|#userid|name> - heal a player to 100 health")]
    public void CmdHeal(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "fun")) return; // Level 3+, like !hp
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!heal <target>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        foreach (var c in t.Players)
        {
            if (c.GetHp() >= 100) { Chat.ReplyT(caller, info, "admin.heal.full", c.PlayerName); continue; }
            c.SetHp(100, false);
            Chat.Tell(c, "admin.heal.healed_by", AdminName(caller));
        }

        Activity(caller, "admin.act.heal", t.Description);
    }

    [ConsoleCommand("css_hp", "Set player health: !hp <target> <amount>")]
    public void CmdHp(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "fun")) return;
        if (info.ArgCount < 3 || !int.TryParse(info.GetArg(2), out var hp) || hp < 1) { Chat.ReplyT(caller, info, "core.usage", "!hp <target> <1-10000>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        hp = Math.Min(hp, 10000);
        foreach (var c in t.Players) c.SetHp(hp);
        Activity(caller, "admin.act.hp", t.Description, hp);
    }

    [ConsoleCommand("css_armor", "Set player armour: !armor <target> <amount> [helmet 0/1]")]
    public void CmdArmor(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "fun")) return;
        if (info.ArgCount < 3 || !int.TryParse(info.GetArg(2), out var armor) || armor < 0) { Chat.ReplyT(caller, info, "core.usage", "!armor <target> <0-100> [helmet 0/1]"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        bool? helmet = info.ArgCount > 3 ? info.GetArg(3) == "1" : null;
        foreach (var c in t.Players) c.SetArmor(Math.Min(armor, 100), helmet);
        Activity(caller, "admin.act.armor", t.Description, armor);
    }

    [ConsoleCommand("css_god", "Toggle god mode: !god <target> [0/1]")]
    public void CmdGod(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "god")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!god <target> [0/1]"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        foreach (var c in t.Players)
        {
            var fx = Fxs(c.Jb()!);
            fx.God = ParseToggle(info, 2, fx.God);
            Chat.Tell(c, fx.God ? "admin.god.on" : "admin.god.off");
        }

        Activity(caller, "admin.act.god", t.Description);
    }

    [ConsoleCommand("css_ungod", "Disable god mode: !ungod <target>")]
    public void CmdUngod(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "god")) return; // Lead+, like !god
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!ungod <target>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1));
        if (t == null) return;
        foreach (var c in t.Players) Fxs(c.Jb()!).God = false;
        Activity(caller, "admin.act.ungod", t.Description);
    }

    // ------------------------------------------------------------------ give / bring / goto

    [ConsoleCommand("css_give", "Give an item: !give <target> <weapon>")]
    public void CmdGive(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "fun")) return;
        if (info.ArgCount < 3) { Chat.ReplyT(caller, info, "core.usage", "!give <target> <weapon>"); return; }
        var item = info.GetArg(2).ToLowerInvariant();
        var weapon = Weapons.Parse(item) ?? (item.StartsWith("item_") ? item : null);
        if (weapon == null) { Chat.ReplyT(caller, info, "admin.give.unknown", item); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        foreach (var c in t.Players) c.Give(weapon);
        Activity(caller, "admin.act.give", t.Description, Weapons.Display(weapon));
    }

    [ConsoleCommand("css_bring", "Teleport a player to you: !bring <target>")]
    public void CmdBring(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "teleport")) return;
        if (caller == null || !caller.IsAliveP()) { Chat.ReplyT(caller, info, "core.only_alive"); return; }
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!bring <target>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        var pos = caller.Origin();
        if (pos == null) return;
        foreach (var c in t.Players.Where(c => c.Slot != caller.Slot)) c.TeleportTo(new Vector(pos.X, pos.Y, pos.Z + 5f));
        Activity(caller, "admin.act.bring", t.Description);
    }

    [ConsoleCommand("css_goto", "Teleport to a player: !goto <target>")]
    public void CmdGoto(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "teleport")) return;
        if (caller == null || !caller.IsAliveP()) { Chat.ReplyT(caller, info, "core.only_alive"); return; }
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!goto <target>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true, singleOnly: true);
        if (t == null) return;
        var pos = t.Players[0].Origin();
        if (pos == null) return;
        caller.TeleportTo(new Vector(pos.X, pos.Y, pos.Z + 5f));
        Activity(caller, "admin.act.goto", t.Description);
    }

    // ------------------------------------------------------------------ teams (SMPlugins Teamswitch + SimpleAdmin team)

    private static CsTeam? ParseTeam(string s) => s.ToLowerInvariant() switch
    {
        "1" or "spec" or "spectator" or "s" => CsTeam.Spectator,
        "2" or "t" or "prisoner" or "prisoners" or "terrorist" => CsTeam.Terrorist,
        "3" or "ct" or "guard" or "guards" or "counterterrorist" => CsTeam.CounterTerrorist,
        _ => null
    };

    private static CsTeam Other(CsTeam t) => t == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;

    [ConsoleCommand("css_team", "Move a player to a team: !team <target> <t|ct|spec>")]
    public void CmdTeam(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "teams")) return;
        if (info.ArgCount < 3 || ParseTeam(info.GetArg(2)) is not { } team) { Chat.ReplyT(caller, info, "core.usage", "!team <target> <t|ct|spec>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1));
        if (t == null) return;
        foreach (var c in t.Players) MoveTeam(c, team);
        Activity(caller, "admin.act.team", t.Description, TeamName(team));
    }

    [ConsoleCommand("css_teamswitch", "Switch a player's team now: !ts <target> [t|ct|spec] (both teams: !swapteams)")]
    [ConsoleCommand("css_ts", "Switch a player's team now: !ts <target> [t|ct|spec] (both teams: !swapteams)")]
    public void CmdTeamswitch(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "teams")) return;
        // !ts used to swap both teams when the target was left out, which turned a typo into a full team swap.
        // Swapping everyone is only done by the explicit !swapteams now.
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!ts <target> [t|ct|spec]  (swap both teams: !swapteams)"); return; }

        var t = Targets.FindOrReply(caller, info, info.GetArg(1));
        if (t == null) return;
        CsTeam? forced = info.ArgCount > 2 ? ParseTeam(info.GetArg(2)) : null;
        foreach (var c in t.Players) MoveTeam(c, forced ?? Other(c.Team));
        Activity(caller, "admin.act.teamswitch", t.Description);
    }

    [ConsoleCommand("css_teamswitch_death", "Switch a player's team when they die: !tsd <target>")]
    [ConsoleCommand("css_tsd", "Switch a player's team when they die")]
    public void CmdTeamswitchDeath(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "teams")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!tsd <target>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1));
        if (t == null) return;
        foreach (var c in t.Players)
        {
            if (!c.IsAliveP()) MoveTeam(c, Other(c.Team));
            else if (!_switchOnDeath.Add(c.Slot)) _switchOnDeath.Remove(c.Slot);
        }

        Activity(caller, "admin.act.teamswitch_death", t.Description);
    }

    [ConsoleCommand("css_teamswitch_roundend", "Switch a player's team at round end: !tsr <target>")]
    [ConsoleCommand("css_tsr", "Switch a player's team at round end")]
    public void CmdTeamswitchRoundEnd(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "teams")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!tsr <target>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1));
        if (t == null) return;
        foreach (var c in t.Players)
            if (!_switchOnRoundEnd.Add(c.Slot)) _switchOnRoundEnd.Remove(c.Slot);
        Activity(caller, "admin.act.teamswitch_roundend", t.Description);
    }

    /// <summary>Admin move: respects nothing but immunity (admin override), uses the Teams contract when present.</summary>
    [ConsoleCommand("css_swapteams", "Swap guards and prisoners (CT-banned players stay prisoners) and restart the round")]
    public void CmdSwapTeams(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "teams")) return;
        SwapTeams(caller);
    }

    /// <summary>
    /// Guards become prisoners and prisoners guards, then the round restarts so everyone spawns on the new side.
    /// CT-banned prisoners stay prisoners; spectators are untouched.
    /// </summary>
    private void SwapTeams(CCSPlayerController? caller)
    {
        var moved = 0;
        foreach (var c in Players.All().Where(c => c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist).ToList())
        {
            var to = Other(c.Team);
            if (to == CsTeam.CounterTerrorist && Jb.Teams.IsCtBanned(c.SteamID)) continue;
            c.ChangeTeam(to);
            moved++;
        }

        Chat.TellAll("admin.swapteams.announce", caller?.PlayerName ?? "Console", moved);
        Server.ExecuteCommand("mp_restartgame 1");
        Jb.Log.LogEvent($"{caller?.PlayerName ?? "Console"} swapped the teams ({moved} players)", caller.Jb(), null, LogType.Admin);
    }

    internal void MoveTeam(CCSPlayerController c, CsTeam team)
    {
        if (!c.IsValid || c.Team == team) return;
        var p = c.Jb();
        if (p != null && team != CsTeam.Spectator)
        {
            // Keep alive players alive with their gear (SwitchTeam), dead ones change normally.
            if (c.IsAliveP()) c.SwitchTeam(team);
            else Jb.Teams.MoveToTeam(p, team, force: true);
        }
        else
        {
            c.ChangeTeam(team);
        }

        Chat.TellAll("admin.teamswitch.announce", c.PlayerName, TeamName(team));
    }

    internal static string TeamName(CsTeam t) => t switch
    {
        CsTeam.Terrorist => Lang.T("admin.team.t"),
        CsTeam.CounterTerrorist => Lang.T("admin.team.ct"),
        CsTeam.Spectator => Lang.T("admin.team.spec"),
        _ => t.ToString()
    };

    // Death / round-end switches (Teamswitch plugin behaviour)
    private void HandleTeamswitchDeath(JbPlayer victim)
    {
        if (!_switchOnDeath.Remove(victim.Slot)) return;
        var slot = victim.Slot;
        Server.NextFrame(() =>
        {
            var c = Players.Get(slot).Controller;
            if (c != null) MoveTeam(c, Other(c.Team));
        });
    }

    private void HandleTeamswitchRoundEnd()
    {
        foreach (var slot in _switchOnRoundEnd.ToList())
        {
            var c = Players.Get(slot).Controller;
            if (c != null) MoveTeam(c, Other(c.Team));
        }

        _switchOnRoundEnd.Clear();
    }
}
