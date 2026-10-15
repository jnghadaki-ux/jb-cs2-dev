using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Commands;

/// <summary>Section "Commands" - UltJB_Command_* plugins (CTBan lives in the Teams module).</summary>
public sealed class CommandsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>UltJB_Command_CTMessage MESSAGE_DELAY.</summary>
    public float GuardMessageDelay { get; set; } = 2f;
    public string GuardMessageSound { get; set; } = "UIPanorama.generic_button_press";

    /// <summary>UltJB_Command_Freekill FREEKILL_USAGE_DELAY.</summary>
    public float FreekillDelay { get; set; } = 30f;
    public string FreekillSound { get; set; } = "UIPanorama.round_report_round_lost";

    /// <summary>UltJB_Command_Roll wait between rolls.</summary>
    public float RollDelay { get; set; } = 1f;

    /// <summary>jailbreak_rules_url / jailbreak_definitions_url. CS2 has no in-game web page viewer: the link is printed.</summary>
    public string RulesUrl { get; set; } = "";
    public string DefinitionsUrl { get; set; } = "";

    public int TimerMin { get; set; } = 10;
    public int TimerMax { get; set; } = 180;
    public string TimerCompleteSound { get; set; } = "UIPanorama.round_report_round_won";

    /// <summary>!fkslay: also respawn the freekilled prisoner where they died.</summary>
    public bool FkSlayRespawn { get; set; } = true;
}

internal sealed class CommandsState : IRoundScoped
{
    public bool DiedThisRound;
    public string KilledBy = "";

    /// <summary>Slot / SteamID of the player who killed this one this round (-1 / 0 = world, suicide or unknown).</summary>
    public int KillerSlot = -1;
    public ulong KillerSteam;
    public bool KillerWasGuard;
    public float NextFreekill;
    public float DeathTime;
    public Vector? LastPos;
    public QAngle? LastAngles;
    public float NextGuardMessage;
    public float NextRoll;

    public void ResetForRound()
    {
        DiedThisRound = false;
        KilledBy = "";
        KillerSlot = -1;
        KillerSteam = 0;
        KillerWasGuard = false;
        NextFreekill = 0;
    }
}

/// <summary>
/// UltJB_Command_CTMessage (!g), Freekill (!freekill, !whokilled/!wk), OneUp (!1up, !xup), Roll (!roll),
/// Rules (!jbrules, !definitions/!defs), Speed (!speed), Timer (!timer, !stoptimer).
/// </summary>
public sealed class CommandsModule : JbModule
{
    private CommandsConfig Cfg => Section<CommandsConfig>("Commands");

    public override string Name => "Commands";
    public override int Order => 80;
    public override bool Enabled => Cfg.Enabled;

    private Timer? _countdown;
    private int _countdownTotal;
    private int _countdownElapsed;
    private int _countdownOwnerSlot = -1;


    private static CommandsState S(JbPlayer p) => p.Data<CommandsState>();

    public override void OnLoad(bool hotReload)
    {
        Plugin.AddCommandListener("say", OnSay);
        Plugin.AddCommandListener("say_team", OnSay);
        Plugin.AddTimer(0.3f, TrackPositions, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
        JbEvents.WardenDeath += OnWardenDeath;
        JbEvents.DayStarted += (_, _, _) => StopCountdown(Lang.T("cmd.timer.stopped_day"));
    }

    public override void OnUnload(bool hotReload)
    {
        StopCountdown(null);
    }

    // ------------------------------------------------------------------ lifecycle

    public override void OnRoundEnd(CsTeam winner, int reason) => StopCountdown(null);

    public override void OnPlayerSpawn(JbPlayer player)
    {
        S(player).DiedThisRound = false;
    }

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        var s = S(victim);
        s.DiedThisRound = true;
        s.DeathTime = (float)Server.EngineTime;
        s.KilledBy = attacker?.Name ?? "world";
        var real = attacker != null && !ReferenceEquals(attacker, victim);
        s.KillerSlot = real ? attacker!.Slot : -1;
        s.KillerSteam = real ? attacker!.SteamId64 : 0;
        s.KillerWasGuard = real && attacker!.IsGuard;
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        if (player.Slot == _countdownOwnerSlot) _countdownOwnerSlot = -1;
    }

    /// <summary>OneUp records non-ducking positions every 0.3 s.</summary>
    private void TrackPositions()
    {
        foreach (var c in Players.Alive())
        {
            var pawn = c.PawnOrNull();
            if (pawn?.AbsOrigin == null) continue;
            if ((pawn.Flags & (uint)PlayerFlags.FL_DUCKING) != 0) continue;
            var s = S(c.Jb()!);
            s.LastPos = VectorUtil.Copy(pawn.AbsOrigin);
            var a = pawn.EyeAngles;
            s.LastAngles = new QAngle(a.X, a.Y, a.Z);
        }
    }

    // ------------------------------------------------------------------ chat interception (hide "!g ..." / "!freekill ..." like the original)

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return HookResult.Continue;
        var text = info.GetArg(1).Trim();
        if (text.StartsWith("!g ", StringComparison.OrdinalIgnoreCase))
        {
            GuardMessage(player, text[3..].Trim());
            return HookResult.Handled;
        }

        if (text.StartsWith("!freekill ", StringComparison.OrdinalIgnoreCase))
        {
            Freekill(player, text[10..].Trim());
            return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    // ------------------------------------------------------------------ !g (message the guards)

    [ConsoleCommand("css_g", "sm_g <message> - message the guard team")]
    public void CmdG(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        GuardMessage(caller, info.ArgString.Trim().Trim('"'));
    }

    private void GuardMessage(CCSPlayerController caller, string message)
    {
        if (!caller.IsAliveP()) { Chat.Tell(caller, "core.only_alive"); return; }
        if (caller.Team == CsTeam.CounterTerrorist) { Chat.Tell(caller, "cmd.g.use_teamchat"); return; }
        if (message.Length < 1) { Chat.Tell(caller, "core.usage", "!g <message>"); return; }
        var s = S(caller.Jb()!);
        var now = (float)Server.EngineTime;
        if (s.NextGuardMessage > now) return;
        s.NextGuardMessage = now + Cfg.GuardMessageDelay;
        var nameColor = caller.Team == CsTeam.Terrorist ? "{lightred}" : "{purple}";
        var line = Lang.T("cmd.g.line", nameColor + caller.PlayerName, message);
        foreach (var c in Players.All())
        {
            if (c.Slot != caller.Slot && !c.IsBot && !Perms.Has(c, "chat") && c.Team != CsTeam.CounterTerrorist) continue;
            Chat.Print(c, line, false);
            Sound.Play(c, Cfg.GuardMessageSound);
        }
    }

    // ------------------------------------------------------------------ freekill

    [ConsoleCommand("css_freekill", "sm_freekill <msg> - let admins know you were freekilled")]
    public void CmdFreekill(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        Freekill(caller, info.ArgString.Trim().Trim('"'));
    }

    private void Freekill(CCSPlayerController caller, string message)
    {
        var p = caller.Jb();
        if (p == null) return;
        var s = S(p);
        if (message.Length < 1) { Chat.Tell(caller, "core.usage", "!freekill <message explaining the freekill>"); return; }
        if (!s.DiedThisRound || caller.IsAliveP()) { Chat.Tell(caller, "cmd.freekill.not_died"); return; }
        var now = (float)Server.EngineTime;
        if (s.NextFreekill > now) { Chat.Tell(caller, "cmd.freekill.too_soon"); return; }
        if (Perms.Has(caller, "chat")) { Chat.Tell(caller, "cmd.freekill.admin"); return; }
        if (caller.Team == CsTeam.CounterTerrorist) { Chat.Tell(caller, "cmd.freekill.guard"); return; }
        s.NextFreekill = now + Cfg.FreekillDelay;
        var head = Lang.T("cmd.freekill.line", caller.PlayerName, s.KilledBy);
        var body = Lang.T("cmd.freekill.message", message);
        foreach (var c in Players.All())
        {
            if (c.Slot != caller.Slot && !Perms.Has(c, "chat") && c.Team != CsTeam.CounterTerrorist && !c.IsBot) continue;
            Chat.Print(c, head, false);
            Chat.Print(c, body, false);
            if (s.KillerWasGuard && Perms.Has(c, "slay")) Chat.Print(c, Lang.T("cmd.fkslay.hint", caller.PlayerName), false);
            Sound.Play(c, Cfg.FreekillSound);
        }

        Jb.Log.LogEvent($"{caller.PlayerName} reported a freekill by {s.KilledBy}: {message}", p, null, LogType.Admin);
    }

    [ConsoleCommand("css_whokilled", "sm_whokilled <who> - check who killed a player")]
    [ConsoleCommand("css_wk", "sm_wk <who> - check who killed a player")]
    public void CmdWhoKilled(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "slay")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!whokilled <target>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true, respectImmunity: false);
        if (t == null) return;
        var target = t.Players[0];
        var s = S(target.Jb()!);
        if (!s.DiedThisRound || target.IsAliveP()) { Chat.ReplyT(caller, info, "cmd.whokilled.not_died", target.PlayerName); return; }
        Chat.ReplyT(caller, info, "cmd.whokilled.result", target.PlayerName, s.KilledBy);
    }

    // ------------------------------------------------------------------ fkslay

    [ConsoleCommand("css_fkslay", "sm_fkslay <prisoner> - slay the guard who freekilled a prisoner and respawn the prisoner")]
    public void CmdFkSlay(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "slay")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!fkslay <prisoner>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true, respectImmunity: false);
        if (t == null) return;
        var victim = t.Players[0];
        var vp = victim.Jb();
        if (vp == null) return;
        var s = S(vp);
        if (!s.DiedThisRound || victim.IsAliveP()) { Chat.ReplyT(caller, info, "cmd.whokilled.not_died", victim.PlayerName); return; }
        if (victim.Team != CsTeam.Terrorist) { Chat.ReplyT(caller, info, "cmd.fkslay.not_prisoner", victim.PlayerName); return; }
        if (!s.KillerWasGuard || s.KillerSlot < 0) { Chat.ReplyT(caller, info, "cmd.fkslay.no_guard", victim.PlayerName, s.KilledBy); return; }

        // The killer must still be the same player (the slot may have been reused after a disconnect).
        var killer = Players.Get(s.KillerSlot);
        var kc = killer.Controller;
        if (!killer.Connected || killer.SteamId64 != s.KillerSteam || kc == null || !kc.IsValid)
        {
            Chat.ReplyT(caller, info, "cmd.fkslay.killer_gone", s.KilledBy);
            if (Cfg.FkSlayRespawn) OneUp(victim, caller, "cmd.oneup.done");
            return;
        }

        var admin = caller?.PlayerName ?? "Console";
        if (kc.IsAliveP()) kc.Kill();
        Chat.TellAll("cmd.fkslay.done", admin, kc.PlayerName, victim.PlayerName);
        Jb.Log.LogEvent($"{admin} slayed {kc.PlayerName} for freekilling {victim.PlayerName}", caller.Jb(), killer, LogType.Admin);

        // Clear the record so one freekill cannot be punished twice.
        s.KillerSlot = -1;
        s.KillerSteam = 0;
        s.KillerWasGuard = false;
        if (Cfg.FkSlayRespawn) OneUp(victim, caller, "cmd.oneup.done");
    }

    // ------------------------------------------------------------------ 1up / xup

    [ConsoleCommand("css_1up", "sm_1up <target> - respawn a player where they died")]
    public void CmdOneUp(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "kick")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!1up <target>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), singleOnly: true);
        if (t == null) return;
        if (!OneUp(t.Players[0], caller, "cmd.oneup.done"))
            Chat.ReplyT(caller, info, "cmd.whokilled.not_died", t.Players[0].PlayerName);
    }

    [ConsoleCommand("css_xup", "sm_xup <seconds> - respawn prisoners that died in the last X seconds")]
    public void CmdXUp(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "kick")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (info.ArgCount < 2 || !int.TryParse(info.GetArg(1), out var seconds) || seconds < 1) { Chat.ReplyT(caller, info, "core.usage", "!xup <seconds>"); return; }
        var now = (float)Server.EngineTime;
        var any = false;
        foreach (var c in Players.Prisoners().Where(c => !c.IsAliveP()).ToList())
        {
            var s = S(c.Jb()!);
            if (!s.DiedThisRound || now - seconds > s.DeathTime) continue;
            any |= OneUp(c, caller, "cmd.xup.done");
        }

        if (!any) Chat.ReplyT(caller, info, "cmd.xup.none", seconds);
    }

    private bool OneUp(CCSPlayerController target, CCSPlayerController? admin, string doneKey)
    {
        var p = target.Jb();
        if (p == null) return false;
        var s = S(p);
        if (!s.DiedThisRound || target.IsAliveP() || target.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist)) return false;
        s.DiedThisRound = false;
        var pos = s.LastPos;
        var ang = s.LastAngles;
        target.RespawnSolid(c =>
        {
            if (pos != null) c.TeleportTo(pos, ang);
        });
        Chat.TellAll(doneKey, target.PlayerName, admin?.PlayerName ?? "Console");
        Jb.Log.LogEvent($"{admin?.PlayerName ?? "Console"} one-upped {target.PlayerName}", admin.Jb(), p, LogType.Admin);
        return true;
    }

    // ------------------------------------------------------------------ roll

    [ConsoleCommand("css_roll", "sm_roll [max] - warden rolls a random number")]
    public void CmdRoll(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        if (!Jb.Warden.IsWarden(caller)) { Chat.Tell(caller, "cmd.roll.not_warden"); return; }
        var s = S(caller.Jb()!);
        var now = (float)Server.EngineTime;
        if (now < s.NextRoll) { Chat.Tell(caller, "cmd.roll.wait"); return; }
        s.NextRoll = now + Cfg.RollDelay;
        var max = 100;
        if (info.ArgCount > 1 && int.TryParse(info.GetArg(1), out var m)) max = Math.Max(1, m);
        Chat.TellAll("cmd.roll.result", caller.PlayerName, Random.Shared.Next(1, max + 1), max);
    }

    // ------------------------------------------------------------------ rules / definitions

    [ConsoleCommand("css_jbrules", "sm_jbrules - jailbreak rules link")]
    public void CmdRules(CCSPlayerController? caller, CommandInfo info) => ShowLink(caller, info, Cfg.RulesUrl, "cmd.rules.title");

    [ConsoleCommand("css_definitions", "sm_definitions - jailbreak definitions link")]
    [ConsoleCommand("css_defs", "sm_defs - jailbreak definitions link")]
    public void CmdDefinitions(CCSPlayerController? caller, CommandInfo info) => ShowLink(caller, info, Cfg.DefinitionsUrl, "cmd.definitions.title");

    private static void ShowLink(CCSPlayerController? caller, CommandInfo info, string url, string titleKey)
    {
        if (string.IsNullOrWhiteSpace(url)) { Chat.ReplyT(caller, info, "cmd.link.not_set"); return; }
        Chat.ReplyT(caller, info, "cmd.link.show", Lang.T(titleKey), url);
        Chat.PrintToConsole(caller, $"{Colors.Strip(Lang.T(titleKey))}: {url}");
    }

    // ------------------------------------------------------------------ speed

    [ConsoleCommand("css_speed", "sm_speed <target> <0.0 to 10.0> - set a player's speed")]
    public void CmdSpeed(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "kick")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (info.ArgCount < 3 || !float.TryParse(info.GetArg(2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 0f || value > 10f)
        {
            Chat.ReplyT(caller, info, "core.usage", "!speed <target> <0.0 to 10.0>");
            return;
        }

        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        foreach (var c in t.Players)
        {
            if (Math.Abs(value - 1f) < 0.001f) Movement.ClearSpeed(c, "cmd.speed");
            else Movement.SetSpeed(c, "cmd.speed", value);
            Chat.TellAll("cmd.speed.set", c.PlayerName, value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), caller?.PlayerName ?? "Console");
        }

        Jb.Log.LogEvent($"{caller?.PlayerName ?? "Console"} set speed of {t.Description} to {value:0.00}", caller.Jb(), null, LogType.Admin);
    }

    // ------------------------------------------------------------------ warden countdown timer

    [ConsoleCommand("css_timer", "sm_timer <seconds> - warden countdown timer")]
    public void CmdTimer(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        if (!Jb.Warden.IsWarden(caller)) { Chat.Tell(caller, "cmd.timer.not_warden"); return; }
        if (Jb.Days.IsInProgress) { Chat.Tell(caller, "cmd.timer.day"); return; }
        if (info.ArgCount < 2) { Chat.Tell(caller, "cmd.timer.enter_time"); return; }
        if (!int.TryParse(info.GetArg(1), out var seconds) || seconds <= 0) { Chat.Tell(caller, "cmd.timer.invalid"); return; }
        seconds = Math.Clamp(seconds, Cfg.TimerMin, Cfg.TimerMax);
        StopCountdown(null);
        _countdownTotal = seconds;
        _countdownElapsed = 0;
        _countdownOwnerSlot = caller.Slot;
        ShowCountdown();
        Chat.TellAll("cmd.timer.started", seconds);
        _countdown = Round.Timer(1.0f, CountdownTick, repeat: true);
    }

    [ConsoleCommand("css_stoptimer", "sm_stoptimer - stop the countdown timer")]
    public void CmdStopTimer(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        if (!Jb.Warden.IsWarden(caller)) { Chat.Tell(caller, "cmd.timer.not_warden_stop"); return; }
        if (_countdown == null) { Chat.Tell(caller, "cmd.timer.none"); return; }
        StopCountdown(null);
        Chat.TellAll("cmd.timer.stopped");
    }

    private void ShowCountdown() =>
        Hud.SetGlobal("cmd.timer", Lang.T("cmd.timer.hud", _countdownTotal - _countdownElapsed), 90, 2f);

    private void CountdownTick()
    {
        _countdownElapsed++;
        if (Jb.LastRequests.CanLastRequest) { StopCountdown(Lang.T("cmd.timer.stopped_lr")); return; }
        if (Jb.Days.IsInProgress) { StopCountdown(Lang.T("cmd.timer.stopped_day")); return; }
        var left = _countdownTotal - _countdownElapsed;
        if (left % 10 == 0 && left > 0) Chat.TellAll("cmd.timer.left", left);
        if (left > 0)
        {
            ShowCountdown();
            return;
        }

        var t = _countdown;
        _countdown = null;
        try { t?.Kill(); }
        catch { /* ignore */ }
        Sound.PlayAll(Cfg.TimerCompleteSound);
        Hud.SetGlobal("cmd.timer", Lang.T("cmd.timer.complete"), 90, 3f);
    }

    private void StopCountdown(string? hudMessage)
    {
        if (_countdown == null) return;
        var t = _countdown;
        _countdown = null;
        try { t.Kill(); }
        catch { /* ignore */ }
        if (hudMessage != null) Hud.SetGlobal("cmd.timer", hudMessage, 90, 3f);
        else Hud.ClearGlobal("cmd.timer");
    }

    private void OnWardenDeath(JbPlayer warden)
    {
        if (_countdown != null && warden.Slot == _countdownOwnerSlot) StopCountdown(null);
    }
}
