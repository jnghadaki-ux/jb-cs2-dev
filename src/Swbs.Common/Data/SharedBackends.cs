using System.Text.Json;
using System.Text.Json.Nodes;
using MySqlConnector;

namespace Swbs.Common;

/// <summary>
/// Where shared player data lives. Values are JSON nodes per (player, namespace); "int" namespaces (points) are changed
/// with <see cref="AddInt"/> so two servers adding at once never overwrite each other. Called off the game thread.
/// </summary>
public interface ISharedBackend : IDisposable
{
    string Describe { get; }

    /// <summary>Create the folder / table. Throws when the backend cannot be reached.</summary>
    void EnsureReady();

    /// <summary>All shared namespaces stored for this player (missing namespaces are simply absent).</summary>
    Dictionary<string, JsonNode?> Load(ulong steamId);

    /// <summary>Replace one namespace's value (null deletes it).</summary>
    void Replace(ulong steamId, string ns, JsonNode? value);

    /// <summary>Add to an integer namespace atomically; the result never goes below 0.</summary>
    void AddInt(ulong steamId, string ns, long delta);

    /// <summary>Every player's value in one namespace (leaderboards).</summary>
    List<KeyValuePair<ulong, JsonNode?>> LoadAll(string ns);
}

/// <summary>
/// Same-machine sharing: one JSON file per player in a folder every server points at
/// (&lt;Folder&gt;/players/&lt;steamid64&gt;.json). Each change takes a per-player lock file so servers never interleave.
/// </summary>
public sealed class FolderBackend : ISharedBackend
{
    private readonly string _dir;

    public FolderBackend(string folder) => _dir = Path.Combine(Path.GetFullPath(folder), "players");

    public string Describe => "folder " + _dir;

    public void EnsureReady()
    {
        Directory.CreateDirectory(_dir);
        var probe = Path.Combine(_dir, ".write-test-" + Environment.ProcessId);
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
    }

    private string PathOf(ulong steamId) => Path.Combine(_dir, steamId + ".json");

    public Dictionary<string, JsonNode?> Load(ulong steamId)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (k, v) in Read(PathOf(steamId))) result[k] = v?.DeepClone();
        return result;
    }

    public void Replace(ulong steamId, string ns, JsonNode? value) => Update(steamId, doc =>
    {
        if (value == null) doc.Remove(ns);
        else doc[ns] = value.DeepClone();
    });

    public void AddInt(ulong steamId, string ns, long delta) => Update(steamId, doc =>
    {
        long now = 0;
        if (doc[ns] is JsonValue v && v.TryGetValue<long>(out var n)) now = n;
        doc[ns] = Math.Max(0, now + delta);
    });

    public List<KeyValuePair<ulong, JsonNode?>> LoadAll(string ns)
    {
        var list = new List<KeyValuePair<ulong, JsonNode?>>();
        if (!Directory.Exists(_dir)) return list;
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            if (!ulong.TryParse(Path.GetFileNameWithoutExtension(file), out var id)) continue;
            var doc = Read(file);
            if (doc[ns] is { } node) list.Add(new(id, node.DeepClone()));
        }

        return list;
    }

    private void Update(ulong steamId, Action<JsonObject> change)
    {
        var path = PathOf(steamId);
        using var _ = AcquireLock(path + ".lock");
        var doc = Read(path);
        change(doc);
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, true);
    }

    private static JsonObject Read(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
    }

    /// <summary>
    /// Exclusive lock file shared by every server process (retried for up to 5 seconds). The lock files stay on disk:
    /// deleting one on close would let a waiting server lock the old file while another creates a new one.
    /// </summary>
    private static FileStream AcquireLock(string lockPath)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (i < 250)
            {
                Thread.Sleep(20);
            }
        }
    }

    public void Dispose() { }
}

/// <summary>
/// Servers on different machines: one MySQL / MariaDB table, a row per (player, namespace). Points rows are added to
/// in SQL (GREATEST(0, value + delta)), so concurrent servers never lose each other's points.
/// </summary>
public sealed class MySqlBackend : ISharedBackend
{
    private readonly string _connectionString;
    private readonly string _table;

    public MySqlBackend(SharedDataConfig.MySqlSettings s)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(s.Table, "^[A-Za-z0-9_]{1,64}$"))
            throw new ArgumentException("MySql.Table may only contain letters, digits and _");
        _table = s.Table;
        _connectionString = new MySqlConnectionStringBuilder
        {
            Server = s.Host,
            Port = (uint)s.Port,
            Database = s.Database,
            UserID = s.User,
            Password = s.Password,
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 10,
            Pooling = true
        }.ConnectionString;
        Describe = $"mysql {s.User}@{s.Host}:{s.Port}/{s.Database}.{s.Table}";
    }

    public string Describe { get; }

    private MySqlConnection Open()
    {
        var c = new MySqlConnection(_connectionString);
        c.Open();
        return c;
    }

    public void EnsureReady()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $@"CREATE TABLE IF NOT EXISTS `{_table}` (
  steamid BIGINT UNSIGNED NOT NULL,
  ns VARCHAR(32) NOT NULL,
  data MEDIUMTEXT NOT NULL,
  updated TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (steamid, ns)
) DEFAULT CHARSET=utf8mb4";
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, JsonNode?> Load(ulong steamId)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT ns, data FROM `{_table}` WHERE steamid = @id";
        cmd.Parameters.AddWithValue("@id", steamId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[r.GetString(0)] = Parse(r.GetString(1));
        return result;
    }

    public void Replace(ulong steamId, string ns, JsonNode? value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        if (value == null)
        {
            cmd.CommandText = $"DELETE FROM `{_table}` WHERE steamid = @id AND ns = @ns";
        }
        else
        {
            cmd.CommandText = $"INSERT INTO `{_table}` (steamid, ns, data) VALUES (@id, @ns, @data) ON DUPLICATE KEY UPDATE data = VALUES(data)";
            cmd.Parameters.AddWithValue("@data", value.ToJsonString());
        }

        cmd.Parameters.AddWithValue("@id", steamId);
        cmd.Parameters.AddWithValue("@ns", ns);
        cmd.ExecuteNonQuery();
    }

    public void AddInt(ulong steamId, string ns, long delta)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO `{_table}` (steamid, ns, data) VALUES (@id, @ns, CAST(GREATEST(0, @d) AS CHAR)) " +
                          "ON DUPLICATE KEY UPDATE data = CAST(GREATEST(0, CAST(data AS SIGNED) + @d) AS CHAR)";
        cmd.Parameters.AddWithValue("@id", steamId);
        cmd.Parameters.AddWithValue("@ns", ns);
        cmd.Parameters.AddWithValue("@d", delta);
        cmd.ExecuteNonQuery();
    }

    public List<KeyValuePair<ulong, JsonNode?>> LoadAll(string ns)
    {
        var list = new List<KeyValuePair<ulong, JsonNode?>>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT steamid, data FROM `{_table}` WHERE ns = @ns";
        cmd.Parameters.AddWithValue("@ns", ns);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new(r.GetUInt64(0), Parse(r.GetString(1))));
        return list;
    }

    private static JsonNode? Parse(string s)
    {
        try { return JsonNode.Parse(s); }
        catch (JsonException) { return null; }
    }

    public void Dispose() => MySqlConnection.ClearAllPools();
}
