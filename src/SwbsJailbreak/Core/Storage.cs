using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SwbsJailbreak.Core;

/// <summary>
/// Persistent key/value storage replacing the SMPlugins MySQL libraries (DatabaseUsers, TimedPunishments,
/// ClientCookies, UserPoints...). Each namespace is one JSON file under
/// addons/counterstrikesharp/configs/plugins/SwbsJailbreak/data/&lt;namespace&gt;.json, kept in memory and
/// flushed (atomically, off the game thread) every 30 seconds, on map end and on unload.
/// Keys are usually SteamID64 strings.
/// </summary>
public static class Storage
{
    private static readonly Dictionary<string, JsonObject> Namespaces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Dirty = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object IoLock = new();
    private static string _dir = "";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string DataDirectory => _dir;

    internal static void Init(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
        Namespaces.Clear();
        Dirty.Clear();
    }

    private static JsonObject Ns(string ns)
    {
        if (Namespaces.TryGetValue(ns, out var obj)) return obj;
        obj = new JsonObject();
        var path = PathFor(ns);
        try
        {
            if (File.Exists(path))
            {
                var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                if (node is JsonObject o) obj = o;
            }
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogError(ex, "Failed to read data file {Path} (a backup will be kept)", path);
            try { File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true); }
            catch { /* ignore */ }
        }

        Namespaces[ns] = obj;
        return obj;
    }

    private static string PathFor(string ns)
    {
        var safe = string.Concat(ns.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
        return Path.Combine(_dir, safe + ".json");
    }

    public static T? Get<T>(string ns, string key)
    {
        var obj = Ns(ns);
        if (!obj.TryGetPropertyValue(key, out var node) || node == null) return default;
        try
        {
            return node.Deserialize<T>(JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    public static bool Has(string ns, string key) => Ns(ns).ContainsKey(key);

    public static void Set<T>(string ns, string key, T value)
    {
        var obj = Ns(ns);
        obj[key] = JsonSerializer.SerializeToNode(value, JsonOptions);
        Dirty.Add(ns);
    }

    public static void Delete(string ns, string key)
    {
        if (Ns(ns).Remove(key)) Dirty.Add(ns);
    }

    public static Dictionary<string, T> All<T>(string ns)
    {
        var result = new Dictionary<string, T>();
        foreach (var (k, node) in Ns(ns))
        {
            if (node == null) continue;
            try
            {
                var v = node.Deserialize<T>(JsonOptions);
                if (v != null) result[k] = v;
            }
            catch
            {
                // skip bad entries
            }
        }

        return result;
    }

    /// <summary>Write dirty namespaces. async=true serialises on this thread and writes on the thread pool.</summary>
    public static void Flush(bool async = true)
    {
        if (Dirty.Count == 0 || string.IsNullOrEmpty(_dir)) return;
        var jobs = Dirty.Select(ns => (Path: PathFor(ns), Json: Ns(ns).ToJsonString(JsonOptions))).ToList();
        Dirty.Clear();
        if (async) Task.Run(() => WriteAll(jobs));
        else WriteAll(jobs);
    }

    private static void WriteAll(List<(string Path, string Json)> jobs)
    {
        lock (IoLock)
        {
            foreach (var (path, json) in jobs)
            {
                try
                {
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, path, true);
                }
                catch (Exception ex)
                {
                    JailbreakPlugin.Instance?.Logger.LogError(ex, "Failed to write data file {Path}", path);
                }
            }
        }
    }

    // ---------------- per-map JSON documents (cells, zones, LR spots) ----------------

    public static string MapDirectory => Path.Combine(_dir, "..", "maps");

    public static T LoadMapDoc<T>(string kind, string map) where T : new()
    {
        var path = Path.Combine(MapDirectory, kind, map + ".json");
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? new T();
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogError(ex, "Failed to read map file {Path}", path);
        }

        return new T();
    }

    public static bool MapDocExists(string kind, string map) => File.Exists(Path.Combine(MapDirectory, kind, map + ".json"));

    public static void SaveMapDoc<T>(string kind, string map, T doc)
    {
        var dir = Path.Combine(MapDirectory, kind);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, map + ".json");
        var json = JsonSerializer.Serialize(doc, JsonOptions);
        Task.Run(() => WriteAll(new List<(string, string)> { (path, json) }));
    }
}
