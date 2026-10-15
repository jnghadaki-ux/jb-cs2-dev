using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Warden;

/// <summary>
/// Warden in-world markers from UltJB_Warden.sp: pointer laser (double-tap USE) with pulse ring, bullet history,
/// bullet line (triple-tap walk), gesture ring (triple-tap duck) and warden paintballs. Everything is drawn with
/// real beam entities. Time base = engine time, as in the original (GetEngineTime). CounterStrikeSharp 1.0.368 has no
/// ray trace, so the pointer marks the warden's ping (stock CS2 ping key) or last bullet impact instead of following
/// the crosshair live.
/// </summary>
public sealed partial class WardenModule
{
    // ---- pointer ----
    private double _ptrStartExpire;
    private double _ptrButtonExpire;
    private CBeam? _ptrBeam;
    private Vector? _ptrEnd;
    private bool _ptrNoTraceWarned;

    private readonly PulseRings _pulses = new();

    // ---- bullet history (global, NOT reset on warden change - original) ----
    private readonly Vector[] _saved = { new(0, 0, 0), new(0, 0, 0) };
    private readonly Vector[] _pulled = { new(0, 0, 0), new(0, 0, 0) };
    private int _savedCount;
    private double _savedTime;
    private double _nextBulletSave;
    private double _nextPaint;

    // ---- gestures / line ----
    private int _ringTaps;
    private double _ringExpire;
    private int _lineTaps;
    private double _lineExpire;
    private Vector? _lineA;
    private Vector? _lineB;
    private CBeam? _lineBeam;
    private float _nextLineCheck;

    // ---- paintballs ----
    private readonly Queue<CEntityInstance?[]> _splats = new();

    private static readonly Color[] PaintColors =
    {
        Color.FromArgb(255, 0, 128, 255), // splat_blue
        Color.FromArgb(255, 0, 220, 0),   // splat_green
        Color.FromArgb(255, 255, 0, 170)  // splat_pink
    };

    private bool MenuEatsUse(CCSPlayerController c) => MenuService.UsesKeys && MenuService.HasMenu(c);

    // ================================================================= input

    private void OnButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        if (pressed == 0 || player == null || !player.IsValid) return;
        var w = Warden;
        if (w == null || w.Slot != player.Slot || !w.IsAlive) return;
        var now = Server.EngineTime;

        if (Cfg.PointerEnabled && (pressed & PlayerButtons.Use) != 0 && !MenuEatsUse(player))
            PointerFreshPress(player, now);

        if (!Cfg.GesturesEnabled) return;
        if ((pressed & PlayerButtons.Duck) != 0) RingTap(w, now);
        else if ((pressed & (PlayerButtons.Speed | PlayerButtons.Walk)) != 0) LineTap(now);
    }

    private void OnTick()
    {
        _pulses.Tick();
        PointerTick();
        LineTick();
        BonusTick();
    }

    // ================================================================= pointer laser

    private double PointerExpire => Math.Max(0.1, Cfg.PointerExpire);

    private void ResetPointerState()
    {
        _ptrStartExpire = 0;
        _ptrButtonExpire = 0;
        _ptrEnd = null;
        _ptrNoTraceWarned = false;
        Fx.SafeRemove(_ptrBeam);
        _ptrBeam = null;
    }

    private void StopPointer()
    {
        _ptrButtonExpire = 0;
        Fx.SafeRemove(_ptrBeam);
        _ptrBeam = null;
    }

    /// <summary>CheckBeamCreation_Pointer for a fresh USE press.</summary>
    private void PointerFreshPress(CCSPlayerController c, double now)
    {
        if (now > _ptrButtonExpire)
        {
            if (now > _ptrStartExpire)
            {
                _ptrStartExpire = now + PointerExpire; // first tap
                return;
            }

            // second tap within the window: activate
            if (!UpdatePointerEnd(c)) return;
            _ptrButtonExpire = now + PointerExpire;
            DrawPointerBeam(c);
            return;
        }

        // pointer active + fresh press: pulse ring at the pointed spot
        if (_ptrEnd != null)
        {
            var e = _ptrEnd;
            _pulses.Spawn(new Vector(e.X, e.Y, e.Z + 2f), 1f, Math.Max(1f, Cfg.PulseRingRadius), Cfg.PulseRingLife,
                WDraw.ToColor(Cfg.PointerColor, Color.OrangeRed), Cfg.PulseRingWidth, 16);
        }

        _ptrButtonExpire = now + PointerExpire;
    }

    private void PointerTick()
    {
        if (_ptrBeam == null && _ptrButtonExpire <= 0) return;
        var now = Server.EngineTime;
        var w = Warden;
        var c = w?.Controller;
        if (c == null || w == null || !w.IsAlive)
        {
            StopPointer();
            return;
        }

        if (now < _ptrButtonExpire + 0.2)
        {
            UpdatePointerEnd(c);
            // Holding USE keeps an active pointer alive.
            if (now <= _ptrButtonExpire && !MenuEatsUse(c) && HoldingUse(c))
                _ptrButtonExpire = now + PointerExpire;
        }

        if (now > _ptrButtonExpire + 0.1)
        {
            StopPointer();
            return;
        }

        DrawPointerBeam(c);
    }

    private static bool HoldingUse(CCSPlayerController c)
    {
        try { return (c.Buttons & PlayerButtons.Use) != 0; }
        catch { return false; }
    }

    /// <summary>
    /// End point = the newest real aim position: the warden's recent ping (the engine's crosshair trace) or the last
    /// bullet impact. Without either the warden is told once how to aim (ping or shoot).
    /// </summary>
    private bool UpdatePointerEnd(CCSPlayerController c)
    {
        var hasPing = WAim.RecentPing(c, Cfg.PingMaxAge, out var ping, out var pingTime);
        if (hasPing && (_savedCount == 0 || pingTime >= _savedTime))
        {
            _ptrEnd = ping;
            return true;
        }

        if (_savedCount > 0)
        {
            _ptrEnd = VectorUtil.Copy(_saved[0]);
            return true;
        }

        if (!_ptrNoTraceWarned)
        {
            _ptrNoTraceWarned = true;
            Chat.Print(c, WText.T("warden.pointer.no_trace"));
        }

        return _ptrEnd != null;
    }

    /// <summary>A warden ping shows the pointer laser at the pinged spot, with a pulse ring, for PingPointerTime.</summary>
    private void OnPing(CCSPlayerController c, Vector pos, int entity)
    {
        if (!Cfg.PointerEnabled || Cfg.PingPointerTime <= 0) return;
        var w = Warden;
        if (w == null || w.Slot != c.Slot || !w.IsAlive) return;
        _ptrEnd = pos;
        _ptrButtonExpire = Math.Max(_ptrButtonExpire, Server.EngineTime + Cfg.PingPointerTime);
        DrawPointerBeam(c);
        _pulses.Spawn(new Vector(pos.X, pos.Y, pos.Z + 2f), 1f, Math.Max(1f, Cfg.PulseRingRadius), Cfg.PulseRingLife,
            WDraw.ToColor(Cfg.PointerColor, Color.OrangeRed), Cfg.PulseRingWidth, 16);
    }

    /// <summary>Beam from the end point (width 0.4) to near the weapon muzzle (end width 1.0).</summary>
    private void DrawPointerBeam(CCSPlayerController c)
    {
        if (_ptrEnd == null) return;
        var pawn = c.PawnOrNull();
        if (pawn == null) return;
        var eye = c.EyePosition();
        var (f, r, u) = WDraw.Basis(pawn.EyeAngles);
        var muzzle = new Vector(eye.X + f.X * 16 + r.X * 5 - u.X * 5, eye.Y + f.Y * 16 + r.Y * 5 - u.Y * 5,
            eye.Z + f.Z * 16 + r.Z * 5 - u.Z * 5);

        if (_ptrBeam is { IsValid: true })
        {
            Fx.MoveBeam(_ptrBeam, _ptrEnd, muzzle);
            return;
        }

        _ptrBeam = WDraw.Beam(_ptrEnd, muzzle, WDraw.ToColor(Cfg.PointerColor, Color.OrangeRed), Cfg.PointerWidth, Cfg.PointerEndWidth);
    }

    // ================================================================= bullet history / paint

    [GameEventHandler]
    public HookResult OnBulletImpact(EventBulletImpact ev, GameEventInfo info)
    {
        var c = ev.Userid;
        var w = Warden;
        if (c == null || !c.IsValid || w == null || w.Slot != c.Slot) return HookResult.Continue;

        var impact = new Vector(ev.X, ev.Y, ev.Z);
        var now = Server.EngineTime;
        if (now > _nextBulletSave)
        {
            // 0.03 s gate so one shot penetrating several surfaces is saved once.
            SaveBulletOrigin(c, impact);
            _nextBulletSave = now + 0.03;
        }

        if (now < _nextPaint) return HookResult.Continue;
        if (!string.Equals(Cfg.PaintballMode, "beam", StringComparison.OrdinalIgnoreCase)) return HookResult.Continue;
        if (TryPaint(c, impact)) _nextPaint = now + Math.Max(0f, Cfg.PaintballDelay);
        return HookResult.Continue;
    }

    private void SaveBulletOrigin(CCSPlayerController c, Vector impact)
    {
        _saved[1] = _saved[0];
        _pulled[1] = _pulled[0];

        // The original pulled the point 10 units off the surface along the trace normal; without a ray trace
        // (CSS 1.0.368) it is pulled 10 units back toward the shooter's eye.
        var n = TowardEye(c, impact);
        _saved[0] = impact;
        _savedTime = Server.EngineTime;
        _pulled[0] = new Vector(impact.X + n.X * 10, impact.Y + n.Y * 10, impact.Z + n.Z * 10);
        if (_savedCount < 2) _savedCount++;
    }

    /// <summary>Warden paintball splat (TE World Decal substitute: two crossed beams on the surface).</summary>
    private bool TryPaint(CCSPlayerController c, Vector impact)
    {
        // No ray trace on CSS 1.0.368, so world and props cannot be told apart; at least never paint on players.
        foreach (var other in Players.Alive())
        {
            if (other.Slot == c.Slot) continue;
            var o = other.Origin();
            if (o == null) continue;
            if (VectorUtil.Distance2D(o, impact) <= 24f && impact.Z >= o.Z - 4f && impact.Z <= o.Z + 76f) return false;
        }

        var color = PaintColors[Random.Shared.Next(PaintColors.Length)];
        var normal = TowardEye(c, impact);

        var p = new Vector(impact.X + normal.X, impact.Y + normal.Y, impact.Z + normal.Z);
        var reference = MathF.Abs(normal.Z) < 0.9f ? new Vector(0, 0, 1) : new Vector(1, 0, 0);
        var a = WDraw.Normalize(WDraw.Cross(normal, reference));
        var b = WDraw.Normalize(WDraw.Cross(normal, a));
        var half = Math.Max(1f, Cfg.PaintballSize) / 2f;
        var width = Math.Max(0.5f, Cfg.PaintballWidth);

        var b1 = WDraw.Beam(new Vector(p.X - a.X * half, p.Y - a.Y * half, p.Z - a.Z * half),
            new Vector(p.X + a.X * half, p.Y + a.Y * half, p.Z + a.Z * half), color, width, width);
        var b2 = WDraw.Beam(new Vector(p.X - b.X * half, p.Y - b.Y * half, p.Z - b.Z * half),
            new Vector(p.X + b.X * half, p.Y + b.Y * half, p.Z + b.Z * half), color, width, width);
        if (b1 == null && b2 == null) return false;
        KeepSplat(new CEntityInstance?[] { b1, b2 });
        return true;
    }

    private void KeepSplat(CEntityInstance?[] parts)
    {
        _splats.Enqueue(parts);
        var max = Math.Max(1, Cfg.PaintballMaxSplats);
        while (_splats.Count > max)
            foreach (var old in _splats.Dequeue())
                Fx.SafeRemove(old);
    }

    private static Vector TowardEye(CCSPlayerController c, Vector impact)
    {
        var eye = c.EyePosition();
        return WDraw.Normalize(new Vector(eye.X - impact.X, eye.Y - impact.Y, eye.Z - impact.Z));
    }

    private void ClearPaint()
    {
        while (_splats.Count > 0)
            foreach (var b in _splats.Dequeue())
                Fx.SafeRemove(b);
    }

    // ================================================================= gestures

    private double TapWindow => Math.Max(0.1, Cfg.GestureTapWindow);

    /// <summary>3x DUCK: green ring through the last two bullet impacts (UltJB_WardenMenu_CreateRing).</summary>
    private void RingTap(JbPlayer warden, double now)
    {
        if (now > _ringExpire) _ringTaps = 0;
        _ringExpire = now + TapWindow;
        _ringTaps++;
        if (_ringTaps != 3 || _savedCount < 2) return;

        var s0 = _saved[0];
        var s1 = _saved[1];
        var radius = VectorUtil.Distance(s0, s1) / 2f;
        var centre = new Vector((s0.X + s1.X) / 2f, (s0.Y + s1.Y) / 2f, (s0.Z + s1.Z) / 2f);
        CreateRing(warden, 0, centre, radius);
    }

    /// <summary>3x WALK: pink line between the last two (pulled) bullet impacts, kept until round end.</summary>
    private void LineTap(double now)
    {
        if (now > _lineExpire) _lineTaps = 0;
        _lineExpire = now + TapWindow;
        _lineTaps++;
        if (_lineTaps != 3 || _savedCount < 2) return;

        _lineA = VectorUtil.Copy(_pulled[0]);
        _lineB = VectorUtil.Copy(_pulled[1]);
        DrawLine();
    }

    private void DrawLine()
    {
        if (_lineA == null || _lineB == null) return;
        if (_lineBeam is { IsValid: true })
        {
            Fx.MoveBeam(_lineBeam, _lineA, _lineB);
            return;
        }

        _lineBeam = WDraw.Beam(_lineA, _lineB, WDraw.ToColor(Cfg.LineColor, Color.DeepPink), Cfg.LineWidth, Cfg.LineWidth);
    }

    /// <summary>Safety redraw every 0.3 s (the original re-sent the TE every 0.3 s) in case the beam was cleaned up.</summary>
    private void LineTick()
    {
        if (_lineA == null) return;
        var now = Server.CurrentTime;
        if (now < _nextLineCheck) return;
        _nextLineCheck = now + 0.3f;
        if (_lineBeam is not { IsValid: true }) DrawLine();
    }

    private void StopLine()
    {
        _lineA = null;
        _lineB = null;
        Fx.SafeRemove(_lineBeam);
        _lineBeam = null;
    }

    /// <summary>Round end / map end / unload cleanup of every warden marker entity.</summary>
    private void CleanupVisuals(bool unload)
    {
        StopPointer();
        StopLine();
        ClearPaint();
        _pulses.Clear();
        _ringTaps = 0;
        _lineTaps = 0;
        if (unload) _savedCount = 0;
    }
}
