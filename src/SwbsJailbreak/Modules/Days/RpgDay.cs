using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Days;

/// <summary>Section "Rpg" (ultjb_warday_rpg_* cvars + assets).</summary>
public sealed class RpgConfig
{
    public bool Enabled { get; set; } = true;
    public float MaxDamage { get; set; } = 30f;              // ultjb_warday_rpg_max_damage
    public float GuardDamageMultiplier { get; set; } = 1.5f; // ultjb_warday_rpg_guard_damage_multiplier
    public float RocketSpeed { get; set; } = 1000f;
    public string ExplosionParticle { get; set; } = "particles/explosions_fx/explosion_basic.vpcf";
    public string ExplosionSound { get; set; } = "BaseGrenade.Explode";
    public string ShootSound { get; set; } = "Weapon_Nova.Single";
    public string AirshotSound { get; set; } = "UIPanorama.round_report_round_won";
}

internal sealed class Rocket
{
    public int OwnerSlot;
    public Vector Pos = new(0, 0, 0);
    public Vector Vel = new(0, 0, 0);
    public float Born;
    public int Tick;
}

internal sealed class RpgPlayer
{
    public int Clip = 4;
    public bool Reloading;
    public float ReloadEnd, NextAttack;
    public uint Launcher;
}

/// <summary>
/// Port of Days/UltJB_Warday_RPG.sp (defrag-style rocket jumping): the knife becomes a 4-rocket launcher (0.8 s per shot,
/// 1.25 s per reloaded rocket), rockets push everyone within 115 units, deal up to 30 damage (self quarter, guards x1.5,
/// airshots kill), no fall damage. The custom launcher/missile models and sounds cannot load in CS2: rockets are real
/// beam trails with a CS2 explosion particle, and the rocket count is shown in the HUD.
/// Rockets explode where their flight segment hits world geometry, found with the CounterStrikeSharp 1.0.376 ray trace
/// (<see cref="Aim.WorldHit"/>); on 1.0.368 there was no trace and the day stayed unregistered.
/// </summary>
public sealed class RpgDay : JbModule
{
    private RpgConfig Cfg => Section<RpgConfig>("Rpg");

    public override string Name => "Days.Rpg";
    public override int Order => 12;

    private const float PushForceBase = 102f, PushRadius = 115f, PushMultiplier = 6f, PushFalloff = 0.55f, PushMinDistance = 32f;
    private const int MaxClip = 4;

    private DayDefinition? _day;
    private bool _inProgress;
    private readonly List<Rocket> _rockets = new();

    private bool Active => _day != null && ReferenceEquals(Jb.Days.Current, _day);
    private static RpgPlayer S(JbPlayer p) => p.Data<RpgPlayer>();

    public override void OnLoad(bool hotReload)
    {
        _instance = this;
        var dayOn = Jb.Days is DaysModule && Cfg.Enabled;
        var lrOn = Jb.LastRequests is LastRequest.LastRequestModule && Section<LastRequest.LrExtraConfig>("LastRequestsExtra").RocketeerEnabled;
        if (!dayOn && !lrOn) return;

        if (dayOn)
        {
            _day = new DayDefinition
            {
                Name = "RPG", Type = DayType.Warday, AllowFreeForAll = true,
                Flags = DayFlags.StripPrisonersWeapons | DayFlags.StripGuardsWeapons | DayFlags.KillWorldWeapons,
                OnStart = _ => _rockets.Clear(),
                OnFreezeEnd = () =>
                {
                    _inProgress = true;
                    foreach (var p in DayUtil.AliveAll())
                    {
                        if ((Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) != 0) DayUtil.Give(p, "weapon_knife");
                        else Prepare(p);
                    }
                },
                OnEnd = _ =>
                {
                    if (!_inProgress) return;
                    _inProgress = false;
                    _rockets.Clear();
                    foreach (var p in DayUtil.AliveAll()) Jb.LastRequests.StripWeapons(p);
                }
            };
            Jb.Days.Register(_day);
            JbEvents.DaySpawnPost += OnDaySpawn;
        }

        if (lrOn)
        {
            // Rocketeer (Rebel, last prisoner only): the RPG launcher against every guard.
            var hp = Section<LastRequest.LrExtraConfig>("LastRequestsExtra").RocketeerHealth;
            Jb.LastRequests.Register(new LastRequestDefinition
            {
                Name = "Rocketeer", Category = "Rebel",
                Description = "The RPG rocket launcher (knife slot) against every guard: rocket jumps, airshots kill.",
                Flags = LrFlags.Rebel | LrFlags.LastPrisonerOnlyCanUse | LrFlags.TempInvincible | LrFlags.NoRadar | LrFlags.RandomTeleportLocation,
                OnStart = p =>
                {
                    Jb.LastRequests.SetHealth(p, Math.Max(1, hp));
                    Jb.LastRequests.StripWeapons(p, true);
                    _armed.Add(p.Slot);
                    Prepare(p);
                },
                OnEnd = (p, _) =>
                {
                    _armed.Remove(p.Slot);
                    if (_armed.Count == 0 && !(Active && _inProgress)) _rockets.Clear();
                    Hud.Clear(p.Controller, "days.rpg");
                    LastRequest.SimpleLastRequests.RestoreIfAlive(p);
                }
            });
        }

        DamageService.Register(Name, 90, OnDamage);
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        JbEvents.DaySpawnPost -= OnDaySpawn;
        _armed.Clear();
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    public override void OnPlayerDisconnect(JbPlayer player) => _armed.Remove(player.Slot);

    private static RpgDay? _instance;

    /// <summary>Players holding the launcher outside the day (Rocketeer LR).</summary>
    private readonly HashSet<int> _armed = new();

    private bool DayRunning => Active && _inProgress;

    private bool Launches(JbPlayer p) => DayRunning || _armed.Contains(p.Slot);

    private void OnDaySpawn(JbPlayer p)
    {
        if (Active && _inProgress) Prepare(p);
    }

    private void Prepare(JbPlayer p)
    {
        var c = p.Controller;
        if (c == null || !c.IsAliveP()) return;
        c.SetArmor(0, false);
        var w = DayUtil.Give(p, "weapon_knife", true);
        var s = S(p);
        s.Launcher = w?.EntityHandle.Raw ?? 0;
        s.Clip = MaxClip;
        s.Reloading = false;
        s.NextAttack = (float)Server.EngineTime + 0.5f; // ATTACK_DELAY_DEPLOY
    }

    private HookResult OnDamage(DamageContext ctx)
    {
        if (!ctx.IsFall) return HookResult.Continue;
        if (DayRunning) return HookResult.Handled;
        return _armed.Contains(ctx.Victim.Slot) ? HookResult.Handled : HookResult.Continue;
    }

    private void OnTick()
    {
        if (!DayRunning && _armed.Count == 0 && _rockets.Count == 0) return;
        var now = (float)Server.EngineTime;
        foreach (var p in Players.Connected())
        {
            var c = p.Controller!;
            var s = S(p);
            if (!c.IsAliveP() || s.Launcher == 0 || !Launches(p)) continue;
            var active = c.ActiveWeapon();
            if (active == null || active.EntityHandle.Raw != s.Launcher) continue;
            // The knife itself never attacks.
            if (active.NextPrimaryAttackTick != int.MaxValue)
            {
                active.NextPrimaryAttackTick = int.MaxValue;
                active.NextSecondaryAttackTick = int.MaxValue;
                Utilities.SetStateChanged(active, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
                Utilities.SetStateChanged(active, "CBasePlayerWeapon", "m_nNextSecondaryAttackTick");
            }

            Hud.Set(c, "days.rpg", Lang.T("days.rpg.hud", s.Clip, MaxClip), 20, 0.3f);
            if (now < s.NextAttack) continue;
            if ((c.Buttons & PlayerButtons.Attack) != 0 && s.Clip > 0 && (Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) == 0)
            {
                Shoot(c, s, now);
                continue;
            }

            if (s.Reloading)
            {
                if (s.Clip >= MaxClip) s.Reloading = false;
                else if (now >= s.ReloadEnd)
                {
                    s.Clip++;
                    s.Reloading = false;
                }
            }
            else if (s.Clip < MaxClip)
            {
                s.Reloading = true;
                s.ReloadEnd = now + 1.25f;
            }
        }

        StepRockets(now);
    }

    private void Shoot(CCSPlayerController c, RpgPlayer s, float now)
    {
        var pawn = c.PawnOrNull();
        if (pawn == null) return;
        var eye = c.EyePosition();
        var ang = pawn.EyeAngles;
        var fwd = VectorUtil.Forward(ang);
        var yaw = ang.Y * MathF.PI / 180f;
        var right = new Vector(MathF.Sin(yaw), -MathF.Cos(yaw), 0f);
        var spawn = new Vector(eye.X + fwd.X * 16f + right.X * 7f, eye.Y + fwd.Y * 16f + right.Y * 7f, eye.Z + fwd.Z * 16f - 6f);
        // Aim at the crosshair point so the offset muzzle still hits what the player looks at.
        var aim = new Vector(eye.X + fwd.X * 8192f, eye.Y + fwd.Y * 8192f, eye.Z + fwd.Z * 8192f);
        var dir = new Vector(aim.X - spawn.X, aim.Y - spawn.Y, aim.Z - spawn.Z);
        var len = MathF.Max(0.001f, dir.Length());
        _rockets.Add(new Rocket
        {
            OwnerSlot = c.Slot, Pos = spawn, Born = now,
            Vel = new Vector(dir.X / len * Cfg.RocketSpeed, dir.Y / len * Cfg.RocketSpeed, dir.Z / len * Cfg.RocketSpeed)
        });
        if (Cfg.ShootSound.Length > 0) Sound.PlayAt(c, Cfg.ShootSound);
        s.Clip--;
        s.NextAttack = now + 0.8f;
        s.Reloading = false;
    }

    private void StepRockets(float now)
    {
        if (_rockets.Count == 0) return;
        var dt = Server.TickInterval;
        foreach (var r in _rockets.ToList())
        {
            var owner = Players.Get(r.OwnerSlot).Controller;
            var next = new Vector(r.Pos.X + r.Vel.X * dt, r.Pos.Y + r.Vel.Y * dt, r.Pos.Z + r.Vel.Z * dt);
            if (++r.Tick % 3 == 0) Fx.Beam(r.Pos, next, Color.FromArgb(200, 255, 140, 0), 2f, 0.4f);

            // Direct player hit (not the owner, not freedays): within 20 units of the swept segment end.
            CCSPlayerController? direct = null;
            foreach (var c in Players.Alive())
            {
                if (c.Slot == r.OwnerSlot || c.Jb() is { } jp && (Jb.LastRequests.GetFlags(jp) & LrFlags.Freeday) != 0) continue;
                var o = c.PawnOrNull()?.AbsOrigin;
                if (o == null) continue;
                if (Math.Abs(next.X - o.X) <= 18f && Math.Abs(next.Y - o.Y) <= 18f && next.Z >= o.Z && next.Z <= o.Z + 72f)
                {
                    direct = c;
                    break;
                }
            }

            Vector? impact = direct != null ? next : WorldImpact(r.Pos, next, owner);
            if (impact == null && now - r.Born > 6f) impact = next; // flew out of the map

            if (impact == null)
            {
                r.Pos = next;
                continue;
            }

            _rockets.Remove(r);
            Explode(r, impact, direct, owner);
        }
    }

    /// <summary>
    /// World hit on the rocket's segment for this tick, pulled 4 units back so the explosion is not inside the wall; null
    /// when the segment is clear. Without a valid owner pawn to ignore there is nothing to trace from: the rocket is
    /// detonated where it is (never an invented point further on).
    /// </summary>
    private static Vector? WorldImpact(Vector from, Vector to, CCSPlayerController? owner)
    {
        var pawn = owner is { IsValid: true } ? owner.PawnOrNull() : null;
        if (pawn == null) return from;
        var hit = Aim.WorldHit(from, to, pawn);
        if (hit == null) return null;
        var dx = from.X - hit.X;
        var dy = from.Y - hit.Y;
        var dz = from.Z - hit.Z;
        var len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 0.01f) return hit;
        var back = MathF.Min(4f, len) / len;
        return new Vector(hit.X + dx * back, hit.Y + dy * back, hit.Z + dz * back);
    }

    private void Explode(Rocket r, Vector at, CCSPlayerController? direct, CCSPlayerController? owner)
    {
        // The explosion particle entity also carries the explosion sound (original: ambient sound at the origin).
        var particle = Cfg.ExplosionParticle.Length > 0 ? Fx.Particle(Cfg.ExplosionParticle, at, 2f) : null;
        if (Cfg.ExplosionSound.Length > 0)
        {
            try { particle?.EmitSound(Cfg.ExplosionSound); }
            catch { /* sound is cosmetic */ }
        }
        foreach (var c in Players.Alive().ToList())
        {
            var p = c.Jb();
            var pawn = c.PawnOrNull();
            if (p == null || pawn?.AbsOrigin == null || pawn.MoveType == MoveType_t.MOVETYPE_NOCLIP) continue;
            if ((Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) != 0) continue;
            var o = pawn.AbsOrigin;
            // Closest point of the (eye-height) hull to the explosion.
            var top = o.Z + 64f;
            var cz = at.Z > top ? top : at.Z < o.Z ? o.Z : at.Z;
            var check = new Vector(o.X, o.Y, cz);
            var dist = VectorUtil.Distance(check, at);
            if (dist > PushRadius) continue;
            var isDirect = direct != null && direct.Slot == c.Slot;
            Damage(c, owner, dist, isDirect);
            if (!c.IsAliveP()) continue;

            var centre = new Vector(o.X, o.Y, o.Z + 32f);
            var d = MathF.Max(PushMinDistance, dist);
            var dir = new Vector(centre.X - at.X, centre.Y - at.Y, centre.Z - at.Z);
            var len = MathF.Max(0.001f, dir.Length());
            var pct = isDirect ? 1f : 1f - d * PushFalloff / PushRadius;
            var force = PushForceBase * pct * PushMultiplier;
            var cur = pawn.AbsVelocity;
            var v = new Vector(dir.X / len * force + cur.X, dir.Y / len * force + cur.Y, dir.Z / len * force + cur.Z);
            if (cur.Z < 0f && dir.Z > 0f && v.Z < 200f) v.Z = 200f;
            pawn.Teleport(null, null, v);
        }
    }

    private void Damage(CCSPlayerController victim, CCSPlayerController? owner, float dist, bool direct)
    {
        if (owner == null || !owner.IsValid) return;
        var pawn = victim.PawnOrNull();
        if (pawn == null) return;
        var dmg = Cfg.MaxDamage * (1f - dist / PushRadius);
        var ffa = Jb.Days.IsFreeForAll;
        if (owner.Slot == victim.Slot)
        {
            dmg /= 4f;
        }
        else
        {
            if (!ffa && victim.Team == owner.Team) return;
            var airborne = (pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) == 0;
            if (direct && airborne)
            {
                dmg = pawn.Health;
                Hud.Set(victim, "days.rpg.air", Lang.T("days.rpg.airshot_by", Hud.Escape(owner.PlayerName)), 80, 3);
                Hud.Set(owner, "days.rpg.air", Lang.T("days.rpg.airshot_on", Hud.Escape(victim.PlayerName)), 80, 3);
                if (Cfg.AirshotSound.Length > 0) Sound.Play(owner, Cfg.AirshotSound);
            }
            else if (!ffa && owner.Team == CsTeam.CounterTerrorist)
            {
                dmg *= Cfg.GuardDamageMultiplier;
            }
        }

        var rounded = (int)MathF.Round(dmg);
        if (rounded <= 0) return;
        if (pawn.Health - rounded > 0)
        {
            pawn.Health -= rounded;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        }
        else
        {
            DamageService.KillWithCredit(victim, owner.Slot == victim.Slot ? null : owner, "rpg");
        }
    }
}
