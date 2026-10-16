using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Admin;

/// <summary>SourceMod funcommands: beacon, timebomb, firebomb, freezebomb, freeze, burn, gravity, blind, noclip, drug.</summary>
public sealed partial class AdminModule
{
    private static bool ParseToggle(CommandInfo info, int index, bool current)
    {
        if (info.ArgCount <= index) return !current;
        var a = info.GetArg(index);
        return a is "1" or "on" or "true";
    }

    // ------------------------------------------------------------------ beacon

    [ConsoleCommand("css_beacon", "sm_beacon <#userid|name> [0/1] - toggle a beacon ring on a player")]
    public void CmdBeacon(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "slay")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!beacon <target> [0/1]"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        foreach (var c in t.Players)
        {
            var p = c.Jb()!;
            var on = ParseToggle(info, 2, Fxs(p).Beacon != null);
            SetBeacon(p, on);
        }

        Activity(caller, "admin.act.beacon", t.Description);
    }

    internal void SetBeacon(JbPlayer p, bool on)
    {
        var fx = Fxs(p);
        try { fx.Beacon?.Kill(); }
        catch { /* ignore */ }
        fx.Beacon = null;
        if (!on) return;
        var slot = p.Slot;
        fx.Beacon = Round.Timer(1.0f, () =>
        {
            var jp = Players.Get(slot);
            var c = jp.Controller;
            if (c == null || !c.IsAliveP())
            {
                try { Fxs(jp).Beacon?.Kill(); }
                catch { /* ignore */ }
                Fxs(jp).Beacon = null;
                return;
            }

            Fx.BeaconPulse(c, TeamColor(c), 10f, Cfg.BeaconRadius, 0.5f, Cfg.BeaconSound);
        }, repeat: true);
    }

    // ------------------------------------------------------------------ bombs

    private enum BombKind { Time, Fire, Freeze }

    [ConsoleCommand("css_timebomb", "sm_timebomb <#userid|name> [0/1]")]
    public void CmdTimeBomb(CCSPlayerController? caller, CommandInfo info) => BombCommand(caller, info, BombKind.Time);

    [ConsoleCommand("css_firebomb", "sm_firebomb <#userid|name> [0/1]")]
    public void CmdFireBomb(CCSPlayerController? caller, CommandInfo info) => BombCommand(caller, info, BombKind.Fire);

    [ConsoleCommand("css_freezebomb", "sm_freezebomb <#userid|name> [0/1]")]
    public void CmdFreezeBomb(CCSPlayerController? caller, CommandInfo info) => BombCommand(caller, info, BombKind.Freeze);

    private void BombCommand(CCSPlayerController? caller, CommandInfo info, BombKind kind)
    {
        if (!Require(caller, info, "slay")) return;
        var cmd = kind switch { BombKind.Time => "!timebomb", BombKind.Fire => "!firebomb", _ => "!freezebomb" };
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", $"{cmd} <target> [0/1]"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        foreach (var c in t.Players)
        {
            var p = c.Jb()!;
            var fx = Fxs(p);
            var current = kind switch { BombKind.Time => fx.TimeBomb, BombKind.Fire => fx.FireBomb, _ => fx.FreezeBomb } != null;
            SetBomb(p, kind, ParseToggle(info, 2, current));
        }

        Activity(caller, kind switch { BombKind.Time => "admin.act.timebomb", BombKind.Fire => "admin.act.firebomb", _ => "admin.act.freezebomb" }, t.Description);
    }

    private void SetBomb(JbPlayer p, BombKind kind, bool on)
    {
        var fx = Fxs(p);
        ref var timer = ref (kind == BombKind.Time ? ref fx.TimeBomb : ref (kind == BombKind.Fire ? ref fx.FireBomb : ref fx.FreezeBomb));
        try { timer?.Kill(); }
        catch { /* ignore */ }
        timer = null;
        var c0 = p.Controller;
        if (!on)
        {
            if (c0 != null && fx.Tinted) { c0.ResetColor(); fx.Tinted = false; }
            Hud.ClearGlobal($"admin.bomb.{p.Slot}");
            return;
        }

        var ticks = kind switch { BombKind.Time => Cfg.TimeBombTicks, BombKind.Fire => Cfg.FireBombTicks, _ => Cfg.FreezeBombTicks };
        fx.BombTicks = Math.Max(1, ticks);
        var slot = p.Slot;
        timer = Round.Timer(1.0f, () => BombTick(slot, kind, ticks), repeat: true);
    }

    private void BombTick(int slot, BombKind kind, int totalTicks)
    {
        var p = Players.Get(slot);
        var c = p.Controller;
        var fx = Fxs(p);
        if (c == null || !c.IsAliveP())
        {
            SetBomb(p, kind, false);
            return;
        }

        fx.BombTicks--;
        var origin = c.Origin();
        if (fx.BombTicks > 0)
        {
            var shade = fx.BombTicks > 1 ? (int)Math.Floor(fx.BombTicks * (128.0 / totalTicks)) : 0;
            var color = kind switch
            {
                BombKind.Time => Color.FromArgb(255, 255, 128, shade),
                BombKind.Fire => Color.FromArgb(255, 255, shade, shade),
                _ => Color.FromArgb(255, shade, shade, 255)
            };
            c.SetColor(color);
            fx.Tinted = true;
            Sound.PlayAt(c, fx.BombTicks > 1 ? Cfg.BombBeepSound : Cfg.BombFinalSound);
            var key = kind switch { BombKind.Time => "admin.bomb.time_hud", BombKind.Fire => "admin.bomb.fire_hud", _ => "admin.bomb.freeze_hud" };
            Hud.SetGlobal($"admin.bomb.{slot}", Lang.T(key, Hud.Escape(c.PlayerName), fx.BombTicks), 100, 1.5f);
            if (origin != null)
            {
                var radius = kind switch { BombKind.Time => Cfg.TimeBombRadius, BombKind.Fire => Cfg.FireBombRadius, _ => Cfg.FreezeBombRadius };
                Fx.Ring(new Vector(origin.X, origin.Y, origin.Z + 10f), radius / 3f, Color.White, 3f, 0.6f, 16);
            }

            return;
        }

        // Detonate.
        SetBomb(p, kind, false);
        if (fx.Tinted) { c.ResetColor(); fx.Tinted = false; }
        if (origin == null) return;
        var pos = new Vector(origin.X, origin.Y, origin.Z + 30f);
        var mode = kind switch { BombKind.Time => Cfg.TimeBombMode, BombKind.Fire => Cfg.FireBombMode, _ => Cfg.FreezeBombMode };
        var blast = kind switch { BombKind.Time => Cfg.TimeBombRadius, BombKind.Fire => Cfg.FireBombRadius, _ => Cfg.FreezeBombRadius };
        switch (kind)
        {
            case BombKind.Time:
                Fx.Particle(Cfg.ExplosionParticle, pos, 4f);
                Sound.PlayAt(c, Cfg.BombExplodeSound);
                c.Kill();
                if (mode > 0)
                    foreach (var other in Players.Alive().Where(o => o.Slot != slot && (mode == 2 || o.Team == c.Team)))
                    {
                        var d = other.Origin() is { } op ? VectorUtil.Distance(op, origin) : float.MaxValue;
                        if (d > blast) continue;
                        var dmg = (int)Math.Floor(220 * (blast - d) / blast);
                        if (dmg > 0) other.Slap(dmg);
                    }

                break;
            case BombKind.Fire:
                Fx.Ring(pos, blast, Color.OrangeRed, 6f, 0.8f, 24);
                Burn(c, Cfg.BurnDuration);
                if (mode > 0)
                    foreach (var other in Players.Alive().Where(o => o.Slot != slot && (mode == 2 || o.Team == c.Team)))
                        if (other.Origin() is { } op && VectorUtil.Distance(op, origin) <= blast)
                            Burn(other, Cfg.BurnDuration);
                break;
            case BombKind.Freeze:
                Fx.Ring(pos, blast, Color.DeepSkyBlue, 6f, 0.8f, 24);
                Freeze(c, Cfg.FreezeDuration);
                if (mode > 0)
                    foreach (var other in Players.Alive().Where(o => o.Slot != slot && (mode == 2 || o.Team == c.Team)))
                        if (other.Origin() is { } op && VectorUtil.Distance(op, origin) <= blast)
                            Freeze(other, Cfg.FreezeDuration);
                break;
        }
    }

    // ------------------------------------------------------------------ freeze

    [ConsoleCommand("css_freeze", "sm_freeze <#userid|name> [time]")]
    public void CmdFreeze(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "slay")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!freeze <target> [seconds]"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        var seconds = Cfg.FreezeDuration;
        if (info.ArgCount > 2 && float.TryParse(info.GetArg(2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)) seconds = Math.Clamp(s, 1f, 120f);
        foreach (var c in t.Players) Freeze(c, seconds);
        Activity(caller, "admin.act.freeze", t.Description, (int)seconds);
    }

    [ConsoleCommand("css_unfreeze", "Unfreeze a player")]
    public void CmdUnfreeze(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "slay")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!unfreeze <target>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        foreach (var c in t.Players) Unfreeze(c);
        Activity(caller, "admin.act.unfreeze", t.Description);
    }

    internal void Freeze(CCSPlayerController c, float seconds)
    {
        var p = c.Jb();
        if (p == null || !c.IsAliveP()) return;
        var fx = Fxs(p);
        try { fx.Freeze?.Kill(); }
        catch { /* ignore */ }
        c.Freeze();
        c.SetColor(Color.FromArgb(192, 0, 128, 255));
        fx.Frozen = true;
        fx.Tinted = true;
        var slot = p.Slot;
        fx.Freeze = Round.Timer(seconds, () =>
        {
            var jp = Players.Get(slot);
            var ctrl = jp.Controller;
            Fxs(jp).Freeze = null;
            if (ctrl != null) Unfreeze(ctrl);
        });
    }

    internal void Unfreeze(CCSPlayerController c)
    {
        var p = c.Jb();
        if (p == null) return;
        var fx = Fxs(p);
        try { fx.Freeze?.Kill(); }
        catch { /* ignore */ }
        fx.Freeze = null;
        if (c.IsAliveP())
        {
            c.Unfreeze();
            if (fx.Tinted) c.ResetColor();
        }

        fx.Frozen = false;
        fx.Tinted = false;
    }

    // ------------------------------------------------------------------ burn

    [ConsoleCommand("css_burn", "sm_burn <#userid|name> [time]")]
    public void CmdBurn(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "slay")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!burn <target> [seconds]"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        var seconds = Cfg.BurnDuration;
        if (info.ArgCount > 2 && float.TryParse(info.GetArg(2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)) seconds = Math.Clamp(s, 0.5f, 20f);
        foreach (var c in t.Players) Burn(c, seconds);
        Activity(caller, "admin.act.burn", t.Description, (int)seconds);
    }

    /// <summary>
    /// CS2 has no IgniteEntity: burning = real damage over time + a flame particle parented to the pawn + orange tint.
    /// </summary>
    internal void Burn(CCSPlayerController c, float seconds)
    {
        var p = c.Jb();
        var pawn = c.PawnOrNull();
        if (p == null || pawn == null || !c.IsAliveP()) return;
        var fx = Fxs(p);
        try { fx.Burn?.Kill(); }
        catch { /* ignore */ }
        Fx.SafeRemove(fx.BurnFx);
        if (pawn.AbsOrigin is { } o)
        {
            fx.BurnFx = Fx.Particle(Cfg.BurnParticle, new Vector(o.X, o.Y, o.Z + 30f), seconds + 0.5f);
            fx.BurnFx?.AcceptInput("SetParent", pawn, pawn, "!activator");
        }

        c.SetColor(Color.FromArgb(255, 255, 140, 40));
        fx.Tinted = true;
        var slot = p.Slot;
        var ticksLeft = (int)Math.Ceiling(seconds * 2);
        var dmg = Math.Max(1, Cfg.BurnDamagePerSecond / 2);
        fx.Burn = Round.Timer(0.5f, () =>
        {
            var jp = Players.Get(slot);
            var ctrl = jp.Controller;
            var f = Fxs(jp);
            if (ctrl == null || !ctrl.IsAliveP() || --ticksLeft <= 0)
            {
                try { f.Burn?.Kill(); }
                catch { /* ignore */ }
                f.Burn = null;
                Fx.SafeRemove(f.BurnFx);
                f.BurnFx = null;
                if (ctrl != null && ctrl.IsAliveP() && !f.Frozen) { ctrl.ResetColor(); f.Tinted = false; }
                return;
            }

            var hp = ctrl.GetHp() - dmg;
            if (hp <= 0) ctrl.Kill();
            else ctrl.SetHp(hp, false);
        }, repeat: true);
    }

    // ------------------------------------------------------------------ gravity / noclip

    [ConsoleCommand("css_gravity", "sm_gravity <#userid|name> [amount] - leave amount off to reset (0.0 - 5.0)")]
    public void CmdGravity(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "slay")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!gravity <target> [0.0-5.0]"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        var amount = 1f;
        if (info.ArgCount > 2 && !float.TryParse(info.GetArg(2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out amount))
        {
            Chat.ReplyT(caller, info, "admin.invalid_amount");
            return;
        }

        amount = Math.Clamp(amount, 0f, 5f);
        foreach (var c in t.Players)
        {
            if (Math.Abs(amount - 1f) < 0.001f) Movement.ClearGravity(c, "admin.gravity");
            else Movement.SetGravity(c, "admin.gravity", amount);
        }

        Activity(caller, "admin.act.gravity", t.Description, amount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
    }

    [ConsoleCommand("css_noclip", "sm_noclip <#userid|name> - toggle noclip")]
    public void CmdNoclip(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "noclip")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!noclip <target>"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        foreach (var c in t.Players) c.SetNoclip(!c.IsNoclip());
        Activity(caller, "admin.act.noclip", t.Description);
    }

    // ------------------------------------------------------------------ blind / drug

    [ConsoleCommand("css_blind", "sm_blind <#userid|name> [amount 0-255] - leave amount off to reset")]
    public void CmdBlind(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "slay")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!blind <target> [0-255]"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        var amount = 0;
        if (info.ArgCount > 2 && !int.TryParse(info.GetArg(2), out amount)) { Chat.ReplyT(caller, info, "admin.invalid_amount"); return; }
        amount = Math.Clamp(amount, 0, 255);
        foreach (var c in t.Players) Blind(c, amount);
        Activity(caller, "admin.act.blind", t.Description, amount);
    }

    internal void Blind(CCSPlayerController c, int alpha)
    {
        var p = c.Jb();
        if (p == null) return;
        var fx = Fxs(p);
        try { fx.Blind?.Kill(); }
        catch { /* ignore */ }
        fx.Blind = null;
        fx.BlindAlpha = alpha;
        if (alpha <= 0)
        {
            Fx.ClearFade(c);
            return;
        }

        if (!Cfg.BlindUseFlash)
        {
            Fx.Fade(c, Color.FromArgb(alpha, 0, 0, 0), 0.5f, 0f, Fx.FadeOut | Fx.FadeStayOut | Fx.FadePurge);
            return;
        }

        // Flashbang overlay (method confirmed working on the owner's server): refresh before it fades.
        var slot = p.Slot;
        void Apply()
        {
            var ctrl = Players.Get(slot).Controller;
            var pawn = ctrl?.PawnOrNull();
            if (pawn == null) return;
            pawn.FlashMaxAlpha = alpha;
            pawn.FlashDuration = 5f;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawnBase", "m_flFlashMaxAlpha");
            Utilities.SetStateChanged(pawn, "CCSPlayerPawnBase", "m_flFlashDuration");
        }

        Apply();
        fx.Blind = Round.Timer(3f, Apply, repeat: true);
    }

    [ConsoleCommand("css_drug", "sm_drug <#userid|name> [0/1]")]
    public void CmdDrug(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "slay")) return;
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "!drug <target> [0/1]"); return; }
        var t = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true);
        if (t == null) return;
        foreach (var c in t.Players)
        {
            var p = c.Jb()!;
            SetDrug(c, ParseToggle(info, 2, Fxs(p).Drug != null));
        }

        Activity(caller, "admin.act.drug", t.Description);
    }

    /// <summary>
    /// SourceMod's drug rolled the view angle and flashed random screen colours every second. CS2 servers cannot
    /// roll the client camera, so the port uses random colour fades + screen shake + FOV wobble (all real effects).
    /// </summary>
    internal void SetDrug(CCSPlayerController c, bool on)
    {
        var p = c.Jb();
        if (p == null) return;
        var fx = Fxs(p);
        try { fx.Drug?.Kill(); }
        catch { /* ignore */ }
        fx.Drug = null;
        if (!on)
        {
            Fx.ClearFade(c);
            Fx.SetFov(c, 0);
            return;
        }

        var slot = p.Slot;
        fx.Drug = Round.Timer(1.0f, () =>
        {
            var ctrl = Players.Get(slot).Controller;
            if (ctrl == null || !ctrl.IsAliveP())
            {
                var f = Fxs(Players.Get(slot));
                try { f.Drug?.Kill(); }
                catch { /* ignore */ }
                f.Drug = null;
                return;
            }

            var rnd = Random.Shared;
            Fx.Fade(ctrl, Color.FromArgb(128, rnd.Next(256), rnd.Next(256), rnd.Next(256)), 0.8f, 0f, Fx.FadeIn | Fx.FadePurge);
            Fx.Shake(ctrl, 6f, 2.5f, 1.0f);
            Fx.SetFov(ctrl, rnd.Next(70, 121));
        }, repeat: true);
    }
}
