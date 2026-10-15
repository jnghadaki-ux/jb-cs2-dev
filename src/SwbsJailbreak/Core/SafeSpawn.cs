using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace SwbsJailbreak.Core;

/// <summary>
/// Keeps players out of walls and from under the map. CCSPlayerController.Respawn() can leave the pawn where it died
/// (a fall into the void, a crouch spot) or, for a player who never spawned this map, at the map origin. Every plugin
/// respawn and every prepared spawn is checked: a player-sized box must fit and there must be a floor below; otherwise
/// the player is moved to the spot the caller prefers (their cell spawn) or a free spawn point of their team.
/// </summary>
public static class SafeSpawn
{
    private static readonly Vector HullMins = new(-16f, -16f, 0f);
    private static readonly Vector HullMaxs = new(16f, 16f, 72f);
    private static readonly TraceOptions BrushOnly = new() { InteractsWith = Masks.PlayerSolidBrushOnly };

    /// <summary>
    /// True when a standing player fits at <paramref name="feet"/> (no world brush inside the hull) and there is world
    /// floor somewhere below (not under the map). Players and props are ignored on purpose (spawns are crowded, players
    /// stand on crates). On a trace failure the spot is trusted.
    /// </summary>
    public static bool Fits(Vector feet, CBaseEntity ignore)
    {
        try
        {
            var from = new Vector(feet.X, feet.Y, feet.Z + 2f);
            var to = new Vector(feet.X, feet.Y, feet.Z + 3f);
            var hull = Trace.TraceHullShape(from, to, HullMins, HullMaxs, ignore, BrushOnly);
            if (hull.IsAllSolid || hull.Fraction < 1f) return false;
            var down = Trace.TraceEndShape(new Vector(feet.X, feet.Y, feet.Z + 8f), new Vector(feet.X, feet.Y, feet.Z - 1024f), ignore, BrushOnly);
            return down.DidHit() && down.Fraction < 1f;
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogDebug(ex, "[SafeSpawn] trace failed");
            return true;
        }
    }

    /// <summary>The map's spawn points for a team (info_player_terrorist / info_player_counterterrorist).</summary>
    public static List<(Vector Pos, QAngle Ang)> TeamSpawns(CsTeam team)
    {
        var designer = team == CsTeam.CounterTerrorist ? "info_player_counterterrorist" : "info_player_terrorist";
        var list = new List<(Vector, QAngle)>();
        foreach (var e in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(designer))
        {
            var o = e.AbsOrigin;
            if (o == null) continue;
            var r = e.AbsRotation;
            list.Add((new Vector(o.X, o.Y, o.Z), new QAngle(0, r?.Y ?? 0, 0)));
        }

        return list;
    }

    /// <summary>
    /// Check where the player stands (or <paramref name="at"/>, a spot the caller just teleported them to) and move them
    /// to <paramref name="fallback"/> or a free team spawn point when it is unsafe. Returns true when nothing had to move.
    /// </summary>
    public static bool Ensure(CCSPlayerController c, Vector? at = null, (Vector Pos, QAngle Ang)? fallback = null)
    {
        var pawn = c.PawnOrNull();
        if (pawn == null || !c.IsAliveP() || c.IsNoclip()) return true;
        var pos = at ?? (pawn.AbsOrigin is { } o ? new Vector(o.X, o.Y, o.Z) : null);
        if (pos == null || Fits(pos, pawn)) return true;

        var candidates = new List<(Vector Pos, QAngle Ang)>();
        if (fallback is { } f) candidates.Add(f);
        var spawns = TeamSpawns(c.Team);
        if (spawns.Count == 0) spawns = TeamSpawns(c.Team == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist);
        candidates.AddRange(spawns.OrderBy(_ => Random.Shared.Next()));
        foreach (var (p, a) in candidates)
        {
            if (!Fits(p, pawn)) continue;
            c.TeleportTo(new Vector(p.X, p.Y, p.Z + 1f), a, new Vector(0, 0, 0));
            JailbreakPlugin.Instance?.Logger.LogInformation("[SafeSpawn] {Name} was in a wall / under the map at {Pos}; moved to {Spot}",
                c.PlayerName, $"{pos.X:0} {pos.Y:0} {pos.Z:0}", $"{p.X:0} {p.Y:0} {p.Z:0}");
            return false;
        }

        return true; // nowhere better found: leave them
    }
}
