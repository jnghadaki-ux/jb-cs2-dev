namespace SwbsJailbreak.Modules.Days;

/// <summary>Engine-free rules of the 0.14 days (unit-tested).</summary>
internal static class DayLogic
{
    // ------------------------------------------------------------------ Gun Game

    /// <summary>
    /// Level after a kill. Any kill with the level weapon or the knife moves up one level; the last level is the knife.
    /// Returns -1 when the kill wins the game (a knife kill on the knife level, or any kill there).
    /// </summary>
    public static int GunGameAdvance(int level, int levels)
    {
        if (levels <= 0) return -1;
        level = Math.Clamp(level, 0, levels - 1);
        return level >= levels - 1 ? -1 : level + 1;
    }

    /// <summary>Level of a victim knifed by someone (classic arms race: they lose one level, never below 0).</summary>
    public static int GunGameDemote(int level, int levels, int by) => Math.Clamp(level - Math.Max(0, by), 0, Math.Max(0, levels - 1));

    /// <summary>The weapon list with the knife forced as the final level and blanks removed.</summary>
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

    /// <summary>Slot of the highest level (ties: whoever reached it first, i.e. the lowest order value).</summary>
    public static int GunGameLeader(IReadOnlyDictionary<int, (int Level, long Order)> levels)
    {
        var best = -1;
        var bestLevel = -1;
        var bestOrder = long.MaxValue;
        foreach (var (slot, (level, order)) in levels)
        {
            if (level > bestLevel || level == bestLevel && order < bestOrder)
            {
                best = slot;
                bestLevel = level;
                bestOrder = order;
            }
        }

        return best;
    }

    // ------------------------------------------------------------------ Tournament

    /// <summary>
    /// Pairs for one bracket round in entry order: (a, b) duels, and a lone last entrant gets a bye (b = -1).
    /// </summary>
    public static List<(int A, int B)> BracketPairs(IReadOnlyList<int> entrants)
    {
        var pairs = new List<(int, int)>();
        for (var i = 0; i < entrants.Count; i += 2)
            pairs.Add(i + 1 < entrants.Count ? (entrants[i], entrants[i + 1]) : (entrants[i], -1));
        return pairs;
    }

    public enum TournamentPhase
    {
        None,
        Lobby,
        Running
    }

    /// <summary>
    /// Knife Tournament LR menu state: null = can be picked, else the phrase key shown next to it. A new tournament needs
    /// at least two prisoners who are still choosing their LR (the picker included); an open lobby can always be joined.
    /// </summary>
    public static string? TournamentUnavailable(TournamentPhase phase, int choosingPrisoners) => phase switch
    {
        TournamentPhase.Running => "lr.menu.tournament_running",
        TournamentPhase.Lobby => null,
        _ => choosingPrisoners >= 2 ? null : "lr.menu.need_two_t"
    };

    /// <summary>Winner of a timed-out duel: more health wins; a tie goes to <paramref name="coin"/> (0 = a, 1 = b).</summary>
    public static int DuelTimeoutWinner(int a, int healthA, int b, int healthB, int coin) =>
        healthA > healthB ? a : healthB > healthA ? b : coin == 0 ? a : b;

    // ------------------------------------------------------------------ Teleport Freeday

    /// <summary>
    /// Swap pairs from an already shuffled list: consecutive players are paired, a pair is used only when at least one
    /// of the two is a prisoner (<paramref name="isPrisoner"/>), and with an odd count the last player stays.
    /// </summary>
    public static List<(int A, int B)> SwapPairs(IReadOnlyList<int> shuffled, Func<int, bool> isPrisoner)
    {
        var pairs = new List<(int, int)>();
        for (var i = 0; i + 1 < shuffled.Count; i += 2)
        {
            var a = shuffled[i];
            var b = shuffled[i + 1];
            if (isPrisoner(a) || isPrisoner(b)) pairs.Add((a, b));
        }

        return pairs;
    }

    // ------------------------------------------------------------------ Spy Day

    /// <summary>Number of spies: one per <paramref name="ratio"/> prisoners (rounded), at least 1, at most prisoners - 1 and the cap.</summary>
    public static int SpyCount(int prisoners, int ratio, int max)
    {
        if (prisoners < 2) return 0;
        var n = (int)Math.Round(prisoners / (double)Math.Max(1, ratio), MidpointRounding.AwayFromZero);
        n = Math.Max(1, n);
        if (max > 0) n = Math.Min(n, max);
        return Math.Min(n, prisoners - 1);
    }

    /// <summary>What a hit does on Spy Day.</summary>
    public enum SpyHit
    {
        Allow,
        Block,
        /// <summary>A guard hit an innocent prisoner: blocked, and the guard takes part of the damage.</summary>
        Misfire
    }

    /// <summary>
    /// Damage rule between roles (guard, spy, innocent prisoner): guards and spies fight each other; innocents never
    /// deal or take player damage from spies; a guard hitting an innocent misfires; same-role hits are blocked.
    /// </summary>
    public static SpyHit SpyDamage(bool attackerGuard, bool attackerSpy, bool victimGuard, bool victimSpy)
    {
        if (attackerGuard && victimSpy) return SpyHit.Allow;
        if (attackerSpy && victimGuard) return SpyHit.Allow;
        if (attackerGuard && !victimGuard && !victimSpy) return SpyHit.Misfire;
        return SpyHit.Block;
    }

    // ------------------------------------------------------------------ Speedrun

    /// <summary>"m:ss.cc" (or "s.cc" under a minute) for race times.</summary>
    public static string RaceTime(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var cs = (long)Math.Round(seconds * 100);
        var m = cs / 6000;
        var s = cs / 100 % 60;
        var c = cs % 100;
        return m > 0 ? $"{m}:{s:00}.{c:00}" : $"{s}.{c:00}";
    }

    /// <summary>Points for a finishing place (1-based) from the reward list; 0 past its end.</summary>
    public static int PlaceReward(IReadOnlyList<int>? rewards, int place) =>
        rewards == null || place < 1 || place > rewards.Count ? 0 : Math.Max(0, rewards[place - 1]);

    /// <summary>True when <paramref name="time"/> beats the stored best (no best yet counts as beaten).</summary>
    public static bool IsBetter(double time, double? best) => best == null || best <= 0 || time < best.Value;
}
