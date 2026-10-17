using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Swbs.Common;

/// <summary>
/// Store points and store items shared by every SWBS server (see <see cref="SharedDataConfig"/>). Storage hands the
/// shared namespaces to this class instead of its own per-plugin JSON files, so the game modes' points modules and the
/// store keep calling Storage.Get / Set as before.
///
/// Players' records are read once (on connect, or on first use) into a cache on the game thread; every change updates
/// the cache and is queued for a background writer, in order. Points are written as additions, never overwrites.
/// When the backend cannot be reached, that player's shared data is read-only (shows 0 / nothing owned, changes are
/// not saved) until a later read succeeds, so a database outage can never overwrite a real balance with 0.
/// </summary>
public static class SharedPlayerData
{
    public const string PointsNs = "points";
    public const string StoreNs = "store";

    /// <summary>Local namespace remembering which players this server's old data was already merged for.</summary>
    public const string MergedNs = "shared_merged";

    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase) { PointsNs, StoreNs };
    private static readonly HashSet<string> IntNamespaces = new(StringComparer.OrdinalIgnoreCase) { PointsNs };

    private static ISharedBackend? _backend;
    private static Writer? _writer;
    private static ISharedHost _host = NullHost.Instance;
    private static bool _mergeExisting;

    /// <summary>After a failed read, no more reads on the game thread until then (they would stall the server).</summary>
    private static DateTime _downUntil;

    private static readonly Dictionary<ulong, Entry> Cache = new();
    private static readonly Dictionary<ulong, Task<Dictionary<string, JsonNode?>>> Preloads = new();
    private static readonly Dictionary<string, (DateTime At, List<KeyValuePair<ulong, JsonNode?>> Rows)> AllSnapshots = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public readonly Dictionary<string, JsonNode?> Values = new(StringComparer.Ordinal);
        public bool Failed;
        public DateTime RetryAt;
    }

    /// <summary>What the shared data needs from the plugin it runs in (engine-free so it can be unit-tested).</summary>
    public interface ISharedHost
    {
        /// <summary>This server's own (pre-sharing) value from its local data files, or null.</summary>
        JsonNode? LocalGet(string ns, string key);

        void LocalSet(string ns, string key, JsonNode? value);

        /// <summary>Run on the game thread (Server.NextFrame on a server, immediately in tests).</summary>
        void OnGameThread(Action action);

        void Info(string message);
        void Error(Exception? ex, string message);
    }

    private sealed class NullHost : ISharedHost
    {
        public static readonly NullHost Instance = new();
        public JsonNode? LocalGet(string ns, string key) => null;
        public void LocalSet(string ns, string key, JsonNode? value) { }
        public void OnGameThread(Action action) => action();
        public void Info(string message) { }
        public void Error(Exception? ex, string message) { }
    }

    /// <summary>True when a shared backend is running (Backend is not "local").</summary>
    public static bool Active => _backend != null;

    public static string Describe => _backend?.Describe ?? "local";

    /// <summary>A player's shared data was (re)read after their first read failed: reload anything cached from it.</summary>
    public static event Action<ulong>? Reloaded;

    /// <summary>Storage asks: is this namespace / key shared? Only real players' SteamID64 keys are.</summary>
    public static bool Routes(string ns, string key, out ulong steamId)
    {
        steamId = 0;
        return _backend != null && Shared.Contains(ns) && ulong.TryParse(key, out steamId) && steamId != 0;
    }

    public static bool RoutesNamespace(string ns) => _backend != null && Shared.Contains(ns);

    public static void Start(ISharedBackend backend, ISharedHost host, bool mergeExisting)
    {
        Stop();
        backend.EnsureReady();
        _backend = backend;
        _host = host;
        _mergeExisting = mergeExisting;
        _writer = new Writer(backend, host);
        host.Info($"Shared points and store items: {backend.Describe}");
    }

    /// <summary>Write everything still queued (up to 10 s) and go back to local storage.</summary>
    public static void Stop()
    {
        _writer?.Dispose();
        _writer = null;
        _backend?.Dispose();
        _backend = null;
        _host = NullHost.Instance;
        _downUntil = default;
        Cache.Clear();
        Preloads.Clear();
        AllSnapshots.Clear();
    }

    /// <summary>Start reading a player's record in the background as soon as they authorise (game thread).</summary>
    public static void Preload(ulong steamId)
    {
        if (_backend is not { } backend || _writer is not { } writer || steamId == 0) return;
        if (Cache.TryGetValue(steamId, out var cached) && !cached.Failed || Preloads.ContainsKey(steamId)) return;
        var task = Task.Run(() =>
        {
            writer.WaitFor(steamId, TimeSpan.FromSeconds(5));
            return backend.Load(steamId);
        });
        Preloads[steamId] = task;
        task.ContinueWith(_ => _host.OnGameThread(() => FinishPreload(steamId, task)));
    }

    private static void FinishPreload(ulong steamId, Task<Dictionary<string, JsonNode?>> task)
    {
        if (!Preloads.TryGetValue(steamId, out var current) || current != task) return;
        Preloads.Remove(steamId);
        var cached = Cache.GetValueOrDefault(steamId);
        if (cached is { Failed: false }) return; // read synchronously meanwhile
        if (task.IsCompletedSuccessfully) Install(steamId, task.Result, wasFailed: cached != null);
        else Fail(steamId, task.Exception?.GetBaseException(), log: cached == null);
    }

    /// <summary>The player left: drop their cached record (queued writes still go out).</summary>
    public static void Forget(ulong steamId)
    {
        Cache.Remove(steamId);
        Preloads.Remove(steamId);
    }

    /// <summary>Drop cached records of players who are not online (offline admin changes, leaderboard lookups).</summary>
    public static void ForgetAllExcept(IEnumerable<ulong> online)
    {
        var keep = online.ToHashSet();
        foreach (var id in Cache.Keys.Where(id => !keep.Contains(id)).ToList()) Cache.Remove(id);
    }

    public static JsonNode? Get(string ns, ulong steamId) =>
        EntryFor(steamId) is { } e && e.Values.TryGetValue(ns, out var v) ? v?.DeepClone() : null;

    public static void Set(string ns, ulong steamId, JsonNode? value)
    {
        if (_writer is not { } writer) return;
        var e = EntryFor(steamId);
        if (e == null || e.Failed) return; // read-only until the record could be read

        if (IntNamespaces.Contains(ns))
        {
            var before = AsLong(e.Values.GetValueOrDefault(ns));
            var after = Math.Max(0, AsLong(value));
            e.Values[ns] = JsonValue.Create(after);
            if (after != before) writer.Enqueue(steamId, b => b.AddInt(steamId, ns, after - before));
            return;
        }

        if (value == null) e.Values.Remove(ns);
        else e.Values[ns] = value.DeepClone();
        var copy = value?.DeepClone();
        writer.Enqueue(steamId, b => b.Replace(steamId, ns, copy));
    }

    /// <summary>Every player's value (leaderboards): a snapshot at most a minute old, with online players' live values.</summary>
    public static IEnumerable<KeyValuePair<string, JsonNode>> All(string ns)
    {
        if (_backend is not { } backend) yield break;
        if (!AllSnapshots.TryGetValue(ns, out var snap) || DateTime.UtcNow - snap.At > TimeSpan.FromMinutes(1))
        {
            try
            {
                _writer?.WaitIdle(TimeSpan.FromSeconds(2));
                snap = (DateTime.UtcNow, backend.LoadAll(ns));
                AllSnapshots[ns] = snap;
            }
            catch (Exception ex)
            {
                _host.Error(ex, $"Could not read shared {ns} for a leaderboard");
                snap = (DateTime.UtcNow, snap.Rows ?? new List<KeyValuePair<ulong, JsonNode?>>());
            }
        }

        var merged = new Dictionary<ulong, JsonNode?>();
        foreach (var (id, node) in snap.Rows) merged[id] = node;
        foreach (var (id, e) in Cache)
            if (!e.Failed) merged[id] = e.Values.GetValueOrDefault(ns);
        foreach (var (id, node) in merged)
            if (node != null) yield return new(id.ToString(), node.DeepClone());
    }

    // ------------------------------------------------------------------------------------------------ internals

    private static Entry? EntryFor(ulong steamId)
    {
        if (_backend is not { } backend || _writer is not { } writer) return null;
        if (Cache.TryGetValue(steamId, out var e))
        {
            // Failed earlier: retry in the background, never on the game thread (Reloaded fires when it works).
            if (e.Failed && DateTime.UtcNow >= e.RetryAt)
            {
                e.RetryAt = DateTime.UtcNow.AddSeconds(30);
                Preload(steamId);
            }

            return e;
        }

        // The backend failed a moment ago: do not stall the game thread on it again, read in the background.
        if (DateTime.UtcNow < _downUntil && !Preloads.ContainsKey(steamId))
        {
            var down = Fail(steamId, null, log: false);
            Preload(steamId);
            return down;
        }

        // Not preloaded yet: read now, on this thread (normally the preload from OnClientAuthorized is done already).
        try
        {
            if (Preloads.Remove(steamId, out var pending))
            {
                if (!pending.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("shared data read took over 5 s");
                return Install(steamId, pending.Result);
            }

            writer.WaitFor(steamId, TimeSpan.FromSeconds(5));
            return Install(steamId, backend.Load(steamId));
        }
        catch (Exception ex)
        {
            return Fail(steamId, ex is AggregateException a ? a.GetBaseException() : ex);
        }
    }

    private static Entry Install(ulong steamId, Dictionary<string, JsonNode?> loaded, bool wasFailed = false)
    {
        var e = new Entry();
        foreach (var (k, v) in loaded) e.Values[k] = v;
        Cache[steamId] = e;
        if (_mergeExisting) MergeLocal(steamId, e);
        if (wasFailed) Reloaded?.Invoke(steamId);
        return e;
    }

    private static Entry Fail(ulong steamId, Exception? ex, bool log = true)
    {
        _downUntil = DateTime.UtcNow.AddSeconds(30);
        if (log) _host.Error(ex, $"Could not read shared points / store items for {steamId}; they are read-only until a retry works");
        var e = new Entry { Failed = true, RetryAt = DateTime.UtcNow.AddSeconds(30) };
        Cache[steamId] = e;
        return e;
    }

    /// <summary>
    /// First time this server sees the player with sharing on: add its own old points and store items to the shared
    /// record (points are added, items are combined), then remember that in the local MergedNs.
    /// </summary>
    private static void MergeLocal(ulong steamId, Entry e)
    {
        var key = steamId.ToString();
        if (_host.LocalGet(MergedNs, key) != null) return;

        if (AsLong(_host.LocalGet(PointsNs, key)) is var oldPoints and > 0)
            Set(PointsNs, steamId, JsonValue.Create(AsLong(e.Values.GetValueOrDefault(PointsNs)) + oldPoints));

        if (_host.LocalGet(StoreNs, key) is { } oldStore)
        {
            var merged = MergeStore(e.Values.GetValueOrDefault(StoreNs), oldStore);
            if (!JsonNode.DeepEquals(merged, e.Values.GetValueOrDefault(StoreNs))) Set(StoreNs, steamId, merged);
        }

        _host.LocalSet(MergedNs, key, JsonValue.Create(DateTime.UtcNow.ToString("O")));
    }

    /// <summary>
    /// Engine-free (unit-tested): shared inventory plus a server's old one. Owned items are combined; the shared
    /// record's equipped items, title and settings win, and the old ones fill what it does not have.
    /// </summary>
    public static JsonNode MergeStore(JsonNode? shared, JsonNode old)
    {
        var o = Deserialize(old);
        if (shared == null) return JsonSerializer.SerializeToNode(o, Storage.JsonOptions)!;
        var s = Deserialize(shared);
        foreach (var id in o.Owned)
            if (!s.Owned.Contains(id, StringComparer.OrdinalIgnoreCase)) s.Owned.Add(id);
        foreach (var (kind, id) in o.Equipped)
            s.Equipped.TryAdd(kind, id);
        if (s.Title.Length == 0) s.Title = o.Title;
        return JsonSerializer.SerializeToNode(s, Storage.JsonOptions)!;
    }

    private static StoreInventory Deserialize(JsonNode node)
    {
        try { return node.Deserialize<StoreInventory>(Storage.JsonOptions) ?? new StoreInventory(); }
        catch (JsonException) { return new StoreInventory(); }
    }

    private static long AsLong(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return v.TryGetValue<double>(out var d) ? (long)d : 0;
    }

    /// <summary>One background thread writing queued changes in order (retrying each a few times on errors).</summary>
    private sealed class Writer : IDisposable
    {
        private readonly BlockingCollection<(ulong Id, Action<ISharedBackend> Op)> _queue = new();
        private readonly ConcurrentDictionary<ulong, int> _pending = new();
        private readonly ISharedBackend _backend;
        private readonly ISharedHost _host;
        private readonly Thread _thread;
        private int _busy;

        public Writer(ISharedBackend backend, ISharedHost host)
        {
            _backend = backend;
            _host = host;
            _thread = new Thread(Run) { IsBackground = true, Name = "SWBS shared data writer" };
            _thread.Start();
        }

        public void Enqueue(ulong id, Action<ISharedBackend> op)
        {
            _pending.AddOrUpdate(id, 1, (_, n) => n + 1);
            _queue.Add((id, op));
        }

        /// <summary>Wait until this player's queued changes are written (so a fresh read sees them).</summary>
        public void WaitFor(ulong id, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (_pending.TryGetValue(id, out var n) && n > 0 && DateTime.UtcNow < until) Thread.Sleep(5);
        }

        public void WaitIdle(TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while ((_queue.Count > 0 || Volatile.Read(ref _busy) != 0) && DateTime.UtcNow < until) Thread.Sleep(5);
        }

        private void Run()
        {
            foreach (var (id, op) in _queue.GetConsumingEnumerable())
            {
                Volatile.Write(ref _busy, 1);
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        op(_backend);
                        break;
                    }
                    catch (Exception ex) when (attempt < 5)
                    {
                        _host.Error(ex, $"Saving shared data for {id} failed (try {attempt} of 5)");
                        Thread.Sleep(1000 * attempt);
                    }
                    catch (Exception ex)
                    {
                        _host.Error(ex, $"Saving shared data for {id} failed; this change is lost");
                        break;
                    }
                }

                _pending.AddOrUpdate(id, 0, (_, n) => Math.Max(0, n - 1));
                Volatile.Write(ref _busy, 0);
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            if (!_thread.Join(TimeSpan.FromSeconds(10)))
                _host.Error(null, $"Shared data writer still busy after 10 s; {_queue.Count} change(s) not written");
        }
    }
}
