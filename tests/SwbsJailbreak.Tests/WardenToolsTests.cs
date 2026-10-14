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
