using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Modules.LastRequest;

/// <summary>
/// Ports of UltJB_LR_Rebel_PhatKid.sp (heavy, slow-falling knife tank: 3000 HP + 1000 per guard, one-hit knife kills,
/// guards get M249 + incendiary) and UltJB_LR_Rebel_Ghost.sp (invisible 1 HP knifer, one-hit knife kills, shows on radar
/// for 0.5 s every 4 s, guards get Nova + knife and cannot pick up or drop weapons).
/// </summary>
public sealed class RebelSpecialLastRequests : JbModule
{
    public override string Name => "LastRequests.RebelSpecial";
    public override int Order => 10;

    private readonly HashSet<int> _phatSlots = new(); // Phat Kid can run for several prisoners (rebel as a team)
    private int _ghostSlot = -1;
    private bool _ghostVisible;
    private Timer? _ghostRadarTimer;
    private float _ghostVisibleUntil;

    public override void OnLoad(bool hotReload)
    {
        if (Jb.LastRequests is not LastRequestModule) return;

        Jb.LastRequests.Register(new LastRequestDefinition
        {
            Name = "Phat Kid", Category = "Rebel", Flags = LrFlags.Rebel | LrFlags.TempInvincible | LrFlags.RandomTeleportLocation,
            OnStart = p =>
            {
                Jb.LastRequests.SetHealth(p, 3000 + 1000 * Players.AliveGuards().Count());
                Jb.LastRequests.StripWeapons(p, true);
                Jb.WeaponSelection.GiveWeapon(p, "weapon_knife");
                foreach (var g in Players.AliveGuards().Select(c => c.Jb()!).Where(g => g != null))
                {
                    Jb.LastRequests.StripWeapons(g, true);
                    Jb.WeaponSelection.GiveWeapon(g, "weapon_knife");
                    Jb.WeaponSelection.GiveWeapon(g, "weapon_m249");
                    Jb.WeaponSelection.GiveWeapon(g, "weapon_incgrenade");
                }

                Movement.SetGravity(p.Controller, "lr.phatkid", 2f);
                Movement.SetSpeed(p.Controller, "lr.phatkid", 1.1f);
                _phatSlots.Add(p.Slot);
            },
            OnEnd = (p, _) =>
            {
                SimpleLastRequests.RestoreIfAlive(p);
                Movement.ClearGravity(p.Controller, "lr.phatkid");
                Movement.ClearSpeed(p.Controller, "lr.phatkid");
                _phatSlots.Remove(p.Slot);
            }
        });

        Jb.LastRequests.Register(new LastRequestDefinition
        {
            Name = "Ghost", Category = "Rebel",
            Flags = LrFlags.LastPrisonerOnlyCanUse | LrFlags.Rebel | LrFlags.NoRadar | LrFlags.NoBeacon | LrFlags.RandomTeleportLocation,
            OnStart = p =>
            {
                Jb.LastRequests.SetHealth(p, 1);
                Jb.LastRequests.StripWeapons(p, true);
                Jb.WeaponSelection.GiveWeapon(p, "weapon_knife_t");
                SetInvisible(p.Controller, true);
                foreach (var g in Players.AliveGuards().Select(c => c.Jb()!).Where(g => g != null))
                {
                    Jb.LastRequests.StripWeapons(g, true);
                    Jb.WeaponSelection.GiveWeapon(g, "weapon_knife");
                    Jb.WeaponSelection.GiveWeapon(g, "weapon_nova");
                }

                _ghostSlot = p.Slot;
                _ghostVisible = false;
                StopRadarTimer();
                _ghostRadarTimer = Plugin.AddTimer(4f, () =>
                {
                    _ghostVisible = true;
                    _ghostVisibleUntil = (float)Server.EngineTime + 0.5f;
                }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
            },
            OnEnd = (p, _) =>
            {
                SimpleLastRequests.RestoreIfAlive(p);
                SetInvisible(p.Controller, false);
                StopRadarTimer();
                if (_ghostSlot == p.Slot) _ghostSlot = -1;
            }
        });

        DamageService.Register(Name, 97, OnDamage);
        WeaponService.SetPickupRule(Name, (player, _, method) =>
            _ghostSlot < 0 || method != AcquireMethod.PickUp || player.Team != CsTeam.CounterTerrorist || player.Jb() is { } g && Jb.WeaponSelection.IsGettingItem(g));
        WeaponService.SetDropRule(Name, (_, _) => _ghostSlot < 0);
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        Plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        WeaponService.RemovePickupRule(Name);
        WeaponService.RemoveDropRule(Name);
        StopRadarTimer();
    }

    public override void OnRoundStart() => ResetRound();

    public override void OnMapStart(string mapName) => ResetRound(); // warmup has no round_start

    private void ResetRound()
    {
        _phatSlots.Clear();
        _ghostSlot = -1;
        StopRadarTimer();
    }

    private void StopRadarTimer()
    {
        try { _ghostRadarTimer?.Kill(); }
        catch { /* already stopped */ }
        _ghostRadarTimer = null;
        _ghostVisible = false;
    }

    private static void SetInvisible(CCSPlayerController? c, bool invisible)
    {
        var pawn = c?.PawnOrNull();
        if (pawn == null) return;
        pawn.RenderMode = invisible ? RenderMode_t.kRenderNone : RenderMode_t.kRenderNormal;
        pawn.Render = Color.FromArgb(invisible ? 0 : 255, pawn.Render.R, pawn.Render.G, pawn.Render.B);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_nRenderMode");
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
    }

    private HookResult OnDamage(DamageContext ctx)
    {
        if (_ghostSlot < 0 && _phatSlots.Count == 0) return HookResult.Continue;

        if (ctx.Victim.Slot == _ghostSlot)
        {
            // Ghost is immune to falls, grenades and fire.
            if (ctx.IsFall) return HookResult.Handled;
            var w = ctx.Weapon;
            if (w.Contains("hegrenade") || w.Contains("inferno") || w.Contains("molotov") || w.Contains("incgrenade")) return HookResult.Handled;
            return HookResult.Continue;
        }

        if (ctx.Victim.Team != CsTeam.CounterTerrorist || ctx.Attacker == null) return HookResult.Continue;
        if (ctx.Attacker.Slot != _ghostSlot && !_phatSlots.Contains(ctx.Attacker.Slot)) return HookResult.Continue;
        // Guards: the rebel's knife kills in one hit, anything else does nothing.
        if (!ctx.IsKnife) return HookResult.Handled;
        ctx.Damage = 1337f;
        return HookResult.Continue;
    }

    private void OnTick()
    {
        if (_ghostSlot < 0) return;
        var pawn = Players.Get(_ghostSlot).Controller?.PawnOrNull();
        if (pawn == null) return;
        if (_ghostVisible && Server.EngineTime > _ghostVisibleUntil) _ghostVisible = false;
        try
        {
            if (pawn.EntitySpottedState.Spotted == _ghostVisible) return;
            pawn.EntitySpottedState.Spotted = _ghostVisible;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_entitySpottedState");
        }
        catch
        {
            // radar is cosmetic
        }
    }

    /// <summary>The ghost's knife would still render in other players' view: hide the ghost's weapons from everyone else.</summary>
    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (_ghostSlot < 0) return;
        var ghost = Players.Get(_ghostSlot).Controller;
        if (ghost == null || !ghost.IsValid || !ghost.IsAliveP()) return;
        var weapons = ghost.Weapons().ToList();
        if (weapons.Count == 0) return;
        foreach (var (info, viewer) in infoList)
        {
            if (viewer == null || !viewer.IsValid || viewer.Slot == _ghostSlot) continue;
            foreach (var w in weapons) info.TransmitEntities.Remove(w);
        }
    }
}
