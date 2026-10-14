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
}
