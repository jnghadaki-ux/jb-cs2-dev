using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwbsJailbreak.Modules.LastRequest;
using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class LastRequestExtraTests
{
    [Fact]
    public void GunRoulette_PicksFiveDifferentGunsByDefault()
    {
        var cfg = new LrExtraConfig();
        Assert.Equal(5, cfg.GunRouletteGuns);
        for (var seed = 0; seed < 50; seed++)
        {
            var guns = LrLogic.PickGuns(cfg.GunRoulettePool, cfg.GunRouletteGuns, new Random(seed));
            Assert.Equal(5, guns.Count);
            Assert.Equal(5, guns.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(guns, g => Assert.Contains(g, cfg.GunRoulettePool));
        }
    }

    [Fact]
    public void GunRoulette_SmallPoolRepeatsAndEmptyPoolGivesNothing()
    {
        var guns = LrLogic.PickGuns(new[] { "weapon_deagle", "weapon_awp", " ", "weapon_DEAGLE" }, 5, new Random(1));
        Assert.Equal(5, guns.Count);
        Assert.Equal(2, guns.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(LrLogic.PickGuns(Array.Empty<string>(), 5, new Random(1)));
        Assert.Empty(LrLogic.PickGuns(new[] { "weapon_deagle" }, 0, new Random(1)));
    }

    [Fact]
    public void GunRoulette_AlwaysEndsAfterTheLastGun()
    {
        // 5 guns x 2 shots = 10 turns, then the healthier player (or a coin) decides: never "until round end".
        Assert.Equal(0, LrLogic.FinalWinner(80, 40, 1));
        Assert.Equal(1, LrLogic.FinalWinner(10, 40, 0));
        Assert.Equal(0, LrLogic.FinalWinner(100, 100, 0));
        Assert.Equal(1, LrLogic.FinalWinner(100, 100, 1));
    }

    [Theory]
    [InlineData(0, 2, 1)]  // rock beats scissors
    [InlineData(1, 0, 1)]  // paper beats rock
    [InlineData(2, 1, 1)]  // scissors beat paper
    [InlineData(2, 0, -1)]
    [InlineData(0, 1, -1)]
    [InlineData(1, 1, 0)]
    public void Rps_Rounds(int a, int b, int expected) => Assert.Equal(expected, LrLogic.RpsRound((LrLogic.Rps)a, (LrLogic.Rps)b));

    [Fact]
    public void Rps_NoPickLoses()
    {
        Assert.Equal(-1, LrLogic.RpsRound(null, LrLogic.Rps.Rock));
        Assert.Equal(1, LrLogic.RpsRound(LrLogic.Rps.Paper, null));
        Assert.Equal(0, LrLogic.RpsRound(null, null));
    }

    [Fact]
    public void MatchWinner_FirstToNeeded()
    {
        Assert.Equal(-1, LrLogic.MatchWinner(1, 1, 2));
        Assert.Equal(0, LrLogic.MatchWinner(2, 1, 2));
        Assert.Equal(1, LrLogic.MatchWinner(0, 2, 2));
    }

    [Fact]
    public void MathQuestions_AreWholeAndAnswerable()
    {
        var r = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var (text, answer) = LrLogic.MathQuestion(r);
            Assert.True(answer >= 0);
            var m = Regex.Match(text, @"^(\d+) ([+\-x]) (\d+)$");
            Assert.True(m.Success, text);
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[3].Value);
            var expected = m.Groups[2].Value switch { "+" => a + b, "-" => a - b, _ => a * b };
            Assert.Equal(expected, answer);
            Assert.True(LrLogic.IsAnswer(" " + answer + " ", answer));
            Assert.False(LrLogic.IsAnswer((answer + 1).ToString(), answer));
        }

        Assert.False(LrLogic.IsAnswer("abc", 3));
        Assert.False(LrLogic.IsAnswer(null, 3));
    }

    [Fact]
    public void TypeRace_IgnoresCaseAndSpaces()
    {
        Assert.True(LrLogic.TypedMatch("  JailBreak ", "jailbreak"));
        Assert.False(LrLogic.TypedMatch("jailbrake", "jailbreak"));
        Assert.False(LrLogic.TypedMatch(null, "jailbreak"));
        Assert.NotEmpty(new LrExtraConfig().TypeRaceWords);
    }

    [Fact]
    public void LongestJump_MeasuresLevelJumpsOnly()
    {
        Assert.Equal(250f, LrLogic.JumpDistance(new Vector3(0, 0, 0), new Vector3(150, 200, 4), 16f)!.Value, 3);
        Assert.Null(LrLogic.JumpDistance(new Vector3(0, 0, 0), new Vector3(200, 0, 40), 16f));
        Assert.Null(LrLogic.JumpDistance(new Vector3(0, 0, 64), new Vector3(200, 0, 0), 16f));
    }

    [Fact]
    public void TargetPractice_ScoresByClosestPass()
    {
        var from = new Vector3(0, 0, 0);
        var to = new Vector3(1000, 0, 0);
        Assert.Equal(0f, LrLogic.SegmentDistance(from, to, new Vector3(500, 0, 0)), 3);
        Assert.Equal(10f, LrLogic.SegmentDistance(from, to, new Vector3(500, 10, 0)), 3);
        Assert.Equal(5f, LrLogic.SegmentDistance(from, to, new Vector3(-3, 4, 0)), 3); // behind the shooter: distance to the start
        Assert.Equal(10, LrLogic.TargetScore(0f));
        Assert.Equal(7, LrLogic.TargetScore(10f));
        Assert.Equal(4, LrLogic.TargetScore(15f));
        Assert.Equal(1, LrLogic.TargetScore(25f));
        Assert.Equal(0, LrLogic.TargetScore(31f));
    }

    [Fact]
    public void SimonSays_ChecksEachAction()
    {
        Assert.True(LrLogic.SimonPassed(LrLogic.SimonAction.Jump, true, false, 0, true));
        Assert.False(LrLogic.SimonPassed(LrLogic.SimonAction.Jump, false, true, 0, false));
        Assert.True(LrLogic.SimonPassed(LrLogic.SimonAction.Crouch, false, true, 0, false));
        Assert.True(LrLogic.SimonPassed(LrLogic.SimonAction.LookUp, false, false, -60, false));
        Assert.False(LrLogic.SimonPassed(LrLogic.SimonAction.LookUp, false, false, -20, false));
        Assert.True(LrLogic.SimonPassed(LrLogic.SimonAction.LookDown, false, false, 70, false));
        Assert.True(LrLogic.SimonPassed(LrLogic.SimonAction.Freeze, false, false, 0, false));
        Assert.False(LrLogic.SimonPassed(LrLogic.SimonAction.Freeze, false, false, 0, true));
        Assert.Equal(2.5f, LrLogic.SimonWindow(1), 3);
        Assert.Equal(2.35f, LrLogic.SimonWindow(2), 3);
        Assert.Equal(1.2f, LrLogic.SimonWindow(30), 3);
    }

    [Fact]
    public void RecentPicks_NewestFirstNoDuplicates()
    {
        var list = RecentLastRequests.Push(null, "Race", 3);
        list = RecentLastRequests.Push(list, "Dodgeball", 3);
        list = RecentLastRequests.Push(list, "race", 3);
        Assert.Equal(new[] { "race", "Dodgeball" }, list);
        list = RecentLastRequests.Push(list, "A", 3);
        list = RecentLastRequests.Push(list, "B", 3);
        Assert.Equal(new[] { "B", "A", "race" }, list);
    }

    [Fact]
    public void LrStats_RecordsWinsLossesPerLr()
    {
        var s = new PlayerStats();
        StatsModule.RecordDuel(s, "Dodgeball", true);
        StatsModule.RecordDuel(s, "Dodgeball", false);
        StatsModule.RecordDuel(s, "Race", true);
        StatsModule.RecordDuel(s, "Race", true);
        StatsModule.RecordDuel(s, "Quick Draw", false);
        Assert.Equal(3, s.LastRequestDuelsWon);
        Assert.Equal(2, s.LastRequestDuelsLost);
        Assert.Equal(60.0, Leaderboard.DuelWinRate(s));
        var top = StatsModule.TopLrs(s, 2);
        Assert.Equal(new[] { ("Race", 2, 0), ("Dodgeball", 1, 1) }, top);
    }

    [Fact]
    public void DuelistBoard_NeedsMinimumDuels()
    {
        var w = new RankWeights { DuelistMinDuels = 3 };
        var few = new PlayerStats { LastRequestDuelsWon = 2, LastRequestDuelsLost = 0 };
        var enough = new PlayerStats { LastRequestDuelsWon = 3, LastRequestDuelsLost = 1 };
        Assert.Equal(0, Leaderboard.Value(few, RankCategory.DuelWinRate, w));
        Assert.Equal(75.0, Leaderboard.Value(enough, RankCategory.DuelWinRate, w));
        Assert.Equal(RankCategory.DuelWinRate, Leaderboard.Aliases["duelist"]);
        Assert.Equal(0, Leaderboard.DuelWinRate(new PlayerStats()));
    }

    [Fact]
    public void DuelBets_CloseAfterTheWindow()
    {
        Assert.True(BettingModule.DuelBetOpen(100f, 110f, 20));
        Assert.True(BettingModule.DuelBetOpen(100f, 120f, 20));
        Assert.False(BettingModule.DuelBetOpen(100f, 121f, 20));
        Assert.False(BettingModule.DuelBetOpen(0f, 1f, 20));
    }

    [Fact]
    public void ExtraLrConfig_AllOnByDefault()
    {
        var c = new LrExtraConfig();
        Assert.True(c.DodgeballEnabled && c.HeadshotDuelEnabled && c.GrenadeWarEnabled && c.TaserDuelEnabled && c.QuickDrawEnabled && c.GunRouletteEnabled);
        Assert.True(c.HotPotatoEnabled && c.HideAndSeekEnabled && c.LongestJumpEnabled && c.ChickenHuntEnabled && c.TargetPracticeEnabled);
        Assert.True(c.RockPaperScissorsEnabled && c.MathRaceEnabled && c.TypeRaceEnabled && c.SimonSaysEnabled);
        Assert.True(c.RocketeerEnabled && c.TeleporterEnabled && c.JetpackFreedayEnabled);
        var f = new LrFeaturesConfig();
        Assert.True(f.RecentPicks && f.SpectatorPanel);
    }

    // ------------------------------------------------------------------ phrases

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "SwbsJailbreak"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Every phrase key the 0.14/0.15 code passes to Lang.T / Tell / TellAll / TellBoth / ReplyT exists in lang/en.</summary>
    [Fact]
    public void NewCode_PhraseKeysExist()
    {
        var root = Path.Combine(RepoRoot(), "src", "SwbsJailbreak");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.GetFiles(Path.Combine(root, "lang", "en"), "*.json"))
            foreach (var p in JsonDocument.Parse(File.ReadAllText(f)).RootElement.EnumerateObject())
                keys.Add(p.Name);

        var files = new[]
        {
            "Modules/LastRequest/DuelLastRequests.cs", "Modules/LastRequest/ContestExtraLastRequests.cs",
            "Modules/LastRequest/MindGameLastRequests.cs", "Modules/LastRequest/TeleporterLastRequest.cs",
            "Modules/LastRequest/LrExtrasModule.cs", "Modules/LastRequest/TournamentLastRequest.cs",
            "Modules/Days/NewDays.cs", "Modules/Days/GunGameDay.cs", "Modules/Days/SpyDay.cs", "Modules/Days/SpeedrunDay.cs",
            "Modules/PlayerExtras/BettingModule.cs", "Modules/PlayerExtras/StatsModule.cs",
            "Modules/PlayerExtras/StoreModule.cs", "Modules/Extras/ChatFormatModule.cs", "Modules/Extras/WorkshopPackModule.cs"
        };
        var call = new Regex(@"(?:Lang\.T|TellAll|TellBoth|Tell|ReplyT)\((?:[^""()]*?,\s*)?""([a-z_]+\.[a-z0-9_.]+)""\s*[,)]"); // a key followed by + is built at run time
        var missing = new List<string>();
        foreach (var rel in files)
        {
            var text = File.ReadAllText(Path.Combine(root, rel));
            foreach (Match m in call.Matches(text))
            {
                var key = m.Groups[1].Value;
                if (key.EndsWith('.') || keys.Contains(key)) continue;
                missing.Add($"{rel}: {key}");
            }
        }

        Assert.Empty(missing);
    }
}
