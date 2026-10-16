using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>
/// Aura styles (StoreItem.Data of an Aura item). "pulse" (or empty) is the original floor and waist ring pulse. The
/// moving styles (orbit, helix, rings) keep a few beams per player and move them every other server frame, so they
/// animate smoothly without creating entities all the time; flames and storm flicker with short-lived beams.
/// </summary>
public sealed partial class StoreModule
{
    /// <summary>Every aura style the store draws (an unknown Data falls back to "pulse").</summary>
    internal static readonly string[] AuraStyles = { "pulse", "orbit", "helix", "rings", "flames", "storm" };

    /// <summary>Beams of a moving aura, kept per player while the same item is worn.</summary>
    private sealed class AuraRig
    {
        public string ItemId = "";
        public string Style = "";
        public readonly List<CBeam> Beams = new();
        public double Started;
    }

    private readonly AuraRig?[] _auraRig = new AuraRig?[Players.MaxSlots];
    private int _frame;

    internal static string AuraStyle(StoreItem item)
    {
        var s = item.Data.Trim().ToLowerInvariant();
        return AuraStyles.Contains(s) ? s : "pulse";
    }

    internal static bool MovingAura(string style) => style is "orbit" or "helix" or "rings";

    // ------------------------------------------------------------------ engine-free geometry (unit-tested)

    /// <summary>Orbit: comet <paramref name="i"/> of 3 at time t (seconds): angle and height above the feet.</summary>
    internal static (float Angle, float Height) OrbitAt(int i, double t)
    {
        var angle = (float)(t * 2.6 + i * Math.PI * 2 / 3);
        var height = 34f + 18f * MathF.Sin((float)(t * 1.7 + i * 2.1));
        return (angle, height);
    }

    /// <summary>Helix: point <paramref name="k"/> of <paramref name="n"/> on strand <paramref name="strand"/> (0/1) at time t.</summary>
    internal static (float Angle, float Height) HelixAt(int strand, int k, int n, double t)
    {
        var u = k / (float)(n - 1);
        return ((float)(u * Math.PI * 3 + t * 2.2 + strand * Math.PI), 4f + u * 68f);
    }

    /// <summary>Rings: ring <paramref name="i"/> of <paramref name="count"/> at time t: height (rising, looping) and radius (narrower higher up).</summary>
    internal static (float Height, float Radius) RingAt(int i, int count, double t)
    {
        var u = (float)((t * 0.55 + i / (double)count) % 1.0);
        return (2f + u * 72f, 26f - u * 12f);
    }

    /// <summary>The colour of a moving aura's beam: "rainbow" cycles the hue over time, other specs stay fixed.</summary>
    internal static Color AuraColor(string spec, double t, int offset, Color fixedColor) =>
        spec.Trim().Equals("rainbow", StringComparison.OrdinalIgnoreCase) ? FromHue((t * 90 + offset * 47) % 360) : fixedColor;

    // ------------------------------------------------------------------ drawing

    /// <summary>Called from the effects tick (every 0.15 s) for a player who wears an aura and may show it.</summary>
    private void AuraTick(CCSPlayerController c, Vector o, StoreItem aura)
    {
        var style = AuraStyle(aura);
        if (MovingAura(style))
        {
            EnsureRig(c, aura, style);
            return;
        }

        RemoveAuraRig(c.Slot);
        var filter = ViewFilter(c);
        switch (style)
        {
            case "flames":
                if (_tick % 2 == 0) FlameRing(o, aura, filter);
                break;
            case "storm":
                if (_tick % 2 == 0) Storm(o, aura, filter);
                break;
            default:
                if (_tick % 7 == 0) Aura(c, o, aura);
                break;
        }
    }

    /// <summary>Flame licks around the feet; white (the default) uses fire colours, any other colour tints them.</summary>
    private void FlameRing(Vector o, StoreItem aura, Func<CCSPlayerController, bool>? filter)
    {
        var fire = aura.Color == "255,255,255";
        for (var i = 0; i < 5; i++)
        {
            var a = Random.Shared.NextDouble() * Math.PI * 2;
            var r = 17f + (float)Random.Shared.NextDouble() * 6f;
            var b = At(o, (float)Math.Cos(a) * r, (float)Math.Sin(a) * r, 1);
            var tip = At(b, Random.Shared.Next(-3, 4), Random.Shared.Next(-3, 4), 14f + (float)Random.Shared.NextDouble() * 22f);
            var color = fire ? FireColor() : Mix(ColorOf(aura.Color, Random.Shared), Color.White, (float)Random.Shared.NextDouble() * 0.4f);
            Restrict(Fx.Beam(b, tip, color, 4f, 0.5f, 0.32f), filter);
        }
    }

    private static Color FireColor() => Random.Shared.Next(3) switch
    {
        0 => Color.FromArgb(255, 255, 220, 60),
        1 => Color.FromArgb(255, 255, 140, 20),
        _ => Color.FromArgb(255, 230, 50, 20)
    };

    /// <summary>Jagged arcs crackling around the body and, now and then, a ground flash.</summary>
    private void Storm(Vector o, StoreItem aura, Func<CCSPlayerController, bool>? filter)
    {
        var color = aura.Color == "255,255,255" ? Color.FromArgb(255, 150, 200, 255) : ColorOf(aura.Color, Random.Shared);
        for (var arc = 0; arc < 2; arc++)
        {
            var a0 = Random.Shared.NextDouble() * Math.PI * 2;
            var a1 = a0 + 0.8 + Random.Shared.NextDouble();
            var z0 = 8f + (float)Random.Shared.NextDouble() * 55f;
            var z1 = z0 + Random.Shared.Next(-18, 19);
            Vector? prev = null;
            for (var s = 0; s <= 4; s++)
            {
                var t = s / 4.0;
                var a = a0 + (a1 - a0) * t;
                var r = 20f + Random.Shared.Next(-4, 5);
                var p = At(o, (float)Math.Cos(a) * r, (float)Math.Sin(a) * r, z0 + (z1 - z0) * (float)t + Random.Shared.Next(-4, 5));
                if (prev != null)
                {
                    Restrict(Fx.Beam(prev, p, Mix(color, Color.White, 0.6f), 1.5f, 0.2f), filter);
                    Restrict(Fx.Beam(prev, p, color, 4f, 0.2f), filter);
                }
                prev = p;
            }
        }

        if (_tick % 8 == 0) Fx.PulseAt(At(o, 0, 0, 3), color, 10f, 40f, 0.4f, 2f, filter, 14);
    }

    /// <summary>Builds the moving aura's beams once per item (rebuilt when the item or style changes).</summary>
    private void EnsureRig(CCSPlayerController c, StoreItem aura, string style)
    {
        if (_auraRig[c.Slot] is { } rig && rig.ItemId == aura.Id && rig.Style == style && rig.Beams.All(b => b.IsValid)) return;
        RemoveAuraRig(c.Slot);
        var o = c.Origin();
        if (o == null) return;
        rig = new AuraRig { ItemId = aura.Id, Style = style, Started = CounterStrikeSharp.API.Server.CurrentTime };
        var count = style switch { "orbit" => 3, "helix" => 2 * (HelixPoints - 1), _ => RingCount * RingSegments };
        var color = ColorOf(aura.Color, Random.Shared);
        var filter = ViewFilter(c);
        var width = style == "orbit" ? 5f : 2f;
        for (var i = 0; i < count; i++)
        {
            var b = Fx.Beam(o, At(o, 0, 0, 1), color, width, style == "orbit" ? 0.3f : width, 0f);
            if (b == null) continue;
            Restrict(b, filter);
            rig.Beams.Add(b);
        }

        _auraRig[c.Slot] = rig;
        MoveRig(c.Slot, rig, o, aura);
    }

    private const int HelixPoints = 8;
    private const int RingCount = 2;
    private const int RingSegments = 12;

    /// <summary>Every other server frame: moves each moving aura with its player.</summary>
    private void AuraFrame()
    {
        if (++_frame % 2 != 0) return;
        for (var slot = 0; slot < _auraRig.Length; slot++)
        {
            if (_auraRig[slot] is not { } rig) continue;
            var c = Players.Get(slot).Controller;
            var item = Equipped(slot, StoreItemType.Aura);
            if (c == null || !c.IsValid || !c.IsAliveP() || item == null || item.Id != rig.ItemId || c.Origin() is not { } o)
            {
                RemoveAuraRig(slot);
                continue;
            }

            MoveRig(slot, rig, o, item);
        }
    }

    private void MoveRig(int slot, AuraRig rig, Vector o, StoreItem aura)
    {
        var t = CounterStrikeSharp.API.Server.CurrentTime - rig.Started;
        var baseColor = ColorOf(aura.Color.Equals("rainbow", StringComparison.OrdinalIgnoreCase) ? "255,255,255" : aura.Color, Random.Shared);
        var rainbow = aura.Color.Trim().Equals("rainbow", StringComparison.OrdinalIgnoreCase);
        try
        {
            switch (rig.Style)
            {
                case "orbit":
                    for (var i = 0; i < rig.Beams.Count; i++)
                    {
                        var (a, h) = OrbitAt(i, t);
                        var (ta, th) = OrbitAt(i, t - 0.16);
                        Fx.MoveBeam(rig.Beams[i], At(o, MathF.Cos(a) * 24f, MathF.Sin(a) * 24f, h), At(o, MathF.Cos(ta) * 24f, MathF.Sin(ta) * 24f, th));
                        if (rainbow) Fx.SetBeamColor(rig.Beams[i], AuraColor(aura.Color, t, i, baseColor));
                    }
                    break;
                case "helix":
                    for (var i = 0; i < rig.Beams.Count; i++)
                    {
                        int strand = i / (HelixPoints - 1), k = i % (HelixPoints - 1);
                        var (a0, h0) = HelixAt(strand, k, HelixPoints, t);
                        var (a1, h1) = HelixAt(strand, k + 1, HelixPoints, t);
                        Fx.MoveBeam(rig.Beams[i], At(o, MathF.Cos(a0) * 20f, MathF.Sin(a0) * 20f, h0), At(o, MathF.Cos(a1) * 20f, MathF.Sin(a1) * 20f, h1));
                        if (rainbow) Fx.SetBeamColor(rig.Beams[i], AuraColor(aura.Color, t, i, baseColor));
                    }
                    break;
                default: // rings
                    for (var ring = 0; ring < RingCount; ring++)
                    {
                        var (h, r) = RingAt(ring, RingCount, t);
                        for (var s = 0; s < RingSegments; s++)
                        {
                            var i = ring * RingSegments + s;
                            if (i >= rig.Beams.Count) break;
                            float a0 = MathF.PI * 2 * s / RingSegments, a1 = MathF.PI * 2 * (s + 1) / RingSegments;
                            Fx.MoveBeam(rig.Beams[i], At(o, MathF.Cos(a0) * r, MathF.Sin(a0) * r, h), At(o, MathF.Cos(a1) * r, MathF.Sin(a1) * r, h));
                            if (rainbow && s == 0)
                                foreach (var b in rig.Beams.Skip(ring * RingSegments).Take(RingSegments))
                                    Fx.SetBeamColor(b, AuraColor(aura.Color, t, ring * 3, baseColor));
                        }
                    }
                    break;
            }
        }
        catch
        {
            RemoveAuraRig(slot); // cosmetic: rebuilt on the next effects tick
        }
    }

    private void RemoveAuraRig(int slot)
    {
        if (_auraRig[slot] is not { } rig) return;
        foreach (var b in rig.Beams) Fx.SafeRemove(b);
        _auraRig[slot] = null;
    }
}
