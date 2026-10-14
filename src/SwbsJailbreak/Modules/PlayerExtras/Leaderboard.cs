namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>How much each stat adds to the overall jailbreak score used by !rank.</summary>
public sealed class RankWeights
{
    public double RoundSurvived { get; set; } = 1;
    public double WardenRound { get; set; } = 3;
    public double GuardKilled { get; set; } = 2;
    public double PrisonerKilled { get; set; } = 1;
    public double LastRequestDuelWon { get; set; } = 3;
    public double Death { get; set; } = 0;
    public double ContrabandFound { get; set; } = 1;
    public double SnitchCorrect { get; set; } = 1;

    /// <summary>Warden ratings a player needs before they appear on the "rating" board.</summary>
    public int RatingMinCount { get; set; } = 5;
}

/// <summary>What a leaderboard is ordered by.</summary>
public enum RankCategory
{
    Score,
    Warden,
    GuardKills,
    PrisonerKills,
    LastRequests,
    Survived,
    Rounds,
    Rating,
    Contraband,
    Snitch
}

public sealed record RankEntry(string SteamId, string Name, double Value, int Position);

/// <summary>Engine-free ranking over the stored <see cref="PlayerStats"/> (unit-tested).</summary>
public static class Leaderboard
{
    /// <summary>Words accepted after !rank, mapped to a category.</summary>
    public static readonly IReadOnlyDictionary<string, RankCategory> Aliases = new Dictionary<string, RankCategory>(StringComparer.OrdinalIgnoreCase)
    {
        ["score"] = RankCategory.Score, ["top"] = RankCategory.Score, ["overall"] = RankCategory.Score,
        ["warden"] = RankCategory.Warden, ["wardens"] = RankCategory.Warden,
        ["guards"] = RankCategory.GuardKills, ["guardkills"] = RankCategory.GuardKills, ["rebel"] = RankCategory.GuardKills,
        ["prisoners"] = RankCategory.PrisonerKills, ["prisonerkills"] = RankCategory.PrisonerKills,
        ["lr"] = RankCategory.LastRequests, ["lrs"] = RankCategory.LastRequests, ["duels"] = RankCategory.LastRequests,
        ["survived"] = RankCategory.Survived, ["survival"] = RankCategory.Survived,
        ["rounds"] = RankCategory.Rounds, ["played"] = RankCategory.Rounds,
        ["rating"] = RankCategory.Rating, ["ratings"] = RankCategory.Rating, ["rated"] = RankCategory.Rating,
        ["contraband"] = RankCategory.Contraband, ["searches"] = RankCategory.Contraband, ["search"] = RankCategory.Contraband,
        ["snitch"] = RankCategory.Snitch, ["snitches"] = RankCategory.Snitch, ["tips"] = RankCategory.Snitch
    };

    /// <summary>Average warden rating (1-5), 0 when unrated.</summary>
    public static double AverageRating(PlayerStats s) =>
        s.WardenRatingCount <= 0 ? 0 : Math.Round((double)s.WardenRatingSum / s.WardenRatingCount, 2);

    public static int Rounds(PlayerStats s) => s.RoundsPrisoner + s.RoundsGuard;

    public static double Score(PlayerStats s, RankWeights w) =>
        s.RoundsSurvived * w.RoundSurvived
        + s.WardenRounds * w.WardenRound
        + s.GuardsKilled * w.GuardKilled
        + s.PrisonersKilled * w.PrisonerKilled
        + s.LastRequestDuelsWon * w.LastRequestDuelWon
        + s.ContrabandFound * w.ContrabandFound
        + s.SnitchCorrect * w.SnitchCorrect
        - s.Deaths * w.Death;

    public static double Value(PlayerStats s, RankCategory category, RankWeights w) => category switch
    {
        RankCategory.Warden => s.WardenRounds,
        RankCategory.GuardKills => s.GuardsKilled,
        RankCategory.PrisonerKills => s.PrisonersKilled,
        RankCategory.LastRequests => s.LastRequestDuelsWon,
        RankCategory.Survived => s.RoundsSurvived,
        RankCategory.Rounds => Rounds(s),
        RankCategory.Rating => s.WardenRatingCount >= Math.Max(1, w.RatingMinCount) ? AverageRating(s) : 0,
        RankCategory.Contraband => s.ContrabandFound,
        RankCategory.Snitch => s.SnitchCorrect,
        _ => Math.Round(Score(s, w), 1)
    };

    /// <summary>
    /// Everyone with at least <paramref name="minRounds"/> rounds, best first. Ties share a position (1, 2, 2, 4) and
    /// are listed by name. Players with a value of 0 are left out of category boards.
    /// </summary>
    public static List<RankEntry> Build(IReadOnlyDictionary<string, PlayerStats> all, RankCategory category, RankWeights w, int minRounds)
    {
        var rows = all
            .Where(kv => Rounds(kv.Value) >= Math.Max(0, minRounds))
            .Select(kv => (Id: kv.Key, Name: string.IsNullOrWhiteSpace(kv.Value.Name) ? kv.Key : kv.Value.Name, Value: Value(kv.Value, category, w)))
            .Where(r => category == RankCategory.Score || r.Value > 0)
            .OrderByDescending(r => r.Value)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<RankEntry>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var position = i > 0 && rows[i].Value.Equals(rows[i - 1].Value) ? result[i - 1].Position : i + 1;
            result.Add(new RankEntry(rows[i].Id, rows[i].Name, rows[i].Value, position));
        }

        return result;
    }
}
