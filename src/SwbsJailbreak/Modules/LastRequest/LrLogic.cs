using System.Numerics;

namespace SwbsJailbreak.Modules.LastRequest;

/// <summary>Engine-free rules of the 0.15 last requests (unit-tested).</summary>
internal static class LrLogic
{
    // ------------------------------------------------------------------ best of N

    /// <summary>0 = a has won the match, 1 = b has, -1 = still playing.</summary>
    public static int MatchWinner(int scoreA, int scoreB, int needed) =>
        scoreA >= needed ? 0 : scoreB >= needed ? 1 : -1;

    /// <summary>After the last allowed round: the higher score wins, a tie goes to <paramref name="coin"/> (0 = a, 1 = b).</summary>
    public static int FinalWinner(int scoreA, int scoreB, int coin) => scoreA > scoreB ? 0 : scoreB > scoreA ? 1 : coin == 0 ? 0 : 1;

    // ------------------------------------------------------------------ Rock Paper Scissors

    public enum Rps
    {
        Rock,
        Paper,
        Scissors
    }

    /// <summary>1 = a wins the round, -1 = b wins, 0 = tie (same pick, or neither picked). No pick loses to a pick.</summary>
    public static int RpsRound(Rps? a, Rps? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        if (a == b) return 0;
        return (a, b) switch
        {
            (Rps.Rock, Rps.Scissors) or (Rps.Paper, Rps.Rock) or (Rps.Scissors, Rps.Paper) => 1,
            _ => -1
        };
    }

    // ------------------------------------------------------------------ Math Race / Type Race

    /// <summary>A sum with a whole, positive-or-zero answer: a + b, a - b (a ≥ b) or a × b (times tables).</summary>
    public static (string Text, int Answer) MathQuestion(Random r)
    {
        switch (r.Next(3))
        {
            case 0:
            {
                int a = r.Next(10, 100), b = r.Next(10, 100);
                return ($"{a} + {b}", a + b);
            }
            case 1:
            {
                int a = r.Next(20, 100), b = r.Next(1, a + 1);
                return ($"{a} - {b}", a - b);
            }
            default:
            {
                int a = r.Next(2, 13), b = r.Next(2, 13);
                return ($"{a} x {b}", a * b);
            }
        }
    }

    public static bool IsAnswer(string? typed, int answer) =>
        int.TryParse((typed ?? "").Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var v) && v == answer;

    public static bool TypedMatch(string? typed, string word) =>
        string.Equals((typed ?? "").Trim(), word.Trim(), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ Gun Roulette

    /// <summary><paramref name="count"/> guns from the pool, all different while the pool allows it.</summary>
    public static List<string> PickGuns(IReadOnlyList<string> pool, int count, Random r)
    {
        var clean = pool.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<string>();
        if (clean.Count == 0 || count <= 0) return result;
        var bag = new List<string>();
        while (result.Count < count)
        {
            if (bag.Count == 0) bag.AddRange(clean.OrderBy(_ => r.Next()));
            result.Add(bag[^1]);
            bag.RemoveAt(bag.Count - 1);
        }

        return result;
    }

    // ------------------------------------------------------------------ Longest Jump

    /// <summary>Horizontal jump distance, or null when the landing is more than <paramref name="maxHeightDiff"/> above/below the takeoff.</summary>
    public static float? JumpDistance(Vector3 takeoff, Vector3 landing, float maxHeightDiff)
    {
        if (MathF.Abs(landing.Z - takeoff.Z) > maxHeightDiff) return null;
        var d = new Vector2(landing.X - takeoff.X, landing.Y - takeoff.Y);
        return d.Length();
    }

    // ------------------------------------------------------------------ Target Practice

    /// <summary>Shortest distance from <paramref name="point"/> to the shot segment from → to.</summary>
    public static float SegmentDistance(Vector3 from, Vector3 to, Vector3 point)
    {
        var d = to - from;
        var len2 = d.LengthSquared();
        if (len2 < 1e-6f) return Vector3.Distance(from, point);
        var t = Math.Clamp(Vector3.Dot(point - from, d) / len2, 0f, 1f);
        return Vector3.Distance(from + d * t, point);
    }

    /// <summary>Points for a shot passing <paramref name="distance"/> units from a target's centre.</summary>
    public static int TargetScore(float distance) => distance switch
    {
        <= 6f => 10,
        <= 12f => 7,
        <= 20f => 4,
        <= 30f => 1,
        _ => 0
    };

    // ------------------------------------------------------------------ Simon Says

    public enum SimonAction
    {
        Jump,
        Crouch,
        LookUp,
        LookDown,
        Freeze
    }

    /// <summary>Did the player do what Simon said during the window? (pitch: negative = looking up.)</summary>
    public static bool SimonPassed(SimonAction action, bool jumped, bool crouching, float pitch, bool moved) => action switch
    {
        SimonAction.Jump => jumped,
        SimonAction.Crouch => crouching,
        SimonAction.LookUp => pitch <= -45f,
        SimonAction.LookDown => pitch >= 45f,
        SimonAction.Freeze => !moved,
        _ => false
    };

    /// <summary>Seconds to react in round <paramref name="round"/> (1-based): 2.5 s, 0.15 s less each round, at least 1.2 s.</summary>
    public static float SimonWindow(int round) => MathF.Max(1.2f, 2.5f - 0.15f * Math.Max(0, round - 1));
}
