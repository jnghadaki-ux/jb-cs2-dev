using System.Text.Json;
using SwbsJailbreak.Core;
using Xunit;

namespace SwbsJailbreak.Tests;

public class SimonSaysJudgeTests
{
    private static InputSample S(bool jump = false, bool duck = false, float speed = 0, float pitch = 0) => new(jump, duck, speed, pitch);

    [Fact]
    public void DuringTheGrace_WrongReactionsAreNotJudged() =>
        Assert.Equal(Verdict.Pending, GameJudge.Simon(SimonOrder.Jump, false, S(jump: true), 0.1, 0.3, 2.5, false));

    [Fact]
    public void DuringTheGrace_AQuickCorrectSimonResponseCounts()
    {
        Assert.Equal(Verdict.Pass, GameJudge.Simon(SimonOrder.Jump, true, S(jump: true), 0.05, 0.3, 2.5, false));
        Assert.Equal(Verdict.Pending, GameJudge.Simon(SimonOrder.StandStill, true, S(speed: 200), 0.1, 0.3, 2.5, false)); // still slowing down
    }

    [Fact]
    public void SimonOrder_PerformedInTime_Passes() =>
        Assert.Equal(Verdict.Pass, GameJudge.Simon(SimonOrder.Jump, true, S(jump: true), 1.0, 0.3, 2.5, false));

    [Fact]
    public void SimonOrder_NotPerformedByTheDeadline_Fails()
    {
        Assert.Equal(Verdict.Pending, GameJudge.Simon(SimonOrder.Crouch, true, S(), 2.0, 0.3, 2.5, false));
        Assert.Equal(Verdict.Fail, GameJudge.Simon(SimonOrder.Crouch, true, S(), 2.5, 0.3, 2.5, false));
    }

    [Fact]
    public void SimonOrder_AlreadyDone_StaysPassed() =>
        Assert.Equal(Verdict.Pass, GameJudge.Simon(SimonOrder.Crouch, true, S(), 3.0, 0.3, 2.5, true));

    [Fact]
    public void OrderWithoutSimon_DoingItFails_IgnoringItPasses()
    {
        Assert.Equal(Verdict.Fail, GameJudge.Simon(SimonOrder.Jump, false, S(jump: true), 0.5, 0.3, 2.5, false));
        Assert.Equal(Verdict.Pending, GameJudge.Simon(SimonOrder.Jump, false, S(), 1.0, 0.3, 2.5, false));
        Assert.Equal(Verdict.Pass, GameJudge.Simon(SimonOrder.Jump, false, S(), 2.5, 0.3, 2.5, false));
    }

    [Fact]
    public void StandStill_MovingFails_StillUntilTheEndPasses()
    {
        Assert.Equal(Verdict.Fail, GameJudge.Simon(SimonOrder.StandStill, true, S(speed: 40), 1.0, 0.3, 2.5, false));
        Assert.Equal(Verdict.Pending, GameJudge.Simon(SimonOrder.StandStill, true, S(speed: 2), 1.0, 0.3, 2.5, false));
        Assert.Equal(Verdict.Pass, GameJudge.Simon(SimonOrder.StandStill, true, S(speed: 2), 2.5, 0.3, 2.5, false));
    }

    [Theory]
    [InlineData(SimonOrder.LookUp, -60f, true)]
    [InlineData(SimonOrder.LookUp, -20f, false)]
    [InlineData(SimonOrder.LookDown, 70f, true)]
    [InlineData(SimonOrder.LookDown, 10f, false)]
    public void LookOrders_UseEyePitch(SimonOrder order, float pitch, bool performs) =>
        Assert.Equal(performs, GameJudge.Performs(order, S(pitch: pitch)));
}

public class RedLightAndMusicalTests
{
    [Theory]
    [InlineData(40f, 0.2, false)] // inside the grace: still stopping
    [InlineData(40f, 0.5, true)]
    [InlineData(10f, 2.0, false)] // under the speed limit
    public void RedLight(float speed, double sinceRed, bool @out) => Assert.Equal(@out, GameJudge.RedLightOut(speed, sinceRed, 0.4, 15f));

    [Fact]
    public void LightPhases_AreTwoToSixSeconds()
    {
        Assert.Equal(2.0, GameJudge.LightPhaseSeconds(0));
        Assert.Equal(6.0, GameJudge.LightPhaseSeconds(1));
        Assert.Equal(6.0, GameJudge.LightPhaseSeconds(9));
    }

    [Fact]
    public void Musical_FirstToEnterClaimsTheSpot_OthersAreOut()
    {
        var samples = new (int, int?, long)[] { (1, 10, 500), (2, 10, 400), (3, 11, 900), (4, null, long.MaxValue), (5, 12, 100) };
        var (safe, @out) = GameJudge.Musical(samples, new HashSet<int> { 10, 11 }); // 12 is not an active spot any more
        Assert.Equal(new HashSet<int> { 2, 3 }, safe);
        Assert.Equal(new HashSet<int> { 1, 4, 5 }, @out);
    }

    [Fact]
    public void Musical_SameEntryTick_LowerSlotWins()
    {
        var (safe, _) = GameJudge.Musical(new (int, int?, long)[] { (7, 1, 50), (3, 1, 50) }, new HashSet<int> { 1 });
        Assert.Equal(new HashSet<int> { 3 }, safe);
    }

    [Theory]
    [InlineData(8, 10, 7)]
    [InlineData(8, 3, 3)]  // capped by the spots on the map
    [InlineData(2, 5, 1)]
    [InlineData(1, 5, 1)]
    public void MusicalChairs_OneSpotFewerThanPlayers(int players, int spots, int expected) =>
        Assert.Equal(expected, GameJudge.MusicalZoneCount(players, spots));

    [Fact]
    public void LastStanding_OneLeftWins_LastOutTogetherShare()
    {
        Assert.Equal(new[] { 4 }, GameJudge.LastStanding(new HashSet<int> { 4 }, new List<int>()));
        Assert.Equal(new[] { 2, 6 }, GameJudge.LastStanding(new HashSet<int>(), new List<int> { 2, 6 }));
        Assert.Empty(GameJudge.LastStanding(new HashSet<int>(), new List<int> { 1, 2, 3 })!); // everyone failed together: no winner
        Assert.Null(GameJudge.LastStanding(new HashSet<int> { 1, 2 }, new List<int> { 3 }));
        Assert.Null(GameJudge.LastStanding(new HashSet<int>(), new List<int>()));
    }
}

public class PickerQueueTests
{
    [Fact]
    public void JoinOrder_Picking_MovesToTheBack_LeavingRemoves()
    {
        var q = new PickerQueue();
        q.Add(1); q.Add(2); q.Add(3); q.Add(1);
        Assert.Equal(new[] { 1, 2, 3 }, q.Slots);
        Assert.Equal(1, q.Next(_ => true));
        q.Picked(1);
        Assert.Equal(new[] { 2, 3, 1 }, q.Slots);
        Assert.Equal(3, q.Next(s => s != 2)); // 2 cannot pick now (dead / rebel): next eligible
        q.Remove(3);
        Assert.Equal(new[] { 2, 1 }, q.Slots);
        Assert.Null(q.Next(_ => false));
    }

    [Fact]
    public void GameButtons_RoundTripThroughTheStorageJsonOptions()
    {
        var doc = new GameButtonsDoc { Buttons = { new GameButtonRecord { Label = "Soccer", Name = "mg_soccer_btn", HammerId = "12345", Class = "func_button", Origin = new[] { 1f, 2f, 3f } } } };
        var back = JsonSerializer.Deserialize<GameButtonsDoc>(JsonSerializer.Serialize(doc, Storage.JsonOptions), Storage.JsonOptions)!;
        Assert.Equal("Soccer", back.Buttons[0].Label);
        Assert.Equal("12345", back.Buttons[0].HammerId);
        Assert.Equal(3f, back.Buttons[0].Origin[2]);
    }
}

public class GameHelperMathTests
{
    [Theory]
    [InlineData(0f, 22f, 350f, 22f)]
    [InlineData(-300f, 22f, 350f, -278f)] // thrust brakes a fall
    [InlineData(340f, 22f, 350f, 350f)]   // capped
    [InlineData(600f, 22f, 350f, 600f)]   // a faster climb (jump pad) is kept
    [InlineData(10f, -5f, 350f, 10f)]     // negative thrust config is ignored
    public void JetpackClimb(float vz, float thrust, float max, float expected) => Assert.Equal(expected, Jetpack.Climb(vz, thrust, max));

    [Fact]
    public void FuelBar_ShowsTheShare()
    {
        Assert.Contains(">|||||</font><font color='#555555'>|||||<", Jetpack.FuelBar(2f, 4f));
        Assert.Contains(">||||||||||</font>", Jetpack.FuelBar(9f, 4f));
        Assert.Contains("'#555555'>||||||||||<", Jetpack.FuelBar(1f, 0f));
    }

    [Fact]
    public void Gap_TotalHorizontalVertical()
    {
        var (total, horizontal, vertical) = GapMath.Measure(new System.Numerics.Vector3(0, 0, 0), new System.Numerics.Vector3(30, 40, -64));
        Assert.Equal(50f, horizontal, 3);
        Assert.Equal(64f, vertical, 3);
        Assert.Equal(MathF.Sqrt(50 * 50 + 64 * 64), total, 3);
    }
}
