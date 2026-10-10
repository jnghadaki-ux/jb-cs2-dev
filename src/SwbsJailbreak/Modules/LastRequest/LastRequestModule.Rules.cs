using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.LastRequest;

/// <summary>Per-map LR teleport destination and rebel points (replace SMPlugins ZoneManager "lr_tele" and PathPoints "rebels").</summary>
public sealed class LrMapPoints
{
    public float[]? LrTeleport { get; set; }
    public List<float[]> RebelPoints { get; set; } = new();
}

public sealed partial class LastRequestModule
{
    private LrMapPoints _points = new();

    /// <summary>Rebel points of this map (also used by the Days API for free-for-all spawns).</summary>
    internal int RebelPointCount => _points.RebelPoints.Count;

    internal IReadOnlyList<float[]> RebelPointList => _points.RebelPoints;
    private string _pointsMap = "";
    private readonly Dictionary<int, int> _transmitCacheTick = new();

    // ================================================================== weapons

    public void SaveWeapons(JbPlayer player)
    {
        var c = player.Controller;
        if (c == null) return;
        var s = S(player);
        s.SavedWeapons.Clear();
        s.SavedActive = c.ActiveWeapon()?.DesignerName ?? "";
        foreach (var w in c.Weapons())
        {
            if (s.SavedWeapons.Any(x => x.Name == w.DesignerName)) continue;
            s.SavedWeapons.Add((w.DesignerName, w.Clip1, w.ReserveAmmo[0], w.ReserveAmmo[1]));
        }

        s.HasSavedWeapons = true;
    }

    public void RestoreWeapons(JbPlayer player)
    {
        var c = player.Controller;
        var s = S(player);
        if (c == null || !s.HasSavedWeapons) return;
        StripWeaponsInternal(c);
        foreach (var (name, clip, r0, r1) in s.SavedWeapons)
        {
            var w = (Jb.WeaponSelection as Modules.WeaponSelection.WeaponSelectionModule)?.GivePlayerItem(player, name);
            if (w == null)
            {
                c.Give(name);
                w = c.Weapons().FirstOrDefault(x => x.DesignerName == name);
            }

            if (w == null || !w.IsValid) continue;
            w.Clip1 = clip;
            w.ReserveAmmo[0] = r0;
            w.ReserveAmmo[1] = r1;
            Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_iClip1");
            Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_pReserveAmmo");
        }

        if (s.SavedActive.Length > 0)
        {
            var slot = Weapons.IsKnife(s.SavedActive) ? "slot3" : Weapons.IsPistol(s.SavedActive) ? "slot2" : Weapons.IsGrenade(s.SavedActive) ? "slot4" : "slot1";
            Server.NextFrame(() =>
            {
                if (c.IsValid && c.IsAliveP()) c.ExecuteClientCommand(slot);
            });
        }
    }

    public void StripWeapons(JbPlayer player, bool saveWeapons = false)
    {
        var c = player.Controller;
        if (c == null) return;
        if (saveWeapons) SaveWeapons(player);
        StripWeaponsInternal(c);
    }

    private static void StripWeaponsInternal(CCSPlayerController c)
    {
        foreach (var w in c.Weapons().ToList()) Jb.Settings.StripWeaponFromOwner(w);
        c.RemoveWeapons();
    }

    /// <summary>CleanupWeaponsFromLR: LR weapons last owned by this player (held or dropped) are deleted.</summary>
    private static void CleanupWeaponsFromLr(JbPlayer p)
    {
        var pawnRaw = p.Controller?.PlayerPawn.Raw ?? 0u;
        if (pawnRaw == 0) return;
        foreach (var w in Utilities.FindAllEntitiesByDesignerName<CCSWeaponBase>("weapon_"))
        {
            try
            {
                if (!w.IsValid || !Jb.WeaponSelection.IsWeaponFromLastRequest(w)) continue;
                if (w.OwnerEntity.Raw != pawnRaw && w.PrevOwner.Raw != pawnRaw) continue;
                w.AcceptInput("Kill");
            }
            catch
            {
                // entity going away
            }
        }
    }

    /// <summary>ShouldBlockWeaponGain (+ OnWeaponCanUse: nobody may pick up another player's LR weapon).</summary>
    private bool PickupRule(CCSPlayerController player, string weapon, AcquireMethod method)
    {
        var p = player.Jb();
        if (p == null || Jb.WeaponSelection.IsGettingItem(p)) return true;
        if (ShouldBlockWeaponGain(p, method == AcquireMethod.PickUp)) return false;
        if (method != AcquireMethod.PickUp || NumStartedIgnore() == 0) return true;
        var item = WeaponService.CurrentItem;
        if (item == IntPtr.Zero) return true;
        var pawnRaw = player.PlayerPawn.Raw;
        foreach (var w in Utilities.FindAllEntitiesByDesignerName<CCSWeaponBase>(weapon))
        {
            try
            {
                if (!w.IsValid || w.AttributeManager.Item.Handle != item) continue;
                if (!Jb.WeaponSelection.IsWeaponFromLastRequest(w)) return true;
                var owner = w.PrevOwner.Raw;
                return owner == 0 || owner == uint.MaxValue || owner == pawnRaw;
            }
            catch
            {
                return true;
            }
        }

        return true;
    }

    private bool ShouldBlockWeaponGain(JbPlayer p, bool checkPickupFlag)
    {
        var c = p.Controller!;
        if (c.Team == CsTeam.Terrorist && S(p).Started)
            return !checkPickupFlag || (GetFlags(p) & LrFlags.AllowWeaponPickups) == 0;
        if (c.Team == CsTeam.CounterTerrorist && GuardsPrisoner(p) is { } prisoner)
            return !checkPickupFlag || (GetFlags(prisoner) & LrFlags.AllowWeaponPickups) == 0;
        return false;
    }

    private HookResult OnBuy(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return HookResult.Continue;
        var p = player.Jb();
        return p != null && !Jb.WeaponSelection.IsGettingItem(p) && ShouldBlockWeaponGain(p, false) ? HookResult.Handled : HookResult.Continue;
    }

    private bool DropRule(CCSPlayerController player, CBasePlayerWeapon? weapon)
    {
        var p = player.Jb();
        if (p == null) return true;
        if (player.Team == CsTeam.Terrorist && S(p).Started) return (GetFlags(p) & LrFlags.AllowWeaponDrops) != 0;
        if (player.Team == CsTeam.CounterTerrorist && GuardsPrisoner(p) is { } prisoner && S(prisoner).Started)
            return (GetFlags(prisoner) & LrFlags.AllowWeaponDrops) != 0;
        return true;
    }

    // ================================================================== damage

    private HookResult OnDamage(DamageContext ctx)
    {
        var victim = ctx.Victim.Jb();
        if (victim == null) return HookResult.Continue;
        var attacker = ctx.Attacker?.Jb();
        if (attacker != null && attacker.Slot == victim.Slot) attacker = null; // self damage behaves like world damage here
        TryRemoveAdminFreeday(victim, attacker);

        if (attacker != null && !S(attacker).CanDealDamage) return HookResult.Handled;
        if (attacker != null && S(attacker).Initialized && !S(attacker).Started) return HookResult.Handled;
        var vs = S(victim);
        if (vs.Initialized && !vs.Started) return HookResult.Handled;
        if (attacker != null && S(attacker).OpponentMenu != null) return HookResult.Handled;
        if (vs.OpponentMenu != null || vs.Invincible) return HookResult.Handled;

        var victimTeam = ctx.Victim.Team;
        if (victimTeam == CsTeam.Terrorist && vs.Started)
        {
            if (attacker != null && (GetFlags(victim) & LrFlags.Freeday) != 0) return HookResult.Handled;
            var opponent = PrisonersOpponent(victim);
            if (opponent != null && attacker?.Slot != opponent.Slot)
            {
                if (attacker != null) return HookResult.Handled;
                CreditTo(ctx, opponent);
                return HookResult.Continue;
            }
        }
        else if (victimTeam == CsTeam.CounterTerrorist)
        {
            var prisoner = GuardsPrisoner(victim);
            if (prisoner != null && attacker?.Slot != prisoner.Slot)
            {
                if (attacker != null) return HookResult.Handled;
                CreditTo(ctx, prisoner);
                return HookResult.Continue;
            }
        }

        if (attacker == null) return HookResult.Continue;
        var attackerTeam = ctx.Attacker!.Team;
        if (attackerTeam == CsTeam.Terrorist && S(attacker).Started)
        {
            var flags = GetFlags(attacker);
            if ((flags & LrFlags.Freeday) != 0 && (attackerTeam != victimTeam || (Jb.Days.IsInProgress && Jb.Days.IsFreeForAll)))
            {
                // A freeday who attacks is slain (next tick: killing inside the damage callback is unsafe).
                S(attacker).CanDealDamage = false;
                var slot = attacker.Slot;
                AddTimer(0.1f, () =>
                {
                    if (Players.Get(slot).Controller is { IsValid: true } ac && ac.IsAliveP()) ac.CommitSuicide(false, true);
                });
                return HookResult.Handled;
            }

            var opponent = PrisonersOpponent(attacker);
            if (opponent != null)
            {
                if (victim.Slot != opponent.Slot) return HookResult.Handled;
                if ((flags & LrFlags.DontAllowDamagingOpponent) != 0) return HookResult.Handled;
            }
        }
        else if (attackerTeam == CsTeam.CounterTerrorist)
        {
            var prisoner = GuardsPrisoner(attacker);
            if (prisoner != null)
            {
                if (victim.Slot != prisoner.Slot) return HookResult.Handled;
                if ((GetFlags(prisoner) & LrFlags.DontAllowDamagingOpponent) != 0) return HookResult.Handled;
            }
        }

        return HookResult.Continue;
    }

    /// <summary>World damage to an LR player is credited to their opponent (original attacker/inflictor rewrite).</summary>
    private static void CreditTo(DamageContext ctx, JbPlayer opponent)
    {
        try
        {
            var pawnRaw = opponent.Controller?.PlayerPawn.Raw ?? 0u;
            if (pawnRaw == 0 || pawnRaw == uint.MaxValue) return;
            ctx.Info.Attacker.Raw = pawnRaw;
            ctx.Info.Inflictor.Raw = pawnRaw;
        }
        catch
        {
            // credit is cosmetic
        }
    }

    private void TryRemoveAdminFreeday(JbPlayer victim, JbPlayer? attacker)
    {
        if (attacker == null) return;
        if (victim.Controller?.Team == attacker.Controller?.Team) return;
        var vs = S(victim);
        if (vs.AdminFreeday && !vs.Started)
        {
            vs.AdminFreeday = false;
            Kill(ref vs.CancelFreedayTimer);
            Chat.Tell(victim, "lr.freeday.removed_damaged", attacker.Name);
        }

        var a = S(attacker);
        if (a.AdminFreeday && !a.Started)
        {
            a.AdminFreeday = false;
            Kill(ref a.CancelFreedayTimer);
            Chat.Tell(attacker, "lr.freeday.removed_damaging", victim.Name);
        }
    }

    /// <summary>Freeday prisoners cannot break func_breakable (vents) - OnBreakableTakeDamage.</summary>
    private HookResult OnEntityTakeDamage(CBaseEntity entity, CTakeDamageInfo info)
    {
        try
        {
            if (entity == null || !entity.IsValid || entity.DesignerName != "func_breakable") return HookResult.Continue;
            var owner = DamageService.ResolveAttacker(info)?.Jb();
            if (owner == null || !S(owner).Started || (GetFlags(owner) & LrFlags.Freeday) == 0) return HookResult.Continue;
            return HookResult.Handled;
        }
        catch
        {
            return HookResult.Continue;
        }
    }

    // ================================================================== visibility (SetTransmit port)

    private bool ShouldSeeAsPrisoner(JbPlayer viewer, JbPlayer other)
    {
        var vs = S(viewer);
        if (!vs.Started) return true;
        var flags = GetFlags(viewer);
        if ((flags & LrFlags.Freeday) != 0) return true;
        if (other.Controller?.Team == viewer.Controller?.Team)
            return _rebelAsTeamStarted && (flags & LrFlags.Rebel) != 0 && (GetFlags(other) & LrFlags.Rebel) != 0;
        var opponent = PrisonersOpponent(viewer);
        return opponent == null || opponent.Slot == other.Slot;
    }

    private static bool ShouldSeeAsGuard(JbPlayer viewer, JbPlayer other)
    {
        var prisoner = GuardsPrisoner(viewer);
        return prisoner == null || prisoner.Slot == other.Slot;
    }

    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (!Cfg.HidePlayersDuringLr || NumStartedIgnore(LrFlags.Freeday) == 0) return;
        foreach (var (info, viewerCtl) in infoList)
        {
            if (viewerCtl == null || !viewerCtl.IsValid || !viewerCtl.IsAliveP()) continue;
            var viewer = viewerCtl.Jb();
            if (viewer == null) continue;
            var team = viewerCtl.Team;
            if (team <= CsTeam.Spectator) continue;
            if (team == CsTeam.Terrorist && !S(viewer).Started) continue;
            if (team == CsTeam.CounterTerrorist && GuardsPrisoner(viewer) == null) continue;

            foreach (var other in Players.Alive())
            {
                if (other.Slot == viewerCtl.Slot) continue;
                var op = other.Jb();
                if (op == null) continue;
                var see = team == CsTeam.Terrorist ? ShouldSeeAsPrisoner(viewer, op) : ShouldSeeAsGuard(viewer, op);
                if (see) continue;
                var pawn = other.PawnOrNull();
                if (pawn == null) continue;
                info.TransmitEntities.Remove(pawn);
                foreach (var w in other.Weapons()) info.TransmitEntities.Remove(w);
            }
        }
    }

    // ================================================================== beacons + radar (OnPostThinkPost)

    private void OnTick()
    {
        if (NumInitialized == 0 && NumStartedIgnore() == 0) return;
        var now = (float)Server.EngineTime;
        foreach (var p in Players.Connected())
        {
            var c = p.Controller!;
            if (!c.IsAliveP()) continue;
            if (c.Team == CsTeam.Terrorist)
            {
                var s = S(p);
                if (s.Initialized || s.Started) UpdateEffects(p, PrisonersOpponent(p), now);
            }
            else if (c.Team == CsTeam.CounterTerrorist)
            {
                var prisoner = GuardsPrisoner(p);
                if (prisoner != null) UpdateEffects(p, prisoner, now);
            }
        }
    }

    private void UpdateEffects(JbPlayer p, JbPlayer? opponent, float now)
    {
        var c = p.Controller!;
        var pawn = c.PawnOrNull();
        if (pawn == null) return;
        // Original read the LR flags of the player itself: guards have none, so they always get radar + beacons.
        var flags = c.Team == CsTeam.Terrorist ? GetFlags(p) : LrFlags.None;

        if ((flags & LrFlags.NoRadar) == 0) SetSpotted(pawn);
        if ((flags & LrFlags.ShowAllGuardsOnRadar) != 0)
            foreach (var g in Players.AliveGuards())
                if (g.PawnOrNull() is { } gp) SetSpotted(gp);

        if (!Cfg.Beacons || (flags & LrFlags.NoBeacon) != 0) return;
        var s = S(p);
        if (opponent != null && now >= s.NextBeam && opponent.Controller?.PawnOrNull()?.AbsOrigin is { } oo && pawn.AbsOrigin is { } po)
        {
            s.NextBeam = now + 0.5f;
            var color = HealthColor(opponent.Controller!);
            Fx.Beam(new Vector(oo.X, oo.Y, oo.Z + 40f), new Vector(po.X, po.Y, po.Z + 40f), color, 1f, 0.6f);
        }

        if (now < s.NextRing) return;
        s.NextRing = now + 0.75f;
        if (S(p).Started || c.Team == CsTeam.CounterTerrorist)
        {
            if (c.Team == CsTeam.Terrorist && (flags & LrFlags.Freeday) != 0)
                Fx.BeaconPulse(c, Color.FromArgb(200, Random.Shared.Next(10, 256), Random.Shared.Next(10, 256), Random.Shared.Next(10, 256)), 5f, 90f, 0.55f);
            else
                Fx.BeaconPulse(c, HealthColor(c), 5f, 90f, 0.55f);
        }
        else
        {
            Fx.BeaconPulse(c, Color.White, 90f, 5f, 0.55f); // initialised but not started: shrinking white ring
        }
    }

    private static void SetSpotted(CCSPlayerPawn pawn)
    {
        try
        {
            if (pawn.EntitySpottedState.Spotted) return;
            pawn.EntitySpottedState.Spotted = true;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_entitySpottedState");
        }
        catch
        {
            // radar is cosmetic
        }
    }

    /// <summary>CalculateBeamColor: green at full health fading to red.</summary>
    private static Color HealthColor(CCSPlayerController c)
    {
        var pawn = c.PawnOrNull();
        var hp = pawn?.Health ?? 0;
        var max = Math.Max(1, pawn?.MaxHealth ?? 100);
        var f = Math.Max(0.002f, (float)hp / max);
        int Lerp(int start, int end) => end + (int)MathF.Round((start - end) * f);
        return Color.FromArgb(200, Lerp(0, 255), Lerp(255, 0), 0);
    }

    // ================================================================== teleports

    /// <summary>
    /// TeleportNearClient(anchor, mover). The original hull-traced a free spot beside the anchor; CSS 1.0.368 has no
    /// trace, so the mover is put on the anchor's own spot (players do not collide while noblock is on) and keeps its
    /// own view angles (faceAnchor needs a spot beside the anchor, so it has no effect here).
    /// </summary>
    internal static void TeleportNear(CCSPlayerController anchor, CCSPlayerController mover, bool faceAnchor)
    {
        var ap = anchor.PawnOrNull();
        var mp = mover.PawnOrNull();
        if (ap?.AbsOrigin == null || mp == null) return;
        var dest = new Vector(ap.AbsOrigin.X, ap.AbsOrigin.Y, ap.AbsOrigin.Z);
        mover.TeleportTo(dest, null, new Vector(0, 0, 0));
    }

    private void TeleportToLrPoint(JbPlayer p)
    {
        var c = p.Controller;
        if (c == null || !c.IsAliveP()) return;
        // Original: the Teleport Destination zone named "lr_tele" (ZoneManager). Falls back to the !lr_setpoint spot.
        if (Jb.Zones.TeleportToDestination("lr_tele", c)) return;
        if (_points.LrTeleport is { Length: 3 } t)
        {
            c.TeleportTo(new Vector(t[0], t[1], t[2]), null, new Vector(0, 0, 0));
            return;
        }

        // No LR point for this map yet: teleport next to the warden (or any alive guard), like the original fallback.
        var anchor = Jb.Warden.Warden?.Controller is { } w && w.IsAliveP() ? w : Players.AliveGuards().FirstOrDefault();
        if (anchor != null) TeleportNear(anchor, c, false);
        Chat.TellAll("lr.points.no_lr_point");
    }

    private int RandomRebelPoint()
    {
        if (_points.RebelPoints.Count == 0)
        {
            Chat.TellAll("lr.points.no_rebel_points");
            return -1;
        }

        return Random.Shared.Next(_points.RebelPoints.Count);
    }

    private void TeleportToRebelPoint(JbPlayer p, int index)
    {
        if (index < 0 || index >= _points.RebelPoints.Count) return;
        var c = p.Controller;
        if (c == null || !c.IsAliveP()) return;
        var pt = _points.RebelPoints[index];
        if (pt.Length < 3) return;
        var yaw = (pt.Length >= 4 ? pt[3] : 0f) + Random.Shared.NextSingle() * 360f;
        c.TeleportTo(new Vector(pt[0], pt[1], pt[2]), new QAngle(0, yaw, 0), new Vector(0, 0, 0));
    }

    // ================================================================== point editing (admin)

    private void LoadPoints(string map)
    {
        _pointsMap = string.IsNullOrEmpty(map) ? "unknown" : map.ToLowerInvariant();
        try { _points = Storage.LoadMapDoc<LrMapPoints>("lrpoints", _pointsMap); }
        catch { _points = new LrMapPoints(); }
    }

    private void SavePoints() => Storage.SaveMapDoc("lrpoints", _pointsMap, _points);

    [ConsoleCommand("css_lr_setpoint", "Save your position as this map's LR teleport point")]
    public void CmdSetLrPoint(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !Perms.Has(caller, "zones")) { if (caller != null) Chat.Tell(caller, "core.no_access"); return; }
        var o = caller.PawnOrNull()?.AbsOrigin;
        if (o == null) return;
        _points.LrTeleport = new[] { MathF.Round(o.X, 1), MathF.Round(o.Y, 1), MathF.Round(o.Z, 1) };
        SavePoints();
        Chat.Tell(caller, "lr.points.lr_set");
    }

    [ConsoleCommand("css_rebelpoint_add", "Add your position as a rebel spawn point for this map")]
    public void CmdAddRebelPoint(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !Perms.Has(caller, "zones")) { if (caller != null) Chat.Tell(caller, "core.no_access"); return; }
        var pawn = caller.PawnOrNull();
        if (pawn?.AbsOrigin == null) return;
        var o = pawn.AbsOrigin;
        _points.RebelPoints.Add(new[] { MathF.Round(o.X, 1), MathF.Round(o.Y, 1), MathF.Round(o.Z, 1), MathF.Round(pawn.EyeAngles.Y, 1) });
        SavePoints();
        Chat.Tell(caller, "lr.points.rebel_added", _points.RebelPoints.Count);
    }

    [ConsoleCommand("css_rebelpoint_clear", "Remove all rebel points of this map")]
    public void CmdClearRebelPoints(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null && !Perms.Has(caller, "zones")) { Chat.Tell(caller, "core.no_access"); return; }
        _points.RebelPoints.Clear();
        SavePoints();
        Chat.ReplyT(caller, info, "lr.points.rebel_cleared");
    }
}
