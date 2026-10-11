using System.Text.Json;
using SwbsJailbreak.Core;
using Xunit;

namespace SwbsJailbreak.Tests;

public class CrewRuleTests
{
    private static readonly string[] Banned = { "admin", "nazi" };

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 1)]
    [InlineData(100, 2)]
    [InlineData(299, 2)]
    [InlineData(300, 3)]
    [InlineData(6000, 7)]
    [InlineData(999999, 7)]
    public void Level_FollowsTheThresholds(int legacy, int level) => Assert.Equal(level, CrewRules.Level(legacy));

    [Fact]
    public void ToNextLevel_CountsDown_AndIsZeroAtMax()
    {
        Assert.Equal(100, CrewRules.ToNextLevel(0));
        Assert.Equal(1, CrewRules.ToNextLevel(299));
        Assert.Equal(0, CrewRules.ToNextLevel(6000));
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(3, 6)]
    [InlineData(7, 8)] // capped at MaxSlots
    public void Slots_GrowWithLevel_UpToTheCap(int level, int slots) => Assert.Equal(slots, CrewRules.Slots(level, 4, 8));

    [Theory]
    [InlineData("The Kings", true)]
    [InlineData("Ab", false)]                       // too short
    [InlineData("Twenty Five Characters!!", true)]  // 24
    [InlineData("This Name Is Far Too Long Ok", false)]
    [InlineData(" Kings", false)]                   // edge space
    [InlineData("The  Kings", false)]               // double space
    [InlineData("K<b>ings", false)]                 // markup characters
    [InlineData("Real Admins", false)]              // banned word
    [InlineData("N.a.z.i Crew", false)]             // banned word hidden with dots
    public void Names_AreValidated(string name, bool ok) => Assert.Equal(ok, CrewRules.ValidName(name, Banned, out _));

    [Theory]
    [InlineData("KNG", true)]
    [InlineData("K", false)]
    [InlineData("ABCDEF", false)]
    [InlineData("K-G", false)]
    [InlineData("ADMIN", false)]
    public void Tags_AreValidated(string tag, bool ok) => Assert.Equal(ok, CrewRules.ValidTag(tag, Banned, out _));

    [Fact]
    public void Tags_AreUpperCased() => Assert.Equal("KNG", CrewRules.NormalizeTag(" kng "));

    [Theory]
    [InlineData(CrewRank.Boss, CrewRank.Lieutenant, true)]
    [InlineData(CrewRank.Boss, CrewRank.Member, true)]
    [InlineData(CrewRank.Boss, CrewRank.Boss, false)]
    [InlineData(CrewRank.Lieutenant, CrewRank.Member, true)]
    [InlineData(CrewRank.Lieutenant, CrewRank.Lieutenant, false)]
    [InlineData(CrewRank.Lieutenant, CrewRank.Boss, false)]
    [InlineData(CrewRank.Member, CrewRank.Member, false)]
    public void KickRights(CrewRank actor, CrewRank target, bool allowed) => Assert.Equal(allowed, CrewRules.CanKick(actor, target));

    [Fact]
    public void OnlyTheBossManages_OfficersInviteAndCallFavors()
    {
        Assert.True(CrewRules.CanManageCrew(CrewRank.Boss));
        Assert.False(CrewRules.CanManageCrew(CrewRank.Lieutenant));
        Assert.True(CrewRules.CanInvite(CrewRank.Lieutenant));
        Assert.False(CrewRules.CanInvite(CrewRank.Member));
        Assert.True(CrewRules.CanUseFavors(CrewRank.Lieutenant));
        Assert.False(CrewRules.CanUseFavors(CrewRank.Member));
    }
}

public class CrewRespectTests
{
    private const long Week = 7L * 24 * 3600;

    [Fact]
    public void Decay_CompoundsPerWholeWeek_AndKeepsTheRemainder()
    {
        var crew = new Crew { Respect = 1000, LastDecayUnix = 1_000_000 };
        CrewRules.ApplyDecay(crew, 1_000_000 + 2 * Week + 100, 10);
        Assert.Equal(810, crew.Respect);                        // 1000 * 0.9 * 0.9
        Assert.Equal(1_000_000 + 2 * Week, crew.LastDecayUnix); // the extra 100 s still count toward the next week
    }

    [Fact]
    public void Decay_WaitsAFullWeek_AndStampsNewCrews()
    {
        var crew = new Crew { Respect = 500, LastDecayUnix = 0 };
        Assert.Equal(42, CrewRules.ApplyDecay(crew, 42, 10));
        Assert.Equal(500, crew.Respect);
        CrewRules.ApplyDecay(crew, 42 + Week - 1, 10);
        Assert.Equal(500, crew.Respect);
    }

    [Theory]
    [InlineData(0, 2, 10, 2)]
    [InlineData(9, 5, 10, 1)]   // only 1 left under the cap
    [InlineData(10, 5, 10, 0)]
    [InlineData(10, -5, 10, -5)] // penalties are never capped
    public void RoundCap(int earned, int amount, int cap, int expected) => Assert.Equal(expected, CrewRules.Grantable(earned, amount, cap));
}

public class CrewTurfTests
{
    private static Dictionary<string, int> P(params (string, int)[] x) => x.ToDictionary(t => t.Item1, t => t.Item2);

    [Fact]
    public void MostMembersTakeTheBlock() => Assert.Equal("b", CrewRules.ResolveTurf(P(("a", 2), ("b", 3)), "a", 2));

    [Fact]
    public void BelowTheMinimum_NothingChanges() => Assert.Equal("a", CrewRules.ResolveTurf(P(("b", 1)), "a", 2));

    [Fact]
    public void TieWithTheHolder_HolderKeepsIt() => Assert.Equal("a", CrewRules.ResolveTurf(P(("a", 2), ("b", 2)), "a", 2));

    [Fact]
    public void TieWithoutTheHolder_NothingChanges()
    {
        Assert.Equal("c", CrewRules.ResolveTurf(P(("a", 2), ("b", 2)), "c", 2));
        Assert.Null(CrewRules.ResolveTurf(P(("a", 2), ("b", 2)), null, 2));
    }

    [Fact]
    public void EmptyBlock_KeepsTheHolder() => Assert.Equal("a", CrewRules.ResolveTurf(new Dictionary<string, int>(), "a", 2));
}

public class CrewModelTests
{
    [Fact]
    public void Favors_AreInformationOrIdentityOnly_AndUnlockInOrder()
    {
        Assert.Equal(new[] { "banner", "colors", "lookout", "rollcall" }, CrewRules.Favors.Select(f => f.Key));
        Assert.All(CrewRules.Favors, f => Assert.True(f.Cost > 0 && f.MinLevel >= 1));
        Assert.All(CrewRules.Favors, f =>
        {
            var d = f.Description.ToLowerInvariant();
            Assert.DoesNotContain("damage", d);
            Assert.DoesNotContain("health", d);
            Assert.DoesNotContain("weapon", d);
        });
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(3, 6)]
    [InlineData(9, 8)]
    public void Colors_UnlockWithLevel(int level, int count) => Assert.Equal(count, CrewColors.Unlocked(level));

    [Fact]
    public void Crew_RoundTripsThroughTheStorageJsonOptions()
    {
        var crew = new Crew
        {
            Id = "0a1b2c3d", Name = "The Kings", Tag = "KNG", Color = 3, Respect = 12, Legacy = 340,
            Members = { new CrewMember { SteamId = "76561198000000001", Name = "Gdaki", Rank = CrewRank.Boss, Earned = 40 } }
        };
        var json = JsonSerializer.Serialize(crew, Storage.JsonOptions);
        var back = JsonSerializer.Deserialize<Crew>(json, Storage.JsonOptions)!;
        Assert.Equal("KNG", back.Tag);
        Assert.Equal(CrewRank.Boss, back.Boss!.Rank);
        Assert.Equal(340, back.Legacy);
        Assert.Equal("Gdaki", back.Member("76561198000000001")!.Name);
    }
}
