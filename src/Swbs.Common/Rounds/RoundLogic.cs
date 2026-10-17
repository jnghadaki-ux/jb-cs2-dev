namespace Swbs.Common;

/// <summary>Engine-free rules of the rounds system (unit-tested).</summary>
public static class RoundLogic
{
    /// <summary>
    /// Is the next round a variation round? With n normal rounds between variations, every (n+1)th round is one.
    /// <paramref name="normalSinceLast"/> counts the normal rounds played since the last variation.
    /// </summary>
    public static bool NextIsVariation(int normalRoundsBetween, int normalSinceLast) => normalSinceLast >= Math.Max(0, normalRoundsBetween);

    /// <summary>Weighted pick of one key (null when nothing has weight). <paramref name="roll"/> is in [0, total weight).</summary>
    public static string? PickWeighted(IReadOnlyList<(string Key, int Weight)> options, Func<int, int> roll)
    {
        var usable = options.Where(o => o.Weight > 0).ToList();
        var total = usable.Sum(o => o.Weight);
        if (total <= 0) return null;
        var r = roll(total);
        foreach (var (key, weight) in usable)
        {
            if (r < weight) return key;
            r -= weight;
        }

        return usable[^1].Key;
    }

    /// <summary>Up to <paramref name="count"/> different keys for a ballot, weighted, keeping the pick order.</summary>
    public static List<string> Ballot(IReadOnlyList<(string Key, int Weight)> options, int count, Func<int, int> roll)
    {
        var left = options.Where(o => o.Weight > 0).ToList();
        var ballot = new List<string>();
        while (ballot.Count < count && left.Count > 0)
        {
            var key = PickWeighted(left, roll)!;
            ballot.Add(key);
            left.RemoveAll(o => o.Key == key);
        }

        return ballot;
    }

    /// <summary>The option with the most votes; ties go to the earliest option on the ballot. -1 without votes.</summary>
    public static int VoteWinner(IEnumerable<int> votes, int options)
    {
        if (options <= 0) return -1;
        var counts = new int[options];
        var any = false;
        foreach (var v in votes)
        {
            if (v < 0 || v >= options) continue;
            counts[v]++;
            any = true;
        }

        if (!any) return -1;
        var best = 0;
        for (var i = 1; i < options; i++)
            if (counts[i] > counts[best]) best = i;
        return best;
    }

    /// <summary>Rotation: the key after <paramref name="last"/> in <paramref name="order"/> (the first when last is unknown).</summary>
    public static string? NextInRotation(IReadOnlyList<string> order, string? last)
    {
        if (order.Count == 0) return null;
        var i = last == null ? -1 : order.ToList().IndexOf(last);
        return order[(i + 1) % order.Count];
    }

    /// <summary>Round placings: slots by score (desc), ties by who got there first (lower order value).</summary>
    public static List<int> Placings(IReadOnlyDictionary<int, (int Score, long Order)> scores) =>
        scores.Where(kv => kv.Value.Score > 0).OrderByDescending(kv => kv.Value.Score).ThenBy(kv => kv.Value.Order).Select(kv => kv.Key).ToList();

    // ---- Gun Game (from SWBS Jailbreak DayLogic)

    /// <summary>The ladder with the knife forced as the final level and blanks removed.</summary>
    public static List<string> GunGameLadder(IEnumerable<string>? weapons)
    {
        var list = (weapons ?? Array.Empty<string>())
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim().ToLowerInvariant())
            .Select(w => w.StartsWith("weapon_") ? w : "weapon_" + w)
            .Where(w => !w.StartsWith("weapon_knife") && w != "weapon_bayonet")
            .ToList();
        list.Add("weapon_knife");
        return list;
    }

    /// <summary>Level after a kill; -1 when the kill finishes the ladder (a kill on the knife level).</summary>
    public static int GunGameAdvance(int level, int levels)
    {
        if (levels <= 0) return -1;
        level = Math.Clamp(level, 0, levels - 1);
        return level >= levels - 1 ? -1 : level + 1;
    }

    public static int GunGameDemote(int level, int levels, int by) => Math.Clamp(level - Math.Max(0, by), 0, Math.Max(0, levels - 1));

    // ---- mutators / objectives

    /// <summary>Vampire: health after healing <paramref name="share"/> of the damage dealt, never above the cap.</summary>
    public static int VampireHeal(int health, float damage, float share, int cap) =>
        Math.Min(Math.Max(health, cap), health + (int)MathF.Round(Math.Max(0, damage) * Math.Clamp(share, 0f, 1f)));

    /// <summary>Bounty Hunter: what killing the bounty is worth.</summary>
    public static int BountyValue(int baseValue, int killsAsBounty) => Math.Max(1, baseValue) + Math.Max(0, killsAsBounty);

    /// <summary>Juggernaut health for a server of this many players.</summary>
    public static int JuggernautHealth(int baseHealth, int perPlayer, int players) => Math.Max(100, baseHealth + Math.Max(0, perPlayer) * Math.Max(0, players - 1));

    /// <summary>Last Man Standing: the round is decided when at most one player still has lives (and someone played).</summary>
    public static bool LastManDecided(int playersWithLives, int playersInRound) => playersInRound >= 2 && playersWithLives <= 1;

    /// <summary>"4:05" for the HUD.</summary>
    public static string Clock(float seconds)
    {
        var s = Math.Max(0, (int)MathF.Ceiling(seconds));
        return $"{s / 60}:{s % 60:00}";
    }
}
