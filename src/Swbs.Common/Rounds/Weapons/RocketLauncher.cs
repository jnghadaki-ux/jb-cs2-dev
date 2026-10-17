using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace Swbs.Common;

/// <summary>
/// The RPG of SMPlugins CustomWeapon_RPG (and the SWBS Jailbreak RPG day): the knife becomes a rocket launcher whose
/// own attacks are blocked. Rockets are simulated every tick and explode on players or on the world (ray trace); the
/// blast pushes everyone in range (rocket jumps), hurts with falloff and kills on a direct hit in the air (airshot).
/// All numbers come from RoundTuning (the original wpn_rpg_* cvars).
/// </summary>
public sealed class RocketLauncher
{
    private sealed class Rocket
    {
        public int OwnerSlot;
        public Vector Pos = new(0, 0, 0);
        public Vector Vel = new(0, 0, 0);
        public float Born;
        public int Tick;

        /// <summary>Height of the floor the shooter stood on (null when they fired in the air).</summary>
        public float? FloorZ;
    }

    private sealed class Holder
    {
        public bool Armed;
        public uint Launcher;
        public int Clip;
        public bool Reloading;
        public float ReloadEnd, NextAttack;
    }

    private readonly RoundsModule _rounds;
    private readonly List<Rocket> _rockets = new();
    private readonly Holder[] _holders = Enumerable.Range(0, 65).Select(_ => new Holder()).ToArray();

    /// <summary>Fire and reload delays are multiplied by this (Rocket Rain: faster).</summary>
    public float DelayFactor { get; set; } = 1f;

    /// <summary>Rockets per clip.</summary>
    public int ClipSize { get; set; } = 4;

    /// <summary>Only players in the air are hurt by rockets (pushes always apply).</summary>
    public bool AirborneOnly { get; set; }

    /// <summary>Called when a rocket killed a player in the air with a direct hit (attacker, victim).</summary>
    public event Action<CCSPlayerController, CCSPlayerController>? Airshot;

    internal RocketLauncher(RoundsModule rounds) => _rounds = rounds;

    private RoundTuning T => _rounds.Tuning;

    public bool IsArmed(int slot) => slot is >= 0 and < 65 && _holders[slot].Armed;

    public bool AnyArmed => _holders.Any(h => h.Armed);

    /// <summary>Give the launcher (the knife) to a living player.</summary>
    public void Arm(CCSPlayerController c)
    {
        if (!c.IsValid || !c.IsAliveP()) return;
        var knife = c.Weapons().FirstOrDefault(w => Weapons.IsKnife(w.DesignerName));
        if (knife == null)
        {
            c.Give(c.Team == CsTeam.CounterTerrorist ? "weapon_knife" : "weapon_knife_t");
            knife = c.Weapons().FirstOrDefault(w => Weapons.IsKnife(w.DesignerName));
        }

        var h = _holders[c.Slot];
        h.Armed = true;
        h.Launcher = knife?.EntityHandle.Raw ?? 0;
        h.Clip = Math.Max(1, ClipSize);
        h.Reloading = false;
        h.NextAttack = (float)Server.EngineTime + T.RpgAttackDelayDeploy;
        Server.NextFrame(() => { if (c.IsValid && c.IsAliveP()) c.ExecuteClientCommand("slot3"); });
    }

    public void Disarm(int slot)
    {
        if (slot is < 0 or >= 65) return;
        var h = _holders[slot];
        if (!h.Armed) return;
        h.Armed = false;
        h.Launcher = 0;
        // Give the knife its attacks back.
        if (Players.Get(slot).Controller is { IsValid: true } c)
        {
            foreach (var w in c.Weapons().Where(w => Weapons.IsKnife(w.DesignerName)))
            {
                w.NextPrimaryAttackTick = Server.TickCount;
                w.NextSecondaryAttackTick = Server.TickCount;
                Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
                Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_nNextSecondaryAttackTick");
            }

            Hud.Clear(c, "rounds.rpg");
        }
    }

    public void Clear()
    {
        _rockets.Clear();
        for (var i = 0; i < _holders.Length; i++) Disarm(i);
        DelayFactor = 1f;
        ClipSize = 4;
        AirborneOnly = false;
        Airshot = null;
    }

    internal void Tick(float now)
    {
        if (_rockets.Count == 0 && !AnyArmed) return;
        foreach (var c in Players.Alive().ToList())
        {
            var h = _holders[c.Slot];
            if (!h.Armed) continue;
            var active = c.ActiveWeapon();
            // Any knife in hand is the launcher (CS2 may hand out a new knife entity).
            if (active == null || !Weapons.IsKnife(active.DesignerName)) continue;
            h.Launcher = active.EntityHandle.Raw;
            if (active.NextPrimaryAttackTick != int.MaxValue)
            {
                active.NextPrimaryAttackTick = int.MaxValue;
                active.NextSecondaryAttackTick = int.MaxValue;
                Utilities.SetStateChanged(active, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
                Utilities.SetStateChanged(active, "CBasePlayerWeapon", "m_nNextSecondaryAttackTick");
            }

            Hud.Set(c, "rounds.rpg", Lang.T("rounds.hud.rockets", Pips(h.Clip, ClipSize), h.Reloading ? Lang.T("rounds.hud.reloading") : ""), 20, 0.3f);
            if (now < h.NextAttack) continue;
            if ((c.Buttons & PlayerButtons.Attack) != 0 && h.Clip > 0)
            {
                Shoot(c, h, now);
                continue;
            }

            if (h.Reloading)
            {
                if (h.Clip >= ClipSize) h.Reloading = false;
                else if (now >= h.ReloadEnd)
                {
                    h.Clip++;
                    h.Reloading = false;
                }
            }
            else if (h.Clip < ClipSize)
            {
                h.Reloading = true;
                h.ReloadEnd = now + Math.Max(0.05f, T.RpgReloadDelay * DelayFactor);
            }
        }

        Step(now);
    }

    private static string Pips(int clip, int max)
    {
        max = Math.Clamp(max, 1, 12);
        clip = Math.Clamp(clip, 0, max);
        return Hud.Color(new string('■', clip), "#ff9a1f") + Hud.Color(new string('□', max - clip), "#777777");
    }

    private void Shoot(CCSPlayerController c, Holder h, float now)
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
        var speed = T.RpgRocketSpeed;
        _rockets.Add(new Rocket
        {
            OwnerSlot = c.Slot, Pos = spawn, Born = now, FloorZ = c.OnGround() ? pawn.AbsOrigin?.Z : null,
            Vel = new Vector(dir.X / len * speed, dir.Y / len * speed, dir.Z / len * speed)
        });
        if (T.RpgShootSound.Length > 0) Sound.PlayAt(c, T.RpgShootSound);
        h.Clip--;
        h.NextAttack = now + Math.Max(0.05f, T.RpgAttackDelayAttack * DelayFactor);
        h.Reloading = false;
    }

    private void Step(float now)
    {
        if (_rockets.Count == 0) return;
        var dt = Server.TickInterval;
        foreach (var r in _rockets.ToList())
        {
            var owner = Players.Get(r.OwnerSlot).Controller;
            var next = new Vector(r.Pos.X + r.Vel.X * dt, r.Pos.Y + r.Vel.Y * dt, r.Pos.Z + r.Vel.Z * dt);
            if (++r.Tick % 3 == 0) Fx.Beam(r.Pos, next, Color.FromArgb(200, 255, 140, 0), 2f, 0.4f);

            CCSPlayerController? direct = null;
            foreach (var c in Players.Alive())
            {
                if (c.Slot == r.OwnerSlot) continue;
                var o = c.PawnOrNull()?.AbsOrigin;
                if (o == null) continue;
                if (Math.Abs(next.X - o.X) <= 18f && Math.Abs(next.Y - o.Y) <= 18f && next.Z >= o.Z && next.Z <= o.Z + 72f)
                {
                    direct = c;
                    break;
                }
            }

            var impact = direct != null ? next : WorldImpact(r.Pos, next, owner);
            // No world trace (CounterStrikeSharp out of date for this CS2 build): the floor the shooter stood on still
            // stops the rocket, so rocket jumps keep working.
            if (impact == null && Aim.TraceBroken && r.FloorZ is { } floor && r.Vel.Z < 0f && next.Z <= floor + 2f)
                impact = new Vector(next.X, next.Y, floor + 2f);
            if (impact == null && now - r.Born > 6f) impact = next; // flew out of the map
            if (impact == null)
            {
                r.Pos = next;
                continue;
            }

            _rockets.Remove(r);
            Explode(impact, direct, owner);
        }
    }

    /// <summary>World hit on this tick's segment, pulled 4 units back out of the wall; null when the segment is clear.</summary>
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

    private void Explode(Vector at, CCSPlayerController? direct, CCSPlayerController? owner)
    {
        var particle = T.RpgExplosionParticle.Length > 0 ? Fx.Particle(T.RpgExplosionParticle, at, 2f) : null;
        if (T.RpgExplosionSound.Length > 0)
        {
            try { particle?.EmitSound(T.RpgExplosionSound); }
            catch { /* sound is cosmetic */ }
        }

        var radius = Math.Max(1f, T.RpgPushMaxDistance);
        var ffa = RoundClock.FreeForAll;
        foreach (var c in Players.Alive().ToList())
        {
            var pawn = c.PawnOrNull();
            if (pawn?.AbsOrigin == null || pawn.MoveType == MoveType_t.MOVETYPE_NOCLIP) continue;
            var o = pawn.AbsOrigin;
            // Closest point of the (eye-height) hull to the explosion.
            var top = o.Z + 64f;
            var cz = at.Z > top ? top : at.Z < o.Z ? o.Z : at.Z;
            var dist = VectorUtil.Distance(new Vector(o.X, o.Y, cz), at);
            if (dist > radius) continue;
            var self = owner != null && owner.Slot == c.Slot;
            var teammate = !self && owner != null && !ffa && owner.Team == c.Team;
            var isDirect = direct != null && direct.Slot == c.Slot;
            var airborne = (pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) == 0;
            if (!teammate) Damage(c, pawn, owner, dist, radius, isDirect, airborne, self);
            if (!c.IsAliveP()) continue;

            var push = self ? T.RpgKnockbackSelf : teammate ? false : T.RpgKnockbackEnemies;
            if (!push) continue;
            var centre = new Vector(o.X, o.Y, o.Z + 32f);
            var d = MathF.Max(T.RpgPushMinDistanceClamp, dist);
            var dir = new Vector(centre.X - at.X, centre.Y - at.Y, centre.Z - at.Z);
            var len = MathF.Max(0.001f, dir.Length());
            var pct = isDirect ? 1f : Math.Max(0f, 1f - d * T.RpgPushFalloffPercent / radius);
            var force = T.RpgPushForceBase * pct * T.RpgPushForceMultiplier;
            var cur = pawn.AbsVelocity;
            var v = new Vector(dir.X / len * force + cur.X, dir.Y / len * force + cur.Y, dir.Z / len * force + cur.Z);
            if (cur.Z < 0f && dir.Z > 0f && v.Z < 200f) v.Z = 200f;
            pawn.Teleport(null, null, v);
        }
    }

    private void Damage(CCSPlayerController victim, CCSPlayerPawn pawn, CCSPlayerController? owner, float dist, float radius,
        bool direct, bool airborne, bool self)
    {
        if (owner == null || !owner.IsValid) return;
        if (self && !T.RpgAllowDamageSelf) return;
        if (!self && AirborneOnly && !airborne) return;
        var dmg = T.RpgBaseDamage * (1f - dist / radius);
        var airshot = false;
        if (self) dmg /= 4f;
        else if (direct && airborne)
        {
            dmg = pawn.Health;
            airshot = true;
        }

        var rounded = (int)MathF.Round(dmg);
        if (rounded <= 0) return;
        if (airshot)
        {
            Hud.Set(victim, "rounds.rpg.air", Lang.T("rounds.airshot_by", Hud.Escape(owner.PlayerName)), 80, 3);
            Hud.Set(owner, "rounds.rpg.air", Lang.T("rounds.airshot_on", Hud.Escape(victim.PlayerName)), 80, 3);
            if (T.RpgAirshotSound.Length > 0) Sound.Play(owner, T.RpgAirshotSound);
            Airshot?.Invoke(owner, victim);
        }

        if (pawn.Health - rounded > 0)
        {
            pawn.Health -= rounded;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        }
        else DamageService.KillWithCredit(victim, self ? null : owner, "rpg");
    }
}
