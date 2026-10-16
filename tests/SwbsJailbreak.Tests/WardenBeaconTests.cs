using SwbsJailbreak.Modules.Warden;
using Xunit;

namespace SwbsJailbreak.Tests;

public class WardenBeaconTests
{
    [Fact]
    public void DefaultTracerColour_IsTheFirstPreset() =>
        Assert.Equal("Orange-red", WardenBeaconRules.Name(new SwbsJailbreak.Modules.Warden.WardenConfig().PointerColor));

    [Fact]
    public void BeaconColourCycle_PassesThroughSameAsTracer()
    {
        var last = WardenBeaconRules.Colors[^1].Rgba;
        Assert.Empty(WardenBeaconRules.NextColor(last, allowEmpty: true));
        Assert.Equal(WardenBeaconRules.Colors[0].Rgba, WardenBeaconRules.NextColor(System.Array.Empty<int>(), allowEmpty: true));
        Assert.Equal(WardenBeaconRules.Colors[0].Rgba, WardenBeaconRules.NextColor(last, allowEmpty: false));
        Assert.Equal("same", WardenBeaconRules.Name(System.Array.Empty<int>(), "same"));
    }

    [Theory]
    [InlineData("red", 255, 30, 30)]
    [InlineData("orangered", 240, 50, 15)]
    [InlineData("10 20 30", 10, 20, 30)]
    [InlineData("10,20,30", 10, 20, 30)]
    public void ParseColor_AcceptsNamesAndRgb(string text, int r, int g, int b)
    {
        var c = WardenBeaconRules.ParseColor(text)!;
        Assert.Equal(new[] { r, g, b }, c[..3]);
    }

    [Theory]
    [InlineData("300 0 0")]
    [InlineData("1 2")]
    [InlineData("mauve")]
    public void ParseColor_RejectsBadInput(string text) => Assert.Null(WardenBeaconRules.ParseColor(text));

    [Fact]
    public void Next_CyclesAndRestartsFromUnknownValues()
    {
        Assert.Equal(60f, WardenBeaconRules.Next(WardenBeaconRules.BeaconSizes, 40f));
        Assert.Equal(25f, WardenBeaconRules.Next(WardenBeaconRules.BeaconSizes, 250f));
        Assert.Equal(25f, WardenBeaconRules.Next(WardenBeaconRules.BeaconSizes, 77f));
    }
}
