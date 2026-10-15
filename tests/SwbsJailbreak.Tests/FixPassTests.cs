using System.Numerics;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.Extras;
using SwbsJailbreak.Modules.PlayerExtras;
using SwbsJailbreak.Modules.Teams;
using SwbsJailbreak.Modules.Zones;
using Xunit;

namespace SwbsJailbreak.Tests;

public class FixPassTests
{
    [Fact]
    public void BhopCap_ScalesOnlyAboveTheCap()
    {
        Assert.Equal((100f, 0f), ServerTweaksModule.CapHorizontal(100f, 0f, 400f));
        var (x, y) = ServerTweaksModule.CapHorizontal(600f, 800f, 400f); // 1000 u/s
        Assert.Equal(400f, MathF.Sqrt(x * x + y * y), 2);
        Assert.Equal(800f / 600f, y / x, 3); // direction kept
    }

    [Fact]
    public void BhopCheck_PerfRatioUsesNewestHops()
    {
        var log = new List<BhopEntry>();
        for (var i = 0; i < 10; i++) log.Add(new BhopEntry { GroundTicks = i < 8 ? 1 : 5 });
        Assert.Equal(1f, BhopCheckModule.PerfRatio(log, 5));
        Assert.Equal(0.8f, BhopCheckModule.PerfRatio(log, 10), 3);
        Assert.Equal(0f, BhopCheckModule.PerfRatio(new List<BhopEntry>(), 10));
    }

    [Fact]
    public void SpawnZone_SpotStaysInsideAndOnTheFloor()
    {
        var box = new ZoneBox(new Vector3(100, 200, 50), new Vector3(-64, -64, 2), new Vector3(64, 64, 72));
        var rng = new Random(1);
        for (var i = 0; i < 200; i++)
        {
            var p = SpawnZone.RandomSpot(box, rng);
            Assert.InRange(p.X, 100 - 48, 100 + 48);
            Assert.InRange(p.Y, 200 - 48, 200 + 48);
            Assert.Equal(52f, p.Z);
        }
    }

    [Fact]
    public void Defaults_OneGuardPerFivePrisoners_ModelsOn_MovementForSurf()
    {
        Assert.Equal(5f, new TeamsConfig().PrisonersPerGuard);
        Assert.False(new PlayerModelsConfig().ApplyModels); // missing models give huge first-person guns
        var mv = new ServerTweaksConfig().MovementCvars;
        Assert.True(float.Parse(mv["sv_airaccelerate"]) >= 100f);
        Assert.Equal("1", mv["sv_enablebunnyhopping"]);
        Assert.DoesNotContain("sv_autobunnyhopping", mv.Keys);
    }

    [Fact]
    public void Store_HasNoOldPathAgents()
    {
        Assert.DoesNotContain(StoreConfig.DefaultItems(), i => ModelPaths.IsLegacyAgentPath(i.Data));
        Assert.Equal(10, StoreConfig.RemovedAgentIds.Length);
    }

    [Theory]
    [InlineData("characters/models/tm_phoenix/tm_phoenix_varianth.vmdl", true)]
    [InlineData("characters/models/ctm_sas/ctm_sas.vmdl", true)]
    [InlineData("agents/models/ctm_diver/ctm_diver_varianta.vmdl", false)]
    [InlineData("characters/models/swbs/store/flash/flash.vmdl", false)]
    public void LegacyAgentPaths_AreDetected(string path, bool legacy) => Assert.Equal(legacy, ModelPaths.IsLegacyAgentPath(path));
}
