namespace SwbsJailbreak.Core;

// Warden Games (design: docs/design/MINIGAMES_SUITE_DESIGN.md §4). Based on SMPlugins by hlstriker: MultigamesGameSelect,
// MultigamesQueue and UserPointsForMinigamesWinner, plus jailbreak classics judged from real engine input.
// Everything in this file is pure logic (no engine types) so the judging rules are unit-tested.

public enum GameReward { None, Points, CrewRespect }

public enum GameEndReason { Won, WardenEnded, WardenLost, TimeUp, RoundEnded, DayStarted, LastRequest, NoPlayers, Unloaded }

/// <summary>Simon Says orders. Stand still is only given as a Simon order (the "not Simon" form has no meaning).</summary>
public enum SimonOrder { Jump, Crouch, StandStill, LookUp, LookDown }

public enum Verdict { Pending, Pass, Fail }

/// <summary>One player's input sample for the current tick.</summary>
public readonly record struct InputSample(bool Jump, bool Duck, float Speed2D, float Pitch);

public static class GameJudge
{
    public const float StillSpeed = 5f;
    public const float LookPitch = 45f; // CS pitch: negative = up

    public static bool Performs(SimonOrder order, InputSample s) => order switch
    {
        SimonOrder.Jump => s.Jump,
        SimonOrder.Crouch => s.Duck,
        SimonOrder.StandStill => s.Speed2D < StillSpeed,
        SimonOrder.LookUp => s.Pitch <= -LookPitch,
        SimonOrder.LookDown => s.Pitch >= LookPitch,
        _ => false
    };

    /// <summary>
    /// Simon Says, judged per tick for one player. <paramref name="elapsed"/> = seconds since the order was given,
    /// <paramref name="done"/> = whether the player already performed a Simon order (sticky).
    /// Simon order: perform it before <paramref name="react"/> or fail (stand still: any movement after the grace fails).
    /// Order without "Simon says": performing it after the grace fails; otherwise pass when the window ends.
    /// </summary>
    public static Verdict Simon(SimonOrder order, bool simonSays, InputSample s, double elapsed, double grace, double react, bool done)
    {
        if (elapsed < grace) return Verdict.Pending;
        if (simonSays)
        {
            if (order == SimonOrder.StandStill)
                return s.Speed2D >= StillSpeed ? Verdict.Fail : elapsed >= react ? Verdict.Pass : Verdict.Pending;
            if (done || Performs(order, s)) return Verdict.Pass;
            return elapsed >= react ? Verdict.Fail : Verdict.Pending;
        }

        if (Performs(order, s)) return Verdict.Fail;
        return elapsed >= react ? Verdict.Pass : Verdict.Pending;
    }

    /// <summary>Red light: moving faster than the limit once the grace after "red" is over.</summary>
    public static bool RedLightOut(float speed2D, double sinceRed, double grace, float limit) => sinceRed >= grace && speed2D > limit;

    /// <summary>Random red/green phase length (2–6 s) from a 0..1 roll.</summary>
    public static double LightPhaseSeconds(double roll) => 2.0 + 4.0 * Math.Clamp(roll, 0, 1);

    /// <summary>
    /// Musical zones: each active zone is claimed by the player who entered it first (ties: lower slot); everyone else is out.
    /// <paramref name="inside"/> = (slot, zone id or null, entry tick).
    /// </summary>
    public static (HashSet<int> Safe, HashSet<int> Out) Musical(IEnumerable<(int Slot, int? Zone, long EntryTick)> inside, ICollection<int> activeZones)
    {
        var safe = new HashSet<int>();
        var all = inside.ToList();
        foreach (var g in all.Where(p => p.Zone is { } z && activeZones.Contains(z)).GroupBy(p => p.Zone!.Value))
            safe.Add(g.OrderBy(p => p.EntryTick).ThenBy(p => p.Slot).First().Slot);
        var @out = all.Select(p => p.Slot).Where(s => !safe.Contains(s)).ToHashSet();
        return (safe, @out);
    }

    /// <summary>Musical chairs: one zone fewer than the players still in (at least one).</summary>
    public static int MusicalZoneCount(int players, int zonesOnMap) => Math.Clamp(players - 1, 1, Math.Max(1, zonesOnMap));

    /// <summary>
    /// Last one standing: the winners once at most one player remains. If the last players went out together, they share it.
    /// Null while the game goes on.
    /// </summary>
    public static IReadOnlyList<int>? LastStanding(ICollection<int> remaining, ICollection<int> outThisCheck)
    {
        if (remaining.Count == 1) return remaining.ToList();
        if (remaining.Count == 0 && outThisCheck.Count > 0) return outThisCheck.ToList();
        return null;
    }
}

/// <summary>MultigamesQueue: everyone gets a turn to pick. Join = back of the queue, picking = back of the queue, leave = removed.</summary>
public sealed class PickerQueue
{
    private readonly List<int> _slots = new();

    public IReadOnlyList<int> Slots => _slots;

    public void Add(int slot)
    {
        if (!_slots.Contains(slot)) _slots.Add(slot);
    }

    public void Remove(int slot) => _slots.Remove(slot);

    public void Clear() => _slots.Clear();

    /// <summary>The first queued slot that may pick now, or null.</summary>
    public int? Next(Func<int, bool> canPick)
    {
        foreach (var s in _slots)
            if (canPick(s)) return s;
        return null;
    }

    /// <summary>The slot picked: move it to the back.</summary>
    public void Picked(int slot)
    {
        if (_slots.Remove(slot)) _slots.Add(slot);
    }
}

/// <summary>A map game button (MultigamesGameSelect), stored per map in maps/game_buttons/&lt;map&gt;.json.</summary>
public sealed class GameButtonRecord
{
    public string Label { get; set; } = "";
    public string Name { get; set; } = "";
    public string HammerId { get; set; } = "";
    public string Class { get; set; } = "";
    public float[] Origin { get; set; } = new float[3];
}

public sealed class GameButtonsDoc
{
    public List<GameButtonRecord> Buttons { get; set; } = new();
}

/// <summary>Jetpacks (hlstriker): climb speed while jump is held in the air, and the HUD fuel bar.</summary>
public static class Jetpack
{
    /// <summary>New vertical speed: thrust is added (it also brakes a fall) up to the cap; faster climbs (jump pads) are kept.</summary>
    public static float Climb(float vz, float thrust, float maxUp) => vz >= maxUp ? vz : Math.Min(maxUp, vz + Math.Max(0f, thrust));

    public static string FuelBar(float fuel, float max, int blocks = 10)
    {
        var filled = max <= 0 ? 0 : (int)Math.Round(Math.Clamp(fuel / max, 0f, 1f) * blocks);
        return "Fuel <font color='#55FF55'>" + new string('|', filled) + "</font><font color='#555555'>" + new string('|', blocks - filled) + "</font>";
    }
}

/// <summary>MeasureGap (Hymns For Disco): distance between two points.</summary>
public static class GapMath
{
    public static (float Total, float Horizontal, float Vertical) Measure(System.Numerics.Vector3 a, System.Numerics.Vector3 b)
    {
        var d = b - a;
        return (d.Length(), MathF.Sqrt(d.X * d.X + d.Y * d.Y), Math.Abs(d.Z));
    }
}
