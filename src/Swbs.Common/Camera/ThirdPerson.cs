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

    /// <summary>
    /// The camera entity: "point_camera" (no model at all) or "prop_dynamic" (an empty prop shrunk to nothing, the older
    /// way; shrinking its skeleton has been seen to make players turn invisible).
    /// </summary>
    public string Entity { get; set; } = "point_camera";
}

/// <summary>
/// A third person camera that works on current CS2 (the thirdperson console command is cheat-protected): a model-less
/// point_camera follows behind the player every frame and is set as their view entity (re-asserted every frame, since the
/// game resets it on spawn and on some state changes). Only its owner receives it (<see cref="FilterTransmit"/>).
/// During the server's entity think the view entity is cleared, so shots and +use still come from the player's eyes, not
/// from the camera. Several reasons can hold the camera on (the !3p command, the Third Person style ...); it stays until
/// all are gone.
/// </summary>
public static class ThirdPerson
{
    private const int Slots = 65;
    private static readonly HashSet<string>?[] Reasons = new HashSet<string>?[Slots];
    /// <summary>Camera entity handles (never stored entity objects: the round restart removes the camera, and touching a removed entity crashes the server).</summary>
    private static readonly uint[] Cameras = new uint[Slots];
    private static readonly float[] CreatedAt = new float[Slots];
    /// <summary>The camera as created, used only until the entity system lists it (the first frame or so).</summary>
    private static readonly CBaseEntity?[] Fresh = new CBaseEntity?[Slots];
    private static readonly uint[] Indices = new uint[Slots];
    private static readonly uint[] HiddenView = new uint[Slots];
    private static int _active;

    public static ThirdPersonConfig Settings { get; set; } = new();

    private static CBaseEntity? Cam(int slot)
    {
        var raw = Cameras[slot];
        if (raw == 0) return null;
        try
        {
            var e = new CHandle<CBaseEntity>(raw).Value;
            if (e is { IsValid: true })
            {
                Fresh[slot] = null;
                return e;
            }
        }
        catch
        {
            // fall through
        }

        // Never longer than a moment: a stored entity object goes stale when the round restart removes it.
        return Fresh[slot] != null && Server.CurrentTime - CreatedAt[slot] < 1f ? Fresh[slot] : null;
    }

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

        if (Cam(c.Slot) != null) return;
        if (Cameras[c.Slot] != 0)
        {
            Cameras[c.Slot] = 0; // removed by the round restart
            Fresh[c.Slot] = null;
            _active = Math.Max(0, _active - 1);
        }

        var cam = CreateCamera();
        if (cam == null) return;
        Cameras[c.Slot] = cam.EntityHandle.Raw;
        Indices[c.Slot] = cam.Index;
        Fresh[c.Slot] = cam;
        CreatedAt[c.Slot] = Server.CurrentTime;
        _active++;
        Place(c, pawn!, cam);
        SetView(pawn!, cam.EntityHandle.Raw);
    }

    private static CBaseEntity? CreateCamera()
    {
        // Only a null entity is a failure here: from then on the camera is looked up by its handle (Cam).
        if (!Settings.Entity.Equals("prop_dynamic", StringComparison.OrdinalIgnoreCase))
        {
            var point = Utilities.CreateEntityByName<CPointCamera>("point_camera");
            if (point != null && point.Handle != IntPtr.Zero)
            {
                point.FOV = 90f;
                point.DispatchSpawn();
                return point;
            }
        }

        var cam = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
        if (cam == null || cam.Handle == IntPtr.Zero) return null;
        cam.Spawnflags = 256; // no collisions
        cam.DispatchSpawn();
        var skeleton = cam.CBodyComponent?.SceneNode?.GetSkeletonInstance();
        if (skeleton != null) skeleton.Scale = 0f;
        Utilities.SetStateChanged(cam, "CBaseEntity", "m_CBodyComponent");
        cam.RenderMode = RenderMode_t.kRenderTransAlpha;
        cam.Render = Color.FromArgb(0, 255, 255, 255);
        Utilities.SetStateChanged(cam, "CBaseModelEntity", "m_clrRender");
        return cam;
    }

    private static void Remove(int slot)
    {
        var raw = Cameras[slot];
        if (raw == 0) return;
        var cam = Cam(slot);
        Cameras[slot] = 0;
        Fresh[slot] = null;
        _active = Math.Max(0, _active - 1);
        HiddenView[slot] = 0;
        var pawn = Utilities.GetPlayerFromSlot(slot)?.PlayerPawn.Value;
        if (pawn is { IsValid: true } && pawn.CameraServices is { } cs && cs.ViewEntity.Raw == raw) SetView(pawn, uint.MaxValue);
        try { cam?.Remove(); }
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
        Array.Clear(Fresh);
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
            if (Cameras[slot] == 0) continue;
            var c = Utilities.GetPlayerFromSlot(slot);
            var pawn = c?.PlayerPawn.Value;
            if (c is not { IsValid: true } || pawn is not { IsValid: true } || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            {
                Remove(slot);
                continue;
            }

            // The round restart removes the camera: make a new one (the reasons still hold).
            var cam = Cam(slot);
            if (cam == null)
            {
                Cameras[slot] = 0;
                Fresh[slot] = null;
                _active = Math.Max(0, _active - 1);
                if (pawn.CameraServices is { } stale && stale.ViewEntity.Raw != uint.MaxValue) SetView(pawn, uint.MaxValue);
                Update(c);
                continue;
            }

            Place(c, pawn, cam);
            if (pawn.CameraServices is { } cs && cs.ViewEntity.Raw != Cameras[slot]) SetView(pawn, Cameras[slot]);
        }
    }

    private static void Place(CCSPlayerController c, CCSPlayerPawn pawn, CBaseEntity cam)
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

    /// <summary>CheckTransmit listener: a camera goes only to its owner (nobody else needs it, or sees an ERROR model).</summary>
    public static void FilterTransmit(CCheckTransmitInfoList infoList)
    {
        if (_active == 0) return;
        // Only cameras that still exist: a removed camera's index may already belong to another entity.
        List<(int Slot, uint Index)>? cams = null;
        for (var slot = 0; slot < Slots; slot++)
            if (Cameras[slot] != 0 && Cam(slot) != null) (cams ??= new()).Add((slot, Indices[slot]));
        if (cams == null) return;
        foreach (var (info, viewer) in infoList)
        {
            var own = viewer is { IsValid: true } ? viewer.Slot : -1;
            foreach (var (slot, index) in cams)
                if (slot != own) info.TransmitEntities.Remove(index);
        }
    }

    /// <summary>OnServerPreEntityThink: no view entity while the game runs player thinks (shots, +use from the eyes).</summary>
    public static void PreThink()
    {
        if (_active == 0) return;
        for (var slot = 0; slot < Slots; slot++)
        {
            var raw = Cameras[slot];
            if (raw == 0) continue;
            var pawn = Utilities.GetPlayerFromSlot(slot)?.PlayerPawn.Value;
            if (pawn is not { IsValid: true } || pawn.CameraServices is not { } cs || cs.ViewEntity.Raw != raw) continue;
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
            var pawn = Utilities.GetPlayerFromSlot(slot)?.PlayerPawn.Value;
            if (pawn is { IsValid: true } && pawn.CameraServices is { } cs) cs.ViewEntity.Raw = raw;
        }
    }
}
