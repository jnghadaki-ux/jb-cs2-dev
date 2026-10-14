using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class StorePortTests
{
    [Fact]
    public void Defaults_HaveEveryPortedKind()
    {
        var items = StoreConfig.DefaultItems();
        Assert.Equal(items.Count, items.Select(i => i.Id.ToLowerInvariant()).Distinct().Count());
        Assert.Equal(9, items.Count(i => i.Type == StoreItemType.ChatColor)); // the 9 donator Colored Chat colours
        Assert.All(items.Where(i => i.Type == StoreItemType.ChatColor), i => Assert.Matches(@"^\{[a-z]+\}$", i.Data));
        foreach (var t in new[] { StoreItemType.ChatTitle, StoreItemType.Transparency, StoreItemType.BodySize, StoreItemType.Aura, StoreItemType.Sparkles, StoreItemType.Halo })
            Assert.Contains(items, i => i.Type == t);
        // Kill sounds, grenade sounds and hats need workshop files: no defaults.
        Assert.DoesNotContain(items, i => i.Type is StoreItemType.KillSound or StoreItemType.GrenadeSound or StoreItemType.Hat);
        Assert.Contains(items, i => i.Type == StoreItemType.KillEffect && i.Data == "fire");
        Assert.Contains(items, i => i.Type == StoreItemType.KillEffect && i.Data == "explosion");
    }

    [Fact]
    public void Merge_AddsOnlyMissingDefaults()
    {
        var mine = new List<StoreItem>
        {
            new() { Id = "tracer_red", Name = "My red", Price = 1 },
            new() { Id = "custom", Name = "Custom", Price = 5 }
        };
        var merged = StoreModule.Merge(mine, StoreConfig.DefaultItems());
        Assert.Equal("My red", merged.First(i => i.Id == "tracer_red").Name); // admin edits stay
        Assert.Contains(merged, i => i.Id == "custom");
        Assert.Contains(merged, i => i.Id == "chat_gold");
        Assert.Equal(merged.Count, merged.Select(i => i.Id).Distinct().Count());
        Assert.Equal(StoreConfig.DefaultItems().Count + 1, merged.Count);
    }

    [Theory]
    [InlineData(0, false, false, false, true)]
    [InlineData(1, false, false, true, true)]
    [InlineData(1, false, false, false, false)]
    [InlineData(2, false, false, true, false)]
    [InlineData(2, false, true, false, true)]  // own effects still shown
    [InlineData(0, true, true, true, false)]   // own effects hidden
    public void Visibility_Settings(int others, bool hideOwn, bool isOwner, bool sameTeam, bool expected) =>
        Assert.Equal(expected, StoreModule.ShouldShow(others, hideOwn, isOwner, sameTeam));

    [Fact]
    public void Titles_AreCleaned()
    {
        Assert.Equal("The Boss", StoreModule.CleanTitle("  {red}The Boss  ", 16));
        Assert.Equal("ABCDEFGH", StoreModule.CleanTitle("ABCDEFGHIJKL", 8));
        Assert.Equal("a b", StoreModule.CleanTitle("a\nb", 16));
        Assert.Equal("", StoreModule.CleanTitle("{}", 16));
        Assert.Equal("", StoreModule.CleanTitle(null, 16));
    }

    [Fact]
    public void BodyItems_OnlyWhenFair()
    {
        Assert.False(StoreModule.BodyItemsAllowed(false, false, false, false));
        Assert.True(StoreModule.BodyItemsAllowed(false, true, false, false));
        Assert.True(StoreModule.BodyItemsAllowed(false, false, true, false));
        Assert.True(StoreModule.BodyItemsAllowed(false, false, false, true));
        Assert.True(StoreModule.BodyItemsAllowed(true, false, false, false));
    }

    [Fact]
    public void ChatLine_StripsPlayerColours()
    {
        Assert.Equal("{gold}[Boss]{default} Name{default}: {red}hi (x)", ChatDecor.Format("{gold}[Boss]{default} ", "Name", "{red}", "hi {x}"));
        Assert.Equal("Name{default}: plain", ChatDecor.Format("", "Name", "", "{green}plain"));
    }
}
