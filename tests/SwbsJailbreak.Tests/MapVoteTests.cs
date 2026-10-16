using SwbsJailbreak.Modules.Extras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class MapVoteTests
{
    [Theory]
    [InlineData("jb_summer|3070212801", "jb_summer", 3070212801UL)]
    [InlineData("jb_summer:3070212801", "jb_summer", 3070212801UL)]
    [InlineData("workshop/3070212801", "", 3070212801UL)]
    [InlineData("3070212801", "", 3070212801UL)]
    [InlineData("  ba_jail_x | workshop/42 ", "ba_jail_x", 42UL)]
    [InlineData("de_dust2", "de_dust2", 0UL)]
    public void Parse_AcceptsWorkshopEntries(string entry, string name, ulong id)
    {
        var m = VoteMap.Parse(entry)!.Value;
        Assert.Equal(name, m.Name);
        Assert.Equal(id, m.WorkshopId);
    }

    [Fact]
    public void Parse_IgnoresEmptyLines_AndLabelsUnnamedMapsByTheirId()
    {
        Assert.Null(VoteMap.Parse("  "));
        Assert.Equal("workshop 42", VoteMap.Parse("42")!.Value.Label);
    }

    [Theory]
    [InlineData(1, 0.6, 1)]
    [InlineData(5, 0.6, 3)]
    [InlineData(10, 0.6, 6)]
    [InlineData(0, 0.6, 1)]
    public void RtvNeeded_RoundsUp_AndNeedsAtLeastOne(int humans, double ratio, int needed) =>
        Assert.Equal(needed, MapVoteRules.RtvNeeded(humans, ratio));

    [Fact]
    public void Winner_MostVotes_TiesBrokenByPick_NoneWithoutVotes()
    {
        Assert.Equal(1, MapVoteRules.Winner(new[] { 1, 1, 0 }, 3, _ => 0));
        Assert.Equal(2, MapVoteRules.Winner(new[] { 0, 2 }, 3, n => n - 1)); // tie 0/2, pick the last
        Assert.Equal(-1, MapVoteRules.Winner(System.Array.Empty<int>(), 3, _ => 0));
    }

    [Fact]
    public void Ballot_NominationsFirst_SkipsExcluded_AndCapsSize()
    {
        var pool = new[] { new VoteMap("a", 1), new VoteMap("b", 2), new VoteMap("c", 3), new VoteMap("d", 4) };
        var ballot = MapVoteRules.Ballot(pool, new[] { new VoteMap("c", 3), new VoteMap("a", 1) }, m => m.Name == "a", 3, new System.Random(1));
        Assert.Equal(3, ballot.Count);
        Assert.Equal("c", ballot[0].Name);
        Assert.DoesNotContain(ballot, m => m.Name == "a");
        Assert.Equal(ballot.Count, ballot.Distinct().Count());
    }

    [Fact]
    public void FormatTime_MinutesAndSeconds() => Assert.Equal("29:05", MapVoteRules.FormatTime(1745.9f));

    [Fact]
    public void ParseCollection_ReturnsItemIds_SkippingSubCollections()
    {
        var json = "{\"response\":{\"collectiondetails\":[{\"publishedfileid\":\"99\",\"children\":[" +
                   "{\"publishedfileid\":\"111\",\"sortorder\":1,\"filetype\":0}," +
                   "{\"publishedfileid\":\"222\",\"sortorder\":2,\"filetype\":2}," +
                   "{\"publishedfileid\":\"333\",\"sortorder\":3,\"filetype\":0}]}]}}";
        Assert.Equal(new ulong[] { 111, 333 }, MapVoteRules.ParseCollection(json));
        Assert.Empty(MapVoteRules.ParseCollection("{\"response\":{}}"));
    }

    [Fact]
    public void ParseTitles_MapsIdsToTitles_SkippingMissingItems()
    {
        var json = "{\"response\":{\"result\":1,\"resultcount\":2,\"publishedfiledetails\":[" +
                   "{\"publishedfileid\":\"111\",\"result\":1,\"title\":\"jb_summer_v2 \"}," +
                   "{\"publishedfileid\":\"222\",\"result\":9}]}}";
        var t = MapVoteRules.ParseTitles(json);
        Assert.Single(t);
        Assert.Equal("jb_summer_v2", t[111]);
    }

    [Theory]
    [InlineData("3070212801", 3070212801UL)]
    [InlineData("workshop/3070212801", 3070212801UL)]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=3070212801&searchtext=", 3070212801UL)]
    [InlineData("jb_summer", 0UL)]
    public void WorkshopIdFrom_AcceptsIdsAndSteamLinks(string text, ulong id) =>
        Assert.Equal(id, MapVoteModule.WorkshopIdFrom(text));

    [Fact]
    public void MultiAddonManagerList_Parses() =>
        Assert.Equal(new ulong[] { 123, 456 }, MultiAddonManager.ParseList(" 123,456 , abc,123"));
}
