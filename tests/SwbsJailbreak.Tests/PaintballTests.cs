using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class PaintballTests
{
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
}
