using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.Admin;
using SwbsJailbreak.Modules.Extras;
using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

/// <summary>Engine-free rules of the ports of AFKManager, MapRatings, Parachute, NoTextSpam, UnlimitedReserveAmmo,
/// LogAdminActivity and the Store.</summary>
public class ServerPluginsTests
{
    // ---------------- NoTextSpam

    [Fact]
    public void Spam_NormalChatPasses()
    {
        var g = new SpamGuard();
        Assert.False(g.Check(1, "hello", 0).Blocked);
        Assert.False(g.Check(1, "how is everyone", 10).Blocked);
    }

    [Fact]
    public void Spam_LongLineRightAfterAnotherIsBlocked()
    {
        var g = new SpamGuard();
        Assert.False(g.Check(1, "hi", 0).Blocked);
        // 0.5 s later 15 cps allows ~8 characters; this line is longer.
        var v = g.Check(1, "this is way too much text", 0.5);
        Assert.True(v.Blocked);
        Assert.Equal("spam.slow_down", v.WarnKey);
    }

    [Fact]
    public void Spam_SameTextRepeatedIsDroppedUntilTheDelay()
    {
        var g = new SpamGuard();
        Assert.False(g.Check(1, "gg", 0).Blocked);
        Assert.True(g.Check(1, "gg", 4).Blocked);    // clamp(2, 5, 50) = 5 s
        Assert.False(g.Check(1, "gg", 10).Blocked);
    }

    [Fact]
    public void Spam_KeepsSpammingGetsGagged()
    {
        var g = new SpamGuard();
        var t = 0.0;
        var gagged = false;
        for (var i = 0; i < 20 && !gagged; i++)
        {
            t += 0.3;
            gagged = g.Check(1, "spam" + i, t).Gag;
        }

        Assert.True(gagged);
    }

    [Fact]
    public void Spam_SameLineSeenByTwoListenersGetsOneVerdict()
    {
        var g = new SpamGuard();
        g.Check(1, "hi", 0);
        var first = g.Check(1, "too long a line right now", 0.2);
        var second = g.Check(1, "too long a line right now", 0.2);
        Assert.Equal(first, second);
        Assert.True(g.WasBlocked(1, "too long a line right now", 0.2));
    }

    // ---------------- AFK

    [Theory]
    [InlineData(32, 29, 3, true)]
    [InlineData(32, 28, 3, false)]
    [InlineData(10, 10, 0, true)]
    public void Afk_KicksOnlyWhenNearFull(int max, int connected, int free, bool kick) =>
        Assert.Equal(kick, AfkModule.ShouldKick(max, connected, free));

    // ---------------- MapRatings

    [Fact]
    public void MapRatings_AverageIgnoresInvalid()
    {
        Assert.Equal((2.0, 3), MapRatingsModule.Summary(new[] { 1, 2, 3, 0, 9 }));
        Assert.Equal((0.0, 0), MapRatingsModule.Summary(Array.Empty<int>()));
    }

    // ---------------- Parachute

    [Theory]
    [InlineData(-500f, true, false, 90f, -90f)]
    [InlineData(-50f, true, false, 90f, null)]   // already slower than the cap
    [InlineData(-500f, false, false, 90f, null)] // not holding E
    [InlineData(-500f, true, true, 90f, null)]   // on the ground
    [InlineData(300f, true, false, 90f, null)]   // going up
    public void Parachute_CapsTheFallOnlyWhileHoldingUseInTheAir(float vz, bool use, bool ground, float speed, float? expected) =>
        Assert.Equal(expected, ParachuteModule.Glide(vz, use, ground, speed));

    // ---------------- Reserve ammo

    [Theory]
    [InlineData("both", true, true)]
    [InlineData("both", false, true)]
    [InlineData("guards", true, true)]
    [InlineData("guards", false, false)]
    [InlineData("prisoners", true, false)]
    [InlineData("off", true, false)]
    public void ReserveAmmo_TeamsSetting(string teams, bool guard, bool expected) =>
        Assert.Equal(expected, ReserveAmmoModule.TeamAllowed(teams, guard));

    // ---------------- Admin activity

    [Fact]
    public void AdminActivity_LineHasTimeMapAdminAndTarget()
    {
        var line = AdminActivityModule.Format(new DateTime(2026, 10, 14, 3, 4, 5, DateTimeKind.Utc), "jb_clouds",
            "{red}slayed{default} Bob", "Spider", 76561198170169983, "Bob", 0);
        Assert.Equal("2026-10-14 03:04:05Z [jb_clouds] slayed Bob | by Spider (76561198170169983) | target Bob (bot)", line);
    }

    // ---------------- Store

    [Fact]
    public void Store_BuyRules()
    {
        var item = new StoreItem { Id = "tracer_red", Price = 300, Type = StoreItemType.Tracer };
        var inv = new StoreInventory();
        Assert.Equal(StoreModule.BuyResult.NotEnoughPoints, StoreModule.CanBuy(item, inv, 299));
        Assert.Equal(StoreModule.BuyResult.Ok, StoreModule.CanBuy(item, inv, 300));
        inv.Owned.Add("TRACER_RED");
        Assert.Equal(StoreModule.BuyResult.AlreadyOwned, StoreModule.CanBuy(item, inv, 9999));
        Assert.Equal(StoreModule.BuyResult.UnknownItem, StoreModule.CanBuy(null, inv, 9999));
    }

    [Fact]
    public void Store_OneEquippedItemPerType()
    {
        var inv = new StoreInventory();
        var red = new StoreItem { Id = "a", Type = StoreItemType.Tracer };
        var blue = new StoreItem { Id = "b", Type = StoreItemType.Tracer };
        var trail = new StoreItem { Id = "t", Type = StoreItemType.Trail };
        Assert.True(StoreModule.ToggleEquip(inv, red));
        Assert.True(StoreModule.ToggleEquip(inv, blue));     // replaces red
        Assert.True(StoreModule.ToggleEquip(inv, trail));    // other type stays
        Assert.Equal("b", inv.Equipped["Tracer"]);
        Assert.Equal("t", inv.Equipped["Trail"]);
        Assert.False(StoreModule.ToggleEquip(inv, blue));    // unequip
        Assert.False(inv.Equipped.ContainsKey("Tracer"));
    }

    [Fact]
    public void Store_Colors()
    {
        var c = StoreModule.ColorOf("10, 20, 300", new Random(1));
        Assert.Equal((10, 20, 255), (c.R, c.G, c.B));
        Assert.Equal(System.Drawing.Color.White.ToArgb(), StoreModule.ColorOf("nonsense", new Random(1)).ToArgb());
        var rainbow = StoreModule.ColorOf("rainbow", new Random(1));
        Assert.Equal(255, rainbow.A);
    }
}
