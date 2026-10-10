using SwbsJailbreak.Core;
using Xunit;

namespace SwbsJailbreak.Tests;

public class RoundStateMachineTests
{
    [Fact]
    public void NormalRoundCycle_IsAccepted_AndCountsRounds()
    {
        var m = new RoundStateMachine();
        Assert.True(m.TryTransition(RoundPhase.FreezeTime));
        Assert.True(m.TryTransition(RoundPhase.Active));
        Assert.True(m.MarkCellsOpened());
        Assert.True(m.TryTransition(RoundPhase.Ended));
        Assert.True(m.TryTransition(RoundPhase.FreezeTime));
        Assert.Equal(2, m.RoundNumber);
        Assert.False(m.CellsOpened); // reset by the new round
    }

    [Fact]
    public void DuplicateTransitions_AreRejected()
    {
        var m = new RoundStateMachine();
        m.TryTransition(RoundPhase.FreezeTime);
        m.TryTransition(RoundPhase.Active);
        Assert.True(m.TryTransition(RoundPhase.Ended));
        Assert.False(m.TryTransition(RoundPhase.Ended));
        Assert.Contains("duplicate", m.LastRejection);
        Assert.Equal(1, m.RoundNumber);
    }

    [Theory]
    [InlineData(RoundPhase.Inactive, RoundPhase.Ended)]
    [InlineData(RoundPhase.Active, RoundPhase.FreezeTime)]
    [InlineData(RoundPhase.FreezeTime, RoundPhase.WaitingForPlayers)]
    public void IllegalTransitions_AreRejected(RoundPhase from, RoundPhase to)
    {
        Assert.False(RoundStateMachine.IsLegal(from, to));
    }

    [Fact]
    public void CellsOpen_OnlyOncePerRound_AndNotBetweenRounds()
    {
        var m = new RoundStateMachine();
        Assert.False(m.MarkCellsOpened()); // inactive
        m.TryTransition(RoundPhase.Active);
        Assert.True(m.MarkCellsOpened());
        Assert.False(m.MarkCellsOpened());
        m.TryTransition(RoundPhase.Ended);
        Assert.False(m.MarkCellsOpened());
    }

    [Fact]
    public void RoundWithoutFreezeTime_CountsOnce()
    {
        var m = new RoundStateMachine();
        Assert.True(m.TryTransition(RoundPhase.Active));
        Assert.Equal(1, m.RoundNumber);
        m.TryTransition(RoundPhase.Ended);
        Assert.True(m.TryTransition(RoundPhase.Active));
        Assert.Equal(2, m.RoundNumber);
    }

    [Fact]
    public void MapReset_ClearsEverything()
    {
        var m = new RoundStateMachine();
        m.TryTransition(RoundPhase.FreezeTime);
        m.ResetForMap();
        Assert.Equal(RoundPhase.Inactive, m.Phase);
        Assert.Equal(0, m.RoundNumber);
    }
}

public class TextTests
{
    [Fact]
    public void ColourTags_AreExpanded_AndStrippable()
    {
        var applied = Colors.Apply("{green}Hi {red}there{default}");
        Assert.DoesNotContain("{green}", applied);
        Assert.Equal("Hi there", Colors.Strip("{green}Hi {red}there{default}"));
        Assert.Equal("plain", Colors.Strip("plain"));
    }

    [Fact]
    public void UnknownPhrase_ReturnsKey_AndFormatsSafely()
    {
        Assert.Equal("missing.key", Lang.T("missing.key"));
        Assert.Equal("missing.key", Lang.T("missing.key", 1, 2));
    }

    [Fact]
    public void PhraseFiles_LoadAndFallBackToEnglish()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(dir, "lang", "en"));
        Directory.CreateDirectory(Path.Combine(dir, "lang", "fr"));
        File.WriteAllText(Path.Combine(dir, "lang", "en", "a.json"), "{ \"x.hello\": \"Hello {0}\", \"x.only_en\": \"EN\" }");
        File.WriteAllText(Path.Combine(dir, "lang", "fr", "a.json"), "{ \"x.hello\": \"Bonjour {0}\" }");
        Lang.Load(dir, "fr");
        Assert.Equal("Bonjour Gdaki", Lang.T("x.hello", "Gdaki"));
        Assert.Equal("EN", Lang.T("x.only_en"));
    }

    [Fact]
    public void HudTime_FormatsMinutesSeconds()
    {
        Assert.Equal("1:05", Hud.Time(65f));
        Assert.Equal("0:00", Hud.Time(-3f));
        Assert.Equal("0:01", Hud.Time(0.2f));
    }
}

public class ConfigAndStorageTests
{
    private sealed class SampleSection
    {
        public bool Enabled { get; set; } = true;
        public int Seconds { get; set; } = 30;
    }

    [Fact]
    public void MissingSection_IsCreatedWithDefaults_AndExistingValuesSurvive()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "cfg.json");
        File.WriteAllText(path, "{ \"Sample\": { \"Seconds\": 99 } }");
        var cfg = new JailbreakConfig();
        cfg.Load(path, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var s = cfg.Section<SampleSection>("Sample");
        Assert.Equal(99, s.Seconds);
        Assert.True(s.Enabled);
        Assert.NotNull(cfg.General);
        cfg.SaveIfDirty(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var text = File.ReadAllText(path);
        Assert.Contains("\"Enabled\"", text);  // new key written back
        Assert.Contains("\"General\"", text);
    }

    [Fact]
    public void InvalidJson_FallsBackToDefaults_AndKeepsBackup()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "cfg.json");
        File.WriteAllText(path, "{ not json");
        var cfg = new JailbreakConfig();
        cfg.Load(path, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Equal(30, cfg.Section<SampleSection>("Sample").Seconds);
        Assert.NotEmpty(Directory.GetFiles(dir, "cfg.json.invalid-*"));
    }

    private sealed class Ban
    {
        public string Reason { get; set; } = "";
        public long Until { get; set; }
    }

    [Fact]
    public void Storage_RoundTrips_AndFlushesAtomically()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Storage.Init(dir);
        Storage.Set("ctbans", "7656119", new Ban { Reason = "freekill", Until = 123 });
        Assert.Equal("freekill", Storage.Get<Ban>("ctbans", "7656119")!.Reason);
        Storage.Flush(async: false);
        Storage.Init(dir); // drop the cache, read from disk
        Assert.Equal(123, Storage.Get<Ban>("ctbans", "7656119")!.Until);
        Storage.Delete("ctbans", "7656119");
        Assert.False(Storage.Has("ctbans", "7656119"));
    }
}

public class CatalogTests
{
    [Theory]
    [InlineData("ak47", "weapon_ak47")]
    [InlineData("weapon_awp", "weapon_awp")]
    [InlineData("Desert Eagle", "weapon_deagle")]
    public void WeaponNames_AreParsed(string input, string expected) => Assert.Equal(expected, Weapons.Parse(input));

    [Fact]
    public void UnknownWeapon_IsNull() => Assert.Null(Weapons.Parse("banana"));

    [Fact]
    public void KnifeDetection_CoversSkins()
    {
        Assert.True(Weapons.IsKnife("weapon_knife_t"));
        Assert.True(Weapons.IsKnife("weapon_bayonet"));
        Assert.False(Weapons.IsKnife("weapon_ak47"));
    }

    [Fact]
    public void Permissions_ResolveCapabilities_AndPassThroughFlags()
    {
        Assert.Equal("@css/ban", Perms.Resolve("ctban"));
        Assert.Equal("@css/kick", Perms.Resolve("@css/kick"));
        Assert.Equal("@css/root", Perms.Resolve("unknown-capability"));
    }
}
