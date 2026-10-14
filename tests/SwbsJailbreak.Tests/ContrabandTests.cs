using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class ContrabandTests
{
    [Theory]
    [InlineData("weapon_ak47", true)]
    [InlineData("weapon_glock", true)]
    [InlineData("weapon_hegrenade", true)]
    [InlineData("weapon_taser", true)]
    [InlineData("weapon_knife", false)]
    [InlineData("weapon_knife_t", false)]
    [InlineData("weapon_bayonet", false)]
    [InlineData("weapon_c4", false)]       // the Jihad bomb has its own rules
    [InlineData("prop_dynamic", false)]
    public void IsContraband_AllowsKnivesAndTheBomb(string weapon, bool expected) =>
        Assert.Equal(expected, ContrabandModule.IsContraband(weapon));

    [Fact]
    public void Boards_RankContrabandAndSnitchTips()
    {
        var all = new Dictionary<string, PlayerStats>
        {
            ["1"] = new() { Name = "Cop", RoundsGuard = 12, SearchesMade = 9, ContrabandFound = 4 },
            ["2"] = new() { Name = "Rat", RoundsPrisoner = 12, SnitchTips = 5, SnitchCorrect = 3 },
            ["3"] = new() { Name = "Nobody", RoundsPrisoner = 12 }
        };
        var w = new RankWeights();
        Assert.Equal(new[] { "Cop" }, Leaderboard.Build(all, RankCategory.Contraband, w, 10).Select(e => e.Name));
        Assert.Equal(new[] { "Rat" }, Leaderboard.Build(all, RankCategory.Snitch, w, 10).Select(e => e.Name));
        Assert.Equal(RankCategory.Contraband, Leaderboard.Aliases["searches"]);
        Assert.Equal(RankCategory.Snitch, Leaderboard.Aliases["snitch"]);
    }

    [Fact]
    public void Score_CountsContrabandAndCorrectTips()
    {
        var s = new PlayerStats { ContrabandFound = 4, SnitchCorrect = 3 };
        Assert.Equal(7, Leaderboard.Score(s, new RankWeights()));
        Assert.Equal(11, Leaderboard.Score(s, new RankWeights { ContrabandFound = 2 }));
    }
}
