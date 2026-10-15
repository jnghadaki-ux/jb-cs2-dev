using SwbsJailbreak.Modules.Days;
using SwbsJailbreak.Modules.Games;
using SwbsJailbreak.Modules.Warden;
using Xunit;

namespace SwbsJailbreak.Tests;

public class DuckHuntAndRocketJumpTests
{
    [Theory]
    [InlineData(100, 25, 0, 100)]
    [InlineData(100, 25, 8, 300)]
    [InlineData(100, -5, 8, 100)]   // a negative per-hunter value never lowers the base
    [InlineData(0, 0, 3, 1)]        // never a 0 HP duck
    public void DuckHealth_GrowsWithHunters(int baseHp, int per, int hunters, int expected) =>
        Assert.Equal(expected, DuckHuntRules.DuckHealth(baseHp, per, hunters));

    [Fact]
    public void HunterWeapons_FollowTheSetting()
    {
        Assert.Equal(new[] { "weapon_ssg08", "weapon_deagle" }, DuckHuntRules.HunterWeapons("both"));
        Assert.Equal(new[] { "weapon_ssg08" }, DuckHuntRules.HunterWeapons("Scout"));
        Assert.Equal(new[] { "weapon_deagle" }, DuckHuntRules.HunterWeapons("deagle"));
        Assert.Equal(new[] { "weapon_ssg08", "weapon_deagle" }, DuckHuntRules.HunterWeapons("nonsense"));
    }

    [Theory]
    [InlineData(true, true, true, true, true)]     // hunter shoots the flying duck
    [InlineData(true, true, true, false, false)]   // not before release
    [InlineData(true, false, true, true, false)]   // hunters never hurt each other
    [InlineData(false, false, true, true, false)]  // the duck cannot hurt hunters
    [InlineData(true, false, false, true, true)]   // other-team damage follows the normal jailbreak rules
    public void OnlyHuntersHitTheDuck(bool hunter, bool duck, bool sameTeam, bool released, bool allowed) =>
        Assert.Equal(allowed, DuckHuntRules.AllowHit(hunter, duck, sameTeam, released));

    [Theory]
    [InlineData(RocketMode.JumpFreeday, false, true, false)]  // freeday: rockets only push
    [InlineData(RocketMode.JumpFreeday, true, true, false)]
    [InlineData(RocketMode.JumpWarday, false, true, true)]    // warday: airborne players are hit
    [InlineData(RocketMode.JumpWarday, false, false, false)]  // grounded players are safe
    [InlineData(RocketMode.JumpWarday, true, true, false)]    // no self damage for rocket jumps
    [InlineData(RocketMode.Rpg, true, false, true)]           // RPG warday keeps the original rules
    public void RocketDamage_PerMode(RocketMode mode, bool self, bool airborne, bool hurts) =>
        Assert.Equal(hurts, RpgDay.RocketHurts(mode, self, airborne));

    [Fact]
    public void Deputy_HasAStockModelByDefault() =>
        Assert.Equal("agents/models/ctm_diver/ctm_diver_varianta.vmdl", new WardenToolsConfig().DeputyModel);
}

public class PackPrisonerModelTests
{
    private const string Pack = "characters/models/swbs/prisoners/male_black_big/male_black_big.vmdl";

    [Fact]
    public void PackPrisoners_AreSkippedByDefault()
    {
        var stock = new SwbsJailbreak.Modules.PlayerExtras.PlayerModelsConfig().PrisonerModels;
        Assert.Equal(stock, SwbsJailbreak.Modules.PlayerExtras.PlayerModelsModule.Usable(new[] { Pack }, false));
        Assert.Equal(new[] { Pack }, SwbsJailbreak.Modules.PlayerExtras.PlayerModelsModule.Usable(new[] { Pack }, true));
        Assert.DoesNotContain(stock, SwbsJailbreak.Modules.PlayerExtras.PlayerModelsModule.IsPackPrisoner);
    }
}
