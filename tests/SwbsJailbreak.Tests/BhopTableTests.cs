using SwbsJailbreak.Modules.Extras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class BhopTableTests
{
    private static List<BhopEntry> Log() => new()
    {
        new BhopEntry { Inputs = 3, AirTicks = 40, GroundTicks = 1, InputTicks = 1, InputsPerTick = 0.075f, Speed = 283.9f, Tick = 1000 },
        new BhopEntry { Inputs = 12, AirTicks = 40, GroundTicks = 4, InputTicks = 6, InputsPerTick = 0.3f, Speed = 251.2f, Tick = 1100 }
    };

    [Fact]
    public void Table_HasNoTabs_AndAlignedColumns()
    {
        var lines = BhopTable.Format(Log(), false, 0.45f, 0.8f);
        Assert.DoesNotContain(lines, l => l.Contains('\t'));          // CS2's console strips tabs
        var header = lines.First(l => l.Contains("Inputs"));
        var row = lines.First(l => l.Contains("283.9"));
        Assert.Equal(header.IndexOf("Speed") + "Speed".Length, row.IndexOf("283.9") + "283.9".Length); // right-aligned
        Assert.Contains("# 1", row);                                    // perfect hop marker
        Assert.Contains("Normal", row);
        Assert.Contains(lines, l => l.Contains("Auto"));
        Assert.Contains(lines, l => l.Contains("50%"));                 // 1 of 2 perfect
    }

    [Fact]
    public void Extended_MarksSuspiciousInputsPerTick()
    {
        var lines = BhopTable.Format(Log(), true, 0.25f, 0.8f);
        Assert.Contains(lines, l => l.Contains("*0.30"));
        Assert.Contains(lines, l => l.Contains("A 6"));
    }
}
