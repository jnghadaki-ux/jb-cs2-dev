using SwbsJailbreak.Core;
using Xunit;

namespace SwbsJailbreak.Tests;

public class AvailabilityTests
{
    [Fact]
    public void Clicks_CycleOnThenMapThenEverywhere()
    {
        var map = new List<string>();
        var all = new List<string>();
        const string key = "Weapon Battle/Gun Roulette";
        Assert.Equal(Availability.On, AvailabilityRules.Of(map, all, key));

        var next = AvailabilityRules.Next(AvailabilityRules.Of(map, all, key));
        AvailabilityRules.Apply(map, all, key, next);
        Assert.Equal(Availability.OffThisMap, AvailabilityRules.Of(map, all, key));
        Assert.Equal(new[] { key }, map);
        Assert.Empty(all);

        next = AvailabilityRules.Next(AvailabilityRules.Of(map, all, key));
        AvailabilityRules.Apply(map, all, key, next);
        Assert.Equal(Availability.OffEverywhere, AvailabilityRules.Of(map, all, key));
        Assert.Empty(map);
        Assert.Equal(new[] { key }, all);

        next = AvailabilityRules.Next(AvailabilityRules.Of(map, all, key));
        AvailabilityRules.Apply(map, all, key, next);
        Assert.Equal(Availability.On, AvailabilityRules.Of(map, all, key));
        Assert.Empty(map);
        Assert.Empty(all);
    }

    [Fact]
    public void EverywhereWinsOverTheMapList_AndOtherKeysStay()
    {
        var map = new List<string> { "a", "b" };
        var all = new List<string> { "b", "c" };
        Assert.Equal(Availability.OffEverywhere, AvailabilityRules.Of(map, all, "b"));
        Assert.Equal(Availability.OffThisMap, AvailabilityRules.Of(map, all, "a"));
        AvailabilityRules.Apply(map, all, "b", Availability.On);
        Assert.Equal(new[] { "a" }, map);
        Assert.Equal(new[] { "c" }, all);
        Assert.Equal("toggle.off_all", AvailabilityRules.TagKey(Availability.OffEverywhere));
        Assert.Equal("toggle.on", AvailabilityRules.TagKey(Availability.On));
    }
}
