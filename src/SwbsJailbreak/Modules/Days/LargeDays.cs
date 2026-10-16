using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.LastRequest;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Days;

/// <summary>Section "DaysLarge": assets and tuning of the larger wardays.</summary>
public sealed class DaysLargeConfig
{
    /// <summary>Hunger Games weapon box model (original wood_crate001a.mdl). Must be a CS2 .vmdl; verify on your build.</summary>
    public string WeaponBoxModel { get; set; } = "models/props/de_dust/hr_dust/dust_crates/dust_crate_style_01_32x32x32.vmdl";
    public string WeaponBoxSound { get; set; } = "Wood_Box.ImpactHard";
    public string HungerGamesMusic { get; set; } = "";

    /// <summary>Throwing Knives: model of the thrown knife (physics prop). Verify on your build.</summary>
    public string ThrowingKnifeModel { get; set; } = "weapons/models/knife/knife_default_ct/weapon_knife_default_ct.vmdl";
    public float ThrowingKnifeSpeed { get; set; } = 2250f;

    /// <summary>How much the thrown knife drops (fraction of sv_gravity 800); 0 = flies dead straight.</summary>
    public float ThrowingKnifeGravity { get; set; } = 0.4f;

    /// <summary>Seconds a knife flies before it is removed (it is removed earlier when it hits a wall or a player).</summary>
    public float ThrowingKnifeLifetime { get; set; } = 3f;
    public float ThrowingKnifeDamage { get; set; } = 100f;
    public string KnifeHitPlayerSound { get; set; } = "Weapon_Knife.Hit";
    public string KnifeHitWorldSound { get; set; } = "Weapon_Knife.HitWall";

    public int CtRetakeInvincibleTime { get; set; } = 60; // ultjb_ctretake_invincible_time (freeze length)

    /// <summary>Horror Movie movement sound (original custom cowbell.mp3 - needs a soundevent addon; empty = none).</summary>
    public string HorrorMoveSound { get; set; } = "";
}

internal sealed class ThrownKnife
{
    public uint Prop;
    public int OwnerSlot;
    public float Born;
    public Vector? LastPos;
    public int Tick;

    /// <summary>Flight state simulated by the plugin (the prop is a non-physics model moved every tick).</summary>
    public Vector Pos = new(0, 0, 0);
    public Vector Vel = new(0, 0, 0);
}

/// <summary>
/// Ports of Days/UltJB_Warday_{HungerGames, Zombie, ThrowingKnives, CTRetake, HorrorMovie}.sp.
/// CS2 differences (documented): weapon boxes and thrown knives are real props whose touches are detected by position
/// checks (no SDKHooks touch in CS2); Horror Movie fog uses the per-player fog controller handle, which may render
/// differently on CS2's fog system.
/// </summary>
public sealed class LargeDays : JbModule
{
    private DaysLargeConfig Cfg => Section<DaysLargeConfig>("DaysLarge");

    public override string Name => "Days.Large";
    public override int Order => 12;

    private DayDefinition? _hunger, _zombie, _knives, _retake, _horror;
    private bool Active(DayDefinition? d) => d != null && ReferenceEquals(Jb.Days.Current, d);

    // hunger games
    private readonly List<uint> _boxes = new();

    // zombie
    private float _zombieSpeed = 1.2f;
    private Timer? _zombieMsgTimer, _zombieSpeedTimer;
    private int _zombieMsg;

    // knives
    private readonly List<ThrownKnife> _thrown = new();

    // retake
    private Timer? _retakeFreezeTimer, _retakeInvincibleTimer;
    private int _retakeCountdown;
    private bool _guardsInvincible;

    // horror
    private uint _fogGuards, _fogPrisoners;
    private float _guardFogDensity = 1f, _prisonerSpeed = 0.5f;
    private Timer? _buffTimer;
    private readonly Dictionary<int, uint> _savedFog = new();
    private readonly float[] _nextMoveSound = new float[Players.MaxSlots];

    public override void OnLoad(bool hotReload)
    {
        if (Jb.Days is not DaysModule) return;
        var days = Jb.Days;

        _hunger = new DayDefinition
        {
            Name = "Hunger Games", Type = DayType.Warday,
            Flags = DayFlags.AllowWeaponPickupsFromDay | DayFlags.AllowWeaponDrops | DayFlags.StripGuardsWeapons | DayFlags.StripPrisonersWeapons |
                    DayFlags.KillWorldWeapons | DayFlags.DisablePrisonersRadar | DayFlags.DisableGuardsRadar | DayFlags.ForceFreeForAll,
            OnStart = _ =>
            {
                Chat.PrintAll(Lang.T("days.hunger.warning"), false);
                Server.NextFrame(() => { if (Active(_hunger)) SpawnWeaponBoxes(); });
            },
            OnFreezeEnd = () =>
            {
                if (Cfg.HungerGamesMusic.Length > 0) Sound.PlayAll(Cfg.HungerGamesMusic);
                foreach (var p in DayUtil.AliveAll())
                    DayUtil.Give(p, p.Controller!.Team == CsTeam.Terrorist ? "weapon_knife_t" : "weapon_knife", true);
            },
            OnEnd = _ => RemoveBoxes()
        };
        days.Register(_hunger);
        _hunger.FreezeTime = 5;
        _hunger.FreezeTeams = FreezeTeams.Guards | FreezeTeams.Prisoners;

        _zombie = new DayDefinition
        {
            Name = "Zombie", Type = DayType.Warday,
            Flags = DayFlags.StripPrisonersWeapons | DayFlags.StripGuardsWeapons | DayFlags.GiveGuardsInfiniteAmmo | DayFlags.KillWorldWeapons,
            OnStart = _ =>
            {
                Chat.PrintAll(Lang.T("days.zombie.infected"), false);
                _zombieMsg = 0;
                Kill(ref _zombieMsgTimer);
                _zombieMsgTimer = Plugin.AddTimer(1f, ZombieMessage, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
                _zombieSpeed = 1.2f;
                Kill(ref _zombieSpeedTimer);
                _zombieSpeedTimer = Plugin.AddTimer(45f, ZombieTired, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
            },
            OnFreezeEnd = () =>
            {
                foreach (var p in DayUtil.AliveAll())
                {
                    var c = p.Controller!;
                    if (c.Team == CsTeam.CounterTerrorist)
                    {
                        DayUtil.Give(p, "weapon_knife");
                        DayUtil.Give(p, "weapon_deagle", true);
                    }
                    else
                    {
                        c.SetArmor(100, null);
                        DayUtil.Give(p, "weapon_knife_t", true);
                        Movement.SetSpeed(c, "day.zombie", 1.2f);
                    }
                }
            },
            OnEnd = _ =>
            {
                Movement.ClearKeyAll("day.zombie");
                Kill(ref _zombieMsgTimer);
                Kill(ref _zombieSpeedTimer);
            }
        };
        days.Register(_zombie);

        _knives = new DayDefinition
        {
            Name = "Throwing Knives", Type = DayType.Warday, AllowFreeForAll = true,
            Flags = DayFlags.StripPrisonersWeapons | DayFlags.StripGuardsWeapons | DayFlags.KillWorldWeapons,
            OnStart = _ => _thrown.Clear(),
            OnFreezeEnd = () =>
            {
                foreach (var p in DayUtil.AliveAll())
                {
                    var c = p.Controller!;
                    DayUtil.Give(p, c.Team == CsTeam.Terrorist ? "weapon_knife_t" : "weapon_knife", true);
                    c.SetArmor(0, false);
                }
            },
            OnEnd = _ =>
            {
                foreach (var k in _thrown) Fx.SafeRemove(new CHandle<CBaseEntity>(k.Prop).Value);
                _thrown.Clear();
            }
        };
        days.Register(_knives);

        _retake = new DayDefinition
        {
            Name = "CT Retake", Type = DayType.Warday,
            Flags = DayFlags.StripGuardsWeapons | DayFlags.AllowWeaponPickups | DayFlags.AllowWeaponDrops,
            OnStart = _ =>
            {
                foreach (var g in Players.AliveGuards())
                {
                    g.SetMoveType(MoveType_t.MOVETYPE_NONE);
                    Fx.Fade(g, Color.FromArgb(255, 0, 0, 0), 1.5f, 1.5f, Fx.FadeOut | Fx.FadeStayOut);
                }

                _guardsInvincible = true;
                _retakeCountdown = 0;
                ShowRetakeCountdown();
                Kill(ref _retakeFreezeTimer);
                _retakeFreezeTimer = Plugin.AddTimer(1f, RetakeTick, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
                Kill(ref _retakeInvincibleTimer);
                _retakeInvincibleTimer = Plugin.AddTimer(70f, () =>
                {
                    _retakeInvincibleTimer = null;
                    _guardsInvincible = false;
                    foreach (var c in Players.Humans()) Hud.Set(c, "days.retake", Lang.T("days.retake.vulnerable"), 60, 4);
                }, TimerFlags.STOP_ON_MAPCHANGE);
            },
            OnEnd = _ =>
            {
                Kill(ref _retakeFreezeTimer);
                Kill(ref _retakeInvincibleTimer);
                _guardsInvincible = false;
            }
        };
        days.Register(_retake);
        _retake.FreezeTime = 0; // UltJB_Day_SetFreezeTime(iDayID, 0)

        _horror = new DayDefinition
        {
            Name = "Horror Movie", Type = DayType.Warday, AllowFreeForAll = true,
            Flags = DayFlags.DisableGuardsRadar | DayFlags.StripPrisonersWeapons | DayFlags.StripGuardsWeapons | DayFlags.KillWorldWeapons,
            OnStart = _ =>
            {
                Cvars.SetTemp("sv_footsteps", "0");
                _prisonerSpeed = 0.5f;
                _guardFogDensity = 1f;
            },
            OnFreezeEnd = HorrorFreezeEnd,
            OnEnd = _ =>
            {
                Kill(ref _buffTimer);
                RemoveFog();
                Cvars.Restore("sv_footsteps");
                Movement.ClearKeyAll("day.horror");
                foreach (var p in Players.Connected()) p.Controller?.ResetColor();
            }
        };
        days.Register(_horror);

        DamageService.Register(Name, 90, OnDamage);
        Plugin.RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        Plugin.RegisterListener<Listeners.OnServerPrecacheResources>(m =>
        {
            if (!string.IsNullOrWhiteSpace(Cfg.WeaponBoxModel)) m.AddResource(Cfg.WeaponBoxModel);
            if (!string.IsNullOrWhiteSpace(Cfg.ThrowingKnifeModel)) m.AddResource(Cfg.ThrowingKnifeModel);
        });
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        Kill(ref _zombieMsgTimer);
        Kill(ref _zombieSpeedTimer);
        Kill(ref _retakeFreezeTimer);
        Kill(ref _retakeInvincibleTimer);
        Kill(ref _buffTimer);
        RemoveBoxes();
        RemoveFog();
    }

    private static void Kill(ref Timer? t)
    {
        try { t?.Kill(); }
        catch { /* already stopped */ }
        t = null;
    }

    // ================================================================== Hunger Games

    private void SpawnWeaponBoxes()
    {
        var points = (Jb.LastRequests as LastRequestModule)?.RebelPointList ?? Array.Empty<float[]>();
        var pts = points.Where(p => p.Length >= 3).Select(p => new Vector(p[0], p[1], p[2])).ToList();
        if (pts.Count == 0) return;
        var onTeams = Players.All().Count(c => c.Team >= CsTeam.Terrorist);
        var count = Math.Max(20, (int)MathF.Round(onTeams * 1.3f)) + (int)MathF.Round(pts.Count * 0.02f);

        // GetNextFurthestPoint: farthest point sampling over the rebel points.
        var chosen = new List<Vector>();
        var remaining = new List<Vector>(pts);
        while (chosen.Count < count && remaining.Count > 0)
        {
            var next = chosen.Count == 0 ? remaining[Random.Shared.Next(remaining.Count)]
                : remaining.OrderByDescending(p => chosen.Min(c => VectorUtil.Distance(p, c))).First();
            remaining.Remove(next);
            chosen.Add(next);
        }

        foreach (var at in chosen)
        {
            try
            {
                var box = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic_override");
                if (box == null) continue;
                box.Collision.SolidType = SolidType_t.SOLID_NONE;
                Fx.SpawnWithModel(box, Cfg.WeaponBoxModel); // model as a spawn key value: no SetupModel staging assertion
                box.Teleport(new Vector(at.X, at.Y, at.Z + 10f), new QAngle(0, Random.Shared.NextSingle() * 360f, 0), new Vector(0, 0, 0));
                box.Glow.GlowColorOverride = Color.Orange;
                box.Glow.GlowType = 3;
                box.Glow.GlowRange = 1024;
                box.Glow.GlowTeam = -1;
                _boxes.Add(box.EntityHandle.Raw);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[Days] weapon box could not be created - check DaysLarge.WeaponBoxModel");
                return;
            }
        }
    }

    private void RemoveBoxes()
    {
        foreach (var raw in _boxes) Fx.SafeRemove(new CHandle<CBaseEntity>(raw).Value);
        _boxes.Clear();
    }

    private void HungerTick()
    {
        if (_boxes.Count == 0) return;
        foreach (var raw in _boxes.ToList())
        {
            var box = new CHandle<CBaseEntity>(raw).Value;
            if (box?.AbsOrigin == null || !box.IsValid)
            {
                _boxes.Remove(raw);
                continue;
            }

            var o = box.AbsOrigin;
            foreach (var c in Players.Alive())
            {
                var po = c.PawnOrNull()?.AbsOrigin;
                if (po == null) continue;
                // original trigger box: 30 x 30 x 38 units around the crate (+ player hull)
                if (Math.Abs(po.X - o.X) > 31f || Math.Abs(po.Y - o.Y) > 31f || po.Z < o.Z - 90f || po.Z > o.Z + 20f) continue;
                _boxes.Remove(raw);
                SpawnBoxWeapon(new Vector(o.X, o.Y, o.Z + 10f));
                if (Cfg.WeaponBoxSound.Length > 0) box.EmitSound(Cfg.WeaponBoxSound);
                box.AcceptInput("Kill");
                break;
            }
        }
    }

    private void SpawnBoxWeapon(Vector at)
    {
        var filter = new WeaponFilter();
        filter.Disabled.UnionWith(new[] { "weapon_awp", "weapon_g3sg1", "weapon_scar20", "weapon_knife" });
        var weapon = Jb.WeaponSelection.GetRandomWeapon(filter);
        if (weapon == null) return;
        try
        {
            var w = Utilities.CreateEntityByName<CBasePlayerWeapon>(weapon);
            if (w == null) return;
            Jb.Days.SetEntityAsSpawnedByDay(w);
            w.DispatchSpawn();
            w.Teleport(at, null, new Vector(0, 0, 0));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[Days] box weapon {Weapon} failed", weapon);
        }
    }

    // ================================================================== Zombie

    private void ZombieMessage()
    {
        _zombieMsg++;
        if (_zombieMsg >= 3)
        {
            Kill(ref _zombieMsgTimer);
            return;
        }

        Chat.PrintAll(Lang.T(_zombieMsg == 1 ? "days.zombie.headshots" : "days.zombie.knife"), false);
    }

    private void ZombieTired()
    {
        _zombieSpeed = Math.Max(0.5f, _zombieSpeed - 0.1f);
        Chat.PrintAll(Lang.T("days.zombie.tired"), false);
        foreach (var c in Players.AlivePrisoners()) Movement.SetSpeed(c, "day.zombie", _zombieSpeed);
    }

    private HookResult OnDamage(DamageContext ctx)
    {
        if (Active(_zombie) && Jb.Days.FreezeTimeRemaining <= 0 && ctx.Attacker != null)
        {
            var attackerKnife = Weapons.IsKnife(ctx.Attacker.ActiveWeapon()?.DesignerName ?? "");
            if (ctx.Victim.Team == CsTeam.CounterTerrorist)
            {
                if (!attackerKnife) return HookResult.Handled;
                ctx.Damage = 1337f;
            }
            else if (ctx.Victim.Team == CsTeam.Terrorist)
            {
                if (ctx.HitGroup == HitGroup_t.HITGROUP_HEAD) ctx.Damage = 250f;
                else if (!attackerKnife) return HookResult.Handled;
            }

            return HookResult.Continue;
        }

        if (Active(_retake) && _guardsInvincible && ctx.Victim.Team == CsTeam.CounterTerrorist && ctx.Attacker != null)
        {
            var a = ctx.Attacker.Jb();
            var v = ctx.Victim.Jb();
            if (a != null && (Jb.LastRequests.GetFlags(a) & LrFlags.Freeday) != 0) return HookResult.Continue;
            if (v != null && (Jb.LastRequests.GetFlags(v) & LrFlags.Freeday) != 0) return HookResult.Continue;
            return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    // ================================================================== Throwing Knives

    private static bool IsLrFreeday(CCSPlayerController c) => c.Jb() is { } p && (Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) != 0;

    private HookResult OnWeaponFire(EventWeaponFire ev, GameEventInfo info)
    {
        if (!Active(_knives)) return HookResult.Continue;
        var c = ev.Userid;
        var weapon = ev.Weapon ?? "";
        if (c == null || !c.IsValid || !c.IsAliveP() || weapon.Length < 8) return HookResult.Continue;
        var w = weapon.Length >= 12 ? weapon[7..12] : weapon[7..];
        if (w != "knife" && w != "bayon") return HookResult.Continue;
        if (IsLrFreeday(c)) return HookResult.Continue; // freeday LR players take no part (the original's TakeDamage applied LR rules)
        if (!ThrowKnife(c)) return HookResult.Continue;
        var delay = Jb.Days.IsFreeForAll || c.Team == CsTeam.Terrorist ? 1f : 1.2f;
        var knife = c.ActiveWeapon();
        if (knife != null)
        {
            var next = Server.TickCount + (int)(delay * 64f);
            knife.NextPrimaryAttackTick = next;
            knife.NextSecondaryAttackTick = next;
            Utilities.SetStateChanged(knife, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
            Utilities.SetStateChanged(knife, "CBasePlayerWeapon", "m_nNextSecondaryAttackTick");
        }

        return HookResult.Continue;
    }

    private bool ThrowKnife(CCSPlayerController c)
    {
        var pawn = c.PawnOrNull();
        if (pawn == null || string.IsNullOrWhiteSpace(Cfg.ThrowingKnifeModel)) return false;
        try
        {
            // A non-physics model moved by the plugin, like the RPG rockets. The old physics prop was removed after
            // ~0.1 s: CS2 does not keep a physics prop's AbsVelocity up to date, so its "slowed down = hit a wall" check
            // fired straight away and knives only flew a couple of metres.
            var eye = c.EyePosition();
            var fwd = VectorUtil.Forward(pawn.EyeAngles);
            var start = new Vector(eye.X + fwd.X * 16f, eye.Y + fwd.Y * 16f, eye.Z + fwd.Z * 16f);
            var vel = new Vector(fwd.X * Cfg.ThrowingKnifeSpeed, fwd.Y * Cfg.ThrowingKnifeSpeed, fwd.Z * Cfg.ThrowingKnifeSpeed);
            var a = pawn.EyeAngles;
            var prop = Fx.Prop(Cfg.ThrowingKnifeModel, start, new QAngle(a.X, a.Y, 0));
            if (prop == null) return false;
            _thrown.Add(new ThrownKnife
            {
                Prop = prop.EntityHandle.Raw, OwnerSlot = c.Slot, Born = (float)Server.EngineTime, LastPos = start, Pos = start, Vel = vel
            });
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Days] throwing knife failed - check DaysLarge.ThrowingKnifeModel");
            return false;
        }
    }

    private void KnivesTick()
    {
        if (_thrown.Count == 0) return;
        var now = (float)Server.EngineTime;
        foreach (var k in _thrown.ToList())
        {
            var prop = new CHandle<CBaseEntity>(k.Prop).Value;
            if (prop?.AbsOrigin == null || !prop.IsValid)
            {
                _thrown.Remove(k);
                continue;
            }

            var owner = Players.Get(k.OwnerSlot).Controller;
            // Fly one tick: slight drop, spin, and a world trace along the segment so walls stop it where it hits.
            var dt = Server.TickInterval;
            k.Vel = new Vector(k.Vel.X, k.Vel.Y, k.Vel.Z - 800f * Math.Max(0f, Cfg.ThrowingKnifeGravity) * dt);
            var from = k.Pos;
            var pos = new Vector(from.X + k.Vel.X * dt, from.Y + k.Vel.Y * dt, from.Z + k.Vel.Z * dt);
            var ownerPawn = owner is { IsValid: true } ? owner.PawnOrNull() : null;
            var wall = ownerPawn != null ? Aim.WorldHit(from, pos, ownerPawn) : null;
            if (wall != null) pos = wall;
            k.Pos = pos;
            var yaw = MathF.Atan2(k.Vel.Y, k.Vel.X) * 180f / MathF.PI;
            prop.Teleport(pos, new QAngle((k.Tick * 40f) % 360f, yaw, 0), null);

            // Trail: a short real beam along the flight path every few ticks.
            if (++k.Tick % 4 == 0 && k.LastPos != null)
            {
                Fx.Beam(k.LastPos, pos, Color.FromArgb(117, 177, 177, 177), 1.5f, 0.5f);
                k.LastPos = pos;
            }

            // Player hit: within ~24 units of the body centre.
            CCSPlayerController? hit = null;
            foreach (var c in Players.Alive())
            {
                if (owner != null && c.Slot == owner.Slot) continue;
                if (!Jb.Days.IsFreeForAll && owner != null && c.Team == owner.Team) continue;
                if (IsLrFreeday(c)) continue;
                var po = c.PawnOrNull()?.AbsOrigin;
                if (po == null) continue;
                if (VectorUtil.Distance(pos, new Vector(po.X, po.Y, po.Z + 36f)) <= 36f && Math.Abs(pos.Z - (po.Z + 36f)) <= 40f)
                {
                    hit = c;
                    break;
                }
            }

            if (hit != null)
            {
                if (Cfg.KnifeHitPlayerSound.Length > 0) prop.EmitSound(Cfg.KnifeHitPlayerSound);
                var pawn = hit.PawnOrNull()!;
                var dmg = (int)Cfg.ThrowingKnifeDamage;
                if (pawn.Health <= dmg) DamageService.KillWithCredit(hit, owner, "knife");
                else
                {
                    pawn.Health -= dmg;
                    Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
                }

                prop.AcceptInput("Kill");
                _thrown.Remove(k);
                continue;
            }

            // World hit (traced above) or flew too long.
            if (wall != null || now - k.Born > Math.Max(0.5f, Cfg.ThrowingKnifeLifetime))
            {
                if (Cfg.KnifeHitWorldSound.Length > 0) prop.EmitSound(Cfg.KnifeHitWorldSound);
                Fx.Particle("particles/impact_fx/impact_spark.vpcf", pos, 1f);
                prop.AcceptInput("Kill");
                _thrown.Remove(k);
            }
        }
    }

    // ================================================================== CT Retake

    private void ShowRetakeCountdown()
    {
        var html = Lang.T("days.retake.unfreeze_in", Cfg.CtRetakeInvincibleTime - _retakeCountdown);
        foreach (var c in Players.Humans()) Hud.Set(c, "days.retake", html, 60, 2);
    }

    private void RetakeTick()
    {
        _retakeCountdown++;
        switch (_retakeCountdown)
        {
            case 5:
                Chat.PrintAll(Lang.T("days.retake.line"), false);
                Chat.PrintAll(Lang.T("days.retake.msg1", Cfg.CtRetakeInvincibleTime), false);
                break;
            case 6: Chat.PrintAll(Lang.T("days.retake.msg2"), false); break;
            case 7: Chat.PrintAll(Lang.T("days.retake.msg3"), false); break;
            case 8: Chat.PrintAll(Lang.T("days.retake.msg4"), false); break;
            case 9: Chat.PrintAll(Lang.T("days.retake.line"), false); break;
        }

        if (_retakeCountdown < Cfg.CtRetakeInvincibleTime)
        {
            ShowRetakeCountdown();
            return;
        }

        Kill(ref _retakeFreezeTimer);
        foreach (var g in Players.AliveGuards())
        {
            g.SetMoveType(MoveType_t.MOVETYPE_WALK);
            Fx.Fade(g, Color.FromArgb(0, 0, 0, 0), 1.5f, 0f, Fx.FadeIn | Fx.FadePurge);
        }

        foreach (var c in Players.Humans()) Hud.Set(c, "days.retake", Lang.T("days.retake.unfrozen"), 60, 4);
        foreach (var p in DayUtil.AliveAll())
        {
            var c = p.Controller!;
            if (c.Weapons().Any(w => Weapons.IsKnife(w.DesignerName))) continue;
            DayUtil.Give(p, c.Team == CsTeam.CounterTerrorist ? "weapon_knife" : "weapon_knife_t", true);
        }
    }

    // ================================================================== Horror Movie

    private void HorrorFreezeEnd()
    {
        var ffa = Jb.Days.IsFreeForAll;
        _fogGuards = CreateFog(0f, 120f, _guardFogDensity);
        _fogPrisoners = CreateFog(0f, 0f, 0.97f);
        foreach (var p in DayUtil.AliveAll())
        {
            var c = p.Controller!;
            var guard = c.Team == CsTeam.CounterTerrorist;
            if (ffa || !guard)
            {
                if (ffa) Jb.Models.ApplyPrisonerModel(p);
                c.SetColor(Color.FromArgb(255, 25, 25, 25));
                Movement.SetGravity(c, "day.horror", 0.6f);
            }

            SetFog(c, ffa || guard ? _fogGuards : _fogPrisoners);
            if (ffa || guard) DayUtil.Give(p, "weapon_knife");
            if (ffa || guard) DayUtil.Give(p, "weapon_sawedoff", true);
            else DayUtil.Give(p, "weapon_knife_t", true);
            Movement.SetSpeed(c, "day.horror", ffa ? 0.8f : guard ? 1f : _prisonerSpeed);
        }

        Kill(ref _buffTimer);
        _buffTimer = Plugin.AddTimer(45f, () =>
        {
            _guardFogDensity = Math.Max(0.1f, _guardFogDensity - 0.00025f);
            var fog = new CHandle<CFogController>(_fogGuards).Value;
            if (fog != null && fog.IsValid)
            {
                fog.Fog.Maxdensity = _guardFogDensity;
                Utilities.SetStateChanged(fog, "CFogController", "m_fog");
            }

            if (Jb.Days.IsFreeForAll)
            {
                foreach (var c in Players.Humans()) Hud.Set(c, "days.horror", Lang.T("days.horror.vision_all"), 60, 4);
                return;
            }

            _prisonerSpeed += 0.07f;
            foreach (var c in Players.AlivePrisoners()) Movement.SetSpeed(c, "day.horror", _prisonerSpeed);
            foreach (var c in Players.Humans()) Hud.Set(c, "days.horror", Lang.T("days.horror.buff"), 60, 4);
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private uint CreateFog(float start, float end, float density)
    {
        try
        {
            var fog = Utilities.CreateEntityByName<CFogController>("env_fog_controller");
            if (fog == null) return 0;
            fog.DispatchSpawn();
            fog.Fog.Enable = true;
            fog.Fog.Blend = false;
            fog.Fog.ColorPrimary = Color.FromArgb(255, 25, 25, 25);
            fog.Fog.Start = start;
            fog.Fog.End = end;
            fog.Fog.Maxdensity = density;
            Utilities.SetStateChanged(fog, "CFogController", "m_fog");
            return fog.EntityHandle.Raw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Days] fog controller could not be created - Horror Movie runs without fog");
            return 0;
        }
    }

    private void SetFog(CCSPlayerController c, uint fogRaw)
    {
        var cam = c.PawnOrNull()?.CameraServices;
        if (cam == null || fogRaw == 0) return;
        try
        {
            var fog = cam.PlayerFog;
            if (!_savedFog.ContainsKey(c.Slot)) _savedFog[c.Slot] = fog.Ctrl.Raw;
            fog.Ctrl.Raw = fogRaw;
            Utilities.SetStateChanged(c.PawnOrNull()!, "CBasePlayerPawn", "m_pCameraServices");
        }
        catch
        {
            // fog is cosmetic
        }
    }

    private void RemoveFog()
    {
        foreach (var (slot, raw) in _savedFog)
        {
            var c = Players.Get(slot).Controller;
            var cam = c?.PawnOrNull()?.CameraServices;
            if (cam == null) continue;
            try
            {
                cam.PlayerFog.Ctrl.Raw = raw;
                Utilities.SetStateChanged(c!.PawnOrNull()!, "CBasePlayerPawn", "m_pCameraServices");
            }
            catch
            {
                // ignore
            }
        }

        _savedFog.Clear();
        Fx.SafeRemove(new CHandle<CBaseEntity>(_fogGuards).Value);
        Fx.SafeRemove(new CHandle<CBaseEntity>(_fogPrisoners).Value);
        _fogGuards = _fogPrisoners = 0;
    }

    private void HorrorTick()
    {
        if (Cfg.HorrorMoveSound.Length == 0 || Jb.Days.FreezeTimeRemaining > 0) return;
        var now = (float)Server.EngineTime;
        var ffa = Jb.Days.IsFreeForAll;
        foreach (var c in Players.Alive())
        {
            if (!ffa && c.Team != CsTeam.Terrorist) continue;
            var pawn = c.PawnOrNull();
            if (pawn == null) continue;
            var speed = MathF.Min(170f, pawn.AbsVelocity.Length());
            if (speed < 10f || now < _nextMoveSound[c.Slot]) continue;
            var t = (speed - 10f) / 160f;
            _nextMoveSound[c.Slot] = now + 1.7f + (0.9f - t * 0.9f);
            Sound.PlayAt(c, Cfg.HorrorMoveSound);
        }
    }

    // ================================================================== tick

    private void OnTick()
    {
        if (Active(_hunger)) HungerTick();
        else if (Active(_knives)) KnivesTick();
        else if (Active(_horror)) HorrorTick();
    }
}
