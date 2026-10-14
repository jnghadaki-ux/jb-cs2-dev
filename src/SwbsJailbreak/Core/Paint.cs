using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Core;

/// <summary>
/// Paintball splats (store paintballs and the warden's paintballs). On CS:GO they were decals replacing the bullet
/// hole (TE World Decal); a CS2 server cannot place decals, so a splat is one of the workshop pack's flat splat models
/// (models/swbs/paintballs, white so it takes the paint colour), laid on the surface the bullet hit with a random
/// shape and rotation. The server is not told which way that surface faces, so <see cref="SurfaceNormal"/> works it
/// out from the shot: walls are taken as facing straight back along the shot's main axis, and a shot downwards (or one
/// that lands at the shooter's own floor height) paints the floor.
/// </summary>
public static class Paint
{
    /// <summary>Units the splat floats in front of the surface (no flicker against the wall).</summary>
    private const float Lift = 0.6f;

    /// <summary>
    /// Engine-free (unit-tested): the outward normal of the surface a shot most likely hit. dir = normalised shot
    /// direction; onShooterFloor = the impact is at the shooter's own floor height.
    /// </summary>
    internal static (float X, float Y, float Z) SurfaceNormal(float dx, float dy, float dz, bool onShooterFloor = false)
    {
        if (onShooterFloor && dz < -0.05f) return (0, 0, 1);
        var wall = MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
        var floor = dz < 0 ? -dz * 1.6f : 0f;   // floors are hit far more often than they look head-on
        var ceiling = dz > 0 ? dz * 1.3f : 0f;
        if (floor >= wall && floor >= ceiling) return (0, 0, 1);
        if (ceiling >= wall) return (0, 0, -1);
        return MathF.Abs(dx) >= MathF.Abs(dy) ? (-MathF.Sign(dx), 0, 0) : (0, -MathF.Sign(dy), 0);
    }

    /// <summary>Engine-free: angles that turn a model's +X axis onto the normal, rolled about it.</summary>
    internal static (float Pitch, float Yaw, float Roll) AnglesFor((float X, float Y, float Z) n, float roll)
    {
        var pitch = -MathF.Asin(Math.Clamp(n.Z, -1f, 1f)) * 180f / MathF.PI;
        var yaw = MathF.Abs(n.X) < 1e-4f && MathF.Abs(n.Y) < 1e-4f ? 0f : MathF.Atan2(n.Y, n.X) * 180f / MathF.PI;
        return (pitch, yaw, roll);
    }

    /// <summary>
    /// A splat model where the shot from <paramref name="eye"/> hit <paramref name="impact"/>, or null when there are no
    /// splat models (pack off, models off) or it could not be created: callers then draw their beam splat.
    /// </summary>
    public static CBaseModelEntity? Splat(Vector impact, Vector eye, float feetZ, Color color, IReadOnlyList<string> models)
    {
        if (models.Count == 0) return null;
        var dx = impact.X - eye.X;
        var dy = impact.Y - eye.Y;
        var dz = impact.Z - eye.Z;
        var len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1f) return null;
        var n = SurfaceNormal(dx / len, dy / len, dz / len, MathF.Abs(impact.Z - feetZ) < 2f);
        var (pitch, yaw, roll) = AnglesFor(n, Random.Shared.NextSingle() * 360f);
        var pos = new Vector(impact.X + n.X * Lift, impact.Y + n.Y * Lift, impact.Z + n.Z * Lift);
        return Fx.Prop(models[Random.Shared.Next(models.Count)], pos, new QAngle(pitch, yaw, roll), color);
    }
}
