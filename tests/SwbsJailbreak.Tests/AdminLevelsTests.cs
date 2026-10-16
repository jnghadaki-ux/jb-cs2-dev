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

        var edited = "{ \"#swbs/lead\": { \"flags\": [\"@css/generic\"], \"immunity\": 50 } }";
        var m2 = JsonNode.Parse(AdminLevels.MergeInto(edited)!)!.AsObject();
        Assert.Single(m2["#swbs/lead"]!["flags"]!.AsArray());
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
}
