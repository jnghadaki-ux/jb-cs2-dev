using System.Numerics;

namespace SwbsJailbreak.Core;

/// <summary>
/// Zone geometry, engine-free so it is unit-tested. Like the SMPlugins ZoneManager a zone is an axis-aligned box
/// (origin + mins/maxs relative to it); the zone's angles are only a facing direction (teleport destinations, push
/// direction, the forward indicator), they never rotate the box.
/// </summary>
public readonly record struct ZoneBox(Vector3 Origin, Vector3 Mins, Vector3 Maxs)
{
    /// <summary>CS2 standing player hull (same size as CS:GO): x/y ±16, z 0..72 from the feet.</summary>
    public static readonly Vector3 HullMins = new(-16f, -16f, 0f);
    public static readonly Vector3 HullMaxs = new(16f, 16f, 72f);

    /// <summary>SMPlugins DEFAULT_ZONE_MINS / DEFAULT_ZONE_MAXS for a zone added at the admin's feet.</summary>
    public static readonly Vector3 DefaultMins = new(-100f, -100f, 2f);
    public static readonly Vector3 DefaultMaxs = new(100f, 100f, 100f);

    public Vector3 WorldMins => Origin + Mins;
    public Vector3 WorldMaxs => Origin + Maxs;
    public Vector3 Center => Origin + (Mins + Maxs) * 0.5f;
    public Vector3 Size => Maxs - Mins;

    /// <summary>Teleport-destination spot: centre in x/y, floor of the box (SMPlugins TryToTeleport).</summary>
    public Vector3 BottomCenter => new(Origin.X + (Mins.X + Maxs.X) * 0.5f, Origin.Y + (Mins.Y + Maxs.Y) * 0.5f, Origin.Z + Mins.Z);

    public bool Contains(Vector3 p)
    {
        var a = WorldMins;
        var b = WorldMaxs;
        return p.X >= a.X && p.X <= b.X && p.Y >= a.Y && p.Y <= b.Y && p.Z >= a.Z && p.Z <= b.Z;
    }

    /// <summary>The player's hull (feet position) overlaps the box: what a trigger touch meant in the original.</summary>
    public bool OverlapsHull(Vector3 feet)
    {
        var a = WorldMins;
        var b = WorldMaxs;
        var pa = feet + HullMins;
        var pb = feet + HullMaxs;
        return pa.X <= b.X && pb.X >= a.X && pa.Y <= b.Y && pb.Y >= a.Y && pa.Z <= b.Z && pb.Z >= a.Z;
    }

    /// <summary>The feet moved through the box this tick (hull-expanded slab test), even if both ends are outside.</summary>
    public bool HullSweepHits(Vector3 from, Vector3 to)
    {
        if (OverlapsHull(from) || OverlapsHull(to)) return true;
        // Expand the box by the hull and test the feet segment against it (slab method).
        var a = WorldMins - HullMaxs;
        var b = WorldMaxs - HullMins;
        var d = to - from;
        float t0 = 0f, t1 = 1f;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = Axis(from, axis);
            var dir = Axis(d, axis);
            var lo = Axis(a, axis);
            var hi = Axis(b, axis);
            if (MathF.Abs(dir) < 1e-6f)
            {
                if (o < lo || o > hi) return false;
                continue;
            }

            var ta = (lo - o) / dir;
            var tb = (hi - o) / dir;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta);
            t1 = MathF.Min(t1, tb);
            if (t0 > t1) return false;
        }

        return true;
    }

    /// <summary>Closest feet position just outside the hull-expanded box (smallest push), for a player stuck inside.</summary>
    public Vector3 NearestOutside(Vector3 feet, float margin = 2f)
    {
        var a = WorldMins - HullMaxs;
        var b = WorldMaxs - HullMins;
        var best = feet;
        var bestDist = float.MaxValue;
        for (var axis = 0; axis < 2; axis++) // horizontal pushes only: never into the floor or ceiling
        {
            var v = Axis(feet, axis);
            var toLo = v - Axis(a, axis) + margin;
            var toHi = Axis(b, axis) - v + margin;
            if (toLo < bestDist)
            {
                bestDist = toLo;
                best = With(feet, axis, Axis(a, axis) - margin);
            }

            if (toHi < bestDist)
            {
                bestDist = toHi;
                best = With(feet, axis, Axis(b, axis) + margin);
            }
        }

        return best;
    }

    public float DistanceTo(Vector3 p)
    {
        var c = Vector3.Clamp(p, WorldMins, WorldMaxs);
        return Vector3.Distance(c, p);
    }

    /// <summary>The 8 corners: 0-3 bottom (mins z), 4-7 top, in the original ShowZoneBox order.</summary>
    public Vector3[] Corners()
    {
        var a = WorldMins;
        var b = WorldMaxs;
        return new[]
        {
            new Vector3(a.X, a.Y, a.Z), new Vector3(a.X, b.Y, a.Z), new Vector3(b.X, b.Y, a.Z), new Vector3(b.X, a.Y, a.Z),
            new Vector3(a.X, a.Y, b.Z), new Vector3(a.X, b.Y, b.Z), new Vector3(b.X, b.Y, b.Z), new Vector3(b.X, a.Y, b.Z)
        };
    }

    /// <summary>The 12 edges as corner index pairs.</summary>
    public static readonly (int A, int B)[] Edges =
    {
        (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7)
    };

    /// <summary>A box spanning two corner points (feet / pinged points), origin at the first corner's floor centre.</summary>
    public static ZoneBox FromCorners(Vector3 c1, Vector3 c2, float minHeight = 72f)
    {
        var lo = Vector3.Min(c1, c2);
        var hi = Vector3.Max(c1, c2);
        if (hi.Z - lo.Z < minHeight) hi.Z = lo.Z + minHeight;
        var origin = new Vector3((lo.X + hi.X) * 0.5f, (lo.Y + hi.Y) * 0.5f, lo.Z);
        return new ZoneBox(origin, lo - origin, hi - origin);
    }

    internal static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    internal static Vector3 With(Vector3 v, int axis, float value) =>
        axis == 0 ? new Vector3(value, v.Y, v.Z) : axis == 1 ? new Vector3(v.X, value, v.Z) : new Vector3(v.X, v.Y, value);
}

/// <summary>Box editing rules ported 1:1 from API_ZoneManager.sp (SetMinsMaxsByAngles) plus the CS2 replacements.</summary>
public static class ZoneEdit
{
    /// <summary>
    /// SetMinsMaxsByAngles: grow (amount &gt; 0) or shrink one face picked from the eye angles. Pitch beyond ±45 moves the
    /// floor/ceiling; otherwise yaw within ±30 of an axis (±30 of ±90 for y) moves that side. Never inverts the box.
    /// </summary>
    public static void ResizeByLook(float pitch, float yaw, ref Vector3 mins, ref Vector3 maxs, float amount)
    {
        yaw = NormalizeYaw(yaw);
        if (pitch > 45f)
        {
            mins.Z -= amount;
            if (mins.Z >= maxs.Z) mins.Z = maxs.Z - 1f;
        }
        else if (pitch < -45f)
        {
            maxs.Z += amount;
            if (maxs.Z <= mins.Z) maxs.Z = mins.Z + 1f;
        }

        if (MathF.Abs(pitch) >= 45f) return;
        if (yaw > 60f && yaw < 120f)
        {
            maxs.Y += amount;
            if (maxs.Y <= mins.Y) maxs.Y = mins.Y + 1f;
        }
        else if (yaw < -60f && yaw > -120f)
        {
            mins.Y -= amount;
            if (mins.Y >= maxs.Y) mins.Y = maxs.Y - 1f;
        }

        if (yaw > -30f && yaw < 30f)
        {
            maxs.X += amount;
            if (maxs.X <= mins.X) maxs.X = mins.X + 1f;
        }
        else if (yaw > 150f || yaw < -150f)
        {
            mins.X -= amount;
            if (mins.X >= maxs.X) mins.X = maxs.X - 1f;
        }
    }

    /// <summary>
    /// CS2 replacement for ExpandToWall (which needed a ray trace): move the face picked by the look direction (same
    /// rule as <see cref="ResizeByLook"/>) to a real point the admin pinged. Returns false if no face matches the look.
    /// </summary>
    public static bool MoveFaceTo(float pitch, float yaw, Vector3 origin, ref Vector3 mins, ref Vector3 maxs, Vector3 point)
    {
        yaw = NormalizeYaw(yaw);
        var rel = point - origin;
        if (pitch > 45f) { mins.Z = MathF.Min(rel.Z, maxs.Z - 1f); return true; }
        if (pitch < -45f) { maxs.Z = MathF.Max(rel.Z, mins.Z + 1f); return true; }
        var moved = false;
        if (yaw > 60f && yaw < 120f) { maxs.Y = MathF.Max(rel.Y, mins.Y + 1f); moved = true; }
        else if (yaw < -60f && yaw > -120f) { mins.Y = MathF.Min(rel.Y, maxs.Y - 1f); moved = true; }
        if (yaw > -30f && yaw < 30f) { maxs.X = MathF.Max(rel.X, mins.X + 1f); moved = true; }
        else if (yaw > 150f || yaw < -150f) { mins.X = MathF.Min(rel.X, maxs.X - 1f); moved = true; }
        return moved;
    }

    /// <summary>Yaw into (-180, 180] like the engine's eye angles.</summary>
    public static float NormalizeYaw(float yaw)
    {
        yaw %= 360f;
        if (yaw > 180f) yaw -= 360f;
        if (yaw <= -180f) yaw += 360f;
        return yaw;
    }

    /// <summary>Unit forward vector of pitch/yaw (Source convention: +pitch looks down).</summary>
    public static Vector3 Forward(float pitch, float yaw)
    {
        var p = pitch * MathF.PI / 180f;
        var y = yaw * MathF.PI / 180f;
        var cp = MathF.Cos(p);
        return new Vector3(cp * MathF.Cos(y), cp * MathF.Sin(y), -MathF.Sin(p));
    }
}

/// <summary>
/// The "enabled / disabled timer" rule shared by the SMPlugins Kill and Blockade zone types (ShouldKill / AddZoneToArray):
/// seconds since round start against an "on" time and an "off" time, 0 = timer not set.
/// </summary>
public static class TimedToggle
{
    public static bool IsOn(float elapsed, int onTime, int offTime)
    {
        var hasOn = onTime != 0;
        var hasOff = offTime != 0;
        var onSet = elapsed >= onTime;
        var offSet = elapsed >= offTime;
        if (hasOn && hasOff)
        {
            if (onSet && offSet) return onTime > offTime;   // both passed: the later one wins
            if (!onSet && !offSet) return onTime >= offTime; // neither yet: whichever comes first is not active yet
            return onSet;
        }

        if (hasOn) return onSet;
        if (hasOff) return !offSet;
        return true; // no timers: always on
    }
}

/// <summary>Enter / exit bookkeeping for one player, engine-free.</summary>
public static class ZoneDiff
{
    public static (List<int> Entered, List<int> Exited) Compute(IReadOnlySet<int> before, IReadOnlySet<int> now)
    {
        var entered = new List<int>();
        var exited = new List<int>();
        foreach (var id in now)
            if (!before.Contains(id)) entered.Add(id);
        foreach (var id in before)
            if (!now.Contains(id)) exited.Add(id);
        entered.Sort();
        exited.Sort();
        return (entered, exited);
    }
}
