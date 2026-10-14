using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class BettingTests
{
    [Theory]
    [InlineData(10, 4.0)] // 10 x 0.8 = 8, capped at the max (4)
    public void MultiplierFor_IsCapped(int alive, double max) =>
        Assert.Equal(max, BettingModule.MultiplierFor(alive, 0.8, 1.5, max));

    [Theory]
    [InlineData(3, 2.4)]
    [InlineData(5, 4.0)]
    [InlineData(10, 5.0)]
    [InlineData(1, 1.5)]
    [InlineData(0, 1.5)]
    public void MultiplierFor_UsesOddsBetweenMinAndMax(int alive, double expected) =>
        Assert.Equal(expected, BettingModule.MultiplierFor(alive, 0.8, 1.5, 5));

    [Fact]
    public void MultiplierFor_NeverBelowOne_EvenWithBadConfig() =>
        Assert.Equal(1.0, BettingModule.MultiplierFor(2, 0.1, 0.2, 0.5));

    [Theory]
    [InlineData(100, 2.4, 240)]
    [InlineData(33, 1.5, 49)]  // rounded down
    [InlineData(10, 5.0, 50)]
    public void Payout_IncludesStakeAndRoundsDown(int stake, double mult, int expected) =>
        Assert.Equal(expected, BettingModule.Payout(stake, mult));
}
