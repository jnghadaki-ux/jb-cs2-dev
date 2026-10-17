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

    [Fact]
    public void ChangeCommands_WorkshopIdFirst_ThenKnownName()
    {
        Assert.Equal(new[] { "host_workshop_map 42", "ds_workshop_changelevel jb_x" },
            MapVoteRules.ChangeCommands(new VoteMap("", 42), "jb_x", _ => false));
        // One way to load it: tried twice.
        Assert.Equal(new[] { "host_workshop_map 42", "host_workshop_map 42" }, MapVoteRules.ChangeCommands(new VoteMap("Pretty Name", 42), "", _ => false));
        Assert.Equal(new[] { "changelevel de_dust2", "ds_workshop_changelevel de_dust2" },
            MapVoteRules.ChangeCommands(new VoteMap("de_dust2", 0), "de_dust2", n => n == "de_dust2"));
    }

    [Theory]
    [InlineData(null, 15, 15)]
    [InlineData(" ", 15, 15)]
    [InlineData("20", 15, 20)]
    [InlineData("0", 15, null)]
    [InlineData("abc", 15, null)]
    [InlineData("500", 15, null)]
    public void ExtendBy_DefaultOrValidMinutes(string? arg, int def, int? expected) =>
        Assert.Equal(expected, MapVoteRules.ExtendBy(arg, def));

    [Fact]
    public void ChangeCommands_SubscribedMapByName_AndNoCommandInjection()
    {
        Assert.Equal(new[] { "ds_workshop_changelevel jb_x", "ds_workshop_changelevel jb_x" }, MapVoteRules.ChangeCommands(new VoteMap("jb_x", 0), "jb_x", _ => false));
        Assert.Empty(MapVoteRules.ChangeCommands(new VoteMap("jb_x; quit", 0), "", _ => true));
        Assert.Equal(new[] { "host_workshop_map 7", "host_workshop_map 7" }, MapVoteRules.ChangeCommands(new VoteMap("", 7), "x;quit", _ => false));
        Assert.False(MapVoteRules.IsMapName("3070212801"));
        Assert.True(MapVoteRules.IsMapName("ba_jail_electric_v2.1"));
    }

    [Theory]
    [InlineData(1, 0.6, 0, 1)]
    [InlineData(3, 0.6, 0, 2)]
    [InlineData(5, 0.6, 0, 3)]
    [InlineData(16, 0.6, 0, 10)]
    [InlineData(2, 0.6, 10, 2)]  // the minimum never needs more votes than there are players
    [InlineData(20, 0.1, 4, 4)]
    [InlineData(0, 0.6, 3, 1)]
    public void RtvNeeded_RatioOfPlayers_MinimumCappedAtPlayers(int players, double ratio, int min, int needed) =>
        Assert.Equal(needed, MapVoteRules.RtvNeeded(players, ratio, min));

    [Theory]
    [InlineData(4, 0, 16, 4)]  // 12 AFK spectators do not count
    [InlineData(4, 1, 16, 5)]  // a spectator who voted does
    [InlineData(0, 0, 3, 3)]   // nobody on a team: everyone counts
    public void RtvEligible_PlayersOnATeam_PlusSpectatorsWhoVoted(int onTeam, int specVoters, int humans, int expected) =>
        Assert.Equal(expected, MapVoteRules.RtvEligible(onTeam, specVoters, humans));

    [Fact]
    public void TimeMath_TimeLeft_TimeLimitFor_ChangeDelay()
    {
        Assert.Null(MapVoteRules.TimeLeft(0f, 10f, 100f));
        Assert.Equal(30f * 60f + 10f - 100f, MapVoteRules.TimeLeft(30f, 10f, 100f));
        Assert.Equal(15f, MapVoteRules.TimeLimitFor(600f, 5f), 3);
        Assert.True(MapVoteRules.TimeLimitFor(0f, 0f) > 0f);
        Assert.Equal("45.5", MapVoteRules.Minutes(45.5f));
        Assert.Equal(8f, MapVoteRules.ChangeDelay(8f, 15f));
        Assert.Equal(3f, MapVoteRules.ChangeDelay(8f, 5f));
        Assert.True(MapVoteRules.ForceEnd(120f, 120));
        Assert.False(MapVoteRules.ForceEnd(30f, 120));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData(" 25 ", 25)]
    [InlineData("-1", null)]
    [InlineData("601", null)]
    [InlineData(null, null)]
    public void ParseMinutes_ZeroTo600(string? arg, int? expected) => Assert.Equal(expected, MapVoteRules.ParseMinutes(arg));
}
