using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Core;

/// <summary>
/// Where a player is aiming, from real engine data. CounterStrikeSharp 1.0.368 (the .NET 8 line this plugin targets) has
/// no managed ray-trace API, so aimed features use the player's last ping instead: the player_ping game event carries
/// the point the engine itself traced from the crosshair (x/y/z) and the pinged entity (entityid). Pinging is the stock
/// CS2 "player_ping" key (middle mouse by default). Nothing here ever invents a position.
/// </summary>
public static class Aim
{
    private readonly record struct Ping(Vector Pos, int Entity, double Time);

    private static readonly Dictionary<int, Ping> Pings = new();

    /// <summary>Raised for every valid ping: (player, point, pinged entity index or 0).</summary>
    public static event Action<CCSPlayerController, Vector, int>? Pinged;

    internal static HookResult OnPlayerPing(EventPlayerPing ev, GameEventInfo info)
    {
        var c = ev.Userid;
        if (c == null || !c.IsValid || c.IsBot) return HookResult.Continue;
        var pos = new Vector(ev.X, ev.Y, ev.Z);
        if (float.IsNaN(pos.X) || float.IsNaN(pos.Y) || float.IsNaN(pos.Z)) return HookResult.Continue;
        var entity = Math.Max(0, ev.Entityid);
        Pings[c.Slot] = new Ping(pos, entity, Server.EngineTime);
        try { Pinged?.Invoke(c, pos, entity); }
        catch (Exception ex) { JailbreakPlugin.Instance?.Logger.LogError(ex, "[Aim] ping handler failed"); }
        return HookResult.Continue;
    }

    /// <summary>The player's last ping if it is at most <paramref name="maxAge"/> seconds old.</summary>
    public static bool TryGetPing(CCSPlayerController c, double maxAge, out Vector pos, out int entity) =>
        TryGetPing(c, maxAge, out pos, out entity, out _);

    /// <summary>As above, also returning the engine time of the ping.</summary>
    public static bool TryGetPing(CCSPlayerController c, double maxAge, out Vector pos, out int entity, out double time)
    {
        pos = new Vector(0, 0, 0);
        entity = 0;
        time = 0;
        if (!Pings.TryGetValue(c.Slot, out var p) || Server.EngineTime - p.Time > maxAge) return false;
        pos = new Vector(p.Pos.X, p.Pos.Y, p.Pos.Z);
        entity = p.Entity;
        time = p.Time;
        return true;
    }

    /// <summary>The pinged entity as T when it still exists.</summary>
    public static T? Entity<T>(int index) where T : CEntityInstance
    {
        if (index <= 0) return null;
        var e = Utilities.GetEntityFromIndex<T>(index);
        return e != null && e.IsValid ? e : null;
    }

    public static void Forget(int slot) => Pings.Remove(slot);

    public static void Reset() => Pings.Clear();

    internal static void Unload()
    {
        Pings.Clear();
        Pinged = null;
    }
}
