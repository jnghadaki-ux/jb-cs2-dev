using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class PoolsTests
{
    [Theory]
    [InlineData(30f, 0, 0, null)]
    [InlineData(61f, 0, 0, "pools.err.start_window")]   // only in the first minute
    [InlineData(10f, 1, 0, "pools.err.started_already")] // one start per player per round
    [InlineData(10f, 0, 3, "pools.err.category_full")]   // at most 3 per category
    public void Starting_IsLimitedToTheFirstMinute_AndCannotBeSpammed(float elapsed, int started, int inCategory, string? why) =>
        Assert.Equal(why, PoolRules.CannotStart(elapsed, 60, started, 1, inCategory, 3));

    [Fact]
    public void Split_SharesThePotWithoutLosingPoints()
    {
        Assert.Equal(new[] { 34, 33, 33 }, PoolRules.Split(100, 3));
        Assert.Equal(100, PoolRules.Split(100, 7).Sum());
        Assert.Empty(PoolRules.Split(100, 0));
    }

    [Fact]
    public void Keys_AreCaseInsensitive_PerCategory()
    {
        Assert.Equal(PoolRules.Key(PoolCategory.Day, "Hide and Seek"), PoolRules.Key(PoolCategory.Day, " hide and seek "));
        Assert.NotEqual(PoolRules.Key(PoolCategory.Day, "x"), PoolRules.Key(PoolCategory.Game, "x"));
    }
}
