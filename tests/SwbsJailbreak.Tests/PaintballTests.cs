using System.Text.RegularExpressions;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.Extras;
using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class PaintballTests
{
    [Theory]
    [InlineData(1f, 0f, 0f, -1f, 0f, 0f)]          // straight at a wall ahead: the wall faces back
    [InlineData(0.2f, -0.98f, 0f, 0f, 1f, 0f)]      // mostly along -Y: the wall faces +Y
    [InlineData(0.6f, 0f, -0.8f, 0f, 0f, 1f)]       // steeply down: the floor
    [InlineData(0.87f, 0f, -0.5f, -1f, 0f, 0f)]     // 30 degrees down: still the wall ahead
    [InlineData(0.5f, 0f, 0.87f, 0f, 0f, -1f)]      // steeply up: the ceiling
    public void SurfaceNormal_FromTheShot(float dx, float dy, float dz, float nx, float ny, float nz) =>
        Assert.Equal((nx, ny, nz), Paint.SurfaceNormal(dx, dy, dz));

    [Fact]
    public void SurfaceNormal_ShotAtTheShootersOwnFloorPaintsTheFloor() =>
        Assert.Equal((0f, 0f, 1f), Paint.SurfaceNormal(0.95f, 0f, -0.3f, onShooterFloor: true));

    [Fact]
    public void Angles_TurnTheSplatOntoTheSurface()
    {
        Assert.Equal((-90f, 0f, 15f), Paint.AnglesFor((0f, 0f, 1f), 15f));     // floor: model +X points up
        Assert.Equal((0f, 180f, 0f), Paint.AnglesFor((-1f, 0f, 0f), 0f));
        Assert.Equal((0f, 90f, 0f), Paint.AnglesFor((0f, 1f, 0f), 0f));
    }

    [Fact]
    public void ColorList_PicksOneOfTheColours()
    {
        var rng = new Random(1);
        var seen = Enumerable.Range(0, 60).Select(_ => StoreModule.ColorOf(StoreConfig.ClassicPaint, rng).ToArgb()).ToHashSet();
        Assert.Equal(3, seen.Count);
        Assert.Contains(System.Drawing.Color.FromArgb(255, 0, 128, 255).ToArgb(), seen);
    }

    [Fact]
    public void Paintballs_AreTheCheapestItems()
    {
        var items = StoreConfig.DefaultItems();
        var paint = items.Where(i => i.Type == StoreItemType.Paintball).ToList();
        Assert.True(paint.Count >= 10);
        Assert.True(paint.Max(i => i.Price) < items.Where(i => i.Type != StoreItemType.Paintball).Min(i => i.Price));
    }

    [Fact]
    public void OldRainbowPrice_IsLoweredOnce_AdminPricesStay()
    {
        var items = new List<StoreItem>
        {
            new() { Id = "paint_random", Name = "Paintballs", Type = StoreItemType.Paintball, Price = 600 },
            new() { Id = "paint_red", Type = StoreItemType.Paintball, Price = 999 }
        };
        StoreModule.CheapenPaintballs(items);
        Assert.Equal(StoreConfig.PaintRainbowPrice, items[0].Price);
        Assert.Equal("Rainbow paintballs", items[0].Name);
        Assert.Equal(999, items[1].Price);
    }

    /// <summary>The splat models the plugin uses are exactly the ones paintballs.py writes.</summary>
    [Fact]
    public void SplatModels_MatchTheConverter()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "scripts", "workshop", "paintballs.py"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var script = File.ReadAllText(Path.Combine(dir!.FullName, "scripts", "workshop", "paintballs.py"));
        var originals = Regex.Match(script, @"ORIGINAL_NAMES = \[([^\]]*)\]").Groups[1].Value;
        var seeds = Regex.Match(script, @"GENERATED_SEEDS = \[([^\]]*)\]").Groups[1].Value.Split(',').Length;
        var names = Regex.Matches(originals, @"""([a-z_]+)""").Select(m => m.Groups[1].Value)
            .Concat(Enumerable.Range(1, seeds).Select(i => $"splat_{i}"));
        Assert.Equal(names.Select(n => $"models/swbs/paintballs/{n}.vmdl"), new WorkshopPackConfig().PaintballModels);
    }
}
