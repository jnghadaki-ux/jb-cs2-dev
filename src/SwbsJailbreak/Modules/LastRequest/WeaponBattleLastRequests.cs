using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Modules.LastRequest;

/// <summary>Section "WeaponBattle" (lr_weapon_battle_countdown).</summary>
public sealed class WeaponBattleConfig
{
    public int Countdown { get; set; } = 5;

    /// <summary>Knife Backstab: hits below this damage do nothing (original BACKSTAB_DAMAGE 180).</summary>
    public float BackstabDamage { get; set; } = 180f;
}

internal enum BattleMode
{
    Classic,
    NoScope,
    ShotForShot,
    MagForMag,
    KnifeBackstab
}

internal sealed class BattleState
{
    public BattleMode Mode;
    public bool Active;
    public int OpponentSlot = -1;
    public string Weapon = "";
    public int EffectId;
    public Timer? Countdown;
    public int CountdownTick;
    public bool Started;

    // turn based modes
    public int TurnSlot = -1;
    public int OldClip;
    public int ShotsFired;
    public int ClipSize;

    // classic: keep the reserve of the battle weapons (by entity handle) topped up
    public readonly Dictionary<uint, int> Reserve = new();
}

/// <summary>
/// Ports of LastRequests/UltJB_LR_WeaponBattle_*.sp: Classic (chosen weapon, unlimited reserve), No Scope (snipers,
/// scope blocked, 99 rounds), Shot for Shot (one bullet per turn), Mag for Mag (up to 5 bullets per turn) and Knife
/// Backstab (only backstabs hurt). Flow per original: opponent menu, weapon menu, effect menu, countdown, slay timer.
/// </summary>
public sealed class WeaponBattleLastRequests : JbModule
{
    private WeaponBattleConfig Cfg => Section<WeaponBattleConfig>("WeaponBattle");

    public override string Name => "LastRequests.WeaponBattle";
    public override int Order => 10;

    private static readonly string[] UnscopedRifles = { "weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_famas", "weapon_galilar" };

    private static BattleState S(JbPlayer p) => p.Data<BattleState>();

    public override void OnLoad(bool hotReload)
    {
        if (Jb.LastRequests is not LastRequestModule) return;
        Register("Classic", BattleMode.Classic);
        Register("No Scope", BattleMode.NoScope);
        Register("Shot for Shot", BattleMode.ShotForShot);
        Register("Mag for Mag", BattleMode.MagForMag);
        Register("Knife Backstab", BattleMode.KnifeBackstab);
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        DamageService.Register(Name, 99, OnDamage);
    }

    public override void OnUnload(bool hotReload) => DamageService.Unregister(OnDamage);

    private void Register(string name, BattleMode mode) =>
        Jb.LastRequests.Register(new LastRequestDefinition
        {
            Name = name, Category = "Weapon Battle",
            OnStart = p =>
            {
                var s = S(p);
                Reset(s);
                s.Mode = mode;
                s.Active = true;
                Jb.LastRequests.DisplayOpponentSelection(p, OnOpponentSelected);
            },
            OnEnd = OnEnd
        });

    private static void Reset(BattleState s)
    {
        try { s.Countdown?.Kill(); }
        catch { /* already stopped */ }
        s.Countdown = null;
        s.Active = s.Started = false;
        s.OpponentSlot = s.TurnSlot = -1;
        s.Weapon = "";
        s.EffectId = 0;
        s.Reserve.Clear();
    }

    private void OnOpponentSelected(JbPlayer p, JbPlayer opponent)
    {
        var s = S(p);
        s.OpponentSlot = opponent.Slot;
        Jb.LastRequests.StripWeapons(p, true);
        Jb.LastRequests.StripWeapons(opponent, true);

        if (s.Mode == BattleMode.KnifeBackstab)
        {
            ChooseEffect(p);
            return;
        }

        var filter = new WeaponFilter();
        switch (s.Mode)
        {
            case BattleMode.NoScope:
                filter.DisabledCategories.UnionWith(new[] { WeaponCategory.Knife, WeaponCategory.Pistols, WeaponCategory.Heavy, WeaponCategory.Smgs, WeaponCategory.Grenades });
                filter.Disabled.UnionWith(UnscopedRifles);
                break;
            case BattleMode.ShotForShot:
            case BattleMode.MagForMag:
                filter.Disabled.Add("weapon_knife");
                break;
        }

        Jb.WeaponSelection.DisplaySelectionMenu(p, (pl, weapon) =>
        {
            S(pl).Weapon = weapon;
            ChooseEffect(pl);
        }, pl => Jb.LastRequests.EndLastRequest(pl), filter);
    }

    private void ChooseEffect(JbPlayer p) =>
        Jb.Effects.DisplaySelectionMenu(p, (pl, effectId) =>
        {
            S(pl).EffectId = effectId;
            Prepare(pl);
        }, pl =>
        {
            pl.Controller?.PrintToChat(" " + Lang.T("lr.battle.no_effect"));
            S(pl).EffectId = 0;
            Prepare(pl);
        });

    private void Prepare(JbPlayer p)
    {
        var s = S(p);
        var opponent = Jb.LastRequests.GetOpponent(p);
        if (!s.Active || opponent == null) return;
        if (s.EffectId > 0)
        {
            var data = Jb.Effects.GetDefaultData(s.EffectId);
            Jb.Effects.StartEffect(p, s.EffectId, data);
            Jb.Effects.StartEffect(opponent, s.EffectId, data);
        }

        if (Cfg.Countdown < 1)
        {
            StartBattle(p);
            return;
        }

        Say(p, opponent, Lang.T("lr.battle.starting_in", Cfg.Countdown));
        s.CountdownTick = 0;
        var slot = p.Slot;
        s.Countdown = Plugin.AddTimer(1f, () =>
        {
            var pl = Players.Get(slot);
            var st = S(pl);
            if (!st.Active) return;
            st.CountdownTick++;
            var left = Cfg.Countdown - st.CountdownTick;
            if (left <= 0)
            {
                try { st.Countdown?.Kill(); }
                catch { /* stopping */ }
                st.Countdown = null;
                StartBattle(pl);
                return;
            }

            var opp = Jb.LastRequests.GetOpponent(pl);
            Say(pl, opp, Lang.T("lr.battle.tick", left));
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static void Say(JbPlayer p, JbPlayer? o, string text)
    {
        p.Controller?.PrintToChat(" " + text);
        o?.Controller?.PrintToChat(" " + text);
    }

    private void StartBattle(JbPlayer p)
    {
        var s = S(p);
        var opponent = Jb.LastRequests.GetOpponent(p);
        if (!s.Active || opponent == null) return;
        s.Started = true;
        var slayTime = s.Mode is BattleMode.Classic or BattleMode.KnifeBackstab ? 45 : 60;
        if (s.Mode != BattleMode.MagForMag) Jb.LastRequests.StartSlayTimer(p, slayTime);
        Say(p, opponent, Lang.T("lr.battle.started"));

        switch (s.Mode)
        {
            case BattleMode.Classic:
                SetupClassic(p, s);
                SetupClassic(opponent, s);
                break;
            case BattleMode.NoScope:
                Jb.WeaponSelection.GiveWeapon(p, s.Weapon);
                Jb.WeaponSelection.GiveWeapon(opponent, s.Weapon);
                break;
            case BattleMode.ShotForShot:
            case BattleMode.MagForMag:
                SetTurn(p, Random.Shared.Next(2) == 0 ? p : opponent);
                if (s.Mode == BattleMode.MagForMag) Jb.LastRequests.StartSlayTimer(p, 60);
                break;
            case BattleMode.KnifeBackstab:
                Jb.WeaponSelection.GiveWeapon(p, "weapon_knife");
                Jb.WeaponSelection.GiveWeapon(opponent, "weapon_knife");
                break;
        }
    }

    private static void SetupClassic(JbPlayer player, BattleState s)
    {
        var w = Jb.WeaponSelection.GiveWeapon(player, s.Weapon);
        if (w == null) return;
        var amount = w.ReserveAmmo[0];
        s.Reserve[w.EntityHandle.Raw] = amount < 1 ? 600 : amount;
    }

    /// <summary>SetCurrentTurn: the opponent loses their weapon, the shooter gets the weapon with 1 bullet / up to 5.</summary>
    private static void SetTurn(JbPlayer prisoner, JbPlayer shooter)
    {
        var s = S(prisoner);
        var other = shooter.Slot == prisoner.Slot ? Jb.LastRequests.GetOpponent(prisoner) : prisoner;
        if (other == null) return;
        s.TurnSlot = shooter.Slot;
        s.ShotsFired = 0;
        Jb.LastRequests.StripWeapons(other, false);
        var w = Jb.WeaponSelection.GiveWeapon(shooter, s.Weapon);
        if (w == null) return;
        if (s.Mode == BattleMode.ShotForShot)
        {
            s.ClipSize = 1;
            w.Clip1 = 1;
        }
        else
        {
            s.ClipSize = Math.Min(5, Math.Max(1, w.Clip1));
            w.Clip1 = s.ClipSize;
        }

        Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_iClip1");
        s.OldClip = s.ClipSize;
        var slot = Weapons.IsPistol(s.Weapon) ? "slot2" : Weapons.IsKnife(s.Weapon) ? "slot3" : Weapons.IsGrenade(s.Weapon) ? "slot4" : "slot1";
        var ctl = shooter.Controller;
        Server.NextFrame(() =>
        {
            if (ctl is { IsValid: true } && ctl.IsAliveP()) ctl.ExecuteClientCommand(slot);
        });
    }

    private void OnTick()
    {
        foreach (var p in Players.Connected())
        {
            var s = S(p);
            if (!s.Active || !s.Started) continue;
            var opponent = Jb.LastRequests.GetOpponent(p);
            if (opponent == null) continue;
            switch (s.Mode)
            {
                case BattleMode.Classic:
                    TopUp(p, s);
                    TopUp(opponent, s);
                    break;
                case BattleMode.NoScope:
                    NoScope(p);
                    NoScope(opponent);
                    break;
                case BattleMode.ShotForShot:
                case BattleMode.MagForMag:
                    var shooter = s.TurnSlot == p.Slot ? p : s.TurnSlot == opponent.Slot ? opponent : null;
                    if (shooter != null) CheckTurn(p, s, shooter, shooter.Slot == p.Slot ? opponent : p);
                    break;
            }
        }
    }

    private static void TopUp(JbPlayer player, BattleState s)
    {
        // Matched by entity handle: CS2 reports e.g. the USP-S as weapon_hkp2000, so names are unreliable.
        var w = player.Controller?.ActiveWeapon();
        if (w == null || !s.Reserve.TryGetValue(w.EntityHandle.Raw, out var amount)) return;
        if (w.ReserveAmmo[0] >= amount) return;
        w.ReserveAmmo[0] = amount;
        Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_pReserveAmmo");
    }

    private static void NoScope(JbPlayer player)
    {
        var w = player.Controller?.ActiveWeapon();
        if (w == null) return;
        if (w.NextSecondaryAttackTick != int.MaxValue)
        {
            w.NextSecondaryAttackTick = int.MaxValue;
            Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_nNextSecondaryAttackTick");
        }

        if (w.Clip1 != 99)
        {
            w.Clip1 = 99;
            Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_iClip1");
        }
    }

    private static void CheckTurn(JbPlayer prisoner, BattleState s, JbPlayer shooter, JbPlayer other)
    {
        var w = shooter.Controller?.ActiveWeapon();
        if (w == null)
        {
            SetTurn(prisoner, other);
            return;
        }

        var clip = w.Clip1;
        if (clip > s.OldClip)
        {
            s.OldClip = clip;
            return;
        }

        if (clip == s.OldClip) return;
        if (s.Mode == BattleMode.ShotForShot)
        {
            SetTurn(prisoner, other);
            return;
        }

        s.ShotsFired++;
        s.OldClip = clip;
        if (s.ShotsFired >= s.ClipSize) SetTurn(prisoner, other);
    }

    private HookResult OnDamage(DamageContext ctx)
    {
        // Knife Backstab: only backstabs count.
        var victim = ctx.Victim.Jb();
        if (victim == null || ctx.Attacker == null) return HookResult.Continue;
        var prisoner = ctx.Victim.Team == CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist ? victim : Jb.LastRequests.GetOpponent(victim);
        if (prisoner == null) return HookResult.Continue;
        var s = S(prisoner);
        if (!s.Active || s.Mode != BattleMode.KnifeBackstab || s.OpponentSlot < 0) return HookResult.Continue;
        return ctx.Damage < Cfg.BackstabDamage ? HookResult.Handled : HookResult.Continue;
    }

    private static void OnEnd(JbPlayer p, JbPlayer? opponent)
    {
        var s = S(p);
        if (!s.Active) return;
        var effect = s.EffectId;
        var hadOpponent = s.OpponentSlot >= 0;
        Reset(s);
        if (!hadOpponent) return;
        Cleanup(p, effect);
        if (opponent != null) Cleanup(opponent, effect);
    }

    private static void Cleanup(JbPlayer player, int effectId)
    {
        if (player.Controller is not { IsValid: true } c) return;
        if (c.IsAliveP()) Jb.LastRequests.RestoreWeapons(player);
        if (effectId > 0) Jb.Effects.StopEffect(player, effectId);
    }
}
