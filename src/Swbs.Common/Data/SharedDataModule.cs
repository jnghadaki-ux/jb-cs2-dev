using System.Text.Json.Nodes;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using Microsoft.Extensions.Logging;

namespace Swbs.Common;

/// <summary>
/// Starts shared points / store items from configs/swbs_shared.json (see <see cref="SharedDataConfig"/>), reads each
/// player's record in the background as soon as Steam authorises them, and drops it when they leave. Runs after the
/// other modules (high Order) so their disconnect saves still reach the shared record.
/// </summary>
public sealed class SharedDataModule : SwbsModule, SharedPlayerData.ISharedHost
{
    public override int Order => 10_000;

    /// <summary>addons/counterstrikesharp/configs/swbs_shared.json (the plugin's data folder is configs/plugins/&lt;Plugin&gt;/data).</summary>
    public static string ConfigPath => Path.GetFullPath(Path.Combine(Storage.DataDirectory, "..", "..", "..", SharedDataConfig.FileName));

    public override void OnLoad(bool hotReload)
    {
        try
        {
            var cfg = SharedDataConfig.LoadOrCreate(ConfigPath);
            if (cfg.CreateBackend() is { } backend) SharedPlayerData.Start(backend, this, cfg.MergeExistingData);
            else Logger.LogInformation("Store points and items are kept per server (Backend \"local\" in {Path})", ConfigPath);
        }
        catch (Exception ex)
        {
            // Never fall back to local silently: that would split players' points between the shared and local copies.
            Logger.LogError(ex, "Shared points / store items could not start ({Path}). Points and the store are read-only until this is fixed and the plugin reloaded", ConfigPath);
            SharedPlayerData.Start(new UnavailableBackend(ex.Message), this, false);
        }

        Plugin.RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
    }

    public override void OnUnload(bool hotReload) => SharedPlayerData.Stop();

    private static void OnClientAuthorized(int slot, SteamID id) => SharedPlayerData.Preload(id.SteamId64);

    public override void OnPlayerDisconnect(SwbsPlayer player)
    {
        if (player.SteamId64 != 0) SharedPlayerData.Forget(player.SteamId64);
    }

    public override void OnMapStart(string mapName) =>
        SharedPlayerData.ForgetAllExcept(Players.Humans().Select(c => c.SteamID));

    // ---- SharedPlayerData.ISharedHost ----

    public JsonNode? LocalGet(string ns, string key) => Storage.LocalGetNode(ns, key);
    public void LocalSet(string ns, string key, JsonNode? value) => Storage.LocalSetNode(ns, key, value);
    public void OnGameThread(Action action) => Server.NextFrame(action);
    public void Info(string message) => Logger.LogInformation("{Message}", message);
    public void Error(Exception? ex, string message) => Logger.LogError(ex, "{Message}", message);

    /// <summary>Stand-in when swbs_shared.json asks for sharing that cannot start: every read fails, nothing is written.</summary>
    private sealed class UnavailableBackend : ISharedBackend
    {
        private readonly string _why;
        public UnavailableBackend(string why) => _why = why;
        public string Describe => "unavailable (" + _why + ")";
        public void EnsureReady() { }
        public Dictionary<string, JsonNode?> Load(ulong steamId) => throw new InvalidOperationException(Describe);
        public void Replace(ulong steamId, string ns, JsonNode? value) => throw new InvalidOperationException(Describe);
        public void AddInt(ulong steamId, string ns, long delta) => throw new InvalidOperationException(Describe);
        public List<KeyValuePair<ulong, JsonNode?>> LoadAll(string ns) => new();
        public void Dispose() { }
    }
}
