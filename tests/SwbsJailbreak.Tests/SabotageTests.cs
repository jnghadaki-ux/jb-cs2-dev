using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class SabotageTests
{
    [Fact]
    public void Catalogue_OriginalEffectsOn_NewOnesOff()
    {
        string[] on = { "smoke", "badtrip", "hotfloor", "heavyboots", "jammed", "paranoia", "thickskin", "lightfeet", "shadow", "secondwind" };
        foreach (var e in SabotageRules.Catalogue) Assert.Equal(on.Contains(e.Id), e.OnByDefault);
        Assert.Equal(SabotageRules.Catalogue.Length, SabotageRules.Catalogue.Select(e => e.Id).Distinct().Count());
        Assert.Equal(31, SabotageRules.Catalogue.Length);
    }

    [Fact]
    public void FillDefaults_AddsMissing_KeepsOwnerValues()
    {
        var map = new Dictionary<string, ShopEffectSettings> { ["smoke"] = new() { Enabled = false, Cost = 1 } };
        Assert.True(SabotageRules.FillDefaults(map));
        Assert.False(map["smoke"].Enabled);
        Assert.Equal(1, map["smoke"].Cost);
        Assert.True(map["badtrip"].Enabled);
        Assert.False(map["vampire"].Enabled);
        Assert.False(SabotageRules.FillDefaults(map));
    }

    [Theory]
    [InlineData(0, 30f, false, 0, false, 0, null)]
    [InlineData(0, 121f, false, 0, false, 0, "sab.err.window")]
    [InlineData(0, 30f, true, 0, false, 0, "sab.err.joined")]
    [InlineData(0, 30f, false, 1, false, 0, "sab.err.one_sabotage")]
    [InlineData(2, 30f, false, 1, false, 0, "sab.err.one_perk")]
    [InlineData(1, 30f, false, 0, true, 1, "sab.err.active")]
    [InlineData(1, 30f, false, 0, false, 2, "sab.err.max_round")]
    [InlineData(3, 30f, false, 0, false, 2, null)] // the guard cap does not limit perks
    public void CannotBuy_EnforcesWindowAndLimits(int kindValue, float elapsed, bool joined, int bought, bool active, int activeAgainst, string? why) =>
        Assert.Equal(why, SabotageRules.CannotBuy((EffectKind)kindValue, elapsed, 120, joined, bought, 1, active, activeAgainst, 2));

    [Fact]
    public void Interval_StaysInRange()
    {
        var rng = new System.Random(3);
        for (var i = 0; i < 100; i++)
        {
            var v = SabotageRules.Interval(35, 50, rng);
            Assert.InRange(v, 35f, 50f);
        }
    }
}
