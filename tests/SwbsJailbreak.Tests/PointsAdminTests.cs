using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class PointsAdminTests
{
    [Theory]
    [InlineData(100, 0, 500, 600)]
    [InlineData(100, 1, 30, 70)]
    [InlineData(100, 1, 500, 0)]                       // never below zero
    [InlineData(100, 2, 2500, 2500)]
    [InlineData(int.MaxValue - 5, 0, 100, int.MaxValue)] // no overflow
    public void AdminChange(int current, int op, int amount, int expected) => // op: 0 give, 1 take, 2 set
        Assert.Equal(expected, PointsModule.AdminChange(current, (PointsModule.PointsOp)op, amount));

    [Fact]
    public void PointsCommands_NeedRootByDefault() =>
        Assert.Equal("@css/root", SwbsJailbreak.Core.Perms.Defaults["points"]);
}
