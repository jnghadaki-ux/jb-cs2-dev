using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Modules.Days;

/// <summary>Section "DaysExtra": settings of individual days.</summary>
public sealed class DaysExtraConfig
{
    /// <summary>Warday Jihad music (original jihad_day.mp3 needs a custom soundevent addon in CS2; empty = none).</summary>
    public string JihadMusic { get; set; } = "";
}

internal static class DayUtil
{
    public static IEnumerable<JbPlayer> AliveAll() => Players.Alive().Select(c => c.Jb()!).Where(p => p != null);

    public static IEnumerable<JbPlayer> AliveOn(CsTeam team) => Players.AliveOnTeam(team).Select(c => c.Jb()!).Where(p => p != null);

    /// <summary>GivePlayerWeapon + make it the active weapon (best effort: the client switches on the slot command).</summary>
    public static CBasePlayerWeapon? Give(JbPlayer p, string weapon, bool active = false)
    {
        var w = Jb.WeaponSelection.GiveWeapon(p, weapon);
        if (active) Select(p, weapon);
        return w;
    }

    public static void Select(JbPlayer p, string weapon)
    {
        var slot = Weapons.IsKnife(weapon) ? "slot3" : Weapons.IsPistol(weapon) ? "slot2" : Weapons.IsGrenade(weapon) ? "slot4" : "slot1";
        var c = p.Controller;
        Server.NextFrame(() =>
        {
            if (c is { IsValid: true } && c.IsAliveP()) c.ExecuteClientCommand(slot);
        });
    }

    public static void SetClip(CBasePlayerWeapon? w, int clip, int? reserve = null)
    {
        if (w == null || !w.IsValid) return;
        w.Clip1 = clip;
        Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_iClip1");
        if (reserve == null) return;
        w.ReserveAmmo[0] = reserve.Value;
        Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_pReserveAmmo");
    }

    public static CBasePlayerWeapon? WeaponInSlot(CCSPlayerController c, gear_slot_t slot) =>
        c.Weapons().FirstOrDefault(w => w.As<CCSWeaponBase>().VData?.GearSlot == slot);
}

/// <summary>
/// Ports of the smaller Days/*.sp plugins. Freedays: Classic, Auto Bhop, Low Gravity, Speed. Wardays: Gang, Jihad, Taser,
/// No Scope, Fat Kid, Low Gravity, Scoutz Knivez, Weapon Selection, Classic, Heavy Assault, One In The Chamber.
/// </summary>
public sealed class SimpleDays : JbModule
{
    private DaysExtraConfig Cfg => Section<DaysExtraConfig>("DaysExtra");

    public override string Name => "Days.Simple";
    public override int Order => 12;

    private const DayFlags PickupsDrops = DayFlags.AllowWeaponPickups | DayFlags.AllowWeaponDrops;
    private const DayFlags StripBoth = DayFlags.StripPrisonersWeapons | DayFlags.StripGuardsWeapons;

    private DayDefinition? _noScope, _scoutz, _oitc, _freeLowGrav, _warLowGrav, _speed, _classic;
    private string _selectedWeapon = "weapon_negev";
    private int _classicEffect;
    private int _classicSelectTime;
    private Timer? _buyTimer;
    private int _buyCountdown;

    private bool Active(DayDefinition? d) => d != null && ReferenceEquals(Jb.Days.Current, d);

    public override void OnLoad(bool hotReload)
    {
        if (Jb.Days is not DaysModule) return;
        var days = Jb.Days;

        // ---------------------------------------------------------- Freedays
        days.Register(new DayDefinition { Name = "Classic", Type = DayType.Freeday, Flags = PickupsDrops, OnStart = _ => { } });

        days.Register(new DayDefinition
        {
            Name = "Auto Bhop", Type = DayType.Freeday, Flags = PickupsDrops,
            OnStart = _ => Cvars.SetTemp("sv_autobunnyhopping", "1"),
            OnEnd = _ => Cvars.Restore("sv_autobunnyhopping")
        });

        _freeLowGrav = new DayDefinition
        {
            Name = "Low Gravity", Type = DayType.Freeday, Flags = PickupsDrops,
            OnStart = _ => { foreach (var p in DayUtil.AliveAll()) Movement.SetGravity(p.Controller, "day.gravity", 0.2f); },
            OnEnd = _ => Movement.ClearKeyAll("day.gravity")
        };
        days.Register(_freeLowGrav);

        _speed = new DayDefinition
        {
            Name = "Speed", Type = DayType.Freeday, Flags = PickupsDrops,
            OnStart = _ =>
            {
                foreach (var p in DayUtil.AliveOn(CsTeam.Terrorist)) Movement.SetSpeed(p.Controller, "day.speed", 2f);
                foreach (var p in DayUtil.AliveOn(CsTeam.CounterTerrorist)) Movement.SetSpeed(p.Controller, "day.speed", 1.5f);
            },
            OnEnd = _ => Movement.ClearKeyAll("day.speed")
        };
        days.Register(_speed);

        // ---------------------------------------------------------- Wardays
        days.Register(new DayDefinition
        {
            Name = "Gang", Type = DayType.Warday, AllowFreeForAll = true,
            Flags = StripBoth | DayFlags.GiveGuardsInfiniteAmmo | DayFlags.KillWorldWeapons,
            OnStart = _ => { },
            OnFreezeEnd = () =>
            {
                foreach (var p in DayUtil.AliveAll())
                {
                    if (p.Controller!.Team == CsTeam.CounterTerrorist)
                    {
                        DayUtil.Give(p, "weapon_usp_silencer");
                        DayUtil.Give(p, "weapon_knife");
                        DayUtil.Give(p, "weapon_m4a1_silencer", true);
                    }
                    else
                    {
                        DayUtil.Give(p, "weapon_glock");
                        DayUtil.Give(p, "weapon_knife_t");
                        DayUtil.Give(p, "weapon_ak47", true);
                    }
                }
            }
        });

        days.Register(new DayDefinition
        {
            Name = "Jihad", Type = DayType.Warday,
            Flags = StripBoth | DayFlags.KillWorldWeapons | DayFlags.DisableWeaponBuying | DayFlags.ForceFreeForAll,
            OnStart = _ => Jb.Jihad.SetAllowBombDropping(false),
            OnFreezeEnd = () =>
            {
                foreach (var p in DayUtil.AliveAll()) Jb.Jihad.SetJihad(p);
                if (Cfg.JihadMusic.Length > 0) Sound.PlayAll(Cfg.JihadMusic, 0.2f);
            },
            OnEnd = _ =>
            {
                Jb.Jihad.SetAllowBombDropping(true);
                foreach (var p in Players.Connected()) Jb.Jihad.ClearJihad(p);
            }
        });

        days.Register(new DayDefinition
        {
            Name = "Taser", Type = DayType.Warday, AllowFreeForAll = true,
            Flags = DayFlags.StripPrisonersWeapons | DayFlags.KillWorldWeapons,
            OnStart = _ =>
            {
                Cvars.SetTemp("mp_taser_recharge_time", "15");
                foreach (var p in DayUtil.AliveAll()) Jb.LastRequests.StripWeapons(p);
            },
            OnFreezeEnd = () => { foreach (var p in DayUtil.AliveAll()) DayUtil.Give(p, "weapon_taser", true); },
            OnEnd = _ => Cvars.Restore("mp_taser_recharge_time")
        });

        _noScope = new DayDefinition
        {
            Name = "No Scope", Type = DayType.Warday, AllowFreeForAll = true,
            Flags = StripBoth | DayFlags.KillWorldWeapons | DayFlags.GiveGuardsInfiniteAmmo | DayFlags.GivePrisonersInfiniteAmmo,
            OnStart = _ => { },
            OnFreezeEnd = () => { foreach (var p in DayUtil.AliveAll()) DayUtil.Give(p, "weapon_awp", true); }
        };
        days.Register(_noScope);

        days.Register(new DayDefinition
        {
            Name = "Fat Kid", Type = DayType.Warday,
            Flags = StripBoth | DayFlags.GiveGuardsInfiniteAmmo | DayFlags.KillWorldWeapons,
            OnStart = _ => { },
            OnFreezeEnd = () =>
            {
                var guards = Players.AliveGuards().Count();
                foreach (var p in DayUtil.AliveAll())
                {
                    if (p.Controller!.Team == CsTeam.CounterTerrorist)
                    {
                        DayUtil.Give(p, "weapon_m249", true);
                    }
                    else
                    {
                        DayUtil.Give(p, "weapon_knife_t", true);
                        Movement.SetSpeed(p.Controller, "day.fatkid", 0.8f);
                        Jb.LastRequests.SetHealth(p, 2000 + 100 * guards);
                    }
                }
            }
        });

        _warLowGrav = new DayDefinition
        {
            Name = "Low Gravity", Type = DayType.Warday,
            Flags = DayFlags.StripPrisonersWeapons | PickupsDrops | DayFlags.GiveGuardsInfiniteAmmo,
            OnStart = _ => { foreach (var p in DayUtil.AliveAll()) Movement.SetGravity(p.Controller, "day.gravity", 0.4f); },
            OnFreezeEnd = () => { foreach (var p in DayUtil.AliveOn(CsTeam.Terrorist)) DayUtil.Give(p, "weapon_knife_t", true); },
            OnEnd = _ => Movement.ClearKeyAll("day.gravity")
        };
        days.Register(_warLowGrav);

        _scoutz = new DayDefinition
        {
            Name = "Scoutz Knivez", Type = DayType.Warday, AllowFreeForAll = true,
            Flags = StripBoth | DayFlags.KillWorldWeapons,
            OnStart = _ => { },
            OnFreezeEnd = () =>
            {
                foreach (var p in DayUtil.AliveAll())
                {
                    var c = p.Controller!;
                    var w = DayUtil.Give(p, "weapon_ssg08", true);
                    DayUtil.SetClip(w, 10, 0);
                    DayUtil.Give(p, c.Team == CsTeam.CounterTerrorist ? "weapon_knife" : "weapon_knife_t");
                    Movement.SetGravity(c, "day.scoutz", 0.275f);
                    c.SetArmor(100, false);
                }
            },
            OnEnd = _ => Movement.ClearKeyAll("day.scoutz")
        };
        days.Register(_scoutz);

        days.Register(new DayDefinition
        {
            Name = "Weapon Selection", Type = DayType.Warday, AllowFreeForAll = true,
            Flags = StripBoth | DayFlags.GiveGuardsInfiniteAmmo | DayFlags.KillWorldWeapons,
            OnStart = starter =>
            {
                _selectedWeapon = "weapon_negev";
                if (starter == null) return;
                var filter = new WeaponFilter();
                filter.Disabled.UnionWith(new[] { "weapon_knife", "weapon_taser" });
                Jb.WeaponSelection.DisplaySelectionMenu(starter, (p, weapon) =>
                {
                    _selectedWeapon = weapon;
                    Jb.Log.LogEvent(Lang.T("days.weaponselection.log", p.Name, weapon), p, null, LogType.Any);
                }, null, filter);
            },
            OnFreezeEnd = () =>
            {
                foreach (var p in DayUtil.AliveAll())
                {
                    DayUtil.Give(p, _selectedWeapon, true);
                    DayUtil.Give(p, p.Controller!.Team == CsTeam.CounterTerrorist ? "weapon_knife" : "weapon_knife_t");
                }
            },
            OnEnd = starter => { foreach (var p in Players.Connected()) Jb.WeaponSelection.CancelSelection(p); }
        });

        _classic = new DayDefinition
        {
            Name = "Classic", Type = DayType.Warday,
            Flags = DayFlags.StripPrisonersWeapons | PickupsDrops | DayFlags.GiveGuardsInfiniteAmmo,
            OnStart = starter =>
            {
                _classicEffect = 0;
                if (starter == null) return;
                Jb.Effects.DisplaySelectionMenu(starter, (_, id) => _classicEffect = id, p =>
                {
                    p.Controller?.PrintToChat(" " + Lang.T("lr.battle.no_effect"));
                    _classicEffect = 0;
                }, _classicSelectTime);
            },
            OnFreezeEnd = () =>
            {
                foreach (var p in DayUtil.AliveOn(CsTeam.Terrorist)) DayUtil.Give(p, "weapon_knife_t", true);
                if (_classicEffect <= 0) return;
                var data = Jb.Effects.GetDefaultData(_classicEffect);
                foreach (var p in DayUtil.AliveAll()) Jb.Effects.StartEffect(p, _classicEffect, data);
            },
            OnEnd = _ =>
            {
                if (_classicEffect <= 0) return;
                foreach (var p in Players.Connected()) Jb.Effects.StopEffect(p, _classicEffect);
            }
        };
        days.Register(_classic);
        // Freeze time = the longer of the effect selection time and the warday freeze time (SetEffectSelectTimeAndFreezeTime).
        var effectTime = Plugin.Config.Section<Modules.Effects.EffectsConfig>("Effects").SelectEffectTime;
        _classicSelectTime = Math.Max(effectTime, _classic.FreezeTime);
        if (_classicSelectTime <= 0) _classicSelectTime = 10;
        _classic.FreezeTime = _classicSelectTime;

        days.Register(new DayDefinition
        {
            Name = "Heavy Assault", Type = DayType.Warday,
            Flags = DayFlags.StripPrisonersWeapons | PickupsDrops | DayFlags.StripGuardsWeapons | DayFlags.GiveGuardsInfiniteAmmo,
            OnStart = _ => { foreach (var p in DayUtil.AliveAll()) Jb.LastRequests.StripWeapons(p); },
            OnFreezeEnd = () =>
            {
                foreach (var p in DayUtil.AliveAll()) DayUtil.Give(p, "weapon_knife");
                Cvars.SetTemp("mp_weapons_allow_heavyassaultsuit", "1");
                foreach (var p in DayUtil.AliveOn(CsTeam.CounterTerrorist)) SetHeavyArmor(p.Controller!, true);
                StartBuyTimer();
            },
            OnEnd = _ =>
            {
                StopBuyTimer();
                Cvars.Restore("mp_weapons_allow_heavyassaultsuit");
                foreach (var p in DayUtil.AliveOn(CsTeam.CounterTerrorist)) SetHeavyArmor(p.Controller!, false);
            }
        });

        _oitc = new DayDefinition
        {
            Name = "One In The Chamber", Type = DayType.Warday, AllowFreeForAll = true,
            Flags = DayFlags.StripPrisonersWeapons | DayFlags.KillWorldWeapons,
            OnStart = _ => { foreach (var p in DayUtil.AliveAll()) Jb.LastRequests.StripWeapons(p); },
            OnFreezeEnd = () =>
            {
                foreach (var p in DayUtil.AliveAll())
                {
                    DayUtil.Give(p, "weapon_knife");
                    var w = DayUtil.Give(p, "weapon_usp_silencer", true);
                    DayUtil.SetClip(w, 1, 0);
                }
            }
        };
        days.Register(_oitc);

        JbEvents.DaySpawnPost += OnDaySpawn;
        DamageService.Register(Name, 90, OnDamage);
        Plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.DaySpawnPost -= OnDaySpawn;
        DamageService.Unregister(OnDamage);
        StopBuyTimer();
    }

    /// <summary>Players respawned during a day get the day's movement modifiers again (originals hooked every client).</summary>
    private void OnDaySpawn(JbPlayer p)
    {
        var c = p.Controller;
        if (c == null) return;
        if (Active(_freeLowGrav)) Movement.SetGravity(c, "day.gravity", 0.2f);
        else if (Active(_warLowGrav)) Movement.SetGravity(c, "day.gravity", 0.4f);
    }

    /// <summary>
    /// Heavy assault suit: CS2 has no m_bHasHeavyArmor schema field, so the real item_heavyassaultsuit is given (requires
    /// mp_weapons_allow_heavyassaultsuit 1, set by the day) and the armor value set like the original (250).
    /// Removing it sets armor 0; the suit model itself stays until the player respawns.
    /// </summary>
    private static void SetHeavyArmor(CCSPlayerController c, bool on)
    {
        if (on && c.IsAliveP())
        {
            try { c.GiveNamedItem("item_heavyassaultsuit"); }
            catch { /* item not available on this build - armor value still applies */ }
        }

        c.SetArmor(on ? 250 : 0, on);
    }

    private void StartBuyTimer()
    {
        StopBuyTimer();
        _buyCountdown = 0;
        Cvars.SetTemp("mp_buy_anywhere", "1");
        ShowBuyCountdown();
        _buyTimer = Plugin.AddTimer(1f, () =>
        {
            _buyCountdown++;
            if (_buyCountdown < 20)
            {
                ShowBuyCountdown();
                return;
            }

            StopBuyTimer();
            foreach (var c in Players.Humans()) Hud.Set(c, "days.buy", Lang.T("days.heavy.buy_over"), 60, 4);
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ShowBuyCountdown()
    {
        var html = Lang.T("days.heavy.buy_time", 20 - _buyCountdown);
        foreach (var c in Players.Humans()) Hud.Set(c, "days.buy", html, 60, 2);
    }

    private void StopBuyTimer()
    {
        if (_buyTimer == null) return;
        try { _buyTimer.Kill(); }
        catch { /* already stopped */ }
        _buyTimer = null;
        Cvars.Restore("mp_buy_anywhere");
    }

    private void OnTick()
    {
        if (!Active(_noScope)) return;
        foreach (var c in Players.Alive())
        {
            var w = c.ActiveWeapon();
            if (w == null || w.NextSecondaryAttackTick == int.MaxValue) continue;
            w.NextSecondaryAttackTick = int.MaxValue;
            Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_nNextSecondaryAttackTick");
        }
    }

    /// <summary>One In The Chamber: any bullet or knife hit from a player kills (freeday players excepted).</summary>
    private HookResult OnDamage(DamageContext ctx)
    {
        if (!Active(_oitc) || ctx.Attacker == null) return HookResult.Continue;
        var bits = ctx.Info.BitsDamageType;
        if ((bits & (DamageTypes_t.DMG_BULLET | DamageTypes_t.DMG_SLASH)) == 0) return HookResult.Continue;
        var a = ctx.Attacker.Jb();
        var v = ctx.Victim.Jb();
        if (a != null && (Jb.LastRequests.GetFlags(a) & LrFlags.Freeday) != 0) return HookResult.Continue;
        if (v != null && (Jb.LastRequests.GetFlags(v) & LrFlags.Freeday) != 0) return HookResult.Continue;
        ctx.Damage = 999f;
        return HookResult.Continue;
    }

    private HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        var attacker = ev.Attacker;
        if (attacker == null || !attacker.IsValid || attacker.Slot == ev.Userid?.Slot) return HookResult.Continue;
        var weapon = ev.Weapon ?? "";
        var prefix = weapon.Length >= 5 ? weapon[..5] : weapon;
        var knife = prefix is "knife" or "bayon";
        if (Active(_scoutz) && knife)
        {
            AddClip(attacker, gear_slot_t.GEAR_SLOT_RIFLE, 2);
        }
        else if (Active(_oitc))
        {
            if (knife) AddClip(attacker, gear_slot_t.GEAR_SLOT_PISTOL, 1);
            else if (prefix == "usp_s") AddClip(attacker, gear_slot_t.GEAR_SLOT_PISTOL, ev.Headshot ? 2 : 1);
        }

        return HookResult.Continue;
    }

    private static void AddClip(CCSPlayerController c, gear_slot_t slot, int amount)
    {
        Server.NextFrame(() =>
        {
            if (!c.IsValid) return;
            var w = DayUtil.WeaponInSlot(c, slot);
            if (w != null) DayUtil.SetClip(w, w.Clip1 + amount);
        });
    }
}
