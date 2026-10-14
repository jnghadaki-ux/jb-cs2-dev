using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class LeaderboardTests
{
    private static PlayerStats P(string name, int roundsT = 10, int roundsCt = 0, int survived = 0, int warden = 0, int guards = 0,
        int prisoners = 0, int duels = 0, int deaths = 0) => new()
    {
        Name = name, RoundsPrisoner = roundsT, RoundsGuard = roundsCt, RoundsSurvived = survived, WardenRounds = warden,
        GuardsKilled = guards, PrisonersKilled = prisoners, LastRequestDuelsWon = duels, Deaths = deaths
    };

    [Fact]
    public void Score_UsesTheWeights()
    {
        var s = P("a", survived: 4, warden: 2, guards: 3, prisoners: 5, duels: 1, deaths: 7);
        // 4*1 + 2*3 + 3*2 + 5*1 + 1*3 - 7*0
        Assert.Equal(24, Leaderboard.Score(s, new RankWeights()));
        Assert.Equal(17, Leaderboard.Score(s, new RankWeights { Death = 1 }));
    }

    [Fact]
    public void Build_OrdersBestFirst_AndSharesTiedPositions()
    {
        var all = new Dictionary<string, PlayerStats>
        {
            ["1"] = P("Zed", warden: 5),
            ["2"] = P("amy", warden: 9),
            ["3"] = P("Bob", warden: 5),
            ["4"] = P("Cat", warden: 1)
        };
        var board = Leaderboard.Build(all, RankCategory.Warden, new RankWeights(), 10);
        Assert.Equal(new[] { "amy", "Bob", "Zed", "Cat" }, board.Select(e => e.Name));
        Assert.Equal(new[] { 1, 2, 2, 4 }, board.Select(e => e.Position));
    }

    [Fact]
    public void Build_SkipsPlayersBelowMinRounds_AndZeroValuesOnCategoryBoards()
    {
        var all = new Dictionary<string, PlayerStats>
        {
            ["1"] = P("new", roundsT: 3, warden: 4),
            ["2"] = P("vet", roundsT: 6, roundsCt: 6, warden: 1),
            ["3"] = P("none", roundsT: 20)
        };
        var warden = Leaderboard.Build(all, RankCategory.Warden, new RankWeights(), 10);
        Assert.Equal(new[] { "vet" }, warden.Select(e => e.Name));

        var overall = Leaderboard.Build(all, RankCategory.Score, new RankWeights(), 10);
        Assert.Equal(new[] { "vet", "none" }, overall.Select(e => e.Name)); // score boards keep 0-score players
    }

    [Theory]
    [InlineData("warden", RankCategory.Warden)]
    [InlineData("LR", RankCategory.LastRequests)]
    [InlineData("guards", RankCategory.GuardKills)]
    [InlineData("top", RankCategory.Score)]
    public void Aliases_MapToCategories(string word, RankCategory expected) =>
        Assert.Equal(expected, Leaderboard.Aliases[word]);
}
