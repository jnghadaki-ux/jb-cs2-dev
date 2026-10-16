using System.Text.RegularExpressions;
using SwbsJailbreak.Modules.PlayerExtras;
using Xunit;

namespace SwbsJailbreak.Tests;

public class StoreEquipmentTests
{
    private static string Script()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tools", "store-pack-2", "equipment.py"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "tools", "store-pack-2", "equipment.py"));
    }

    private static HashSet<string> Built(string script, string table)
    {
        var m = Regex.Match(script, $@"^{table} = \{{(.*?)^\}}", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(m.Success, table);
        return Regex.Matches(m.Groups[1].Value, @"'([a-z_0-9]+)':").Select(x => x.Groups[1].Value).ToHashSet();
    }

    /// <summary>Every face and back store item is a model store pack 2's generator builds, and every built model is sold.</summary>
    [Fact]
    public void Defaults_MatchTheGenerator()
    {
        var script = Script();
        var items = StoreConfig.DefaultItems();
        foreach (var (type, table, slot) in new[] { (StoreItemType.Face, "FACE", "face"), (StoreItemType.Back, "BACK", "back") })
        {
            var sold = items.Where(i => i.Type == type).ToList();
            Assert.All(sold, i => Assert.Matches($@"^{Regex.Escape(StoreConfig.PackGearFolder)}{slot}/[a-z_0-9]+\.vmdl$", i.Data));
            Assert.All(sold, i => Assert.StartsWith(slot + "_", i.Id));
            var names = sold.Select(i => Path.GetFileNameWithoutExtension(i.Data)).OrderBy(x => x);
            Assert.Equal(Built(script, table).OrderBy(x => x), names);
        }

        Assert.Contains("MODEL_ROOT = 'models/swbs/store2'", script);
    }

    [Fact]
    public void PackTwoItems_HiddenUntilTheStorePackIsOn()
    {
        var items = StoreConfig.DefaultItems().Where(i => i.Type is StoreItemType.Face or StoreItemType.Back).ToList();
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.False(StoreModule.Available(i)));
        // An admin's own face / back model outside the pack folder is not hidden.
        Assert.True(StoreModule.Available(new StoreItem { Type = StoreItemType.Back, Data = "models/my/cape.vmdl" }));
    }

    [Fact]
    public void Attachments_AreOnTheAgentSkeleton()
    {
        var cfg = new StoreConfig();
        Assert.Equal("clip_limit", cfg.FaceAttachment);
        Assert.Equal("c4", cfg.BackAttachment);
        Assert.True(cfg.FaceForward > 0 && cfg.BackDepth > 0 && cfg.BackBelowEye > 0);
    }
}
