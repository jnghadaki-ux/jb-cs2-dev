using System.Text.RegularExpressions;
using SwbsJailbreak.Modules.Extras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class WorkshopPackTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "scripts", "workshop", "build_pack.py"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>The pack builder writes exactly the sound events the plugin points its settings at.</summary>
    [Fact]
    public void PackScript_DefinesEveryPluginEvent()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "workshop", "build_pack.py"));
        var inScript = Regex.Matches(script, @"^\s+""(swbs\.[a-z_]+)"": \(", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal(WorkshopPackModule.Events.All.OrderBy(x => x), inScript.OrderBy(x => x));
        Assert.Contains(new WorkshopPackConfig().SoundEventsFile.Replace("soundevents/", ""), script);
    }

    [Fact]
    public void Pack_IsOffUntilEnabled()
    {
        var c = new WorkshopPackConfig();
        Assert.False(c.Enabled);
        Assert.False(c.SoundsApplied);
        Assert.Equal("soundevents/swbs_jailbreak.vsndevts", c.SoundEventsFile);
    }

    /// <summary>The plugin's default model paths are exactly the .vmdl files the converter writes (models/swbs/&lt;name&gt;.vmdl).</summary>
    [Fact]
    public void ModelPaths_MatchTheConverter()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "workshop", "props.py"));
        var names = Regex.Matches(script, @"^\s+""([a-z_]+)"": \(""Mods/", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        var c = new WorkshopPackConfig();
        var paths = new[] { c.FlagModel, c.CapturePointModel, c.FinishLineModel, c.MissileModel, c.RocketLauncherModel };
        Assert.Equal(names.Select(n => $"models/swbs/{n}.vmdl").OrderBy(x => x), paths.OrderBy(x => x));
        Assert.True(c.UseModels);
    }

    [Theory]
    [InlineData(1f, 0f, 0f, 90f, 0f)]    // flying straight ahead (+X): nose (+Z) tipped forward
    [InlineData(0f, 1f, 0f, 90f, 90f)]
    [InlineData(0f, 0f, 1f, 0f, 0f)]     // straight up: no tilt
    [InlineData(0f, 0f, -1f, 180f, 0f)]  // straight down: upside down
    public void Missile_PointsAlongItsFlight(float x, float y, float z, float pitch, float yaw)
    {
        var a = SwbsJailbreak.Modules.Days.RpgDay.MissileAngles(new CounterStrikeSharp.API.Modules.Utils.Vector(x * 900, y * 900, z * 900));
        Assert.Equal(pitch, a.X, 2);
        Assert.Equal(yaw, a.Y, 2);
    }
}
