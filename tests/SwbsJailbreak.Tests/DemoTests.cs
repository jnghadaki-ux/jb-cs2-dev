using SwbsJailbreak.Modules.Extras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class DemoTests
{
    [Fact]
    public void FileName_IsSafeAndSortable()
    {
        var n = DemoRules.FileName("SWBS #1 | Jailbreak!", "jb_San Palmora", new System.DateTime(2026, 10, 16, 21, 4, 5, System.DateTimeKind.Utc));
        Assert.Equal("swbs_1_jailbreak_jb_san_palmora_2026-10-16_21-04-05", n);
        Assert.StartsWith("server_", DemoRules.FileName("  ", "de_dust2", System.DateTime.UtcNow));
    }

    [Theory]
    [InlineData(5, "00:05")]
    [InlineData(761, "12:41")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-3, "00:00")]
    public void Stamp_FormatsDemoTime(double seconds, string expected) => Assert.Equal(expected, DemoRules.Stamp(seconds));

    [Fact]
    public void Expired_UsesKeepDays_ZeroKeepsForever()
    {
        var now = new System.DateTime(2026, 10, 16);
        Assert.True(DemoRules.Expired(now.AddDays(-15), now, 14));
        Assert.False(DemoRules.Expired(now.AddDays(-13), now, 14));
        Assert.False(DemoRules.Expired(now.AddDays(-400), now, 0));
    }
}
