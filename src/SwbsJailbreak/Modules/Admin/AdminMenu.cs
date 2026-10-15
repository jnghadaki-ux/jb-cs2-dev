using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Admin;

/// <summary>
/// "!admin" menu (SourceMod adminmenu equivalent): Player commands, Jailbreak controls, Server commands.
/// Player actions follow the owner's SimpleAdmin flow: pick a player, then an action.
/// </summary>
public sealed partial class AdminModule
{
    [ConsoleCommand("css_admin", "Open the admin menu")]
    [ConsoleCommand("css_adminmenu", "Open the admin menu")]
    public void CmdAdminMenu(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) { Chat.ReplyT(caller, info, "core.console_only_ingame"); return; }
        if (!Require(caller, info, "admin")) return;
        OpenMainMenu(caller);
    }

    private void OpenMainMenu(CCSPlayerController admin)
    {
        var m = new JbMenu(Lang.T("admin.menu.title"));
        m.Add(Lang.T("admin.menu.players"), a => OpenPlayerList(a, m));
        m.Add(Lang.T("admin.menu.jailbreak"), a => OpenJailbreakMenu(a, m));
        m.Add(Lang.T("admin.menu.server"), a => OpenServerMenu(a, m), Perms.Has(admin, "map"));
        m.Open(admin);
    }

    private void OpenPlayerList(CCSPlayerController admin, JbMenu parent)
    {
        var m = new JbMenu(Lang.T("admin.menu.players")) { Parent = parent };
        foreach (var c in Players.All().OrderBy(c => c.Team).ThenBy(c => c.PlayerName))
        {
            var slot = c.Slot;
            var tag = c.Team switch { CsTeam.Terrorist => "T", CsTeam.CounterTerrorist => "CT", _ => "SPEC" };
            var alive = c.IsAliveP() ? "" : " †";
            m.Add($"[{tag}] {Hud.Escape(c.PlayerName)}{alive}", a => OpenPlayerActions(a, slot, m), Perms.CanTarget(admin, c));
        }

        m.Open(admin);
    }

    private void OpenPlayerActions(CCSPlayerController admin, int slot, JbMenu parent)
    {
        var target = Players.Get(slot).Controller;
        if (target == null) { Chat.Tell(admin, "admin.target_gone"); return; }
        var m = new JbMenu(Hud.Escape(target.PlayerName)) { Parent = parent };
        var p = target.Jb()!;
        var fx = Fxs(p);

        void Act(string capability, string labelKey, Action<CCSPlayerController, CCSPlayerController> action, bool keepOpen = false)
        {
            m.Add(Lang.T(labelKey), a =>
            {
                var t = Players.Get(slot).Controller;
                if (t == null) { Chat.Tell(a, "admin.target_gone"); return; }
                if (!Perms.Has(a, capability) || !Perms.CanTarget(a, t)) { Chat.Tell(a, "core.no_access"); return; }
                action(a, t);
            }, Perms.Has(admin, capability), keepOpen);
        }

        Act("slay", "admin.menu.act.slay", (a, t) => { t.Kill(); Activity(a, "admin.act.slay", t.PlayerName); });
        Act("slay", "admin.menu.act.slap", (a, t) => { t.Slap(5); Sound.PlayAt(t, Cfg.SlapSound); Activity(a, "admin.act.slap", t.PlayerName, 5); }, true);
        Act("slay", fx.Beacon != null ? "admin.menu.act.unbeacon" : "admin.menu.act.beacon", (a, t) => { SetBeacon(t.Jb()!, Fxs(t.Jb()!).Beacon == null); Activity(a, "admin.act.beacon", t.PlayerName); });
        Act("slay", fx.Frozen ? "admin.menu.act.unfreeze" : "admin.menu.act.freeze", (a, t) =>
        {
            if (Fxs(t.Jb()!).Frozen) { Unfreeze(t); Activity(a, "admin.act.unfreeze", t.PlayerName); }
            else { Freeze(t, Cfg.FreezeDuration); Activity(a, "admin.act.freeze", t.PlayerName, (int)Cfg.FreezeDuration); }
        });
        Act("slay", "admin.menu.act.timebomb", (a, t) => { SetBomb(t.Jb()!, BombKind.Time, Fxs(t.Jb()!).TimeBomb == null); Activity(a, "admin.act.timebomb", t.PlayerName); });
        Act("slay", "admin.menu.act.firebomb", (a, t) => { SetBomb(t.Jb()!, BombKind.Fire, Fxs(t.Jb()!).FireBomb == null); Activity(a, "admin.act.firebomb", t.PlayerName); });
        Act("slay", "admin.menu.act.freezebomb", (a, t) => { SetBomb(t.Jb()!, BombKind.Freeze, Fxs(t.Jb()!).FreezeBomb == null); Activity(a, "admin.act.freezebomb", t.PlayerName); });
        Act("slay", "admin.menu.act.burn", (a, t) => { Burn(t, Cfg.BurnDuration); Activity(a, "admin.act.burn", t.PlayerName, (int)Cfg.BurnDuration); });
        Act("slay", "admin.menu.act.drug", (a, t) => { SetDrug(t, Fxs(t.Jb()!).Drug == null); Activity(a, "admin.act.drug", t.PlayerName); });
        Act("slay", fx.BlindAlpha > 0 ? "admin.menu.act.unblind" : "admin.menu.act.blind", (a, t) =>
        {
            var on = Fxs(t.Jb()!).BlindAlpha == 0;
            Blind(t, on ? 255 : 0);
            Activity(a, "admin.act.blind", t.PlayerName, on ? 255 : 0);
        });
        Act("fun", "admin.menu.act.noclip", (a, t) => { t.SetNoclip(!t.IsNoclip()); Activity(a, "admin.act.noclip", t.PlayerName); });
        Act("fun", fx.God ? "admin.menu.act.ungod" : "admin.menu.act.god", (a, t) => { var f = Fxs(t.Jb()!); f.God = !f.God; Activity(a, f.God ? "admin.act.god" : "admin.act.ungod", t.PlayerName); });
        Act("respawn", "admin.menu.act.respawn", (a, t) =>
        {
            if (t.IsAliveP() || t.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist)) { Chat.Tell(a, "admin.respawn.none"); return; }
            t.RespawnSolid();
            Activity(a, "admin.act.respawn", t.PlayerName);
        });
        Act("slay", "admin.menu.act.bring", (a, t) =>
        {
            if (!a.IsAliveP() || a.Origin() is not { } pos) { Chat.Tell(a, "core.only_alive"); return; }
            t.TeleportTo(new Vector(pos.X, pos.Y, pos.Z + 5f));
            Activity(a, "admin.act.bring", t.PlayerName);
        });
        Act("slay", "admin.menu.act.goto", (a, t) =>
        {
            if (!a.IsAliveP() || t.Origin() is not { } pos) { Chat.Tell(a, "core.only_alive"); return; }
            a.TeleportTo(new Vector(pos.X, pos.Y, pos.Z + 5f));
            Activity(a, "admin.act.goto", t.PlayerName);
        });
        Act("teams", "admin.menu.act.swap", (a, t) => { MoveTeam(t, t.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist); Activity(a, "admin.act.teamswitch", t.PlayerName); });
        Act("teams", "admin.menu.act.spec", (a, t) => { MoveTeam(t, CsTeam.Spectator); Activity(a, "admin.act.team", t.PlayerName, TeamName(CsTeam.Spectator)); });
        Act("mute", p.AdminVoiceMuted ? "admin.menu.act.unmute" : "admin.menu.act.mute", (a, t) =>
        {
            var jp = t.Jb()!;
            jp.AdminVoiceMuted = !jp.AdminVoiceMuted;
            ApplyVoice(jp);
            Activity(a, jp.AdminVoiceMuted ? "admin.act.mute" : "admin.act.unmute", t.PlayerName, Lang.T("admin.time.session"));
        });
        Act("mute", p.AdminChatGagged ? "admin.menu.act.ungag" : "admin.menu.act.gag", (a, t) =>
        {
            var jp = t.Jb()!;
            jp.AdminChatGagged = !jp.AdminChatGagged;
            Activity(a, jp.AdminChatGagged ? "admin.act.gag" : "admin.act.ungag", t.PlayerName, Lang.T("admin.time.session"));
        });
        Act("teams", "admin.menu.act.warn", (a, t) =>
        {
            var reason = Lang.T("admin.warn.menu_reason");
            var list = AddWarning(t, reason, a);
            Chat.Tell(t, "admin.warn.you", reason, list.Count);
            Activity(a, "admin.act.warn", t.PlayerName, reason, list.Count);
        });
        Act("kick", "admin.menu.act.kick", (a, t) => { Activity(a, "admin.act.kick", t.PlayerName, Lang.T("admin.kick.default_reason")); Kick(t, Lang.T("admin.kick.default_reason")); });
        foreach (var (minutes, key) in new[] { (30, "admin.menu.act.ban30"), (1440, "admin.menu.act.ban1d"), (0, "admin.menu.act.banperm") })
        {
            var mins = minutes;
            Act("ban", key, (a, t) =>
            {
                if (t.IsBot) { Chat.Tell(a, "admin.ban.bot"); return; }
                var reason = Lang.T("admin.ban.menu_reason");
                AddBan(t.SteamID, t.PlayerName, mins, reason, a, "");
                Activity(a, "admin.act.ban", t.PlayerName, FormatDuration(mins), reason);
                Kick(t, BanMessage(Storage.Get<BanRecord>(BanNs, t.SteamID.ToString())!));
            });
        }

        m.Open(admin);
    }

    private void OpenJailbreakMenu(CCSPlayerController admin, JbMenu parent)
    {
        var m = new JbMenu(Lang.T("admin.menu.jailbreak")) { Parent = parent };
        m.Add(Lang.T("admin.menu.jb.open_cells"), a =>
        {
            if (Jb.Cells.ForceOpen(a)) LogActivity(a, "opened the cells");
            else Chat.Tell(a, "admin.menu.jb.cells_failed");
        }, Perms.Has(admin, "cells") && Jb.Cells.DoExist, true);
        m.Add(Lang.T("admin.menu.jb.close_cells"), a =>
        {
            if (Jb.Cells.ForceClose(a)) LogActivity(a, "closed the cells");
            else Chat.Tell(a, "admin.menu.jb.cells_failed");
        }, Perms.Has(admin, "cells") && Jb.Cells.DoExist, true);
        m.Add(Lang.T("admin.menu.jb.remove_warden"), a =>
        {
            if (!Jb.Warden.HasWarden) { Chat.Tell(a, "admin.menu.jb.no_warden"); return; }
            Jb.Warden.RemoveWarden();
            LogActivity(a, "removed the warden");
        }, Perms.Has(admin, "warden.manage") && Jb.Warden.HasWarden);
        m.Add(Lang.T("admin.menu.jb.days"), a => Jb.Days.OpenMenu(a, null, true), Perms.Has(admin, "days"));
        m.Add(Lang.T("admin.menu.jb.edit_days"), a => Plugin.GetModule<Days.DaysModule>()?.OpenEditMenu(a, m),
            Perms.Has(admin, "days.edit") && Plugin.GetModule<Days.DaysModule>() != null);
        m.Add(Lang.T("admin.menu.jb.edit_lrs"), a => Plugin.GetModule<LastRequest.LastRequestModule>()?.OpenEditMenu(a, m),
            Perms.Has(admin, "lr.edit") && Plugin.GetModule<LastRequest.LastRequestModule>() != null);
        m.Add(Lang.T("admin.menu.jb.end_day"), a =>
        {
            Jb.Days.EndDay();
            LogActivity(a, "ended the current day");
        }, Perms.Has(admin, "days") && Jb.Days.IsInProgress);
        m.Add(Lang.T("admin.menu.jb.abort_lrs"), a =>
        {
            Jb.LastRequests.EndAll();
            Activity(a, "admin.act.abortlrs");
        }, Perms.Has(admin, "lr") && Jb.LastRequests.NumStartedIgnore() > 0);
        m.Open(admin);
    }

    private void OpenServerMenu(CCSPlayerController admin, JbMenu parent)
    {
        var m = new JbMenu(Lang.T("admin.menu.server")) { Parent = parent };
        m.Add(Lang.T("admin.menu.server.rr"), a => { Server.ExecuteCommand("mp_restartgame 1"); Activity(a, "admin.act.rr"); });
        if (Cfg.MapList.Count > 0)
            m.Add(Lang.T("admin.menu.server.maps"), a =>
            {
                var maps = new JbMenu(Lang.T("admin.menu.server.maps")) { Parent = m };
                foreach (var map in Cfg.MapList)
                {
                    var name = map;
                    maps.Add(Hud.Escape(name), x => ChangeMap(x, name, null));
                }

                maps.Open(a);
            });
        m.Add(Lang.T("admin.menu.server.reloadadmins"), a =>
        {
            Server.ExecuteCommand("css_admins_reload");
            Server.ExecuteCommand("css_groups_reload");
            Activity(a, "admin.act.reloadadmins");
        }, Perms.Has(admin, "ban"));
        m.Open(admin);
    }
}
