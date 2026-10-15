using System.Drawing;
using CounterStrikeSharp.API.Core;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>
/// Store effects drawn with beams only, so they always render (particles need precaching and exact CS2 paths; the
/// optional particles are extras on top). Every effect respects the owner's view filter (store settings).
/// </summary>
public sealed partial class StoreModule
{
    /// <summary>The particle files the store can add on top of the beam effects (precached at map start).</summary>
    internal static readonly string[] Particles =
    {
        "particles/explosions_fx/explosion_basic.vpcf",
        "particles/burning_fx/burning_character_b.vpcf"
    };

    private static Color Dim(Color c, float k) => Color.FromArgb(255, (int)(c.R * k), (int)(c.G * k), (int)(c.B * k));

    private static Color Mix(Color a, Color b, float t) =>
        Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    private static Vector At(Vector o, float dx, float dy, float dz) => new(o.X + dx, o.Y + dy, o.Z + dz);

    private void Later(float seconds, Action a) => Plugin.AddTimer(seconds, () =>
    {
        try { a(); }
        catch { /* cosmetic */ }
    });

    // ------------------------------------------------------------------ kill effects

    /// <summary>Kill effect at the victim's feet <paramref name="o"/>; style from the item's Data.</summary>
    private void KillEffect(StoreItem fx, Vector o, Func<CCSPlayerController, bool>? filter)
    {
        var color = ColorOf(fx.Color, Random.Shared);
        switch (fx.Data.Trim().ToLowerInvariant())
        {
            case "fire": FireKill(o, filter); break;
            case "explosion": ExplosionKill(o, color, filter); break;
            case "lightning": LightningKill(o, fx.Color == "255,255,255" ? Color.FromArgb(255, 170, 210, 255) : color, filter); break;
            case "souls": SoulsKill(o, fx.Color == "255,255,255" ? Color.FromArgb(255, 200, 240, 255) : color, filter); break;
            default: ShockwaveKill(o, color, filter); break;
        }
    }

    /// <summary>Two expanding rings (floor and chest height) and a short light pillar.</summary>
    private void ShockwaveKill(Vector o, Color color, Func<CCSPlayerController, bool>? filter)
    {
        Fx.PulseAt(At(o, 0, 0, 6), color, 8f, 230f, 0.6f, 5f, filter, 20);
        Later(0.12f, () => Fx.PulseAt(At(o, 0, 0, 40), Mix(color, Color.White, 0.4f), 6f, 150f, 0.5f, 3f, filter, 16));
        Restrict(Fx.Beam(At(o, 0, 0, 0), At(o, 0, 0, 110), Mix(color, Color.White, 0.5f), 6f, 1f, 0.35f), filter);
    }

    /// <summary>Flames that lick upward in three waves (orange core, red tips) plus the burning particle on top.</summary>
    private void FireKill(Vector o, Func<CCSPlayerController, bool>? filter)
    {
        var colors = new[] { Color.FromArgb(255, 255, 220, 60), Color.FromArgb(255, 255, 140, 20), Color.FromArgb(255, 230, 50, 20) };
        for (var wave = 0; wave < 4; wave++)
        {
            Later(wave * 0.22f, () =>
            {
                for (var i = 0; i < 9; i++)
                {
                    var a = Random.Shared.NextDouble() * Math.PI * 2;
                    var r = (float)Random.Shared.NextDouble() * 18f;
                    var b = At(o, (float)Math.Cos(a) * r, (float)Math.Sin(a) * r, 2);
                    var h = 25f + (float)Random.Shared.NextDouble() * 45f;
                    var tip = At(b, Random.Shared.Next(-6, 7), Random.Shared.Next(-6, 7), h);
                    Restrict(Fx.Beam(b, tip, colors[Random.Shared.Next(colors.Length)], 5f, 0.5f, 0.3f), filter);
                }
            });
        }

        Restrict(Fx.Particle(Particles[1], At(o, 0, 0, 10), 2.5f), filter);
    }

    /// <summary>A starburst of spikes, a white flash pillar and a shockwave, plus the explosion particle.</summary>
    private void ExplosionKill(Vector o, Color color, Func<CCSPlayerController, bool>? filter)
    {
        var centre = At(o, 0, 0, 30);
        var hot = Color.FromArgb(255, 255, 230, 120);
        for (var i = 0; i < 16; i++)
        {
            var yaw = i * Math.PI * 2 / 16 + Random.Shared.NextDouble() * 0.3;
            var up = (Random.Shared.NextDouble() - 0.25) * 1.2;
            var len = 70f + (float)Random.Shared.NextDouble() * 50f;
            var end = At(centre, (float)(Math.Cos(yaw) * Math.Cos(up)) * len, (float)(Math.Sin(yaw) * Math.Cos(up)) * len, (float)Math.Sin(up) * len);
            Restrict(Fx.Beam(centre, end, i % 2 == 0 ? hot : color, 6f, 0.5f, 0.35f), filter);
        }

        Restrict(Fx.Beam(At(o, 0, 0, 0), At(o, 0, 0, 160), Color.White, 14f, 2f, 0.15f), filter);
        Fx.PulseAt(At(o, 0, 0, 8), color, 20f, 260f, 0.45f, 6f, filter, 20);
        Restrict(Fx.Particle(Particles[0], centre, 2f), filter);
    }

    /// <summary>A jagged bolt from the sky to the body, struck twice, with a ground flash.</summary>
    private void LightningKill(Vector o, Color color, Func<CCSPlayerController, bool>? filter)
    {
        void Strike()
        {
            var prev = At(o, Random.Shared.Next(-40, 41), Random.Shared.Next(-40, 41), 420);
            for (var i = 1; i <= 8; i++)
            {
                var t = i / 8f;
                var next = i == 8 ? At(o, 0, 0, 20) : At(o, Random.Shared.Next(-26, 27) * (1 - t), Random.Shared.Next(-26, 27) * (1 - t), 420 * (1 - t) + 20 * t);
                Restrict(Fx.Beam(prev, next, Mix(color, Color.White, 0.6f), 4f, 0.2f), filter);
                Restrict(Fx.Beam(prev, next, color, 9f, 0.2f), filter);
                prev = next;
            }

            Fx.PulseAt(At(o, 0, 0, 4), color, 10f, 110f, 0.3f, 4f, filter, 14);
        }

        Strike();
        Later(0.18f, Strike);
    }

    /// <summary>Three wisps spiralling upward from the body.</summary>
    private void SoulsKill(Vector o, Color color, Func<CCSPlayerController, bool>? filter)
    {
        for (var w = 0; w < 3; w++)
        {
            var phase = w * Math.PI * 2 / 3;
            Vector? last = null;
            for (var s = 0; s <= 14; s++)
            {
                var step = s;
                Later(step * 0.07f, () =>
                {
                    var a = phase + step * 0.55;
                    var r = 14f + step * 1.2f;
                    var p = At(o, (float)Math.Cos(a) * r, (float)Math.Sin(a) * r, 20 + step * 9f);
                    if (last != null) Restrict(Fx.Beam(last, p, Mix(color, Color.White, step / 14f), 3f, 1f, 0.45f), filter);
                    last = p;
                });
            }
        }
    }

    // ------------------------------------------------------------------ polished continuous effects

    /// <summary>Tracer: bright core, soft glow and a small spark where the bullet landed.</summary>
    private void Tracer(CCSPlayerController c, Vector impact, StoreItem tracer)
    {
        var eye = c.EyePosition();
        var start = new Vector(eye.X, eye.Y, eye.Z - 4f);
        var color = ColorOf(tracer.Color, Random.Shared);
        var life = Math.Max(0.05f, Cfg.TracerSeconds);
        var filter = ViewFilter(c);
        Restrict(Fx.Beam(start, impact, Dim(color, 0.55f), 3.5f, 1.5f, life * 0.8f), filter);
        Restrict(Fx.Beam(start, impact, Mix(color, Color.White, 0.35f), 1f, 0.6f, life), filter);
        for (var i = 0; i < 3; i++)
            Restrict(Fx.Beam(impact, At(impact, Random.Shared.Next(-6, 7), Random.Shared.Next(-6, 7), Random.Shared.Next(-6, 7)), color, 1.2f, 0.2f, 0.15f), filter);
    }

    /// <summary>Sparkles: little twinkling crosses around the body.</summary>
    private void Sparkle(CCSPlayerController c, Vector o, StoreItem item)
    {
        var filter = ViewFilter(c);
        for (var i = 0; i < 2; i++)
        {
            var p = At(o, Random.Shared.Next(-16, 17), Random.Shared.Next(-16, 17), Random.Shared.Next(8, 70));
            var color = ColorOf(item.Color, Random.Shared);
            var s = 2.5f + (float)Random.Shared.NextDouble() * 2f;
            Restrict(Fx.Beam(At(p, 0, 0, -s), At(p, 0, 0, s), Mix(color, Color.White, 0.3f), 1.2f, 0.25f), filter);
            Restrict(Fx.Beam(At(p, -s, 0, 0), At(p, s, 0, 0), color, 1.2f, 0.25f), filter);
        }
    }

    /// <summary>Aura: a floor ring and a fainter waist ring pulsing outward.</summary>
    private void Aura(CCSPlayerController c, Vector o, StoreItem aura)
    {
        var color = ColorOf(aura.Color, Random.Shared);
        var filter = ViewFilter(c);
        Fx.PulseAt(At(o, 0, 0, 3), color, 20f, 48f, 0.9f, 2.5f, filter, 14);
        Fx.PulseAt(At(o, 0, 0, 34), Dim(color, 0.6f), 16f, 30f, 0.9f, 1.5f, filter, 12);
    }
}
