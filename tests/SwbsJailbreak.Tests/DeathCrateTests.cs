using System.Numerics;
using SwbsJailbreak.Modules.Games;
using Xunit;

namespace SwbsJailbreak.Tests;

public class DeathCrateTests
{
    [Fact]
    public void Speed_GrowsWithTime_UpToTheCap()
    {
        Assert.Equal(380f, DeathCrateRules.Speed(380, 12, 1100, 0));
        Assert.Equal(500f, DeathCrateRules.Speed(380, 12, 1100, 10));
        Assert.Equal(1100f, DeathCrateRules.Speed(380, 12, 1100, 1000));
    }

    [Fact]
    public void ClampToRing_PullsOutsidePointsToTheEdge()
    {
        var c = new Vector3(100, 100, 50);
        var p = DeathCrateRules.ClampToRing(c, 200, new Vector3(600, 100, 999));
        Assert.Equal(new Vector3(300, 100, 50), p);
        Assert.Equal(new Vector3(150, 100, 50), DeathCrateRules.ClampToRing(c, 200, new Vector3(150, 100, 0)));
    }

    [Theory]
    [InlineData(0, 0, 0, true)]     // standing on the crate's spot
    [InlineData(55, 0, 0, true)]    // crate half 40 + hull 16 reaches x=56
    [InlineData(57, 0, 0, false)]
    [InlineData(0, 0, 81, false)]   // above the 80-unit crate
    public void Hits_UsesTheCrateBoxAndPlayerHull(float x, float y, float z, bool hit) =>
        Assert.Equal(hit, DeathCrateRules.Hits(Vector3.Zero, 40, new Vector3(x, y, z)));

    [Fact]
    public void OutsideRing_IgnoresHeight()
    {
        Assert.False(DeathCrateRules.OutsideRing(Vector3.Zero, 100, new Vector3(60, 60, 500)));
        Assert.True(DeathCrateRules.OutsideRing(Vector3.Zero, 100, new Vector3(80, 80, 0)));
    }
}
