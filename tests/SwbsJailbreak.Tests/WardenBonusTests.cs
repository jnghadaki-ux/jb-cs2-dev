using SwbsJailbreak.Modules.Warden;
using Xunit;

namespace SwbsJailbreak.Tests;

public class WardenBonusTests
{
    [Theory]
    [InlineData(5, 2, 0)]  // many guards: no bonus
    [InlineData(3, 2, 0)]  // just above the limit
    [InlineData(2, 2, 1)]  // two guards: one step
    [InlineData(1, 2, 2)]  // last guard: bonus has stacked to two steps
    [InlineData(0, 2, 0)]  // nobody alive: nothing to give
    [InlineData(1, 1, 1)]
    [InlineData(2, 3, 2)]
    public void Steps_AppearOnlyWhenGuardsAreFew_AndStackAsTheyDie(int alive, int max, int expected) =>
        Assert.Equal(expected, WardenModule.BonusSteps(alive, max));

    [Fact]
    public void Steps_NeverDecreaseAsGuardsDie()
    {
        var last = 0;
        for (var alive = 6; alive >= 1; alive--)
        {
            var steps = WardenModule.BonusSteps(alive, 2);
            Assert.True(steps >= last);
            last = steps;
        }
    }
}
