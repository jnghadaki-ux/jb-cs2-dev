using System.Text.Json.Nodes;
using Swbs.Common;
using Xunit;

namespace SwbsJailbreak.Tests;

[CollectionDefinition("SharedData", DisableParallelization = true)]
public class SharedDataCollection { }

/// <summary>Shared points / store items (Swbs.Common.SharedPlayerData) on a real folder backend, two "servers" at once.</summary>
[Collection("SharedData")]
public sealed class SharedDataTests : IDisposable
{
    private const ulong Player = 76561198000000001;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swbs-shared-" + Guid.NewGuid().ToString("N"));

    private sealed class Host : SharedPlayerData.ISharedHost
    {
        public readonly Dictionary<(string, string), JsonNode?> Local = new();
        public readonly List<string> Errors = new();
        public JsonNode? LocalGet(string ns, string key) => Local.GetValueOrDefault((ns, key))?.DeepClone();
        public void LocalSet(string ns, string key, JsonNode? value) => Local[(ns, key)] = value?.DeepClone();
        public void OnGameThread(Action action) => action();
        public void Info(string message) { }
        public void Error(Exception? ex, string message) => Errors.Add(message);
    }

    private sealed class BrokenBackend : ISharedBackend
    {
        public int Writes;
        public string Describe => "broken";
        public void EnsureReady() { }
        public Dictionary<string, JsonNode?> Load(ulong steamId) => throw new IOException("database down");
        public void Replace(ulong steamId, string ns, JsonNode? value) => Writes++;
        public void AddInt(ulong steamId, string ns, long delta) => Writes++;
        public List<KeyValuePair<ulong, JsonNode?>> LoadAll(string ns) => new();
        public void Dispose() { }
    }

    public void Dispose()
    {
        SharedPlayerData.Stop();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static long Points(ISharedBackend b) => b.Load(Player).GetValueOrDefault("points")?.GetValue<long>() ?? 0;

    [Fact]
    public void Points_AreAdded_NeverOverwritten_WhenAnotherServerChangesThemToo()
    {
        SharedPlayerData.Start(new FolderBackend(_dir), new Host(), mergeExisting: false);
        var otherServer = new FolderBackend(_dir);

        Assert.Null(SharedPlayerData.Get("points", Player)); // this server reads the player: no points yet
        otherServer.AddInt(Player, "points", 50);            // meanwhile the player earns 50 elsewhere
        SharedPlayerData.Set("points", Player, JsonValue.Create(10)); // this server gives 10 (0 -> 10)
        SharedPlayerData.Stop();                              // flushes the writer

        Assert.Equal(60, Points(otherServer));
    }

    [Fact]
    public void Points_NeverGoBelowZero()
    {
        var b = new FolderBackend(_dir);
        b.EnsureReady();
        b.AddInt(Player, "points", 5);
        b.AddInt(Player, "points", -20);
        Assert.Equal(0, Points(b));
    }

    [Fact]
    public void StoreItems_BoughtOnOneServer_ShowOnAnother()
    {
        SharedPlayerData.Start(new FolderBackend(_dir), new Host(), mergeExisting: false);
        var inv = new JsonObject { ["Owned"] = new JsonArray("trail_rainbow"), ["Equipped"] = new JsonObject { ["Trail"] = "trail_rainbow" } };
        SharedPlayerData.Set("store", Player, inv);
        SharedPlayerData.Stop();

        SharedPlayerData.Start(new FolderBackend(_dir), new Host(), mergeExisting: false); // "another server"
        var seen = SharedPlayerData.Get("store", Player)!;
        Assert.Equal("trail_rainbow", seen["Owned"]![0]!.GetValue<string>());
        Assert.Equal("trail_rainbow", seen["Equipped"]!["Trail"]!.GetValue<string>());
    }

    [Fact]
    public void ExistingServerData_IsMergedOnce()
    {
        var shared = new FolderBackend(_dir);
        shared.EnsureReady();
        shared.AddInt(Player, "points", 200);
        shared.Replace(Player, "store", new JsonObject { ["Owned"] = new JsonArray("hat_crown"), ["Title"] = "" });

        var host = new Host();
        var key = Player.ToString();
        host.Local[("points", key)] = JsonValue.Create(300);
        host.Local[("store", key)] = new JsonObject { ["Owned"] = new JsonArray("trail_rainbow", "hat_crown"), ["Title"] = "pro" };

        SharedPlayerData.Start(new FolderBackend(_dir), host, mergeExisting: true);
        Assert.Equal(500, SharedPlayerData.Get("points", Player)!.GetValue<long>());
        SharedPlayerData.Stop();

        var store = shared.Load(Player)["store"]!;
        Assert.Equal(new[] { "hat_crown", "trail_rainbow" }, store["Owned"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("pro", store["Title"]!.GetValue<string>());
        Assert.NotNull(host.Local[(SharedPlayerData.MergedNs, key)]);

        // Next start on the same server: the marker stops a second merge.
        SharedPlayerData.Start(new FolderBackend(_dir), host, mergeExisting: true);
        Assert.Equal(500, SharedPlayerData.Get("points", Player)!.GetValue<long>());
        SharedPlayerData.Stop();
        Assert.Equal(500, Points(shared));
    }

    [Fact]
    public void UnreachableBackend_IsReadOnly_SoARealBalanceIsNeverOverwritten()
    {
        var broken = new BrokenBackend();
        var host = new Host();
        SharedPlayerData.Start(broken, host, mergeExisting: true);

        Assert.Null(SharedPlayerData.Get("points", Player));
        SharedPlayerData.Set("points", Player, JsonValue.Create(10));
        SharedPlayerData.Set("store", Player, new JsonObject());
        SharedPlayerData.Stop();

        Assert.Equal(0, broken.Writes);
        Assert.NotEmpty(host.Errors);
    }

    private sealed class FlakyBackend : ISharedBackend
    {
        private readonly ISharedBackend _real;
        public volatile bool Down = true;
        public FlakyBackend(ISharedBackend real) => _real = real;
        public string Describe => "flaky";
        public void EnsureReady() => _real.EnsureReady();
        public Dictionary<string, JsonNode?> Load(ulong steamId) => Down ? throw new IOException("down") : _real.Load(steamId);
        public void Replace(ulong steamId, string ns, JsonNode? value) => _real.Replace(steamId, ns, value);
        public void AddInt(ulong steamId, string ns, long delta) => _real.AddInt(steamId, ns, delta);
        public List<KeyValuePair<ulong, JsonNode?>> LoadAll(string ns) => _real.LoadAll(ns);
        public void Dispose() { }
    }

    [Fact]
    public void AfterAnOutage_TheRecordIsReadInTheBackground_AndReloadedFires()
    {
        var real = new FolderBackend(_dir);
        real.EnsureReady();
        real.AddInt(Player, "points", 70);
        var flaky = new FlakyBackend(real);
        SharedPlayerData.Start(flaky, new Host(), mergeExisting: false);
        var reloaded = new System.Collections.Concurrent.ConcurrentDictionary<ulong, bool>();
        Action<ulong> onReload = id => reloaded[id] = true; // the second player's background retry may also finish
        SharedPlayerData.Reloaded += onReload;
        try
        {
            Assert.Null(SharedPlayerData.Get("points", Player)); // down: read-only, shows nothing
            var other = Player + 5;
            Assert.Null(SharedPlayerData.Get("points", other)); // still down: no second blocking read

            flaky.Down = false;
            SharedPlayerData.Preload(Player); // what a retry does, off the game thread
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!reloaded.ContainsKey(Player) && DateTime.UtcNow < until) Thread.Sleep(10);

            Assert.True(reloaded.ContainsKey(Player));
            Assert.Equal(70, SharedPlayerData.Get("points", Player)!.GetValue<long>());
        }
        finally
        {
            SharedPlayerData.Reloaded -= onReload;
        }
    }

    [Fact]
    public void OnlySharedNamespaces_AndRealSteamIds_AreRouted()
    {
        Assert.False(SharedPlayerData.Routes("points", Player.ToString(), out _)); // sharing off
        SharedPlayerData.Start(new FolderBackend(_dir), new Host(), mergeExisting: false);
        Assert.True(SharedPlayerData.Routes("points", Player.ToString(), out var id));
        Assert.Equal(Player, id);
        Assert.True(SharedPlayerData.Routes("store", Player.ToString(), out _));
        Assert.False(SharedPlayerData.Routes("points", "bot3", out _));
        Assert.False(SharedPlayerData.Routes("playtime", Player.ToString(), out _));
        Assert.False(SharedPlayerData.Routes("bans", Player.ToString(), out _));
    }

    [Fact]
    public void Storage_SendsPointsAndStoreToTheSharedBackend_AndKeepsTheRestLocal()
    {
        var local = Path.Combine(_dir, "local-data");
        SwbsJailbreak.Core.Storage.Init(local);
        SharedPlayerData.Start(new FolderBackend(_dir), new Host(), mergeExisting: false);
        var key = Player.ToString();

        SwbsJailbreak.Core.Storage.Set("points", key, 42);
        SwbsJailbreak.Core.Storage.Set("bans", key, "local only");
        Assert.Equal(42, SwbsJailbreak.Core.Storage.Get<int>("points", key));
        Assert.True(SwbsJailbreak.Core.Storage.Has("points", key));
        Assert.Equal(42, SwbsJailbreak.Core.Storage.All<int>("points")[key]);
        SwbsJailbreak.Core.Storage.Flush(async: false);
        SharedPlayerData.Stop();

        Assert.Equal(42, Points(new FolderBackend(_dir)));
        Assert.False(File.Exists(Path.Combine(local, "points.json")));
        Assert.Contains("local only", File.ReadAllText(Path.Combine(local, "bans.json")));
    }

    [Fact]
    public void Leaderboard_ListsEveryPlayer()
    {
        var b = new FolderBackend(_dir);
        b.EnsureReady();
        b.AddInt(Player, "points", 7);
        b.AddInt(Player + 1, "points", 9);
        SharedPlayerData.Start(new FolderBackend(_dir), new Host(), mergeExisting: false);
        var all = SharedPlayerData.All("points").ToDictionary(kv => kv.Key, kv => kv.Value.GetValue<long>());
        Assert.Equal(7, all[Player.ToString()]);
        Assert.Equal(9, all[(Player + 1).ToString()]);
    }

    /// <summary>
    /// Real MySQL / MariaDB round trip. Runs only with SWBS_TEST_MYSQL="host;port;database;user;password" set (it creates
    /// and drops its own table), so the normal build needs no database.
    /// </summary>
    [Fact]
    public void MySql_RoundTrip_WhenATestDatabaseIsConfigured()
    {
        var env = Environment.GetEnvironmentVariable("SWBS_TEST_MYSQL");
        if (string.IsNullOrEmpty(env)) return;
        var p = env.Split(';');
        var s = new SharedDataConfig.MySqlSettings
        {
            Host = p[0], Port = int.Parse(p[1]), Database = p[2], User = p[3], Password = p[4],
            Table = "swbs_test_" + Guid.NewGuid().ToString("N")[..8]
        };
        var other = new MySqlBackend(s);
        try
        {
            SharedPlayerData.Start(new MySqlBackend(s), new Host(), mergeExisting: false);
            Assert.Null(SharedPlayerData.Get("points", Player));
            other.AddInt(Player, "points", 50);
            SharedPlayerData.Set("points", Player, JsonValue.Create(10));
            SharedPlayerData.Set("store", Player, new JsonObject { ["Owned"] = new JsonArray("trail_rainbow") });
            SharedPlayerData.Stop();

            var row = other.Load(Player);
            Assert.Equal(60, row["points"]!.GetValue<long>());
            Assert.Equal("trail_rainbow", row["store"]!["Owned"]![0]!.GetValue<string>());
            other.AddInt(Player, "points", -100);
            Assert.Equal(0, other.Load(Player)["points"]!.GetValue<long>());
            Assert.Single(other.LoadAll("store"));
            other.Replace(Player, "store", null);
            Assert.False(other.Load(Player).ContainsKey("store"));
        }
        finally
        {
            using var c = new MySqlConnector.MySqlConnection($"Server={s.Host};Port={s.Port};Database={s.Database};User ID={s.User};Password={s.Password}");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"DROP TABLE IF EXISTS `{s.Table}`";
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public void ConfigTemplate_DefaultsToLocal_AndPicksTheBackend()
    {
        var cfg = SharedDataConfig.Parse(SharedDataConfig.Template);
        Assert.Equal("local", cfg.Backend);
        Assert.Null(cfg.CreateBackend());
        Assert.True(cfg.MergeExistingData);
        Assert.IsType<FolderBackend>(SharedDataConfig.Parse("{\"Backend\":\"folder\",\"Folder\":\"/tmp/x\"}").CreateBackend());
        Assert.IsType<MySqlBackend>(SharedDataConfig.Parse("{\"Backend\":\"MySQL\"}").CreateBackend());
        Assert.Throws<InvalidOperationException>(() => SharedDataConfig.Parse("{\"Backend\":\"folder\"}").CreateBackend());
        Assert.Throws<InvalidOperationException>(() => SharedDataConfig.Parse("{\"Backend\":\"redis\"}").CreateBackend());
    }
}
