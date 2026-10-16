using System.Text.Json.Nodes;
using SwbsJailbreak.Core;
using Xunit;

namespace SwbsJailbreak.Tests;

public class AdminLevelsTests
{
    [Fact]
    public void Tiers_AreCumulative_WithRisingImmunity()
    {
        var all = AdminLevels.All;
        Assert.Equal(new[] { "Level 1", "Level 2", "Level 3", "Lead" }, all.Select(l => l.Name));
        for (var i = 1; i < all.Length; i++)
        {
            Assert.True(all[i].Immunity > all[i - 1].Immunity);
            Assert.All(all[i - 1].Flags, f => Assert.Contains(f, all[i].Flags));
        }

        Assert.DoesNotContain(all, l => l.Flags.Contains("@css/root") || l.Flags.Contains("@css/rcon"));
    }

    [Fact]
    public void Merge_AddsMissingGroups_AndKeepsExistingOnes()
    {
        var existing = "{ \"#css/admin\": { \"flags\": [\"@css/ban\"], \"immunity\": 5 }, \"#swbs/level1\": { \"flags\": [\"@css/generic\"], \"immunity\": 1 } }";
        var merged = JsonNode.Parse(AdminLevels.MergeInto(existing)!)!.AsObject();
        Assert.Equal(5, (int)merged["#css/admin"]!["immunity"]!);
        Assert.Equal(1, (int)merged["#swbs/level1"]!["immunity"]!); // the owner's edited group stays as it is
        Assert.Equal(50, (int)merged["#swbs/lead"]!["immunity"]!);
        Assert.Null(AdminLevels.MergeInto(merged.ToJsonString()));   // nothing left to add
        Assert.Null(AdminLevels.MergeInto("{ not json"));             // never overwrite an unreadable file
        Assert.NotNull(AdminLevels.MergeInto(null));                  // no file yet: create it
    }

    [Fact]
    public void Merge_UpgradesAnUntouchedOldLeadGroup_ButNotAnEditedOne()
    {
        var oldLead = "[\"@css/reservation\",\"@css/generic\",\"@css/chat\",\"@css/slay\",\"@css/kick\",\"@css/ban\",\"@css/unban\",\"@css/cheats\",\"@css/changemap\",\"@css/config\",\"@css/cvar\"]";
        var merged = JsonNode.Parse(AdminLevels.MergeInto("{ \"#swbs/lead\": { \"flags\": " + oldLead + ", \"immunity\": 55 } }")!)!.AsObject();
        var flags = merged["#swbs/lead"]!["flags"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("@swbs/unban", flags);
        Assert.Contains("@swbs/noclip", flags);
        Assert.Equal(55, (int)merged["#swbs/lead"]!["immunity"]!); // the owner's immunity stays

        // An edited group keeps its own flags but still inherits everything from the tier below it.
        var edited = "{ \"#swbs/lead\": { \"flags\": [\"@swbs/custom\"], \"immunity\": 50 } }";
        var m2 = JsonNode.Parse(AdminLevels.MergeInto(edited)!)!.AsObject();
        var leadFlags = m2["#swbs/lead"]!["flags"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("@swbs/custom", leadFlags);
        Assert.All(AdminLevels.All.Single(l => l.Name == "Level 3").Flags, f => Assert.Contains(f, leadFlags));
        Assert.DoesNotContain("@swbs/noclip", leadFlags); // a flag the owner took out of their own tier stays out
    }

    [Fact]
    public void Merge_MakesEveryTierInheritTheOneBelow_IncludingOwnerAddedFlags()
    {
        var doc = "{ \"#swbs/level1\": { \"flags\": [\"@css/generic\", \"@my/extra\"], \"immunity\": 10 }," +
                  "  \"#swbs/level2\": { \"flags\": [\"@css/kick\"], \"immunity\": 20 } }";
        var merged = JsonNode.Parse(AdminLevels.MergeInto(doc)!)!.AsObject();
        List<string> F(string g) => merged[g]!["flags"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        foreach (var (lower, upper) in new[] { ("#swbs/level1", "#swbs/level2"), ("#swbs/level2", "#swbs/level3"), ("#swbs/level3", "#swbs/lead") })
            Assert.All(F(lower), f => Assert.Contains(f, F(upper)));
        Assert.Contains("@my/extra", F("#swbs/lead"));
        Assert.Equal(10, (int)merged["#swbs/level1"]!["immunity"]!);
        Assert.Null(AdminLevels.MergeInto(merged.ToJsonString())); // stable once it is a ladder
    }

    [Fact]
    public void TierChecks_AreALadder()
    {
        int Tier(string name) => System.Array.FindIndex(AdminLevels.All, l => l.Name == name);
        Assert.True(AdminLevels.TierGrants(Tier("Lead"), "@css/slay"));      // Level 1 flag
        Assert.True(AdminLevels.TierGrants(Tier("Level 3"), "@css/kick"));   // Level 2 flag
        Assert.False(AdminLevels.TierGrants(Tier("Level 1"), "@css/kick"));
        Assert.False(AdminLevels.TierGrants(-1, "@css/generic"));
        Assert.True(AdminLevels.TierImplies(Tier("Level 3"), "#swbs/level1"));
        Assert.True(AdminLevels.TierImplies(Tier("Level 2"), "#SWBS/level2"));
        Assert.False(AdminLevels.TierImplies(Tier("Level 2"), "#swbs/lead"));
        Assert.False(AdminLevels.TierImplies(Tier("Lead"), "#css/admin"));
        Assert.Equal(Tier("Level 3"), AdminLevels.TierOf(g => g is "#swbs/level1" or "#swbs/level3"));
        Assert.Equal(-1, AdminLevels.TierOf(_ => false));
    }

    [Fact]
    public void UnbanNoclipGod_AreLeadOnly()
    {
        foreach (var cap in new[] { "unban", "noclip", "god" })
        {
            var flag = Perms.Defaults[cap];
            Assert.Contains(flag, AdminLevels.All.Single(l => l.Name == "Lead").Flags);
            Assert.DoesNotContain(flag, AdminLevels.All.Single(l => l.Name == "Level 3").Flags);
        }

        Assert.Contains(Perms.Defaults["ban"], AdminLevels.All.Single(l => l.Name == "Level 3").Flags); // level 3 can ban
    }

    [Fact]
    public void FlagOnlyAdmins_GetTheirRankAndEverythingBelow()
    {
        int Tier(string name) => System.Array.FindIndex(AdminLevels.All, l => l.Name == name);
        Assert.Equal(new[] { "@css/kick", "@css/ban" }, AdminLevels.Signature(Tier("Level 2")));
        Assert.Equal(Tier("Level 2"), AdminLevels.TierOfFlags(f => f == "@css/ban"));
        Assert.Equal(Tier("Level 3"), AdminLevels.TierOfFlags(f => f is "@css/ban" or "@css/cheats"));
        Assert.Equal(Tier("Lead"), AdminLevels.TierOfFlags(f => f == "@swbs/noclip"));
        Assert.Equal(Tier("Level 1"), AdminLevels.TierOfFlags(f => f == "@css/generic"));
        Assert.Equal(-1, AdminLevels.TierOfFlags(f => f == "@css/chat")); // a chat-only helper is not ranked
        Assert.True(AdminLevels.TierGrants(AdminLevels.TierOfFlags(f => f == "@css/ban"), "@css/slay"));
    }

    [Fact]
    public void Targeting_FollowsTheLadder_RootAboveAll()
    {
        int Tier(string name) => System.Array.FindIndex(AdminLevels.All, l => l.Name == name);
        var root = AdminLevels.EffectiveImmunity(0, true, -1);
        var lead = AdminLevels.EffectiveImmunity(0, false, Tier("Lead"));
        var l2 = AdminLevels.EffectiveImmunity(0, false, Tier("Level 2"));
        var l1 = AdminLevels.EffectiveImmunity(10, false, Tier("Level 1"));
        Assert.True(root > lead && lead > l2 && l2 > l1);
        Assert.Equal(0u, AdminLevels.EffectiveImmunity(0, false, -1));
        Assert.Equal(80u, AdminLevels.EffectiveImmunity(80, false, Tier("Level 1"))); // a higher configured immunity stays
    }
}
