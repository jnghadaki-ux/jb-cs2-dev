using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Modules.Extras;
using SwbsJailbreak.Modules.Zones;

namespace Swbs.Common;

/// <summary>
/// The jailbreak side of the shared Store / Zone Manager / BhopCheck (src/Swbs.Common): jailbreak points and the jail
/// log, store effects off during wardays and non-freeday LRs, prisoner / guard wording, the Notify and Cell Block zone
/// types, and spawn zones placed after the Settings module's spawn handling at round start.
/// </summary>
public static partial class CommonHost
{
    public static partial string ServerLabel => "SWBS Jailbreak CS2";
    public static partial string TeamNameT => "Prisoners";
    public static partial string TeamNameCT => "Guards";

    public static partial bool PointsAvailable => Jb.Points is not NullServices;
    public static partial int GetPoints(JbPlayer player) => Jb.Points.GetPoints(player);
    public static partial void AddPoints(JbPlayer player, int amount, string reason) => Jb.Points.AddPoints(player, amount, reason);

    public static partial void Log(string message, JbPlayer? actor, JbPlayer? target, CommonLogType type) =>
        Jb.Log.LogEvent(message, actor, target, type switch
        {
            CommonLogType.Item => LogType.Item,
            CommonLogType.Admin => LogType.Admin,
            _ => LogType.Any
        });

    /// <summary>Player effects and hats are off during wardays and in LRs other than freedays (they would give hiding players away).</summary>
    public static partial bool StoreEffectsAllowed(CCSPlayerController player)
    {
        if (Jb.Days.CurrentType == DayType.Warday) return false;
        var p = player.Jb();
        if (p == null) return false;
        var prisoner = player.Team == CsTeam.Terrorist ? p : Jb.LastRequests.GetOpponent(p);
        if (prisoner == null || !Jb.LastRequests.HasStartedLastRequest(prisoner)) return true;
        return (Jb.LastRequests.GetFlags(prisoner) & LrFlags.Freeday) != 0;
    }

    /// <summary>Body items only in warmup, on freedays and in freeday LRs (or always with BodyItemsAnytime).</summary>
    public static partial bool StoreBodyAllowed(CCSPlayerController player, bool anytime)
    {
        var p = player.Jb();
        var freedayLr = p != null && (Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) != 0;
        return BodyItemsAllowed(anytime, Round.IsWarmup, Jb.Days.CurrentType == DayType.Freeday, freedayLr)
               && (anytime || StoreEffectsAllowed(player) || freedayLr);
    }

    internal static bool BodyItemsAllowed(bool anytime, bool warmup, bool freedayDay, bool freedayLr) => anytime || warmup || freedayDay || freedayLr;

    public static partial bool PlayerModelsModuleActive => Jb.Models is not NullServices;

    public static partial bool PackModelsOn => WorkshopPackModule.PackModelsOn;
    public static partial bool HatModelsOn => WorkshopPackModule.HatModelsOn;
    public static partial IReadOnlyList<string> PaintballModels() => WorkshopPackModule.PaintballModels();

    /// <summary>16 checks a second on 64 tick: no timers here, and fast movers are still caught by the swept test.</summary>
    public static partial int ZoneCheckEveryTicksDefault => 4;

    /// <summary>Respawns later in the round (days, !respawn, !1up) keep their own position.</summary>
    public static partial bool SpawnZonesOnEverySpawnDefault => false;

    private static ZonesModule? _zones;

    public static partial void OnZonesLoaded(ZonesModule zones)
    {
        _zones = zones;
        Jb.Zones = zones;
        zones.RegisterType(new NotifyZone(zones));
        zones.RegisterType(new CellBlockZone());

        // The Settings module places players on spawn; spawn zones move them after that.
        zones.HostPlacesSpawns = true;
        JbEvents.SettingsSpawnPost += PlaceAtSpawnZone;
    }

    public static partial void OnZonesUnloaded(ZonesModule zones)
    {
        JbEvents.SettingsSpawnPost -= PlaceAtSpawnZone;
        if (ReferenceEquals(Jb.Zones, zones)) Jb.Zones = NullZoneService.Instance;
        if (ReferenceEquals(_zones, zones)) _zones = null;
    }

    private static void PlaceAtSpawnZone(JbPlayer player)
    {
        if (_zones is not { } z) return;
        if (z.Cfg.SpawnZonesOnEverySpawn || z.InRoundStartWindow) z.TryPlaceAtSpawnZone(player);
    }
}

/// <summary>The shared code calls a player's game-mode data <c>c.Swbs()</c>; here that is the jailbreak player.</summary>
public static class CommonPlayerExtensions
{
    public static JbPlayer? Swbs(this CCSPlayerController? c) => c.Jb();
}
