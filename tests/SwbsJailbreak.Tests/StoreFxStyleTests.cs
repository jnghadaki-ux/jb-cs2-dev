using System.Numerics;
using System.Text.RegularExpressions;
using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class StoreFxStyleTests
{
    private static string Tool(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }

    private static IEnumerable<string> Table(string script, string name)
    {
        var m = Regex.Match(script, $@"^{name} = \{{(.*?)^\}}", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(m.Success, name);
        return Regex.Matches(m.Groups[1].Value, @"'([a-z_0-9]+)':").Select(x => x.Groups[1].Value);
    }

    /// <summary>Every generated hat, face and back model has emitter points for equipment effects, and nothing else does.</summary>
    [Fact]
    public void Emitters_CoverEveryPackModel()
    {
        var equipment = Tool("tools", "store-pack-2", "equipment.py");
        var models = Table(Tool("tools", "workshop-hats", "hats.py"), "HATS")
            .Concat(Table(equipment, "FACE")).Concat(Table(equipment, "BACK")).OrderBy(x => x);
        Assert.Equal(models, StoreModule.GearEmitters.Keys.OrderBy(x => x));
        Assert.All(StoreModule.GearEmitters.Values, e => Assert.NotEmpty(e));
        Assert.All(StoreModule.GearEmitters.Values.SelectMany(e => e), e => Assert.Equal(1f, e.Dir.Length(), 3));
        Assert.Single(StoreModule.EmittersOf("models/my/own_hat.vmdl"));
        Assert.Equal(2, StoreModule.EmittersOf("models/swbs/store2/back/jetpack.vmdl").Length);
        Assert.All(StoreModule.EmittersOf("models/swbs/store2/back/jetpack.vmdl"), e => Assert.True(e.Dir.Z < -0.99f)); // nozzles blow down
    }

    [Fact]
    public void ToWorld_FollowsSourceAngles()
    {
        static void Near(Vector3 a, Vector3 b) => Assert.True(Vector3.Distance(a, b) < 1e-3f, $"{a} != {b}");
        var o = new Vector3(100, 200, 300);
        Near(new Vector3(110, 200, 300), StoreModule.ToWorld(o, 0, 0, 0, new Vector3(10, 0, 0)));
        Near(new Vector3(100, 210, 300), StoreModule.ToWorld(o, 0, 90, 0, new Vector3(10, 0, 0)));   // yaw 90: forward is +Y
        Near(new Vector3(100, 200, 290), StoreModule.ToWorld(o, 90, 0, 0, new Vector3(10, 0, 0)));   // pitch 90: looking down
        Near(new Vector3(100, 200, 310), StoreModule.ToWorld(o, 0, 0, 90, new Vector3(0, 10, 0)));   // roll 90: left goes up
        Near(new Vector3(90, 200, 300), StoreModule.ToWorld(o, 0, 180, 0, new Vector3(10, 0, 0)));
    }

    [Fact]
    public void DefaultItems_UseKnownStyles()
    {
        var items = StoreConfig.DefaultItems();
        var auras = items.Where(i => i.Type == StoreItemType.Aura).ToList();
        Assert.All(auras.Where(i => i.Data.Length > 0), i => Assert.Contains(i.Data, StoreModule.AuraStyles));
        foreach (var style in StoreModule.AuraStyles.Where(s => s != "pulse"))
            Assert.Contains(auras, i => i.Data == style);
        Assert.Equal("pulse", StoreModule.AuraStyle(new StoreItem { Type = StoreItemType.Aura, Data = "" }));
        Assert.Equal("pulse", StoreModule.AuraStyle(new StoreItem { Type = StoreItemType.Aura, Data = "nonsense" }));

        var gearFx = items.Where(i => i.Type == StoreItemType.GearEffect).ToList();
        Assert.Equal(StoreModule.GearEffectStyles.OrderBy(x => x), gearFx.Select(i => i.Data).Distinct().OrderBy(x => x));
        // Hidden until there is equipment to come out of.
        Assert.All(gearFx, i => Assert.False(StoreModule.Available(i)));
    }

    [Fact]
    public void MovingAuras_StayAroundThePlayer()
    {
        for (var t = 0.0; t < 20; t += 0.37)
        {
            for (var i = 0; i < 3; i++) Assert.InRange(StoreModule.OrbitAt(i, t).Height, 10f, 60f);
            for (var k = 0; k < 8; k++) Assert.InRange(StoreModule.HelixAt(k % 2, k, 8, t).Height, 0f, 80f);
            for (var r = 0; r < 2; r++)
            {
                var (h, radius) = StoreModule.RingAt(r, 2, t);
                Assert.InRange(h, 0f, 80f);
                Assert.InRange(radius, 10f, 30f);
            }
        }

        // The two rings stay half a cycle apart.
        Assert.Equal(36f, MathF.Abs(StoreModule.RingAt(0, 2, 0.1).Height - StoreModule.RingAt(1, 2, 0.1).Height), 2);
        Assert.Equal(System.Drawing.Color.Red.ToArgb(), StoreModule.AuraColor("255,0,0", 3, 1, System.Drawing.Color.Red).ToArgb());
        Assert.NotEqual(StoreModule.AuraColor("rainbow", 0, 0, default).ToArgb(), StoreModule.AuraColor("rainbow", 1, 0, default).ToArgb());
    }
}
