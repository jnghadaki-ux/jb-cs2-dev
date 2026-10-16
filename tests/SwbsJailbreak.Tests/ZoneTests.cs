using System.Numerics;
using System.Text.Json;
using SwbsJailbreak.Core;
using Xunit;

namespace SwbsJailbreak.Tests;

public class ZoneGeometryTests
{
    private static ZoneBox Box(float x = 0, float y = 0, float z = 0) => new(new Vector3(x, y, z), ZoneBox.DefaultMins, ZoneBox.DefaultMaxs);

    [Fact]
    public void DefaultZone_MatchesTheOriginalSize_AndFloorCentre()
    {
        var b = Box(10, 20, 30);
        Assert.Equal(new Vector3(200, 200, 98), b.Size);
        Assert.Equal(new Vector3(10, 20, 32), b.BottomCenter); // origin z + mins z (2)
    }

    [Theory]
    [InlineData(0, 0, 0, true)]        // feet inside
    [InlineData(115, 0, 50, true)]     // hull (±16) reaches the side at x=100
    [InlineData(117, 0, 50, false)]    // just beyond the hull
    [InlineData(0, 0, -70, true)]      // head (feet+72) reaches the floor at z=2
    [InlineData(0, 0, -71, false)]
    [InlineData(0, 0, 100, true)]      // feet on the ceiling plane
    [InlineData(0, 0, 101, false)]
    public void HullOverlap_IsWhatATriggerTouchMeant(float x, float y, float z, bool expected) =>
        Assert.Equal(expected, Box().OverlapsHull(new Vector3(x, y, z)));

    [Fact]
    public void FastPlayer_CannotTunnelThroughAThinWall()
    {
        // 8 units thick wall at x = 0, player moves 60 units in one tick from one side to the other.
        var wall = new ZoneBox(Vector3.Zero, new Vector3(-4, -200, 0), new Vector3(4, 200, 128));
        Assert.False(wall.OverlapsHull(new Vector3(-50, 0, 0)));
        Assert.False(wall.OverlapsHull(new Vector3(50, 0, 0)));
        Assert.True(wall.HullSweepHits(new Vector3(-50, 0, 0), new Vector3(50, 0, 0)));
        Assert.False(wall.HullSweepHits(new Vector3(-50, 300, 0), new Vector3(50, 300, 0))); // passes beside it
    }

    [Fact]
    public void NearestOutside_PushesHorizontallyOutOfTheClosestSide()
    {
        var wall = new ZoneBox(Vector3.Zero, new Vector3(-10, -200, 0), new Vector3(10, 200, 128));
        var p = wall.NearestOutside(new Vector3(-3, 50, 0));
        Assert.False(wall.OverlapsHull(p));
        Assert.True(p.X < 0);          // left side was closer
        Assert.Equal(50, p.Y);         // no sideways drift
        Assert.Equal(0, p.Z);          // never pushed into the floor or ceiling
    }

    [Fact]
    public void FromCorners_SpansBothPoints_WithAtLeastPlayerHeight()
    {
        var b = ZoneBox.FromCorners(new Vector3(100, 100, 0), new Vector3(-100, 50, 10));
        Assert.Equal(new Vector3(-100, 50, 0), b.WorldMins);
        Assert.Equal(new Vector3(100, 100, 72), b.WorldMaxs); // 10 high raised to 72
    }

    [Fact]
    public void Corners_AndEdges_DrawAClosedBox()
    {
        var corners = Box().Corners();
        Assert.Equal(8, corners.Length);
        Assert.Equal(12, ZoneBox.Edges.Length);
        foreach (var (a, b) in ZoneBox.Edges)
        {
            var diff = corners[a] - corners[b];
            var changedAxes = (diff.X != 0 ? 1 : 0) + (diff.Y != 0 ? 1 : 0) + (diff.Z != 0 ? 1 : 0);
            Assert.Equal(1, changedAxes); // every edge runs along one axis
        }
    }
}

public class ZoneEditRuleTests
{
    [Theory]
    [InlineData(0, 0, "maxX")]       // looking +x
    [InlineData(0, 90, "maxY")]      // +y
    [InlineData(0, -90, "minY")]     // -y
    [InlineData(0, 180, "minX")]     // -x
    [InlineData(0, -179, "minX")]
    [InlineData(60, 0, "minZ")]      // looking down moves the floor
    [InlineData(-60, 0, "maxZ")]     // looking up moves the ceiling
    [InlineData(0, 45, "none")]      // diagonal: the original changes nothing
    public void ResizeByLook_MovesTheFaceTheOriginalMoved(float pitch, float yaw, string face)
    {
        var mins = new Vector3(-10, -10, 0);
        var maxs = new Vector3(10, 10, 50);
        ZoneEdit.ResizeByLook(pitch, yaw, ref mins, ref maxs, 4);
        var expectedMins = new Vector3(face == "minX" ? -14 : -10, face == "minY" ? -14 : -10, face == "minZ" ? -4 : 0);
        var expectedMaxs = new Vector3(face == "maxX" ? 14 : 10, face == "maxY" ? 14 : 10, face == "maxZ" ? 54 : 50);
        Assert.Equal(expectedMins, mins);
        Assert.Equal(expectedMaxs, maxs);
    }

    [Fact]
    public void ResizeByLook_NeverInvertsTheBox()
    {
        var mins = new Vector3(-2, -2, 0);
        var maxs = new Vector3(2, 2, 10);
        for (var i = 0; i < 10; i++) ZoneEdit.ResizeByLook(0, 0, ref mins, ref maxs, -4);
        Assert.Equal(-1, maxs.X); // clamped to mins + 1
        Assert.True(maxs.X > mins.X);
    }

    [Fact]
    public void MoveFaceTo_ReplacesExpandToWall_WithAPingedPoint()
    {
        var mins = new Vector3(-10, -10, 0);
        var maxs = new Vector3(10, 10, 50);
        var origin = new Vector3(100, 0, 0);
        Assert.True(ZoneEdit.MoveFaceTo(0, 0, origin, ref mins, ref maxs, new Vector3(250, 999, 999)));
        Assert.Equal(150, maxs.X);              // wall at x=250 relative to origin 100
        Assert.Equal(new Vector3(-10, -10, 0), mins);
        Assert.False(ZoneEdit.MoveFaceTo(0, 45, origin, ref mins, ref maxs, new Vector3(0, 0, 0)));
    }

    [Theory]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(540, 180)]
    [InlineData(-180, 180)]
    public void Yaw_IsNormalized(float input, float expected) => Assert.Equal(expected, ZoneEdit.NormalizeYaw(input), 3);
}

public class TimedToggleTests
{
    // Truth table of SMPlugins ZoneType_Kill ShouldKill (on = kill / blockade solid).
    [Theory]
    [InlineData(0, 0, 0, true)]      // no timers: always on
    [InlineData(5, 10, 0, false)]    // only "on" timer, not reached
    [InlineData(15, 10, 0, true)]    // only "on" timer, reached
    [InlineData(5, 0, 10, true)]     // only "off" timer, not reached
    [InlineData(15, 0, 10, false)]   // only "off" timer, reached
    [InlineData(5, 10, 20, false)]   // on at 10, off at 20: before both
    [InlineData(15, 10, 20, true)]   // between
    [InlineData(25, 10, 20, false)]  // after both: off came last
    [InlineData(5, 20, 10, true)]    // off at 10, on at 20: on until 10
    [InlineData(15, 20, 10, false)]
    [InlineData(25, 20, 10, true)]   // on came last
    public void MatchesTheOriginalKillAndBlockadeTimers(float elapsed, int on, int off, bool expected) =>
        Assert.Equal(expected, TimedToggle.IsOn(elapsed, on, off));
}

public class ZoneTrackingTests
{
    [Fact]
    public void Diff_ReportsEnteredAndExited_Sorted()
    {
        var (entered, exited) = ZoneDiff.Compute(new HashSet<int> { 1, 2, 5 }, new HashSet<int> { 2, 7, 3 });
        Assert.Equal(new[] { 3, 7 }, entered);
        Assert.Equal(new[] { 1, 5 }, exited);
    }

    [Fact]
    public void Diff_NoChange_IsEmpty()
    {
        var (entered, exited) = ZoneDiff.Compute(new HashSet<int> { 4 }, new HashSet<int> { 4 });
        Assert.Empty(entered);
        Assert.Empty(exited);
    }
}

public class ZoneRecordTests
{
    [Fact]
    public void Normalize_RepairsHandEditedFiles()
    {
        var r = new ZoneRecord
        {
            Id = 3, Type = "  KILL ", Origin = new float[] { 1, 2 }, Mins = new float[] { 10, 0, 0 }, Maxs = new float[] { -10, 0, 5 },
            Ints = new[] { 5 }, Strings = new[] { "a", null! }, Label = null!
        };
        r.Normalize();
        Assert.Equal("kill", r.Type);
        Assert.Equal(new float[] { 1, 2, 0 }, r.Origin);
        Assert.Equal(-10, r.Mins[0]);   // swapped
        Assert.Equal(10, r.Maxs[0]);
        Assert.Equal(1, r.Maxs[1] - r.Mins[1]); // zero-size axis widened to 1 unit
        Assert.Equal(new[] { 5, 0 }, r.Ints);
        Assert.Equal(new[] { "a", "", "", "", "" }, r.Strings);
        Assert.Equal("", r.Label);
    }

    [Fact]
    public void MapDoc_RoundTripsThroughTheStorageJsonOptions()
    {
        var doc = new ZoneMapDoc();
        var r = new ZoneRecord { Id = 1, Type = ZoneTypeKeys.TeleportDestination, Origin = new float[] { 1.5f, -2, 3 }, Angles = new float[] { 0, 90, 0 } };
        r.Strings[0] = "lr_tele";
        doc.Zones.Add(r);
        var json = JsonSerializer.Serialize(doc, Storage.JsonOptions);
        Assert.Contains("\"teleport_destination\"", json);
        Assert.Contains("\"lr_tele\"", json);
        var back = JsonSerializer.Deserialize<ZoneMapDoc>(json, Storage.JsonOptions)!;
        Assert.Single(back.Zones);
        Assert.Equal("lr_tele", back.Zones[0].Strings[0]);
        Assert.Equal(90, back.Zones[0].Angles[1]);
    }
}

public class MapInputRulesTests
{
    [Theory]
    [InlineData("cell_button", "Cell_Button", true)]
    [InlineData("cell_*", "cell_door_3", true)]
    [InlineData("cell_*", "armory_door", false)]
    [InlineData("", "cell_button", false)]
    [InlineData("cell_button", null, false)]
    public void NameMatches_IsCaseInsensitive_WithTrailingWildcard(string pattern, string? name, bool expected) =>
        Assert.Equal(expected, SwbsJailbreak.Modules.Zones.MapInputRules.NameMatches(pattern, name));

    [Theory]
    [InlineData("func_button", "Press")]
    [InlineData("func_door_rotating", "Toggle")]
    [InlineData("logic_relay", "Trigger")]
    [InlineData("info_target", null)]
    public void DefaultInput_FollowsTheClass(string cls, string? expected) =>
        Assert.Equal(expected, SwbsJailbreak.Modules.Zones.MapInputRules.DefaultInput(cls));

    [Fact]
    public void Activation_MapsChoicesToTriggerAndTeam()
    {
        Assert.Equal((SwbsJailbreak.Modules.Zones.MapInputTrigger.PressUse, 0), SwbsJailbreak.Modules.Zones.MapInputRules.Activation(0));
        Assert.Equal((SwbsJailbreak.Modules.Zones.MapInputTrigger.PressUse, 3), SwbsJailbreak.Modules.Zones.MapInputRules.Activation(1));
        Assert.Equal((SwbsJailbreak.Modules.Zones.MapInputTrigger.Enter, 2), SwbsJailbreak.Modules.Zones.MapInputRules.Activation(5));
        Assert.Equal((SwbsJailbreak.Modules.Zones.MapInputTrigger.RoundStart, 0), SwbsJailbreak.Modules.Zones.MapInputRules.Activation(6));
    }

    [Fact]
    public void BoxesOverlap_TouchingCountsAndApartDoesNot()
    {
        var a0 = new Vector3(0, 0, 0); var a1 = new Vector3(10, 10, 10);
        Assert.True(SwbsJailbreak.Modules.Zones.MapInputRules.BoxesOverlap(a0, a1, new Vector3(10, 0, 0), new Vector3(20, 5, 5)));
        Assert.False(SwbsJailbreak.Modules.Zones.MapInputRules.BoxesOverlap(a0, a1, new Vector3(11, 0, 0), new Vector3(20, 5, 5)));
    }
}
