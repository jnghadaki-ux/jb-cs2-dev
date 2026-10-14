using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Core;

/// <summary>
/// Where a player is aiming, from real engine data. Ping-based aim (below) dates from CounterStrikeSharp 1.0.368, which had
/// no ray-trace API; <see cref="LookTarget"/> uses the 1.0.376 Trace API. Aimed features that use pings: the player_ping game event carries
/// the point the engine itself traced from the crosshair (x/y/z) and the pinged entity (entityid). Pinging is the stock
/// CS2 "player_ping" key (middle mouse by default). Nothing here ever invents a position.
/// </summary>
public static class Aim
{
    private readonly record struct Ping(Vector Pos, int Entity, double Time);

    private static readonly Dictionary<int, Ping> Pings = new();

    /// <summary>Raised for every valid ping: (player, point, pinged entity index or 0).</summary>
    public static event Action<CCSPlayerController, Vector, int>? Pinged;

    internal static HookResult OnPlayerPing(EventPlayerPing ev, GameEventInfo info)
    {
        var c = ev.Userid;
        if (c == null || !c.IsValid || c.IsBot) return HookResult.Continue;
        var pos = new Vector(ev.X, ev.Y, ev.Z);
        if (float.IsNaN(pos.X) || float.IsNaN(pos.Y) || float.IsNaN(pos.Z)) return HookResult.Continue;
        var entity = Math.Max(0, ev.Entityid);
        Pings[c.Slot] = new Ping(pos, entity, Server.EngineTime);
        try { Pinged?.Invoke(c, pos, entity); }
        catch (Exception ex) { JailbreakPlugin.Instance?.Logger.LogError(ex, "[Aim] ping handler failed"); }
        return HookResult.Continue;
    }

    /// <summary>The player's last ping if it is at most <paramref name="maxAge"/> seconds old.</summary>
    public static bool TryGetPing(CCSPlayerController c, double maxAge, out Vector pos, out int entity) =>
        TryGetPing(c, maxAge, out pos, out entity, out _);

    /// <summary>As above, also returning the engine time of the ping.</summary>
    public static bool TryGetPing(CCSPlayerController c, double maxAge, out Vector pos, out int entity, out double time)
    {
        pos = new Vector(0, 0, 0);
        entity = 0;
        time = 0;
        if (!Pings.TryGetValue(c.Slot, out var p) || Server.EngineTime - p.Time > maxAge) return false;
        pos = new Vector(p.Pos.X, p.Pos.Y, p.Pos.Z);
        entity = p.Entity;
        time = p.Time;
        return true;
    }

    /// <summary>The pinged entity as T when it still exists.</summary>
    public static T? Entity<T>(int index) where T : CEntityInstance
    {
        if (index <= 0) return null;
        var e = Utilities.GetEntityFromIndex<T>(index);
        return e != null && e.IsValid ? e : null;
    }

    // ------------------------------------------------------------------ look-at (CSS 1.0.376 ray traces)

    /// <summary>
    /// The alive player <paramref name="viewer"/> is looking at, within <paramref name="maxDistance"/> units. First a
    /// ray trace from the eyes along the view (stops at walls, hits player hitboxes); if that misses, the visible player
    /// closest to the crosshair inside <paramref name="coneDegrees"/>. Null when nobody qualifies or tracing fails.
    /// </summary>
    public static CCSPlayerController? LookTarget(CCSPlayerController viewer, float maxDistance = 1500f, float coneDegrees = 6f)
    {
        try
        {
            var pawn = viewer.PawnOrNull();
            if (pawn?.AbsOrigin == null) return null;
            var eye = viewer.EyePosition();
            var forward = VectorUtil.Forward(pawn.EyeAngles);
            var end = new Vector(eye.X + forward.X * maxDistance, eye.Y + forward.Y * maxDistance, eye.Z + forward.Z * maxDistance);

            var hit = CounterStrikeSharp.API.Modules.Utils.Trace.TraceEndShape(eye, end, pawn, ShotOptions);
            if (hit.DidHit() && PlayerFromEntity(hit.HitEntity()) is { } direct && direct.Slot != viewer.Slot && direct.IsAliveP())
                return direct;

            var candidates = Players.Alive()
                .Where(c => c.Slot != viewer.Slot)
                .Select(c => (c, pos: Chest(c)))
                .Where(x => x.pos != null)
                .ToList();
            var best = PickInCone(eye, forward, candidates.Select(x => (x.c.Slot, x.pos!)).ToList(), maxDistance, coneDegrees);
            if (best < 0) return null;
            var target = candidates.First(x => x.c.Slot == best);
            return HasLineOfSight(eye, target.pos!, pawn, target.c) ? target.c : null;
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogDebug(ex, "[Aim] look trace failed");
            return null;
        }
    }

    private static readonly CounterStrikeSharp.API.Modules.Utils.TraceOptions ShotOptions = new()
    {
        InteractsWith = CounterStrikeSharp.API.Modules.Utils.Masks.Shot
    };

    private static readonly CounterStrikeSharp.API.Modules.Utils.TraceOptions WorldOptions = new()
    {
        InteractsWith = CounterStrikeSharp.API.Modules.Utils.Masks.ShotBrushOnly
    };

    private static Vector? Chest(CCSPlayerController c)
    {
        var o = c.PawnOrNull()?.AbsOrigin;
        return o == null ? null : new Vector(o.X, o.Y, o.Z + 48f);
    }

    /// <summary>True when no world geometry blocks the line between the two points.</summary>
    private static bool HasLineOfSight(Vector from, Vector to, CBaseEntity ignore, CCSPlayerController target)
    {
        var t = CounterStrikeSharp.API.Modules.Utils.Trace.TraceEndShape(from, to, ignore, WorldOptions);
        return !t.DidHit() || t.Fraction >= 0.98f || PlayerFromEntity(t.HitEntity())?.Slot == target.Slot;
    }

    private static CCSPlayerController? PlayerFromEntity(CEntityInstance? ent)
    {
        if (ent == null || ent.Handle == IntPtr.Zero || !ent.IsValid || ent.DesignerName != "player") return null;
        var pawn = new CCSPlayerPawn(ent.Handle);
        var c = pawn.OriginalController.Value;
        return c is { IsValid: true } ? c : null;
    }

    /// <summary>
    /// Engine-free: the candidate closest to the view direction within <paramref name="coneDegrees"/> and
    /// <paramref name="maxDistance"/>, ties to the nearer one; -1 when none (unit-tested).
    /// </summary>
    internal static int PickInCone(Vector eye, Vector forward, IReadOnlyList<(int Slot, Vector Pos)> candidates, float maxDistance, float coneDegrees)
    {
        var best = -1;
        var bestAngle = float.MaxValue;
        var bestDist = float.MaxValue;
        var fLen = MathF.Sqrt(forward.X * forward.X + forward.Y * forward.Y + forward.Z * forward.Z);
        if (fLen < 1e-4f) return -1;
        foreach (var (slot, pos) in candidates)
        {
            var dx = pos.X - eye.X;
            var dy = pos.Y - eye.Y;
            var dz = pos.Z - eye.Z;
            var dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist < 1f || dist > maxDistance) continue;
            var cos = (dx * forward.X + dy * forward.Y + dz * forward.Z) / (dist * fLen);
            var angle = MathF.Acos(Math.Clamp(cos, -1f, 1f)) * 180f / MathF.PI;
            if (angle > coneDegrees) continue;
            if (angle < bestAngle - 0.01f || (MathF.Abs(angle - bestAngle) <= 0.01f && dist < bestDist))
            {
                best = slot;
                bestAngle = angle;
                bestDist = dist;
            }
        }

        return best;
    }

    public static void Forget(int slot) => Pings.Remove(slot);

    public static void Reset() => Pings.Clear();

    internal static void Unload()
    {
        Pings.Clear();
        Pinged = null;
    }
}
