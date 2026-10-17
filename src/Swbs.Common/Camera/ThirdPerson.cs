using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace Swbs.Common;

/// <summary>Section "ThirdPerson": the !3p follow camera.</summary>
public sealed class ThirdPersonConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Who may use !3p: "everyone", "admin" (permission "admin") or "off". Empty = the game mode's default.</summary>
    public string Command { get; set; } = "";

    /// <summary>Camera distance behind the eyes, height above them and sideways offset (positive = over the right shoulder).</summary>
    public float Distance { get; set; } = 110f;
    public float Height { get; set; } = 10f;
    public float Side { get; set; } = 0f;

    /// <summary>Pull the camera in front of walls instead of looking through them.</summary>
    public bool Collision { get; set; } = true;
}

/// <summary>
/// A third person camera that works on current CS2 (the thirdperson console command is cheat-protected): an invisible,
/// model-less prop_dynamic follows behind the player every frame and is set as their view entity (re-asserted every
/// frame, since the game resets it on spawn and on some state changes). During the server's entity think the view entity
/// is cleared, so shots and +use still come from the player's eyes, not from the camera. Several reasons can hold the
/// camera on (the !3p command, the Third Person style ...); it stays until all are gone.
/// </summary>
public static class ThirdPerson
{
    private const int Slots = 65;
    private static readonly HashSet<string>?[] Reasons = new HashSet<string>?[Slots];
    private static readonly CDynamicProp?[] Cameras = new CDynamicProp?[Slots];
    private static readonly uint[] HiddenView = new uint[Slots];
    private static int _active;

    public static ThirdPersonConfig Settings { get; set; } = new();

    /// <summary>Extra rule for the !3p command (KZ: not during a timed run). Styles that force the camera ignore it.</summary>
    public static Func<CCSPlayerController, bool> Allowed { get; set; } = _ => true;

    public static bool IsOn(CCSPlayerController c) => c.Slot is >= 0 and < Slots && Reasons[c.Slot] is { Count: > 0 };

    public static bool Has(CCSPlayerController c, string reason) => c.Slot is >= 0 and < Slots && Reasons[c.Slot]?.Contains(reason) == true;

    public static void Set(CCSPlayerController c, string reason, bool on)
    {
        if (!c.IsValid || c.Slot is < 0 or >= Slots) return;
        var set = Reasons[c.Slot] ??= new HashSet<string>();
        if (on) set.Add(reason);
        else set.Remove(reason);
        Update(c);
    }

    /// <summary>Forget everything about a slot (disconnect).</summary>
    public static void Clear(int slot)
    {
        if (slot is < 0 or >= Slots) return;
        Reasons[slot] = null;
        Remove(slot);
    }

    /// <summary>Create or remove the camera to match the reasons and whether the player is alive.</summary>
    public static void Update(CCSPlayerController c)
    {
        if (!c.IsValid || c.Slot is < 0 or >= Slots) return;
        var pawn = c.PlayerPawn.Value;
        var want = IsOn(c) && pawn is { IsValid: true } && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE;
        if (!want)
        {
            Remove(c.Slot);
            return;
        }

        if (Cameras[c.Slot] is { IsValid: true }) return;
        var cam = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
        if (cam == null) return;
        cam.Spawnflags = 256; // no collisions
        cam.DispatchSpawn();
        var skeleton = cam.CBodyComponent?.SceneNode?.GetSkeletonInstance();
        if (skeleton != null) skeleton.Scale = 0f;
        Utilities.SetStateChanged(cam, "CBaseEntity", "m_CBodyComponent");
        cam.RenderMode = RenderMode_t.kRenderTransAlpha;
        cam.Render = Color.FromArgb(0, 255, 255, 255);
        Utilities.SetStateChanged(cam, "CBaseModelEntity", "m_clrRender");
        Cameras[c.Slot] = cam;
        _active++;
        Place(c, pawn!, cam);
        SetView(pawn!, cam.EntityHandle.Raw);
    }

    private static void Remove(int slot)
    {
        var cam = Cameras[slot];
        if (cam == null) return;
        Cameras[slot] = null;
        _active = Math.Max(0, _active - 1);
        HiddenView[slot] = 0;
        var pawn = Players.Get(slot).Controller?.PlayerPawn.Value;
        if (pawn is { IsValid: true } && pawn.CameraServices is { } cs && cs.ViewEntity.Raw == cam.EntityHandle.Raw) SetView(pawn, uint.MaxValue);
        try { if (cam.IsValid) cam.Remove(); }
        catch { /* gone with the map */ }
    }

    public static void RemoveAll()
    {
        for (var i = 0; i < Slots; i++) Remove(i);
    }

    /// <summary>Map change: the entities are already gone.</summary>
    public static void Forget()
    {
        Array.Clear(Cameras);
        Array.Clear(HiddenView);
        _active = 0;
    }

    private static void SetView(CCSPlayerPawn pawn, uint raw)
    {
        if (pawn.CameraServices == null) return;
        pawn.CameraServices.ViewEntity.Raw = raw;
        Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pCameraServices");
    }

    /// <summary>Every frame: move each camera behind its player and keep it set as their view.</summary>
    public static void Tick()
    {
        if (_active == 0) return;
        for (var slot = 0; slot < Slots; slot++)
        {
            var cam = Cameras[slot];
            if (cam == null) continue;
            var c = Players.Get(slot).Controller;
            var pawn = c?.PlayerPawn.Value;
            if (c is not { IsValid: true } || pawn is not { IsValid: true } || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || !cam.IsValid)
            {
                Remove(slot);
                continue;
            }

            Place(c, pawn, cam);
            if (pawn.CameraServices is { } cs && cs.ViewEntity.Raw != cam.EntityHandle.Raw) SetView(pawn, cam.EntityHandle.Raw);
        }
    }

    private static void Place(CCSPlayerController c, CCSPlayerPawn pawn, CDynamicProp cam)
    {
        var o = pawn.AbsOrigin;
        if (o == null) return;
        var s = Settings;
        var angles = pawn.EyeAngles;
        Vector forward = new(), right = new(), up = new();
        NativeAPI.AngleVectors(angles.Handle, forward.Handle, right.Handle, up.Handle);
        var eye = new Vector(o.X, o.Y, o.Z + pawn.ViewOffset.Z);
        var pivot = new Vector(eye.X, eye.Y, eye.Z + s.Height);
        var want = new Vector(
            pivot.X - forward.X * s.Distance + right.X * s.Side,
            pivot.Y - forward.Y * s.Distance + right.Y * s.Side,
            pivot.Z - forward.Z * s.Distance + right.Z * s.Side);
        if (s.Collision)
        {
            var hit = Aim.WorldHit(pivot, want, pawn);
            if (hit != null)
            {
                // Stay 10 units in front of the wall.
                var dx = pivot.X - hit.X;
                var dy = pivot.Y - hit.Y;
                var dz = pivot.Z - hit.Z;
                var len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                var back = len > 0.01f ? MathF.Min(10f, len) / len : 0f;
                want = new Vector(hit.X + dx * back, hit.Y + dy * back, hit.Z + dz * back);
            }
        }

        var v = pawn.AbsVelocity;
        cam.Teleport(want, new QAngle(angles.X, angles.Y, 0), new Vector(v.X, v.Y, v.Z));
    }

    /// <summary>OnServerPreEntityThink: no view entity while the game runs player thinks (shots, +use from the eyes).</summary>
    public static void PreThink()
    {
        if (_active == 0) return;
        for (var slot = 0; slot < Slots; slot++)
        {
            if (Cameras[slot] is not { } cam) continue;
            var pawn = Players.Get(slot).Controller?.PlayerPawn.Value;
            if (pawn is not { IsValid: true } || pawn.CameraServices is not { } cs || cs.ViewEntity.Raw != cam.EntityHandle.Raw) continue;
            HiddenView[slot] = cs.ViewEntity.Raw;
            cs.ViewEntity.Raw = uint.MaxValue;
        }
    }

    /// <summary>OnServerPostEntityThink: put the camera back (no network update: the value is the same as before).</summary>
    public static void PostThink()
    {
        if (_active == 0) return;
        for (var slot = 0; slot < Slots; slot++)
        {
            var raw = HiddenView[slot];
            if (raw == 0) continue;
            HiddenView[slot] = 0;
            var pawn = Players.Get(slot).Controller?.PlayerPawn.Value;
            if (pawn is { IsValid: true } && pawn.CameraServices is { } cs) cs.ViewEntity.Raw = raw;
        }
    }
}
