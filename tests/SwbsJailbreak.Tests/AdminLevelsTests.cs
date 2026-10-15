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
}
