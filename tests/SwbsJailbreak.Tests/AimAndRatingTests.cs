using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class AimAndRatingTests
{
    private static readonly Vector Eye = new(0, 0, 0);
    private static readonly Vector East = new(1, 0, 0);

    [Fact]
    public void PickInCone_TakesTheClosestToTheCrosshair()
    {
        var picked = Aim.PickInCone(Eye, East, new List<(int, Vector)>
        {
            (1, new Vector(500, 40, 0)),  // ~4.6 degrees off
            (2, new Vector(500, 10, 0)),  // ~1.1 degrees off
            (3, new Vector(-500, 0, 0))   // behind
        }, 1500, 6);
        Assert.Equal(2, picked);
    }

    [Fact]
    public void PickInCone_IgnoresOutsideTheConeOrRange()
    {
        Assert.Equal(-1, Aim.PickInCone(Eye, East, new List<(int, Vector)> { (1, new Vector(500, 200, 0)) }, 1500, 6));
        Assert.Equal(-1, Aim.PickInCone(Eye, East, new List<(int, Vector)> { (1, new Vector(2000, 0, 0)) }, 1500, 6));
        Assert.Equal(-1, Aim.PickInCone(Eye, new Vector(0, 0, 0), new List<(int, Vector)> { (1, new Vector(100, 0, 0)) }, 1500, 6));
    }

    [Fact]
    public void PickInCone_SameAngle_PrefersTheNearerPlayer()
    {
        var picked = Aim.PickInCone(Eye, East, new List<(int, Vector)>
        {
            (1, new Vector(900, 0, 0)),
            (2, new Vector(300, 0, 0))
        }, 1500, 6);
        Assert.Equal(2, picked);
    }

    [Fact]
    public void RatingBoard_NeedsMinimumRatings_AndAverages()
    {
        var all = new Dictionary<string, PlayerStats>
        {
            ["1"] = new() { Name = "Fair", RoundsGuard = 20, WardenRatingSum = 27, WardenRatingCount = 6 },  // 4.5
            ["2"] = new() { Name = "Few", RoundsGuard = 20, WardenRatingSum = 10, WardenRatingCount = 2 },   // too few
            ["3"] = new() { Name = "Harsh", RoundsGuard = 20, WardenRatingSum = 10, WardenRatingCount = 5 }  // 2.0
        };
        var board = Leaderboard.Build(all, RankCategory.Rating, new RankWeights(), 10);
        Assert.Equal(new[] { "Fair", "Harsh" }, board.Select(e => e.Name));
        Assert.Equal(4.5, board[0].Value);
        Assert.Equal(0, Leaderboard.AverageRating(new PlayerStats()));
    }
}
