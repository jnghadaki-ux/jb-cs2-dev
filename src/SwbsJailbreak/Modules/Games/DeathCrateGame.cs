using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using SwbsJailbreak.Core;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;
using QAngle = CounterStrikeSharp.API.Modules.Utils.QAngle;

namespace SwbsJailbreak.Modules.Games;

/// <summary>Death Crate rules that need no engine (unit-tested).</summary>
internal static class DeathCrateRules
{
    /// <summary>The crate speeds up the longer the game runs.</summary>
    public static float Speed(float start, float accelPerSecond, float max, float elapsed) =>
        Math.Clamp(start + Math.Max(0f, accelPerSecond) * Math.Max(0f, elapsed), 1f, Math.Max(1f, max));

    /// <summary>A point on the ring (angle in radians), at the ring's floor height.</summary>
    public static Vector3 RingPoint(Vector3 center, float radius, float angle) =>
        new(center.X + radius * MathF.Cos(angle), center.Y + radius * MathF.Sin(angle), center.Z);

    /// <summary>Keep a lunge target inside the ring (players standing at the edge still get hit, the crate never leaves).</summary>
    public static Vector3 ClampToRing(Vector3 center, float radius, Vector3 p)
    {
        var d = new Vector2(p.X - center.X, p.Y - center.Y);
        var len = d.Length();
        if (len > radius && len > 0.001f) d *= radius / len;
        return new Vector3(center.X + d.X, center.Y + d.Y, center.Z);
    }

    /// <summary>The crate (a cube of the given half size, resting on its bottom at <paramref name="crateBottom"/>) touches a player's hull.</summary>
    public static bool Hits(Vector3 crateBottom, float half, Vector3 feet) =>
        new ZoneBox(crateBottom, new Vector3(-half, -half, 0f), new Vector3(half, half, half * 2f)).OverlapsHull(feet);

    /// <summary>Horizontal distance check for ringing out.</summary>
    public static bool OutsideRing(Vector3 center, float radius, Vector3 feet) =>
        new Vector2(feet.X - center.X, feet.Y - center.Y).Length() > radius;
}

/// <summary>
/// Death Crate: the prisoners stand in a ring and a crate is thrown violently around it, lunging at players and at random
/// points of the ring, faster and faster. A touch kills; stepping out of the ring is out. The last DeathCrateSurvivors win.
/// Many jailbreak maps have this game built from physics entities that do not work in CS2; this version is driven by the
/// plugin, so it runs on any map. The ring is centred on the map's "deathcrate" Named zone if there is one, else on the
/// warden's last ping, else on the warden.
/// </summary>
internal sealed class DeathCrateGame : WardenGame
{
    private const string ZoneName = "deathcrate";
    private static readonly Color RingColor = Color.FromArgb(255, 255, 60, 40);

    private Vector3 _center;
    private float _radius;
    private float _half;
    private bool _released;
    private float _releaseAt;
    private float _releasedAt;
    private Vector3 _pos;     // crate bottom centre
    private Vector3 _target;
    private float _hop;
    private QAngle _spin = new(0, 0, 0);
    private CBaseModelEntity? _crate;
    private List<CBeam> _ring = new();

    public override string Key => "death_crate";
    public override string NameKey => "games.deathcrate.name";
    public override string RulesKey => "games.deathcrate.rules";
    public override bool PrisonerPickable => false;

    public override void Start(GameSession s)
    {
        var cfg = s.Cfg;
        _released = false;
        _radius = Math.Clamp(cfg.DeathCrateRadius, 128f, 2000f);
        _half = 16f * Math.Clamp(cfg.DeathCrateScale, 0.5f, 6f);
        _releaseAt = Server.CurrentTime + Math.Max(0f, cfg.DeathCrateDelaySeconds);
        _center = PickCenter(s);
        _pos = _center;
        _target = _center;
        _hop = 0;

        _ring = Fx.Ring(V(_center + new Vector3(0, 0, 4f)), _radius, RingColor, 3f, 0f, 32);
        _crate = Fx.Prop(cfg.DeathCrateModel, V(_pos), new QAngle(0, 0, 0));
        if (_crate != null) Scale(_crate, Math.Clamp(cfg.DeathCrateScale, 0.5f, 6f));
        Chat.TellAll("games.deathcrate.countdown", Math.Max(0, (int)MathF.Ceiling(cfg.DeathCrateDelaySeconds)));
    }

    private static Vector3 PickCenter(GameSession s)
    {
        if (Jb.Zones.FindByName(ZoneTypeKeys.Named, ZoneName) is { } z) return z.Box.BottomCenter;
        var w = GameSession.C(s.WardenSlot);
        if (w != null && Aim.TryGetPing(w, 30, out var ping, out _)) return new Vector3(ping.X, ping.Y, ping.Z);
        var o = w?.PawnOrNull()?.AbsOrigin ?? s.Alive().FirstOrDefault()?.PawnOrNull()?.AbsOrigin;
        return o != null ? new Vector3(o.X, o.Y, o.Z) : Vector3.Zero;
    }

    private static void Scale(CBaseModelEntity e, float scale)
    {
        var node = e.CBodyComponent?.SceneNode;
        if (node == null) return;
        try
        {
            node.Scale = scale;
            Utilities.SetStateChanged(e, "CBaseEntity", "m_CBodyComponent");
        }
        catch { /* visual only */ }
    }

    public override void AddWardenItems(GameSession s, JbMenu m)
    {
        if (_released) return;
        m.Add(Lang.T("games.deathcrate.release_now"), p =>
        {
            _releaseAt = Server.CurrentTime;
            s.Module.RefreshControl(p);
        });
    }

    public override void Tick(GameSession s)
    {
        var now = Server.CurrentTime;
        if (!_released)
        {
            if (now < _releaseAt) return;
            _released = true;
            _releasedAt = now;
            NewTarget(s);
            Chat.TellAll("games.deathcrate.released", s.Cfg.DeathCrateSurvivors);
        }

        var elapsed = now - _releasedAt;
        if (elapsed >= s.Cfg.DeathCrateMaxSeconds) { s.Win(s.Players.ToList()); return; }

        // Move: lunge straight at the target, hop and tumble on the way.
        var dt = Server.TickInterval;
        var speed = DeathCrateRules.Speed(s.Cfg.DeathCrateSpeed, s.Cfg.DeathCrateAcceleration, s.Cfg.DeathCrateMaxSpeed, elapsed);
        var flat = new Vector3(_target.X - _pos.X, _target.Y - _pos.Y, 0f);
        var dist = flat.Length();
        var step = speed * dt;
        Vector3 vel;
        if (dist <= step + 1f)
        {
            _pos = new Vector3(_target.X, _target.Y, _center.Z);
            vel = Vector3.Zero;
            NewTarget(s);
        }
        else
        {
            vel = flat / dist * speed;
            _pos += vel * dt;
        }

        _hop += dt * 6f;
        var lift = MathF.Abs(MathF.Sin(_hop)) * s.Cfg.DeathCrateHopHeight;
        _spin = new QAngle((_spin.X + 540f * dt) % 360f, (_spin.Y + 720f * dt) % 360f, (_spin.Z + 360f * dt) % 360f);
        var bottom = _pos + new Vector3(0, 0, lift);
        if (_crate is { IsValid: true })
            _crate.Teleport(new CssVector(bottom.X, bottom.Y, bottom.Z + _half), _spin, V(vel));

        // Hits and ring-outs.
        foreach (var c in s.Alive().ToList())
        {
            if (s.Over) return;
            var o = c.PawnOrNull()?.AbsOrigin;
            if (o == null) continue;
            var feet = new Vector3(o.X, o.Y, o.Z);
            if (DeathCrateRules.Hits(bottom, _half, feet))
            {
                // Throw the victim the way the crate was going, then kill: the death eliminates them.
                var push = dist > 0.001f ? flat / dist * 700f : Vector3.Zero;
                c.SetVelocity(new CssVector(push.X, push.Y, 450f));
                c.Kill();
            }
            else if (DeathCrateRules.OutsideRing(_center, _radius + 48f, feet))
            {
                s.Eliminate(c.Slot, "games.out.left_area");
            }
        }

        if (!s.Over && s.Players.Count <= Math.Max(1, s.Cfg.DeathCrateSurvivors)) s.Win(s.Players.ToList());
    }

    /// <summary>Lunge at a random player most of the time, else at a random point of the ring.</summary>
    private void NewTarget(GameSession s)
    {
        var alive = s.Alive().ToList();
        if (alive.Count > 0 && Random.Shared.NextDouble() < Math.Clamp(s.Cfg.DeathCrateAggression, 0f, 1f))
        {
            var o = alive[Random.Shared.Next(alive.Count)].PawnOrNull()?.AbsOrigin;
            if (o != null)
            {
                // Overshoot a little so the crate sweeps through the spot instead of stopping on it.
                var p = new Vector3(o.X, o.Y, _center.Z);
                var dir = p - _pos;
                if (dir.LengthSquared() > 1f) p += Vector3.Normalize(dir) * 96f;
                _target = DeathCrateRules.ClampToRing(_center, _radius, p);
                return;
            }
        }

        _target = DeathCrateRules.RingPoint(_center, _radius * (0.4f + 0.6f * Random.Shared.NextSingle()), Random.Shared.NextSingle() * MathF.Tau);
    }

    public override string Status(GameSession s)
    {
        if (!_released)
            return Hud.Escape(Lang.T("games.deathcrate.status_wait", Math.Max(0, (int)MathF.Ceiling(_releaseAt - Server.CurrentTime))));
        var left = Math.Max(0, (int)MathF.Ceiling(s.Cfg.DeathCrateMaxSeconds - (Server.CurrentTime - _releasedAt)));
        return Hud.Escape(Lang.T("games.deathcrate.status", s.Players.Count, left));
    }

    public override void End(GameSession s)
    {
        Fx.SafeRemove(_crate);
        _crate = null;
        foreach (var b in _ring) Fx.SafeRemove(b);
        _ring = new List<CBeam>();
    }

    private static CssVector V(Vector3 v) => new(v.X, v.Y, v.Z);
}
