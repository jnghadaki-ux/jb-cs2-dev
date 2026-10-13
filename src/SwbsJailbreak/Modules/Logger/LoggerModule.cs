using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.WeaponSelection;

namespace SwbsJailbreak.Modules.Logger;

public sealed class LoggerConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Output of trigger_teleport that logs a teleport use (original: OnEndTouch). Use OnStartTouch if a CS2 map never fires OnEndTouch.</summary>
    public string TeleportOutput { get; set; } = "OnEndTouch";

    /// <summary>Lines kept per log (global and per player); oldest lines are dropped beyond this.</summary>
    public int MaxLinesPerLog { get; set; } = 5000;

    /// <summary>Lines printed per server frame by !logs (original: 40).</summary>
    public int LinesPerFrame { get; set; } = 40;

    /// <summary>Force sv_damage_print_enable 0 like the original (skipped when the cvar does not exist).</summary>
    public bool HideDamagePrint { get; set; } = true;
}

/// <summary>
/// Port of UltJB_Logger.sp (v1.8): per-player + global round logs of drops, pickups, button presses/damage, vent
/// damage/breaks, respawns, kills, damage, teleports and trigger_hurt healing, plus the external LogEvent API, the
/// live admin console broadcasts and the !logs console viewer.
/// </summary>
public sealed class LoggerModule : JbModule, ILogService
{
    private sealed record LogLine(string Text, LogType Type);

    private readonly List<LogLine>[] _logs = Enumerable.Range(0, Players.MaxSlots + 1).Select(_ => new List<LogLine>()).ToArray();
    private float _roundStart;
    private int _roundStartTick;

    private string _lastHitVent = "";
    private readonly double[] _nextVentMessage = new double[Players.MaxSlots];
    private readonly int[] _hurtsTouched = new int[Players.MaxSlots];
    private readonly int[] _healthSample = new int[Players.MaxSlots];
    private int _anyHurtTouching;
    private bool _damagePrintMissingLogged;

    private LoggerConfig Cfg => Section<LoggerConfig>("Logger");

    public override int Order => 6;
    public override bool Enabled => Cfg.Enabled;

    public override void OnLoad(bool hotReload)
    {
        Jb.Log = this;
        if (hotReload) // Cold start: no map yet, and Server.* reads the engine globals natively (crashes the server); OnMapStart covers it.
        {
            _roundStart = Server.CurrentTime;
            _roundStartTick = Server.TickCount;
        }

        Plugin.RegisterListener<Listeners.OnEntityParentChanged>(OnEntityParentChanged);
        Plugin.RegisterListener<Listeners.OnEntityTakeDamagePre>(OnEntityTakeDamagePre);
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);

        Plugin.HookEntityOutput("func_button", "OnPressed", OnButtonPressed, HookMode.Post);
        Plugin.HookEntityOutput("func_rot_button", "OnPressed", OnButtonPressed, HookMode.Post);
        Plugin.HookEntityOutput("func_breakable", "OnBreak", OnVentBreak, HookMode.Post);
        var teleportOutput = string.IsNullOrWhiteSpace(Cfg.TeleportOutput) ? "OnEndTouch" : Cfg.TeleportOutput.Trim();
        Plugin.HookEntityOutput("trigger_teleport", teleportOutput, OnTeleport, HookMode.Post);
        Plugin.HookEntityOutput("trigger_hurt", "OnStartTouch", OnHurtEnter, HookMode.Post);
        Plugin.HookEntityOutput("trigger_hurt", "OnEndTouch", OnHurtLeave, HookMode.Post);

        ApplyDamagePrint();
    }

    public override void OnMapStart(string mapName)
    {
        _damagePrintMissingLogged = false;
        ApplyDamagePrint();
        ResetHurtTracking();
    }

    // ================================================================== ILogService

    public void LogEvent(string message, JbPlayer? primary, JbPlayer? secondary, LogType type, bool global = true)
    {
        var t = Math.Max(0, (int)(Server.CurrentTime - _roundStart));
        var line = new LogLine($"[{(t / 60) % 60:00}:{t % 60:00}] {message}", type);

        var primaryIdx = primary != null ? primary.Slot + 1 : 0;
        var secondaryIdx = secondary != null ? secondary.Slot + 1 : 0;
        if (primaryIdx > 0) Push(primaryIdx, line);
        if (secondaryIdx > 0 && secondaryIdx != primaryIdx) Push(secondaryIdx, line);
        if (global) Push(0, line);

        if (primaryIdx == 0 && secondaryIdx == 0 && !global)
        {
            foreach (var c in Players.Humans())
            {
                if (!OverrideAccess.Check(c, "sm_logwarn", "@css/unban")) continue;
                c.PrintToConsole(Lang.T("logger.warn_unknown_target"));
                c.PrintToConsole(message);
            }
        }
    }

    private void Push(int index, LogLine line)
    {
        if (index < 0 || index >= _logs.Length) return;
        var list = _logs[index];
        list.Add(line);
        var max = Math.Max(100, Cfg.MaxLinesPerLog);
        if (list.Count > max) list.RemoveRange(0, list.Count - max);
    }

    private static JbPlayer? J(CCSPlayerController? c) => Players.Get(c);

    // ================================================================== round / players

    public override void OnRoundStart()
    {
        // Dump the global log to SourceTV consoles, then clear all logs (original ClearLogs).
        foreach (var tv in Utilities.GetPlayers().Where(c => c is { IsValid: true, IsHLTV: true }))
            foreach (var l in _logs[0])
                tv.PrintToConsole(l.Text);

        foreach (var list in _logs) list.Clear();
        _roundStart = Server.CurrentTime;
        _roundStartTick = Server.TickCount;
        ResetHurtTracking();
        ApplyDamagePrint();

        var banner = Lang.T("logger.new_round_banner");
        foreach (var c in Players.Humans()) c.PrintToConsole(banner);
    }

    public override void OnPlayerConnected(JbPlayer player)
    {
        // Fix for slot reuse: the original kept the previous occupant's lines.
        _logs[player.Slot + 1].Clear();
        _hurtsTouched[player.Slot] = 0;
        _nextVentMessage[player.Slot] = 0;
    }

    public override void OnPlayerDisconnect(JbPlayer player) => ClearHurt(player.Slot);

    public override void OnPlayerSpawn(JbPlayer player)
    {
        // The core delivers spawns one frame late; spawns of the round restart itself happened before the original
        // cleared the logs at round_start, so they never appeared there either.
        if (Server.TickCount - _roundStartTick <= 2) return;
        var c = player.Controller;
        if (c == null || !c.IsAliveP()) return;
        LogEvent(Lang.T("logger.respawned", c.PlayerName), player, null, LogType.Any);
    }

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        ClearHurt(victim.Slot);
        string attackerName, victimName, weapon;
        if (attacker != null && attacker.Slot == victim.Slot)
        {
            attackerName = $"({TeamTag(victim)}) {victim.Name}";
            victimName = Lang.T("logger.themself");
            weapon = Lang.T("logger.from_suicide");
        }
        else if (attacker == null || attacker.Controller == null)
        {
            attackerName = $"({TeamTag(victim)}) {victim.Name}";
            victimName = Lang.T("logger.themself");
            weapon = Lang.T("logger.from_world");
        }
        else
        {
            attackerName = $"({TeamTag(attacker)}) {attacker.Name}";
            victimName = $"({TeamTag(victim)}) {victim.Name}";
            weapon = Lang.T("logger.with_weapon", ev.Weapon ?? "");
        }

        var msg = Lang.T("logger.killed", attackerName, victimName, weapon);
        LogEvent(msg, attacker, victim, LogType.Attack);
        foreach (var c in Players.Humans()) c.PrintToConsole(msg);
    }

    private static string TeamTag(JbPlayer p) => p.Team == CsTeam.CounterTerrorist ? "CT" : "T";

    [GameEventHandler]
    public HookResult OnPlayerHurtLog(EventPlayerHurt ev, GameEventInfo info)
    {
        var victimC = ev.Userid;
        var attackerC = ev.Attacker;
        var victim = J(victimC);
        if (victim == null) return HookResult.Continue;
        var attacker = J(attackerC);
        var hp = ev.DmgHealth;
        var armor = ev.DmgArmor;

        if (attacker != null && attacker.Slot == victim.Slot)
        {
            LogEvent(Lang.T("logger.hurt_self", victim.Name, hp, armor), victim, null, LogType.Attack);
            return HookResult.Continue;
        }

        if (attacker == null)
        {
            LogEvent(Lang.T("logger.hurt_world", victim.Name, hp, armor), victim, null, LogType.Attack);
            return HookResult.Continue;
        }

        var msg = Lang.T("logger.hurt", attacker.Name, victim.Name, hp, armor);
        LogEvent(msg, attacker, victim, LogType.Attack);
        if (attacker.Team != CsTeam.Terrorist)
            foreach (var c in Players.Humans())
                c.PrintToConsole(msg);
        return HookResult.Continue;
    }

    // ================================================================== items

    private void OnEntityParentChanged(CEntityInstance entity, CEntityInstance newParent)
    {
        try
        {
            if (entity == null || !entity.IsValid) return;
            var dn = entity.DesignerName;
            if (dn == null || !dn.StartsWith("weapon_", StringComparison.Ordinal)) return;
            if (newParent != null && newParent.IsValid) return; // equipped, not dropped

            var raw = entity.EntityHandle.Raw;
            // Verify on the next frame: weapons removed by strips/cleanup are deleted, real drops stay in the world.
            Server.NextFrame(() =>
            {
                var w = new CHandle<CCSWeaponBase>(raw).Value;
                if (w == null || !w.IsValid) return;
                if (w.OwnerEntity.Value is { IsValid: true }) return; // re-parented / picked up again
                var owner = w.PrevOwner.Value?.OriginalController.Value;
                var p = J(owner);
                if (p == null) return;
                LogEvent(Lang.T("logger.dropped", p.Name, WeaponTable.ResolveClassname(w)), p, null, LogType.Item, false);
            });
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[Logger] parent change handler failed");
        }
    }

    [GameEventHandler]
    public HookResult OnItemPickupLog(EventItemPickup ev, GameEventInfo info)
    {
        var c = ev.Userid;
        var p = J(c);
        if (p == null || c == null) return HookResult.Continue;
        var classname = "";
        try
        {
            var def = (ushort)ev.Defindex;
            foreach (var w in c.Weapons())
            {
                if (w.AttributeManager.Item.ItemDefinitionIndex != def) continue;
                classname = WeaponTable.ResolveClassname(w);
                break;
            }
        }
        catch
        {
            // fall back to the event's item name
        }

        if (string.IsNullOrEmpty(classname))
            classname = WeaponTable.FindByDefIndex((ushort)ev.Defindex)?.Classname ?? "weapon_" + ev.Item;
        LogEvent(Lang.T("logger.picked_up", p.Name, classname), p, null, LogType.Item, false);
        return HookResult.Continue;
    }

    // ================================================================== map entities

    private static CCSPlayerController? ControllerOfPawn(CEntityInstance? ent)
    {
        if (ent == null || !ent.IsValid || ent.DesignerName != "player") return null;
        var pawn = ent.As<CCSPlayerPawn>();
        var c = pawn.OriginalController.Value;
        if (c is { IsValid: true }) return c;
        var bc = pawn.Controller.Value;
        return bc is { IsValid: true } ? bc.As<CCSPlayerController>() : null;
    }

    /// <summary>Activator → client, else the activator's m_hOwnerEntity if that is a client (original OnIn logic).</summary>
    private static CCSPlayerController? ResolveActivator(CEntityInstance? activator)
    {
        if (activator == null || !activator.IsValid) return null;
        var c = ControllerOfPawn(activator);
        if (c != null) return c;
        try
        {
            return ControllerOfPawn(activator.As<CBaseEntity>().OwnerEntity.Value);
        }
        catch
        {
            return null;
        }
    }

    private HookResult OnButtonPressed(CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay)
    {
        var c = ResolveActivator(activator);
        var p = J(c);
        if (p == null || c == null) return HookResult.Continue;
        var buttonName = caller?.Entity?.Name ?? "";
        var msg = Lang.T("logger.button_pressed", c.PlayerName, buttonName);
        LogEvent(msg, p, null, LogType.Use);
        if (c.Team == CsTeam.CounterTerrorist) BroadcastAdmins(msg);
        return HookResult.Continue;
    }

    private HookResult OnEntityTakeDamagePre(CBaseEntity entity, CTakeDamageInfo info)
    {
        try
        {
            if (entity == null || !entity.IsValid) return HookResult.Continue;
            var dn = entity.DesignerName;
            if (dn is "func_button" or "func_rot_button" or "momentary_rot_button") OnButtonDamaged(entity, info);
            else if (dn == "func_breakable") OnVentDamaged(entity, info);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[Logger] entity damage handler failed");
        }

        return HookResult.Continue;
    }

    private void OnButtonDamaged(CBaseEntity button, CTakeDamageInfo info)
    {
        var att = info.Attacker.Value;
        var c = ControllerOfPawn(att) ?? ControllerOfPawn(att?.OwnerEntity.Value);
        var p = J(c);
        if (p == null || c == null) return;
        var msg = Lang.T("logger.button_damaged", c.PlayerName, button.Entity?.Name ?? "");
        LogEvent(msg, p, null, LogType.Use);
        if (c.Team == CsTeam.CounterTerrorist) BroadcastAdmins(Lang.T("logger.admin_prefix", msg));
    }

    private void OnVentDamaged(CBaseEntity vent, CTakeDamageInfo info)
    {
        var att = info.Attacker.Value;
        var inf = info.Inflictor.Value;
        CCSPlayerController? owner;
        var attIsPawn = ControllerOfPawn(att) != null;
        if (attIsPawn)
            owner = inf == null || !inf.IsValid || inf.Handle == att!.Handle ? ControllerOfPawn(att) : ControllerOfPawn(inf.OwnerEntity.Value) ?? ControllerOfPawn(att);
        else
            owner = ControllerOfPawn(att?.OwnerEntity.Value);

        var p = J(owner);
        if (p == null || owner == null) return;
        var now = Server.EngineTime;
        if (_nextVentMessage[p.Slot] > now) return;
        _nextVentMessage[p.Slot] = now + 0.04;

        string weapon;
        var ability = info.Ability.Value;
        if (ability is { IsValid: true } && (ability.DesignerName ?? "").StartsWith("weapon_"))
            weapon = WeaponTable.ResolveClassname(ability.As<CBasePlayerWeapon>());
        else if (inf is { IsValid: true } && inf.DesignerName != "player")
            weapon = inf.DesignerName ?? "";
        else if (att is { IsValid: true } && att.DesignerName != "player")
            weapon = att.DesignerName ?? "";
        else
            weapon = Lang.T("logger.unknown_weapon");

        var ventName = vent.Entity?.Name ?? "";
        _lastHitVent = ventName;
        var msg = Lang.T("logger.vent_damaged", owner.PlayerName, weapon, ventName);
        LogEvent(msg, p, null, LogType.Break, false);
        if (owner.Team != CsTeam.CounterTerrorist) return;
        Hud.Set(owner, "logger.vent", Hud.Color(Hud.Escape(Lang.T("logger.vent_warning")), "#FF4040"), 90, 3f);
        BroadcastAdmins(Lang.T("logger.admin_prefix", msg));
    }

    private HookResult OnVentBreak(CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay)
    {
        var c = ControllerOfPawn(activator);
        var p = J(c);
        if (p == null || c == null) return HookResult.Continue;
        LogEvent(Lang.T("logger.vent_broke", c.PlayerName, _lastHitVent), p, null, LogType.Break);
        return HookResult.Continue;
    }

    private HookResult OnTeleport(CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay)
    {
        var c = ControllerOfPawn(activator);
        var p = J(c);
        if (p == null || c == null || caller == null || !caller.IsValid) return HookResult.Continue;
        var trigger = caller.As<CBaseEntity>();
        LogEvent(Lang.T("logger.teleport", c.PlayerName, trigger.Entity?.Name ?? "", trigger.Target ?? ""), p, null, LogType.Use, false);
        return HookResult.Continue;
    }

    private HookResult OnHurtEnter(CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay)
    {
        var c = ControllerOfPawn(activator);
        if (c == null || c.Slot is < 0 or >= Players.MaxSlots) return HookResult.Continue;
        _healthSample[c.Slot] = c.GetHp();
        if (_hurtsTouched[c.Slot]++ == 0) _anyHurtTouching++;
        return HookResult.Continue;
    }

    private HookResult OnHurtLeave(CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay)
    {
        var c = ControllerOfPawn(activator);
        if (c == null || c.Slot is < 0 or >= Players.MaxSlots) return HookResult.Continue;
        if (_hurtsTouched[c.Slot] <= 0) return HookResult.Continue;
        if (--_hurtsTouched[c.Slot] == 0) _anyHurtTouching = Math.Max(0, _anyHurtTouching - 1);
        return HookResult.Continue;
    }

    private void ClearHurt(int slot)
    {
        if (slot is < 0 or >= Players.MaxSlots || _hurtsTouched[slot] == 0) return;
        _hurtsTouched[slot] = 0;
        _anyHurtTouching = Math.Max(0, _anyHurtTouching - 1);
    }

    private void ResetHurtTracking()
    {
        Array.Clear(_hurtsTouched);
        _anyHurtTouching = 0;
    }

    private void OnTick()
    {
        if (_anyHurtTouching == 0) return;
        for (var slot = 0; slot < Players.MaxSlots; slot++)
        {
            if (_hurtsTouched[slot] == 0) continue;
            var p = Players.Get(slot);
            var c = p.Controller;
            if (c == null || !c.IsAliveP())
            {
                ClearHurt(slot);
                continue;
            }

            var hp = c.GetHp();
            if (_healthSample[slot] < hp)
                LogEvent(Lang.T("logger.healed", c.PlayerName, hp - _healthSample[slot]), p, null, LogType.Attack, false);
            _healthSample[slot] = hp;
        }
    }

    private static bool IsLogAdmin(CCSPlayerController c) => c.IsHLTV || OverrideAccess.Check(c, "sm_say", "@css/chat");

    private static void BroadcastAdmins(string message)
    {
        foreach (var c in Utilities.GetPlayers())
        {
            if (c is not { IsValid: true } || c.IsBot) continue;
            if (!IsLogAdmin(c)) continue;
            c.PrintToConsole(message);
        }
    }

    private void ApplyDamagePrint()
    {
        if (!Cfg.HideDamagePrint) return;
        try
        {
            var cv = ConVar.Find("sv_damage_print_enable");
            if (cv == null)
            {
                if (!_damagePrintMissingLogged) Logger.LogDebug("[Logger] sv_damage_print_enable does not exist in this CS2 build - skipped");
                _damagePrintMissingLogged = true;
                return;
            }

            if (cv.StringValue != "0" && !string.Equals(cv.StringValue, "false", StringComparison.OrdinalIgnoreCase))
                Server.ExecuteCommand("sv_damage_print_enable 0");
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[Logger] could not force sv_damage_print_enable");
        }
    }

    // ================================================================== !logs

    [ConsoleCommand("css_log", "sm_log <player> <num> - List the actions relating to <player> (last <num> lines).")]
    [ConsoleCommand("css_logs", "sm_log <player> <num> - List the actions relating to <player> (last <num> lines).")]
    [ConsoleCommand("sm_log", "sm_log <player> <num> - List the actions relating to <player> (last <num> lines).")]
    [ConsoleCommand("sm_logs", "sm_log <player> <num> - List the actions relating to <player> (last <num> lines).")]
    [RequiresPermissions("@css/ban")]
    public void OnLogsCommand(CCSPlayerController? player, CommandInfo info)
    {
        var lines = 0;
        JbPlayer? target = null;

        if (info.ArgCount > 1)
        {
            var arg = info.GetArg(1);
            var res = Targets.Find(player, arg, singleOnly: true, respectImmunity: false);
            if (res.Ok)
            {
                target = J(res.Players[0]);
            }
            else
            {
                // Original: FindTarget failed -> treat the argument as a line count; <= 0 stops.
                if (!int.TryParse(arg, out lines))
                {
                    Chat.Reply(player, info, res.Error ?? Lang.T("core.target.none"));
                    return;
                }

                if (lines <= 0) return;
            }

            if (info.ArgCount > 2)
                lines = int.TryParse(info.GetArg(2), out var n) ? n : 0;
            else if (target != null)
                lines = 0;
        }

        if (player != null && player.Team == CsTeam.CounterTerrorist && player.IsAliveP() && (target == null || target.IsAlive))
        {
            Chat.ReplyT(player, info, "logger.alive_ct");
            return;
        }

        if (target == null) Chat.ReplyT(player, info, "logger.printing");
        else Chat.ReplyT(player, info, "logger.printing_for", target.Name);

        var source = _logs[target == null ? 0 : target.Slot + 1];
        var start = lines > 0 ? Math.Max(0, source.Count - lines) : 0;
        var snapshot = source.Skip(start).Select(l => l.Text).ToList();

        Chat.PrintToConsole(player, " ");
        StreamLines(player?.Slot ?? -1, player?.SteamID ?? 0, snapshot, 0);
    }

    private void StreamLines(int slot, ulong steamId, List<string> lines, int offset)
    {
        CCSPlayerController? c = null;
        if (slot >= 0)
        {
            c = Utilities.GetPlayerFromSlot(slot);
            if (c is not { IsValid: true } || c.SteamID != steamId) return; // caller left
        }

        var perFrame = Math.Max(1, Cfg.LinesPerFrame);
        var end = Math.Min(lines.Count, offset + perFrame);
        for (var i = offset; i < end; i++)
        {
            if (c == null) Server.PrintToConsole(lines[i]);
            else c.PrintToConsole(lines[i]);
        }

        if (end < lines.Count) Server.NextFrame(() => StreamLines(slot, steamId, lines, end));
    }
}

/// <summary>SourceMod CheckCommandAccess(client, "override", FLAG) on top of CSS admin_overrides.json.</summary>
internal static class OverrideAccess
{
    public static bool Check(CCSPlayerController? player, string overrideName, string defaultFlag)
    {
        if (player == null) return true;
        if (!player.IsValid || player.IsBot) return false;
        try
        {
            if (AdminManager.CommandIsOverriden(overrideName))
            {
                var perms = AdminManager.GetPermissionOverrides(overrideName);
                if (perms.Length == 0) return true;
                return perms.Any(f => f.StartsWith('#') ? AdminManager.PlayerInGroup(player, f) : AdminManager.PlayerHasPermissions(player, f));
            }

            return AdminManager.PlayerHasPermissions(player, defaultFlag) || AdminManager.PlayerHasPermissions(player, "@css/root");
        }
        catch
        {
            return false;
        }
    }
}
