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

    /// <summary>The prisoner paths the plugin switches PlayerModels to are the ones prisoners.py writes.</summary>
    [Fact]
    public void PrisonerPaths_MatchTheConverter()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "workshop", "prisoners.py"));
        var root = Regex.Match(script, @"^OUT_ROOT = ""([^""]+)""", RegexOptions.Multiline).Groups[1].Value;
        var block = script[script.IndexOf("PRISONERS = {", StringComparison.Ordinal)..];
        block = block[..block.IndexOf('}')];
        var names = Regex.Matches(block, @"""([a-z_]+)"": """).Select(m => m.Groups[1].Value);
        Assert.Equal(names.Select(n => $"{root}/{n}/{n}.vmdl"), WorkshopPackModule.PrisonerModels);
    }

    /// <summary>The store items point at the .vmdl files store_models.py writes, and are player model items.</summary>
    [Fact]
    public void StoreModelItems_MatchTheConverter()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "workshop", "store_models.py"));
        var root = Regex.Match(script, @"^OUT_ROOT = ""([^""]+)""", RegexOptions.Multiline).Groups[1].Value;
        var block = script[script.IndexOf("STORE_MODELS = {", StringComparison.Ordinal)..];
        block = block[..block.IndexOf("\n}", StringComparison.Ordinal)];
        var names = Regex.Matches(block, @"^    ""([a-z_]+)"": \{", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var kotor = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "workshop", "kotor_models.py"));
        var kblock = kotor[kotor.IndexOf("KOTOR_MODELS = {", StringComparison.Ordinal)..];
        kblock = kblock[..kblock.IndexOf("\n}", StringComparison.Ordinal)];
        names.AddRange(Regex.Matches(kblock, @"^    ""([a-z_]+)"": \{", RegexOptions.Multiline).Select(m => m.Groups[1].Value));
        var items = WorkshopPackModule.StoreModelItems();
        Assert.NotEmpty(names);
        Assert.Equal(names.Select(n => $"{root}/{n}/{n}.vmdl"), items.Select(i => i.Data));
        Assert.All(items, i => Assert.Equal(SwbsJailbreak.Modules.PlayerExtras.StoreItemType.PlayerModel, i.Type));
        Assert.False(new WorkshopPackConfig().StoreModels);
    }

    [Theory]
    [InlineData(false, CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist, true)]
    [InlineData(false, CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist, false)]
    [InlineData(true, CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist, true)]
    [InlineData(true, CounterStrikeSharp.API.Modules.Utils.CsTeam.Spectator, false)]
    public void StorePlayerModel_PrisonersOnlyByDefault(bool guardsToo, CounterStrikeSharp.API.Modules.Utils.CsTeam team, bool wears) =>
        Assert.Equal(wears, SwbsJailbreak.Modules.PlayerExtras.StoreModule.WearsPlayerModel(team, guardsToo));

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
