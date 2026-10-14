using SwbsJailbreak;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.Extras;
using SwbsJailbreak.Modules.Warden;
using Xunit;

namespace SwbsJailbreak.Tests;

public class WardenToolsTests
{
    private static List<CommandEntry> Catalogue() => CommandCatalog.Build(
        typeof(JailbreakPlugin).Assembly.GetTypes().Where(t => typeof(JbModule).IsAssignableFrom(t) || t == typeof(JailbreakPlugin)));

    [Theory]
    [InlineData("css_rand")]
    [InlineData("css_ff")]
    [InlineData("css_extend")]
    [InlineData("css_deputy")]
    [InlineData("css_repeat")]
    [InlineData("css_guns")]
    [InlineData("css_ts")]
    [InlineData("css_swapteams")]
    [InlineData("css_stats")]
    [InlineData("css_rank")]
    [InlineData("css_votewarden")]
    [InlineData("css_split")]
    [InlineData("css_fkslay")]
    public void NewCommands_AreRegistered(string command)
    {
        var name = command["css_".Length..];
        Assert.Contains(Catalogue(), e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
                                          || e.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryParseColor_ReadsRgbAndClamps()
    {
        Assert.True(WardenToolsModule.TryParseColor("255, 215, 0", out var c));
        Assert.Equal((255, 215, 0), (c.R, c.G, c.B));
        Assert.True(WardenToolsModule.TryParseColor("300,-5,10", out c));
        Assert.Equal((255, 0, 10), (c.R, c.G, c.B));
        Assert.False(WardenToolsModule.TryParseColor("", out _));
        Assert.False(WardenToolsModule.TryParseColor("1,2", out _));
        Assert.False(WardenToolsModule.TryParseColor("a,b,c", out _));
    }
}

public class VoteAndSplitTests
{
    [Theory]
    [InlineData(10, 0.6, 3, 6)]  // 60 % of 10
    [InlineData(4, 0.6, 3, 3)]   // minimum wins
    [InlineData(7, 0.6, 3, 5)]   // 4.2 rounds up
    [InlineData(0, 0.6, 3, 3)]
    [InlineData(20, 1.5, 3, 20)] // ratio clamped to 100 %
    public void VotesNeeded_RoundsUpAndRespectsMinimum(int eligible, double ratio, int min, int expected) =>
        Assert.Equal(expected, SwbsJailbreak.Modules.Warden.WardenToolsModule.VotesNeeded(eligible, ratio, min));

    [Theory]
    [InlineData(10, 2)]
    [InlineData(10, 3)]
    [InlineData(7, 4)]
    [InlineData(4, 4)]
    public void SplitAssign_IsBalanced(int count, int teams)
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var result = SwbsJailbreak.Modules.Warden.WardenToolsModule.SplitAssign(count, teams, new Random(seed));
            Assert.Equal(count, result.Length);
            var sizes = Enumerable.Range(0, teams).Select(t => result.Count(r => r == t)).ToList();
            Assert.True(sizes.Max() - sizes.Min() <= 1);
            Assert.All(result, r => Assert.InRange(r, 0, teams - 1));
        }
    }
}
