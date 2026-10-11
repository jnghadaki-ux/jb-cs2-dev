using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Effects;

/// <summary>
/// Ports of Mods/UltimateJailbreak/Effects/*.sp. Names match the originals because LRs and days look effects up by name.
/// "Third Person" is not registered: CS2 servers cannot switch a client to third person (the client command is
/// cheat-protected and there is no server-side camera control in CounterStrikeSharp) - see FEATURE_GAPS.md.
/// </summary>
internal static class BuiltInEffects
{
    public const string Drugs = "Drugs";
    public const string Drunk = "Drunk";
    public const string FarOut = "Far Out";
    public const string HsOnly = "HS Only";
    public const string LowGravity = "Low Gravity";
    public const string Speed = "Speed";

    public static void RegisterAll(EffectsModule module, EffectsConfig cfg)
    {
        // ---------------------------------------------------------- Drugs (DEFAULT_SLAP_DELAY 0.75)
        var drugsId = 0;
        drugsId = module.Register(new EffectDefinition
        {
            Name = Drugs,
            DefaultData = 0.75f,
            OnStart = (p, data) =>
            {
                var s = module.StateOf(p);
                s.DrugColor = new[] { 255, 0, 0, 140 };
                s.DrugStep = 0;
                s.SlapDelay = data > 0 ? data : 0.75f;
                s.NextSlap = (float)Server.EngineTime + s.SlapDelay;
                var interval = 1f / Math.Clamp(cfg.DrugsUpdatesPerSecond, 2f, 64f);
                // Original stepped 3 colour units every 0.01 s; keep the same cycle speed at a lower update rate.
                var step = Math.Max(3, (int)Math.Round(3 * interval / 0.01f));
                var slot = p.Slot;
                EffectsModule.SetEffectTimer(p, drugsId, Round.Timer(interval, () => DrugsTick(module, slot, step), repeat: true));
            },
            OnStop = p =>
            {
                var c = p.Controller;
                if (c != null) Fx.Fade(c, Color.FromArgb(255, 1, 1, 1), 0f, 0f, Fx.FadePurge);
            }
        });

        // ---------------------------------------------------------- Drunk
        var drunkId = 0;
        drunkId = module.Register(new EffectDefinition
        {
            Name = Drunk,
            OnStart = (p, _) =>
            {
                var s = module.StateOf(p);
                s.DrunkFov = 90;
                s.DrunkIncreaseFov = false;
                s.DrunkAngle = 0;
                s.DrunkNextView = 0;
                var slot = p.Slot;
                EffectsModule.SetEffectTimer(p, drunkId, Round.Timer(1f / 16f, () => DrunkTick(module, slot), repeat: true));
            },
            OnStop = p =>
            {
                var c = p.Controller;
                if (c != null) Fx.SetFov(c, 0);
            }
        });

        // ---------------------------------------------------------- Far Out (FOV 160)
        module.Register(new EffectDefinition
        {
            Name = FarOut,
            OnStart = (p, _) =>
            {
                var c = p.Controller;
                if (c != null) Fx.SetFov(c, cfg.FarOutFov);
            },
            OnStop = p =>
            {
                var c = p.Controller;
                if (c != null) Fx.SetFov(c, 0);
            }
        });

        // ---------------------------------------------------------- HS Only (player only takes headshot / knife damage)
        module.HeadshotsOnlyId = module.Register(new EffectDefinition
        {
            Name = HsOnly,
            DefaultData = 1.5f,
            OnStart = (_, _) => { },
            OnStop = _ => { }
        });

        // ---------------------------------------------------------- Low Gravity (0.4)
        module.Register(new EffectDefinition
        {
            Name = LowGravity,
            OnStart = (p, _) => Movement.SetGravity(p.Controller, "effect.lowgravity", cfg.LowGravityValue),
            OnStop = p => Movement.ClearGravity(p.Controller, "effect.lowgravity")
        });

        // ---------------------------------------------------------- Speed (default 1.5)
        module.Register(new EffectDefinition
        {
            Name = Speed,
            DefaultData = 1.5f,
            OnStart = (p, data) => Movement.SetSpeed(p.Controller, "effect.speed", data > 0 ? data : 1.5f),
            OnStop = p => Movement.ClearSpeed(p.Controller, "effect.speed")
        });
    }

    private static void DrugsTick(EffectsModule module, int slot, int step)
    {
        var p = Players.Get(slot);
        var c = p.Controller;
        if (c == null || !c.IsAliveP()) return;
        var s = module.StateOf(p);
        var now = (float)Server.EngineTime;
        if (now >= s.NextSlap)
        {
            c.Slap(0);
            s.NextSlap = now + s.SlapDelay;
        }

        ref var col = ref s.DrugColor;
        switch (s.DrugStep)
        {
            case 0: col[2] += step; if (col[2] >= 255) { col[2] = 255; s.DrugStep++; } break; // increase blue
            case 1: col[0] -= step; if (col[0] <= 0) { col[0] = 0; s.DrugStep++; } break;     // decrease red
            case 2: col[1] += step; if (col[1] >= 255) { col[1] = 255; s.DrugStep++; } break; // increase green
            case 3: col[2] -= step; if (col[2] <= 0) { col[2] = 0; s.DrugStep++; } break;     // decrease blue
            case 4: col[0] += step; if (col[0] >= 255) { col[0] = 255; s.DrugStep++; } break; // increase red
            default: col[1] -= step; if (col[1] <= 0) { col[1] = 0; s.DrugStep = 0; } break;  // decrease green
        }

        Fx.Fade(c, Color.FromArgb(col[3], col[0], col[1], col[2]), 0f, 0f, Fx.FadeStayOut | Fx.FadePurge);
    }

    /// <summary>
    /// Drunk: FOV swings between 30 and 150 (original per-think +-1..2). The original also rolled the camera to +-45..60
    /// degrees every 0.8 s with a short dark fade at each flip; CS2 does not let the server roll the client camera, so
    /// the flip keeps the dark fade and adds a short screen shake instead.
    /// </summary>
    private static void DrunkTick(EffectsModule module, int slot)
    {
        var p = Players.Get(slot);
        var c = p.Controller;
        if (c == null || !c.IsAliveP()) return;
        var s = module.StateOf(p);
        if (s.DrunkFov <= 30) s.DrunkIncreaseFov = true;
        else if (s.DrunkFov >= 150) s.DrunkIncreaseFov = false;
        s.DrunkFov += (s.DrunkIncreaseFov ? 1 : -1) * Random.Shared.Next(4, 9);
        Fx.SetFov(c, Math.Clamp(s.DrunkFov, 30, 150));

        var now = (float)Server.EngineTime;
        if (now < s.DrunkNextView) return;
        s.DrunkNextView = now + 0.8f;
        if (s.DrunkAngle <= -60) s.DrunkIncreaseAngle = true;
        else if (s.DrunkAngle >= 60) s.DrunkIncreaseAngle = false;
        s.DrunkAngle += s.DrunkIncreaseAngle ? 5 : -5;
        var flipped = false;
        if (s.DrunkIncreaseAngle && s.DrunkAngle > -45 && s.DrunkAngle < 45) { s.DrunkAngle = 45; flipped = true; }
        else if (!s.DrunkIncreaseAngle && s.DrunkAngle < 45 && s.DrunkAngle > -45) { s.DrunkAngle = -45; flipped = true; }
        if (flipped) Fx.Fade(c, Color.FromArgb(255, 5, 5, 5), 1f, 0f, Fx.FadeIn);
        Fx.Shake(c, 3f, 1.5f, 0.8f);
    }
}
