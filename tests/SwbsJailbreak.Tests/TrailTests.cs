using System.Text.RegularExpressions;
using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class TrailTests
{
    private static string Script()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "scripts", "workshop", "trails.py"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "scripts", "workshop", "trails.py"));
    }

    /// <summary>Every piece a trail style uses is a model trails.py builds, and every built piece is used.</summary>
    [Fact]
    public void Pieces_MatchTheGenerator()
    {
        var script = Script();
        var built = new HashSet<string>();
        foreach (var list in new[] { "FLAT", "CARDS", "SOLIDS", "FIRE", "ICE", "GALAXY" })
        {
            var m = Regex.Match(script, $@"^{list} = \[([^\]]*)\]", RegexOptions.Multiline);
            Assert.True(m.Success, list);
            foreach (Match n in Regex.Matches(m.Groups[1].Value, @"""([a-z_0-9]+)""")) built.Add(n.Groups[1].Value);
        }

        var used = StoreTrails.Styles.Values.SelectMany(s => s.Pieces).ToHashSet();
        Assert.Equal(built.OrderBy(x => x), used.OrderBy(x => x));
        Assert.All(StoreTrails.AllModels(), m => Assert.StartsWith("models/swbs/trails/", m));
    }

    [Fact]
    public void OnlyFootprintsGoOnTheFloor()
    {
        foreach (var (name, style) in StoreTrails.Styles)
        {
            if (name == "footprints") Assert.Equal(TrailPlacement.Footsteps, style.Placement);
            else
            {
                Assert.Equal(TrailPlacement.Back, style.Placement);
                Assert.True(style.MinZ >= 30f && style.MaxZ <= 64f && style.MinZ <= style.MaxZ, name); // around the back
            }
        }
    }

    [Fact]
    public void DefaultTrailItems_UseKnownStyles()
    {
        var trails = StoreConfig.DefaultItems().Where(i => i.Type == StoreItemType.Trail).ToList();
        foreach (var style in StoreTrails.Styles.Keys) Assert.Contains(trails, t => t.Data.Equals(style, StringComparison.OrdinalIgnoreCase));
        Assert.All(trails.Where(t => t.Data.Length > 0), t => Assert.NotNull(StoreTrails.StyleOf(t)));
        Assert.Contains(trails, t => t.Data.Length == 0); // the beam trails stay
    }

    [Fact]
    public void DropPoints_EverySpacingAlongTheMove()
    {
        var (pts, last) = StoreTrails.DropPoints((0, 0, 0), (70, 0, 0), 24f);
        Assert.Equal(new[] { 24f, 48f }, pts.Select(p => p.X));
        Assert.Equal(48f, last.X);

        var (none, same) = StoreTrails.DropPoints((0, 0, 0), (10, 0, 0), 24f);
        Assert.Empty(none);
        Assert.Equal(0f, same.X); // keeps measuring from the last drop

        var (capped, jumped) = StoreTrails.DropPoints((0, 0, 0), (1000, 0, 0), 24f);
        Assert.Equal(3, capped.Count);
        Assert.Equal(1000f, jumped.X); // teleport / big jump: restart from here
    }

    [Fact]
    public void Yaw_FollowsTheMove()
    {
        Assert.Equal(0f, StoreTrails.Yaw(5, 0));
        Assert.Equal(90f, StoreTrails.Yaw(0, 5));
        Assert.Null(StoreTrails.Yaw(0.01f, 0.01f));
    }
}
