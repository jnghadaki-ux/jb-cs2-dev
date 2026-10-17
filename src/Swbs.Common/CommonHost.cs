using CounterStrikeSharp.API.Core;

namespace Swbs.Common;

/// <summary>Which log an event goes to (the game mode maps it to its own activity / admin log).</summary>
public enum CommonLogType { Any, Item, Admin }

/// <summary>
/// What the shared Store, Zone Manager and BhopCheck need from the game mode they run in. Everything under
/// src/Swbs.Common is byte-identical in every SWBS repo (bhop, minigames, jailbreak); each repo implements these
/// members once in its own CommonHost.&lt;Mode&gt;.cs. A missing member is a build error, not a silent default.
/// </summary>
public static partial class CommonHost
{
    // ---- server identity ----

    /// <summary>Shown in console reports, e.g. "SWBS CS2" or "SWBS Jailbreak CS2".</summary>
    public static partial string ServerLabel { get; }

    /// <summary>What the game mode calls each team in menus ("Terrorists" / "Prisoners", "Counter-Terrorists" / "Guards").</summary>
    public static partial string TeamNameT { get; }
    public static partial string TeamNameCT { get; }

    // ---- points (the store's currency) ----

    /// <summary>False when the game mode runs without a points module (the store then says so and stays closed).</summary>
    public static partial bool PointsAvailable { get; }

    public static partial int GetPoints(SwbsPlayer player);

    /// <summary>Adds (or with a negative amount takes) points. Reason is for the game mode's own logs.</summary>
    public static partial void AddPoints(SwbsPlayer player, int amount, string reason);

    // ---- logging ----

    public static partial void Log(string message, SwbsPlayer? actor, SwbsPlayer? target, CommonLogType type);

    // ---- store rules ----

    /// <summary>Player effects and hats may show for this player right now (off when they would give a hiding player away).</summary>
    public static partial bool StoreEffectsAllowed(CCSPlayerController player);

    /// <summary>Body items (see-through, size) may apply right now. anytime = StoreConfig.BodyItemsAnytime.</summary>
    public static partial bool StoreBodyAllowed(CCSPlayerController player, bool anytime);

    /// <summary>True when the game mode has a player-models module that raises <see cref="PlayerModelsApplied"/>.</summary>
    public static partial bool PlayerModelsModuleActive { get; }

    /// <summary>Raised by the game mode after its player-models module set a player's model (the store re-applies its own).</summary>
    public static event Action<SwbsPlayer>? PlayerModelsApplied;

    public static void RaisePlayerModelsApplied(SwbsPlayer player) => PlayerModelsApplied?.Invoke(player);

    // ---- workshop pack ----

    public static partial bool PackModelsOn { get; }
    public static partial bool HatModelsOn { get; }
    public static partial IReadOnlyList<string> PaintballModels();

    // ---- zones ----

    /// <summary>Default for ZonesConfig.CheckEveryTicks (1 for timer servers, more where exact touches do not matter).</summary>
    public static partial int ZoneCheckEveryTicksDefault { get; }

    /// <summary>Default for ZonesConfig.SpawnZonesOnEverySpawn.</summary>
    public static partial bool SpawnZonesOnEverySpawnDefault { get; }

    /// <summary>The zone module loaded: register game-mode zone types, publish the service, hook spawn placement.</summary>
    public static partial void OnZonesLoaded(ZonesModule zones);

    public static partial void OnZonesUnloaded(ZonesModule zones);
}
