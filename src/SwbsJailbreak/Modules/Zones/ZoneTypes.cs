using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Zones;

public sealed partial class ZonesModule
{
    /// <summary>
    /// The SMPlugins Plugins/ZoneTypes set, rebuilt on CSS. Not registered: Flashlight (EF_DIMLIGHT has no CS2 meaning)
    /// and Hide Toggle (needs the HidePlayers plugin, not ported). Push, Notify and Cell Block are additions.
    /// </summary>
    private void RegisterBuiltInTypes()
    {
        RegisterType(new NotSetZone());
        RegisterType(new KillZone(this));
        RegisterType(new PushZone());
        RegisterType(new BlockadeZone(this));
        RegisterType(new RespawnZone());
        RegisterType(new TeleportZone(this));
        RegisterType(new TeleportDestinationZone());
        RegisterType(new WeaponStripZone());
        RegisterType(new DamageBlockerZone(this));
        RegisterType(new NamedZone());
        RegisterType(new NotifyZone(this));
        RegisterType(new CellBlockZone());
    }
}

/// <summary>ZONE_TYPE_NOT_SET (CreateNotSetZoneType).</summary>
internal sealed class NotSetZone : ZoneType
{
    public override string Key => ZoneTypeKeys.NotSet;
    public override string DisplayName => "Not set";
    public override int LegacyId => 0;
    public override string Summary(Zone zone) => "";
}

/// <summary>ZoneType_Kill: kills every living player touching it, optionally only between an enable and a disable time.</summary>
internal sealed class KillZone : ZoneType
{
    private readonly ZonesModule _m;
    public KillZone(ZonesModule m) => _m = m;

    public override string Key => ZoneTypeKeys.Kill;
    public override string DisplayName => "Kill";
    public override int LegacyId => 2;
    public override string Description => "Kills players touching it. Timers count from round start.";

    public override IReadOnlyList<ZoneField> Fields { get; } = new[]
    {
        new ZoneField("Enabled timer", ZoneFieldKind.Seconds, 1, "starts killing after N s"),
        new ZoneField("Disabled timer", ZoneFieldKind.Seconds, 2, "stops killing after N s")
    };

    public override void OnTouch(Zone zone, CCSPlayerController player)
    {
        if (!player.IsAliveP()) return;
        if (!TimedToggle.IsOn(_m.RoundElapsed, zone.GetInt(1), zone.GetInt(2))) return;
        player.CommitSuicide(false, true);
    }

    public override string Summary(Zone zone) => Timers(zone.GetInt(1), zone.GetInt(2));

    internal static string Timers(int on, int off) =>
        on == 0 && off == 0 ? "always" : $"{(on != 0 ? $"on {on}s" : "")}{(on != 0 && off != 0 ? " / " : "")}{(off != 0 ? $"off {off}s" : "")}";
}

/// <summary>ADDITION (ZONE_TYPE_PUSH had no plugin in the repository): a jump pad that launches players along the zone's angles.</summary>
internal sealed class PushZone : ZoneType
{
    public override string Key => ZoneTypeKeys.Push;
    public override string DisplayName => "Push (jump pad)";
    public override int LegacyId => 3;
    public override string Description => "Launches players along the zone's angles (set with Edit angles; look up to launch upward).";

    public override IReadOnlyList<ZoneField> Fields { get; } = new[]
    {
        new ZoneField("Force", ZoneFieldKind.Number, 1, "units/s, 0 = 600") { Min = 0, Max = 5000 }
    };

    public override void OnEnter(Zone zone, CCSPlayerController player)
    {
        var pawn = player.PawnOrNull();
        if (pawn == null || pawn.MoveType == MoveType_t.MOVETYPE_NOCLIP) return;
        var force = zone.GetInt(1) > 0 ? zone.GetInt(1) : 600;
        var f = ZoneEdit.Forward(zone.Angles.X, zone.Angles.Y) * force;
        pawn.Teleport(null, null, new CssVector(f.X, f.Y, f.Z));
    }

    public override string Summary(Zone zone) => $"force {(zone.GetInt(1) > 0 ? zone.GetInt(1) : 600)}";
}

/// <summary>
/// ZoneType_Blockade: a wall that can turn solid / not solid on timers. CS2 note: managed code cannot spawn a solid
/// brush reliably, so a solid blockade pushes players back out every tick (swept hull test, no tunnelling through thin
/// walls) and, with Zones.ShowBlockades, is drawn as a red beam box so players see why they are blocked.
/// </summary>
internal sealed class BlockadeZone : ZoneType
{
    private readonly ZonesModule _m;
    private readonly Dictionary<int, Vector3> _lastFeet = new();
    private readonly Dictionary<int, Vector3> _lastFree = new();
    private readonly Dictionary<int, List<CBeam>> _beams = new();
    private readonly Dictionary<int, int> _beamVersion = new();

    public BlockadeZone(ZonesModule m) => _m = m;

    public override string Key => ZoneTypeKeys.Blockade;
    public override string DisplayName => "Blockade";
    public override int LegacyId => 7;
    public override bool EveryTick => true;
    public override string Description => "Blocks players while solid. Timers count from round start; no timers = always solid.";

    public override IReadOnlyList<ZoneField> Fields { get; } = new[]
    {
        new ZoneField("Solid timer", ZoneFieldKind.Seconds, 1, "turns solid after N s"),
        new ZoneField("Not-solid timer", ZoneFieldKind.Seconds, 2, "opens after N s")
    };

    public override void OnUnassigned(Zone zone) => RemoveBeams(zone.Id);

    public override void OnRoundStart()
    {
        _lastFeet.Clear();
        _lastFree.Clear();
    }

    public override void OnTick(IReadOnlyList<Zone> zones)
    {
        var elapsed = _m.RoundElapsed;
        var solid = new List<Zone>(zones.Count);
        foreach (var z in zones)
        {
            var on = TimedToggle.IsOn(elapsed, z.GetInt(1), z.GetInt(2));
            if (on) solid.Add(z);
            UpdateBeams(z, on && _m.Cfg.ShowBlockades);
        }

        foreach (var c in Players.Alive())
        {
            var pawn = c.PawnOrNull();
            var o = pawn?.AbsOrigin;
            if (pawn == null || o == null) continue;
            var feet = new Vector3(o.X, o.Y, o.Z);
            if (pawn.MoveType == MoveType_t.MOVETYPE_NOCLIP || solid.Count == 0)
            {
                _lastFeet[c.Slot] = feet;
                _lastFree[c.Slot] = feet;
                continue;
            }

            var from = _lastFeet.TryGetValue(c.Slot, out var lf) ? lf : feet;
            // A jump of more than 64 units in one tick is a teleport (LR, rebel points, days, respawn), not movement:
            // never sweep across the map, only check where the player landed.
            var teleported = System.Numerics.Vector3.DistanceSquared(from, feet) > 64f * 64f;
            Zone? hit = null;
            foreach (var z in solid)
            {
                if (teleported ? !z.Box.OverlapsHull(feet) : !z.Box.HullSweepHits(from, feet)) continue;
                hit = z;
                break;
            }

            if (hit == null)
            {
                _lastFeet[c.Slot] = feet;
                _lastFree[c.Slot] = feet;
                continue;
            }

            // Back to the last free spot, or (teleported / spawned inside, or the wall turned solid on top of them) out
            // through the nearest side.
            var back = !teleported && _lastFree.TryGetValue(c.Slot, out var free) && !solid.Any(z => z.Box.OverlapsHull(free))
                ? free
                : hit.Box.NearestOutside(feet);
            pawn.Teleport(new CssVector(back.X, back.Y, back.Z), null, new CssVector(0, 0, 0));
            _lastFeet[c.Slot] = back;
        }
    }

    private void UpdateBeams(Zone z, bool show)
    {
        if (!show)
        {
            RemoveBeams(z.Id);
            return;
        }

        var have = _beams.TryGetValue(z.Id, out var list) && list.Count == 12 && list.All(b => b.IsValid);
        if (have && _beamVersion.TryGetValue(z.Id, out var v) && v == z.Version) return;
        RemoveBeams(z.Id);
        var c = _m.Cfg.BlockadeColor;
        static int B(int v) => Math.Clamp(v, 0, 255);
        var color = c is { Length: >= 3 } ? Color.FromArgb(B(c.Length >= 4 ? c[3] : 255), B(c[0]), B(c[1]), B(c[2])) : Color.Red;
        var corners = z.Box.Corners();
        list = new List<CBeam>(12);
        foreach (var (a, b) in ZoneBox.Edges)
        {
            var beam = Fx.Beam(new CssVector(corners[a].X, corners[a].Y, corners[a].Z), new CssVector(corners[b].X, corners[b].Y, corners[b].Z), color, 1.5f);
            if (beam != null) list.Add(beam);
        }

        _beams[z.Id] = list;
        _beamVersion[z.Id] = z.Version;
    }

    private void RemoveBeams(int id)
    {
        if (_beams.Remove(id, out var list))
            foreach (var b in list) Fx.SafeRemove(b);
        _beamVersion.Remove(id);
    }

    public override string Summary(Zone zone) => KillZone.Timers(zone.GetInt(1), zone.GetInt(2)).Replace("always", "always solid");
}

/// <summary>ZoneType_Respawn: touching it sends the player to a random spawn point of their team (else the other team's).</summary>
internal sealed class RespawnZone : ZoneType
{
    private List<(Vector3 Pos, QAngle Ang)>? _t, _ct;

    public override string Key => ZoneTypeKeys.Respawn;
    public override string DisplayName => "Respawn";
    public override int LegacyId => 8;
    public override string Description => "Teleports players to a random spawn point of their team.";

    public override void OnRoundStart()
    {
        _t = null;
        _ct = null;
    }

    public override void OnEnter(Zone zone, CCSPlayerController player)
    {
        _t ??= Spawns("info_player_terrorist");
        _ct ??= Spawns("info_player_counterterrorist");
        var own = player.Team == CsTeam.CounterTerrorist ? _ct : _t;
        var other = player.Team == CsTeam.CounterTerrorist ? _t : _ct;
        var list = own.Count > 0 ? own : other;
        if (list.Count == 0) return;
        var (pos, ang) = list[Random.Shared.Next(list.Count)];
        player.TeleportTo(new CssVector(pos.X, pos.Y, pos.Z), ang, new CssVector(0, 0, 0));
    }

    private static List<(Vector3, QAngle)> Spawns(string designer)
    {
        var list = new List<(Vector3, QAngle)>();
        foreach (var e in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(designer))
        {
            var o = e.AbsOrigin;
            if (o == null) continue;
            var r = e.AbsRotation;
            list.Add((new Vector3(o.X, o.Y, o.Z), new QAngle(r?.X ?? 0, r?.Y ?? 0, 0)));
        }

        return list;
    }

    public override string Summary(Zone zone) => "";
}

/// <summary>ZoneType_Teleport: touching it teleports to the Teleport Destination with the same name (data string 1).</summary>
internal sealed class TeleportZone : ZoneType
{
    private readonly ZonesModule _m;
    public TeleportZone(ZonesModule m) => _m = m;

    public override string Key => ZoneTypeKeys.Teleport;
    public override string DisplayName => "Teleport";
    public override int LegacyId => 10;
    public override string Description => "Teleports players to the Teleport Destination with this name.";

    public override IReadOnlyList<ZoneField> Fields { get; } = new[]
    {
        new ZoneField("Destination name", ZoneFieldKind.Text, 1, "(name of a Teleport Destination zone)")
    };

    public override void OnEnter(Zone zone, CCSPlayerController player)
    {
        var target = zone.Name;
        if (target.Length == 0)
        {
            if (!player.IsBot) Chat.Tell(player, "zones.teleport.no_destination");
            return;
        }

        var dest = _m.FindByName(ZoneTypeKeys.TeleportDestination, target);
        if (dest == null)
        {
            if (!player.IsBot) Chat.Tell(player, "zones.teleport.missing");
            return;
        }

        _m.TeleportTo(dest, player);
    }

    public override string Summary(Zone zone) => zone.Name.Length > 0 ? "→ " + zone.Name : "no destination";
}

/// <summary>ZONE_TYPE_TELEPORT_DESTINATION: a named spot; the jailbreak uses "lr_tele", Capture the Flag "ctf_flag" / "ctf_capture".</summary>
internal sealed class TeleportDestinationZone : ZoneType
{
    public override string Key => ZoneTypeKeys.TeleportDestination;
    public override string DisplayName => "Teleport Destination";
    public override int LegacyId => 11;
    public override string Description => "Arrival point (floor centre, facing the zone's angles). lr_tele = LR teleport, ctf_flag / ctf_capture = Capture the Flag.";

    public override IReadOnlyList<ZoneField> Fields { get; } = new[]
    {
        new ZoneField("Name", ZoneFieldKind.Text, 1, "(lr_tele, ctf_flag, ctf_capture or your own)")
    };

    public override string Summary(Zone zone) => zone.Name.Length > 0 ? zone.Name : "unnamed";
}

/// <summary>ZoneType_WeaponStrip: entering removes all of the player's weapons.</summary>
internal sealed class WeaponStripZone : ZoneType
{
    public override string Key => ZoneTypeKeys.WeaponStrip;
    public override string DisplayName => "Weapon Strip";
    public override int LegacyId => 13;
    public override string Description => "Removes every weapon of a player who enters.";

    public override void OnEnter(Zone zone, CCSPlayerController player)
    {
        if (player.IsAliveP()) player.RemoveWeapons();
    }

    public override string Summary(Zone zone) => "";
}

/// <summary>
/// ZoneType_DamageBlocker: blocks the chosen damage types for players inside. Port fix: leaving one blocker no longer
/// clears the protection of another blocker the player is still standing in (the original reset all bits on any exit).
/// The check uses the victim's position when the damage happens (not the 16 Hz enter/exit poll), so a fast fall into a
/// fall blocker is caught and flag edits apply at once.
/// </summary>
internal sealed class DamageBlockerZone : ZoneType
{
    private readonly ZonesModule _m;
    private bool _registered;

    public DamageBlockerZone(ZonesModule m) => _m = m;

    public override string Key => ZoneTypeKeys.DamageBlocker;
    public override string DisplayName => "Damage Blocker";
    public override int LegacyId => 14;
    public override string Description => "Blocks the selected damage types for players inside.";

    public override IReadOnlyList<ZoneField> Fields { get; } = new[]
    {
        new ZoneField("Blocked damage", ZoneFieldKind.Flags, 1)
        {
            Options = new[]
            {
                ("Fall", (int)DamageTypes_t.DMG_FALL), ("Drown", (int)DamageTypes_t.DMG_DROWN),
                ("Crush", (int)DamageTypes_t.DMG_CRUSH), ("Fire (molotov / burn)", (int)DamageTypes_t.DMG_BURN)
            }
        }
    };

    public override void OnAssigned(Zone zone)
    {
        if (_registered) return;
        _registered = true;
        DamageService.Register("Zones.DamageBlocker", 210, OnDamage);
    }

    private HookResult OnDamage(DamageContext ctx)
    {
        var dmg = (int)ctx.Info.BitsDamageType;
        var o = ctx.VictimPawn.AbsOrigin;
        if (dmg == 0 || o == null) return HookResult.Continue;
        var feet = new Vector3(o.X, o.Y, o.Z);
        foreach (var z in _m.OfType(Key))
            if ((z.GetInt(1) & dmg) != 0 && ReferenceEquals(z.Type, this) && z.Box.OverlapsHull(feet)) return HookResult.Handled;
        return HookResult.Continue;
    }

    internal void Stop()
    {
        if (!_registered) return;
        DamageService.Unregister(OnDamage);
        _registered = false;
    }

    public override string Summary(Zone zone)
    {
        var b = zone.GetInt(1);
        var names = Fields[0].Options.Where(o => (b & o.Value) != 0).Select(o => o.Name.Split(' ')[0]).ToList();
        return names.Count == 0 ? "nothing blocked" : string.Join("+", names);
    }
}

/// <summary>ZoneType_Named: a named area other modules look up by name (ZoneTypeNamed_OnStartTouch → Jb.Zones.Entered).</summary>
internal sealed class NamedZone : ZoneType
{
    public override string Key => ZoneTypeKeys.Named;
    public override string DisplayName => "Named";
    public override int LegacyId => 15;
    public override string Description => "A named area. Various features can reference this name.";

    public override IReadOnlyList<ZoneField> Fields { get; } = new[] { new ZoneField("Name", ZoneFieldKind.Text, 1) };
    public override string Summary(Zone zone) => zone.Name.Length > 0 ? zone.Name : "unnamed";
}

/// <summary>ADDITION: tells guards (or everyone / admins) when a prisoner (or guard / anyone) enters, e.g. the armory or vents.</summary>
internal sealed class NotifyZone : ZoneType
{
    private readonly ZonesModule _m;
    private readonly Dictionary<(int Zone, int Slot), float> _next = new();

    public NotifyZone(ZonesModule m) => _m = m;

    public override string Key => ZoneTypeKeys.Notify;
    public override string DisplayName => "Notify (area alert)";
    public override string Description => "Announces when a player enters this area. No punishment, information only.";

    public override IReadOnlyList<ZoneField> Fields { get; } = new[]
    {
        new ZoneField("Area name", ZoneFieldKind.Text, 1, "(shown in the alert, e.g. Armory)"),
        new ZoneField("Triggered by", ZoneFieldKind.Choice, 1) { Options = new[] { ("Prisoners", 0), ("Guards", 1), ("Everyone", 2) } },
        new ZoneField("Alert goes to", ZoneFieldKind.Choice, 2) { Options = new[] { ("Guards", 0), ("Everyone", 1), ("Admins", 2) } }
    };

    public override void OnRoundStart() => _next.Clear();

    public override void OnEnter(Zone zone, CCSPlayerController player)
    {
        var who = zone.GetInt(1);
        if (who == 0 && player.Team != CsTeam.Terrorist) return;
        if (who == 1 && player.Team != CsTeam.CounterTerrorist) return;
        var now = Server.CurrentTime;
        if (_next.TryGetValue((zone.Id, player.Slot), out var t) && now < t) return;
        _next[(zone.Id, player.Slot)] = now + Math.Max(0f, _m.Cfg.NotifyCooldown);
        var area = zone.Name.Length > 0 ? zone.Name : zone.Title;
        var msg = Lang.T("zones.notify.entered", Chat.Name(player), area);
        foreach (var c in Players.Humans())
        {
            var send = zone.GetInt(2) switch
            {
                1 => true,
                2 => Perms.IsAdmin(c),
                _ => c.Team == CsTeam.CounterTerrorist
            };
            if (send) Chat.Print(c, msg);
        }
    }

    public override string Summary(Zone zone) => (zone.Name.Length > 0 ? zone.Name : "unnamed") + " alert";
}

/// <summary>ADDITION: marks a cell block (data string 1 = block name). Crews claim cell blocks as turf.</summary>
internal sealed class CellBlockZone : ZoneType
{
    public override string Key => ZoneTypeKeys.CellBlock;
    public override string DisplayName => "Cell Block";
    public override string Description => "A block of cells. Crews claim cell blocks as turf.";

    public override IReadOnlyList<ZoneField> Fields { get; } = new[] { new ZoneField("Block name", ZoneFieldKind.Text, 1, "(e.g. A, B, Upper)") };
    public override string Summary(Zone zone) => zone.Name.Length > 0 ? "block " + zone.Name : "unnamed block";
}
