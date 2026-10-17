using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace Swbs.Common;

/// <summary>
/// SMPlugins ThrowingKnife for the rounds: every knife attack throws a knife that flies, drops a little, leaves a grey
/// trail and deals RoundTuning.KnifeDamage to the first enemy it reaches; walls stop it with a spark. The knife is a
/// model moved every tick with a world trace (CS2 does not keep a physics prop's velocity up to date).
/// </summary>
public sealed class ThrowingKnives
{
    private sealed class Thrown
    {
        public uint Prop;
        public int OwnerSlot;
        public float Born;
        public Vector LastPos = new(0, 0, 0);
        public Vector Pos = new(0, 0, 0);
        public Vector Vel = new(0, 0, 0);
        public int Tick;
    }

    private readonly RoundsModule _rounds;
    private readonly List<Thrown> _thrown = new();

    internal ThrowingKnives(RoundsModule rounds) => _rounds = rounds;

    private RoundTuning T => _rounds.Tuning;

    /// <summary>Called when a thrown knife hits a player (thrower, victim, killed).</summary>
    public event Action<CCSPlayerController, CCSPlayerController, bool>? Hit;

    public void Clear()
    {
        foreach (var k in _thrown) Fx.SafeRemove(new CHandle<CBaseEntity>(k.Prop).Value);
        _thrown.Clear();
        Hit = null;
    }

    /// <summary>Throw a knife from the player's eyes and put their knife on cooldown. False when nothing was thrown.</summary>
    public bool Throw(CCSPlayerController c)
    {
        var pawn = c.PawnOrNull();
        if (pawn == null || !c.IsAliveP() || string.IsNullOrWhiteSpace(T.KnifeModel)) return false;
        try
        {
            var eye = c.EyePosition();
            var fwd = VectorUtil.Forward(pawn.EyeAngles);
            var start = new Vector(eye.X + fwd.X * 16f, eye.Y + fwd.Y * 16f, eye.Z + fwd.Z * 16f);
            var a = pawn.EyeAngles;
            var prop = Fx.Prop(T.KnifeModel, start, new QAngle(a.X, a.Y, 0));
            if (prop == null) return false;
            _thrown.Add(new Thrown
            {
                Prop = prop.EntityHandle.Raw, OwnerSlot = c.Slot, Born = (float)Server.EngineTime, LastPos = start, Pos = start,
                Vel = new Vector(fwd.X * T.KnifeSpeed, fwd.Y * T.KnifeSpeed, fwd.Z * T.KnifeSpeed)
            });
        }
        catch
        {
            return false; // model missing on this build
        }

        var knife = c.ActiveWeapon();
        if (knife != null)
        {
            var next = Server.TickCount + (int)(Math.Max(0.1f, T.KnifeDelay) / Server.TickInterval);
            knife.NextPrimaryAttackTick = next;
            knife.NextSecondaryAttackTick = next;
            Utilities.SetStateChanged(knife, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
            Utilities.SetStateChanged(knife, "CBasePlayerWeapon", "m_nNextSecondaryAttackTick");
        }

        return true;
    }

    internal void Tick(float now)
    {
        if (_thrown.Count == 0) return;
        var ffa = RoundClock.FreeForAll;
        var dt = Server.TickInterval;
        foreach (var k in _thrown.ToList())
        {
            var prop = new CHandle<CBaseEntity>(k.Prop).Value;
            if (prop == null || !prop.IsValid)
            {
                _thrown.Remove(k);
                continue;
            }

            var owner = Players.Get(k.OwnerSlot).Controller;
            k.Vel = new Vector(k.Vel.X, k.Vel.Y, k.Vel.Z - 800f * Math.Max(0f, T.KnifeGravity) * dt);
            var from = k.Pos;
            var pos = new Vector(from.X + k.Vel.X * dt, from.Y + k.Vel.Y * dt, from.Z + k.Vel.Z * dt);
            var ownerPawn = owner is { IsValid: true } ? owner.PawnOrNull() : null;
            var wall = ownerPawn != null ? Aim.WorldHit(from, pos, ownerPawn) : null;
            if (wall != null) pos = wall;
            k.Pos = pos;
            var yaw = MathF.Atan2(k.Vel.Y, k.Vel.X) * 180f / MathF.PI;
            prop.Teleport(pos, new QAngle((k.Tick * 40f) % 360f, yaw, 0), null);

            if (++k.Tick % 4 == 0)
            {
                Fx.Beam(k.LastPos, pos, Color.FromArgb(117, 177, 177, 177), 1.5f, 0.5f);
                k.LastPos = pos;
            }

            CCSPlayerController? hit = null;
            var head = false;
            foreach (var c in Players.Alive())
            {
                if (c.Slot == k.OwnerSlot) continue;
                if (!ffa && owner != null && c.Team == owner.Team) continue;
                var po = c.PawnOrNull()?.AbsOrigin;
                if (po == null) continue;
                if (VectorUtil.Distance(pos, new Vector(po.X, po.Y, po.Z + 36f)) <= 36f && Math.Abs(pos.Z - (po.Z + 36f)) <= 40f)
                {
                    hit = c;
                    head = pos.Z >= po.Z + 58f;
                    break;
                }
            }

            if (hit != null)
            {
                if (T.KnifeHitPlayerSound.Length > 0) prop.EmitSound(T.KnifeHitPlayerSound);
                var pawn = hit.PawnOrNull()!;
                var dmg = (int)(T.KnifeDamage * (head ? 2f : 1f));
                var killed = pawn.Health <= dmg;
                if (owner is { IsValid: true }) Hit?.Invoke(owner, hit, killed);
                if (killed) DamageService.KillWithCredit(hit, owner, "knife");
                else
                {
                    pawn.Health -= dmg;
                    Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
                }

                prop.AcceptInput("Kill");
                _thrown.Remove(k);
                continue;
            }

            if (wall != null || now - k.Born > 3f)
            {
                if (T.KnifeHitWorldSound.Length > 0) prop.EmitSound(T.KnifeHitWorldSound);
                Fx.Particle("particles/impact_fx/impact_spark.vpcf", pos, 1f);
                prop.AcceptInput("Kill");
                _thrown.Remove(k);
            }
        }
    }
}
