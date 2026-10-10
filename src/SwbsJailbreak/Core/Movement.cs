using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace SwbsJailbreak.Core;

/// <summary>
/// Stackable speed / gravity modifiers. CS2 continuously resets m_flVelocityModifier (damage slowdown recovery)
/// and gravity scale, so the active multipliers are re-applied every tick. Several systems can apply at once
/// (an effect, a day, an admin command); the product of all active multipliers is used.
/// Cleared automatically on death, team change and round start.
/// </summary>
public static class Movement
{
    private sealed class State
    {
        public readonly Dictionary<string, float> Speed = new();
        public readonly Dictionary<string, float> Gravity = new();
        public bool GravityApplied;
        public bool SpeedApplied;

        public bool Empty => Speed.Count == 0 && Gravity.Count == 0;
    }

    private static readonly State?[] States = new State?[Players.MaxSlots];

    private static State Get(int slot) => States[slot] ??= new State();

    public static void SetSpeed(CCSPlayerController? c, string key, float multiplier)
    {
        if (c == null || !c.IsValid) return;
        Get(c.Slot).Speed[key] = multiplier;
        Apply(c);
    }

    public static void ClearSpeed(CCSPlayerController? c, string key)
    {
        if (c == null || !c.IsValid) return;
        var s = States[c.Slot];
        if (s == null || !s.Speed.Remove(key)) return;
        Apply(c);
    }

    public static void SetGravity(CCSPlayerController? c, string key, float multiplier)
    {
        if (c == null || !c.IsValid) return;
        Get(c.Slot).Gravity[key] = multiplier;
        Apply(c);
    }

    public static void ClearGravity(CCSPlayerController? c, string key)
    {
        if (c == null || !c.IsValid) return;
        var s = States[c.Slot];
        if (s == null || !s.Gravity.Remove(key)) return;
        Apply(c);
    }

    /// <summary>Remove every modifier with this key from every player (e.g. when a day ends).</summary>
    public static void ClearKeyAll(string key)
    {
        for (var i = 0; i < Players.MaxSlots; i++)
        {
            var s = States[i];
            if (s == null) continue;
            var changed = s.Speed.Remove(key) | s.Gravity.Remove(key);
            if (changed)
            {
                var c = Utilities.GetPlayerFromSlot(i);
                if (c is { IsValid: true }) Apply(c);
            }
        }
    }

    public static float SpeedOf(CCSPlayerController c)
    {
        var s = States[c.Slot];
        if (s == null || s.Speed.Count == 0) return 1f;
        return s.Speed.Values.Aggregate(1f, (a, b) => a * b);
    }

    public static float GravityOf(CCSPlayerController c)
    {
        var s = States[c.Slot];
        if (s == null || s.Gravity.Count == 0) return 1f;
        return s.Gravity.Values.Aggregate(1f, (a, b) => a * b);
    }

    /// <summary>Drop all modifiers for a slot and restore defaults on the pawn.</summary>
    public static void Reset(int slot)
    {
        var s = States[slot];
        if (s == null) return;
        var hadAny = !s.Empty || s.GravityApplied || s.SpeedApplied;
        s.Speed.Clear();
        s.Gravity.Clear();
        if (!hadAny) return;
        var c = Utilities.GetPlayerFromSlot(slot);
        if (c is { IsValid: true }) Apply(c);
    }

    public static void ResetAll()
    {
        for (var i = 0; i < Players.MaxSlots; i++) Reset(i);
    }

    private static void Apply(CCSPlayerController c)
    {
        var s = States[c.Slot];
        var pawn = c.PawnOrNull();
        if (s == null || pawn == null) return;

        var speed = SpeedOf(c);
        if (s.Speed.Count > 0 || s.SpeedApplied)
        {
            pawn.VelocityModifier = speed;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
            s.SpeedApplied = s.Speed.Count > 0;
        }

        var gravity = GravityOf(c);
        if (s.Gravity.Count > 0 || s.GravityApplied)
        {
            pawn.GravityScale = gravity;
            pawn.ActualGravityScale = gravity;
            s.GravityApplied = s.Gravity.Count > 0;
        }
    }

    /// <summary>Called every tick by the core.</summary>
    internal static void Tick()
    {
        for (var i = 0; i < Players.MaxSlots; i++)
        {
            var s = States[i];
            if (s == null || s.Empty) continue;
            var c = Utilities.GetPlayerFromSlot(i);
            if (c is not { IsValid: true } || !c.IsAliveP()) continue;
            var pawn = c.PawnOrNull();
            if (pawn == null) continue;
            if (s.Speed.Count > 0)
            {
                var speed = SpeedOf(c);
                if (Math.Abs(pawn.VelocityModifier - speed) > 0.001f)
                {
                    pawn.VelocityModifier = speed;
                    Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
                }
            }

            if (s.Gravity.Count > 0)
            {
                var g = GravityOf(c);
                if (Math.Abs(pawn.ActualGravityScale - g) > 0.001f || Math.Abs(pawn.GravityScale - g) > 0.001f)
                {
                    pawn.GravityScale = g;
                    pawn.ActualGravityScale = g;
                }
            }
        }
    }
}
