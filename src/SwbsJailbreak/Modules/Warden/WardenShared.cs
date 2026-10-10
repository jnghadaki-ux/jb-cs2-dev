using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Warden;

/// <summary>
/// Phrase helper for this area. Core <see cref="Lang.T"/> runs string.Format before colour tags are expanded, so a
/// coloured phrase with {0} placeholders throws and comes back unformatted. Here the colour tags are expanded first
/// (they become control bytes) and only then are the placeholders substituted. Works unchanged if the core is fixed.
/// </summary>
internal static class WText
{
    public static string T(string key, params object?[] args)
    {
        var raw = Lang.T(key);
        if (args.Length == 0) return raw;
        var coloured = Colors.Apply(raw);
        try
        {
            return string.Format(coloured, args);
        }
        catch (FormatException)
        {
            return coloured;
        }
    }

    /// <summary>Player name safe for centre HTML.</summary>
    public static string HtmlName(string name) => Hud.Escape(name);

    /// <summary>Chat line + the same text in the player's console (original "CPrintToChat + PrintToConsole").</summary>
    public static void TellBoth(CCSPlayerController? player, string text)
    {
        if (player == null || !player.IsValid || player.IsBot) return;
        Chat.Print(player, text);
        Chat.PrintToConsole(player, Colors.Strip(Chat.Prefix) + " " + Colors.Strip(text));
    }
}

/// <summary>Beam drawing helpers (CS2 has no temp entities; every line/ring is a real "beam" entity).</summary>
internal static class WDraw
{
    public static Color ToColor(int[]? rgba, Color fallback)
    {
        if (rgba == null || rgba.Length < 3) return fallback;
        var a = rgba.Length >= 4 ? rgba[3] : 255;
        return Color.FromArgb(Math.Clamp(a, 0, 255), Math.Clamp(rgba[0], 0, 255), Math.Clamp(rgba[1], 0, 255), Math.Clamp(rgba[2], 0, 255));
    }

    /// <summary>Parse "R,G,B" or "R,G,B,A"; null when empty/invalid.</summary>
    public static Color? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        var v = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out v[i])) return null;
        return ToColor(v, Color.White);
    }

    /// <summary>A beam whose start (Width) is at <paramref name="start"/> and end (EndWidth) at <paramref name="end"/>.</summary>
    public static CBeam? Beam(Vector start, Vector end, Color color, float width, float endWidth, float amplitude = 0f)
    {
        try
        {
            var beam = Utilities.CreateEntityByName<CBeam>("beam");
            if (beam == null) return null;
            beam.RenderMode = color.A < 255 ? RenderMode_t.kRenderTransAlpha : RenderMode_t.kRenderNormal;
            beam.Render = color;
            beam.Width = width;
            beam.EndWidth = endWidth;
            if (amplitude > 0) beam.Amplitude = amplitude;
            beam.Teleport(start, new QAngle(0, 0, 0), new Vector(0, 0, 0));
            beam.EndPos.X = end.X;
            beam.EndPos.Y = end.Y;
            beam.EndPos.Z = end.Z;
            beam.DispatchSpawn();
            return beam;
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogWarning(ex, "Warden: failed to create beam");
            return null;
        }
    }

    /// <summary>Regular polygon of beams at constant Z (replacement for TE_SetupBeamRingPoint).</summary>
    public static List<CBeam> Ring(Vector center, float radius, Color color, float width, int segments, float amplitude = 0f)
    {
        segments = Math.Clamp(segments, 3, 64);
        var list = new List<CBeam>(segments);
        var pts = RingPoints(center, radius, segments);
        for (var i = 0; i < segments; i++)
        {
            var b = Beam(pts[i], pts[(i + 1) % segments], color, width, width, amplitude);
            if (b != null) list.Add(b);
        }

        return list;
    }

    public static void MoveRing(List<CBeam> ring, Vector center, float radius)
    {
        if (ring.Count == 0) return;
        var pts = RingPoints(center, radius, ring.Count);
        for (var i = 0; i < ring.Count; i++)
            Fx.MoveBeam(ring[i], pts[i], pts[(i + 1) % ring.Count]);
    }

    public static void ColorRing(List<CBeam> ring, Color color)
    {
        foreach (var b in ring) Fx.SetBeamColor(b, color);
    }

    public static bool RingValid(List<CBeam>? ring, int expected) =>
        ring != null && ring.Count == expected && ring.All(b => b.IsValid);

    public static void Remove(List<CBeam>? ring)
    {
        if (ring == null) return;
        foreach (var b in ring) Fx.SafeRemove(b);
        ring.Clear();
    }

    public static Vector[] RingPoints(Vector c, float r, int segments)
    {
        var pts = new Vector[segments];
        for (var i = 0; i < segments; i++)
        {
            var a = 2 * MathF.PI * i / segments;
            pts[i] = new Vector(c.X + r * MathF.Cos(a), c.Y + r * MathF.Sin(a), c.Z);
        }

        return pts;
    }

    public static float Length(Vector v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    public static Vector Normalize(Vector v)
    {
        var l = Length(v);
        return l < 0.0001f ? new Vector(0, 0, 1) : new Vector(v.X / l, v.Y / l, v.Z / l);
    }

    public static Vector Cross(Vector a, Vector b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>Forward / right / up vectors of an eye angle (roll ignored, as for player eyes).</summary>
    public static (Vector Forward, Vector Right, Vector Up) Basis(QAngle angles)
    {
        var pitch = angles.X * MathF.PI / 180f;
        var yaw = angles.Y * MathF.PI / 180f;
        float sp = MathF.Sin(pitch), cp = MathF.Cos(pitch), sy = MathF.Sin(yaw), cy = MathF.Cos(yaw);
        var forward = new Vector(cp * cy, cp * sy, -sp);
        var right = new Vector(sy, -cy, 0);
        var up = new Vector(sp * cy, sp * sy, cp);
        return (forward, right, up);
    }
}

/// <summary>
/// Expanding / shrinking ring pulses (TE_SetupBeamRingPoint with start→end radius over a lifetime). Each pulse is a
/// beam polygon whose radius is re-interpolated every 0.05 s and which is removed when its life ends. Ticked from
/// the owning module's OnTick listener.
/// </summary>
internal sealed class PulseRings
{
    private sealed class Pulse
    {
        public required List<CBeam> Beams;
        public required Vector Center;
        public float R0;
        public float R1;
        public float Start;
        public float Life;
    }

    private const float Step = 0.05f;
    private readonly List<Pulse> _pulses = new();
    private float _nextStep;

    public int Count => _pulses.Count;

    public void Spawn(Vector center, float startRadius, float endRadius, float life, Color color, float width, int segments, float amplitude = 0f)
    {
        if (life <= 0) return;
        var beams = WDraw.Ring(center, Math.Max(0.5f, startRadius), color, width, segments, amplitude);
        if (beams.Count == 0) return;
        _pulses.Add(new Pulse
        {
            Beams = beams,
            Center = center,
            R0 = startRadius,
            R1 = endRadius,
            Start = Server.CurrentTime,
            Life = life
        });
    }

    public void Tick()
    {
        if (_pulses.Count == 0) return;
        var now = Server.CurrentTime;
        if (now < _nextStep) return;
        _nextStep = now + Step;
        for (var i = _pulses.Count - 1; i >= 0; i--)
        {
            var p = _pulses[i];
            var t = (now - p.Start) / p.Life;
            if (t >= 1f || p.Beams.Any(b => !b.IsValid))
            {
                WDraw.Remove(p.Beams);
                _pulses.RemoveAt(i);
                continue;
            }

            WDraw.MoveRing(p.Beams, p.Center, Math.Max(0.5f, p.R0 + (p.R1 - p.R0) * t));
        }
    }

    public void Clear()
    {
        foreach (var p in _pulses) WDraw.Remove(p.Beams);
        _pulses.Clear();
    }
}

/// <summary>
/// World traces. The CSS ray-trace API (Trace/TraceOptions) only exists in the net10.0 build (CSS 1.0.370+). On the
/// net8.0 build every method returns false and callers fall back to a REAL position (the player's own origin or a
/// recorded bullet_impact) - never an invented point.
/// </summary>
internal static class WTrace
{
    public static bool Available => VectorUtil.HasRayTrace;

    /// <summary>Eye trace along the view, ignoring players (MASK_PLAYERSOLID + TraceFilter_DontHitPlayers).</summary>
    public static bool EyeTrace(CCSPlayerController c, out Vector end, out Vector normal)
    {
#if NET10_0_OR_GREATER
        var pawn = c.PawnOrNull();
        if (pawn?.AbsOrigin != null)
        {
            var eye = c.EyePosition();
            var tr = Trace.TraceShape(eye, pawn.EyeAngles, pawn, new TraceOptions
            {
                InteractsAs = Contents.Player,
                InteractsWith = Contents.Solid | Contents.Window | Contents.PlayerClip,
                InteractsExclude = Contents.Player | Contents.NoDraw
            });
            if (tr.DidHit())
            {
                end = tr.EndPos;
                normal = tr.Normal;
                return true;
            }
        }
#endif
        end = new Vector(0, 0, 0);
        normal = new Vector(0, 0, 0);
        return false;
    }

    /// <summary>
    /// Trace from the eye to just past an impact point. worldHit = the first thing hit is the world (not a player or
    /// other entity). Returns false when tracing is unavailable.
    /// </summary>
    public static bool ImpactTrace(CCSPlayerController c, Vector impact, out bool worldHit, out Vector normal)
    {
#if NET10_0_OR_GREATER
        var pawn = c.PawnOrNull();
        if (pawn?.AbsOrigin != null)
        {
            var eye = c.EyePosition();
            var dir = WDraw.Normalize(new Vector(impact.X - eye.X, impact.Y - eye.Y, impact.Z - eye.Z));
            var end = new Vector(impact.X + dir.X * 2f, impact.Y + dir.Y * 2f, impact.Z + dir.Z * 2f);
            var tr = Trace.TraceEndShape(eye, end, pawn, new TraceOptions
            {
                InteractsAs = Contents.Player,
                InteractsWith = Contents.Solid | Contents.Window | Contents.PlayerClip | Contents.Player | Contents.Hitbox
                                | Contents.PhysicsProp | Contents.Debris,
                InteractsExclude = Contents.NoDraw
            });
            normal = tr.Normal;
            if (!tr.DidHit())
            {
                worldHit = false;
                return true;
            }

            var hit = tr.HitEntity();
            worldHit = !hit.IsValid || hit.Index == 0 || hit.DesignerName == "worldent";
            return true;
        }
#endif
        worldHit = false;
        normal = new Vector(0, 0, 0);
        return false;
    }
}
