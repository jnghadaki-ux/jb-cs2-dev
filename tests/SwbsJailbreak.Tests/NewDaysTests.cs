using SwbsJailbreak.Modules.Days;
using Xunit;

namespace SwbsJailbreak.Tests;

public class NewDaysTests
{
    [Fact]
    public void GunGameLadder_NormalisesAndEndsWithKnife()
    {
        var ladder = DayLogic.GunGameLadder(new[] { "glock", " weapon_AK47 ", "", "weapon_knife_t", "bayonet", "awp" });
        Assert.Equal(new[] { "weapon_glock", "weapon_ak47", "weapon_awp", "weapon_knife" }, ladder);
        Assert.Equal(new[] { "weapon_knife" }, DayLogic.GunGameLadder(null));
    }

    [Fact]
    public void GunGameAdvance_MovesUpAndWinsOnTheLastLevel()
    {
        Assert.Equal(1, DayLogic.GunGameAdvance(0, 4));
        Assert.Equal(3, DayLogic.GunGameAdvance(2, 4));
        Assert.Equal(-1, DayLogic.GunGameAdvance(3, 4)); // kill on the knife level wins
        Assert.Equal(-1, DayLogic.GunGameAdvance(0, 1));
        Assert.Equal(-1, DayLogic.GunGameAdvance(0, 0));
    }

    [Fact]
    public void GunGameDemote_NeverBelowZero()
    {
        Assert.Equal(2, DayLogic.GunGameDemote(3, 5, 1));
        Assert.Equal(0, DayLogic.GunGameDemote(0, 5, 1));
        Assert.Equal(0, DayLogic.GunGameDemote(1, 5, 4));
        Assert.Equal(3, DayLogic.GunGameDemote(3, 5, 0));
    }

    [Fact]
    public void GunGameLeader_HighestLevelThenFirstToReachIt()
    {
        var levels = new Dictionary<int, (int, long)> { [1] = (4, 10), [2] = (6, 30), [3] = (6, 20), [4] = (2, 1) };
        Assert.Equal(3, DayLogic.GunGameLeader(levels));
        Assert.Equal(-1, DayLogic.GunGameLeader(new Dictionary<int, (int, long)>()));
    }

    [Fact]
    public void BracketPairs_PairsInOrderWithAByeForTheOddOne()
    {
        Assert.Equal(new[] { (1, 2), (3, 4) }, DayLogic.BracketPairs(new[] { 1, 2, 3, 4 }));
        Assert.Equal(new[] { (1, 2), (3, 4), (5, -1) }, DayLogic.BracketPairs(new[] { 1, 2, 3, 4, 5 }));
        Assert.Equal(new[] { (7, -1) }, DayLogic.BracketPairs(new[] { 7 }));
        Assert.Empty(DayLogic.BracketPairs(Array.Empty<int>()));
    }

    [Fact]
    public void BracketRounds_AlwaysEndWithOneChampion()
    {
        for (var n = 2; n <= 33; n++)
        {
            var entrants = Enumerable.Range(0, n).ToList();
            var rounds = 0;
            while (entrants.Count > 1)
            {
                entrants = DayLogic.BracketPairs(entrants).Select(p => p.B < 0 ? p.A : Math.Min(p.A, p.B)).ToList();
                rounds++;
            }

            Assert.Single(entrants);
            Assert.Equal((int)Math.Ceiling(Math.Log2(n)), rounds);
        }
    }

    [Fact]
    public void DuelTimeoutWinner_HealthThenCoin()
    {
        Assert.Equal(1, DayLogic.DuelTimeoutWinner(1, 80, 2, 40, 1));
        Assert.Equal(2, DayLogic.DuelTimeoutWinner(1, 10, 2, 40, 0));
        Assert.Equal(1, DayLogic.DuelTimeoutWinner(1, 50, 2, 50, 0));
        Assert.Equal(2, DayLogic.DuelTimeoutWinner(1, 50, 2, 50, 1));
    }

    [Fact]
    public void SwapPairs_NeverGuardWithGuardAndOddOneStays()
    {
        // 1-3 prisoners, 10-12 guards
        static bool Prisoner(int s) => s < 10;
        var pairs = DayLogic.SwapPairs(new[] { 1, 10, 11, 12, 2, 3, 4 }, Prisoner);
        Assert.Equal(new[] { (1, 10), (2, 3) }, pairs); // (11,12) guards only: skipped; 4 has no partner
        Assert.Empty(DayLogic.SwapPairs(new[] { 1 }, Prisoner));
        foreach (var (a, b) in DayLogic.SwapPairs(Enumerable.Range(0, 20).ToArray(), s => s % 3 == 0))
            Assert.True(a % 3 == 0 || b % 3 == 0);
    }

    [Theory]
    [InlineData(1, 4, 4, 0)]
    [InlineData(2, 4, 4, 1)]
    [InlineData(4, 4, 4, 1)]
    [InlineData(6, 4, 4, 2)]
    [InlineData(10, 4, 4, 3)]
    [InlineData(30, 4, 4, 4)]
    [InlineData(30, 4, 0, 8)]
    [InlineData(3, 1, 0, 2)]
    public void SpyCount_RatioCapAndAlwaysOneInnocent(int prisoners, int ratio, int max, int expected) =>
        Assert.Equal(expected, DayLogic.SpyCount(prisoners, ratio, max));

    [Fact]
    public void SpyDamage_Rules()
    {
        // attackerGuard, attackerSpy, victimGuard, victimSpy
        Assert.Equal(DayLogic.SpyHit.Allow, DayLogic.SpyDamage(true, false, false, true));    // guard -> spy
        Assert.Equal(DayLogic.SpyHit.Allow, DayLogic.SpyDamage(false, true, true, false));    // spy -> guard
        Assert.Equal(DayLogic.SpyHit.Misfire, DayLogic.SpyDamage(true, false, false, false)); // guard -> innocent
        Assert.Equal(DayLogic.SpyHit.Block, DayLogic.SpyDamage(false, false, true, false));   // innocent -> guard
        Assert.Equal(DayLogic.SpyHit.Block, DayLogic.SpyDamage(false, true, false, false));   // spy -> innocent
        Assert.Equal(DayLogic.SpyHit.Block, DayLogic.SpyDamage(false, false, false, true));   // innocent -> spy
        Assert.Equal(DayLogic.SpyHit.Block, DayLogic.SpyDamage(true, false, true, false));    // guard -> guard
        Assert.Equal(DayLogic.SpyHit.Block, DayLogic.SpyDamage(false, true, false, true));    // spy -> spy
    }

    [Theory]
    [InlineData(0, "0.00")]
    [InlineData(9.5, "9.50")]
    [InlineData(59.999, "1:00.00")]
    [InlineData(83.456, "1:23.46")]
    [InlineData(-3, "0.00")]
    public void RaceTime_Formats(double seconds, string expected) => Assert.Equal(expected, DayLogic.RaceTime(seconds));

    [Fact]
    public void PlaceReward_AndRecordChecks()
    {
        var rewards = new List<int> { 30, 20, 10 };
        Assert.Equal(30, DayLogic.PlaceReward(rewards, 1));
        Assert.Equal(10, DayLogic.PlaceReward(rewards, 3));
        Assert.Equal(0, DayLogic.PlaceReward(rewards, 4));
        Assert.Equal(0, DayLogic.PlaceReward(rewards, 0));
        Assert.Equal(0, DayLogic.PlaceReward(null, 1));
        Assert.True(DayLogic.IsBetter(30, null));
        Assert.True(DayLogic.IsBetter(30, 31));
        Assert.False(DayLogic.IsBetter(31, 30));
        Assert.True(DayLogic.IsBetter(31, 0));
    }

    [Fact]
    public void NewDays_ConfigDefaultsAreOn()
    {
        var c = new NewDaysConfig();
        Assert.True(c.JuggernautEnabled && c.DodgeballEnabled && c.HeDayEnabled && c.HeadshotOnlyEnabled && c.TeleportFreedayEnabled);
        Assert.True(c.GunGameEnabled && c.SpyDayEnabled && c.TournamentEnabled && c.SpeedrunEnabled);
        Assert.True(new RpgConfig().Enabled);
        Assert.NotEmpty(c.GunGameWeapons);
    }
}
