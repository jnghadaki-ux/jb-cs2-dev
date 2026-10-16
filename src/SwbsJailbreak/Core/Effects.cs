using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Core;

/// <summary>
/// Visual / audio helpers replacing SourceMod temp entities (TE_SetupBeamPoints, TE_SetupBeamRingPoint),
/// screen fade/shake user messages and EmitSound.
/// CS2 has no temp-entity API, so beams are real "beam" entities that are removed after their lifetime.
/// </summary>
public static class Fx
{
    // ------------------------------------------------------------------ beams

    /// <summary>Create a beam between two points. lifetime &lt;= 0 keeps it until removed by the caller.</summary>
    public static CBeam? Beam(Vector start, Vector end, Color color, float width = 2f, float lifetime = 0f) =>
        Beam(start, end, color, width, width, lifetime);

    /// <summary>A beam that tapers from <paramref name="width"/> at the start to <paramref name="endWidth"/> at the end.</summary>
    public static CBeam? Beam(Vector start, Vector end, Color color, float width, float endWidth, float lifetime)
    {
        try
        {
            var beam = Utilities.CreateEntityByName<CBeam>("beam");
            if (beam == null) return null;
            beam.RenderMode = color.A < 255 ? RenderMode_t.kRenderTransAlpha : RenderMode_t.kRenderNormal;
            beam.Render = color;
            beam.Width = width;
            beam.EndWidth = endWidth;
            beam.Teleport(start, new QAngle(0, 0, 0), new Vector(0, 0, 0));
            beam.EndPos.X = end.X;
            beam.EndPos.Y = end.Y;
            beam.EndPos.Z = end.Z;
            beam.DispatchSpawn();
            if (lifetime > 0) RemoveLater(beam, lifetime);
            return beam;
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogWarning(ex, "Failed to create beam");
            return null;
        }
    }

    /// <summary>Move an existing beam (start via Teleport, end via networked m_vecEndPos).</summary>
    public static void MoveBeam(CBeam? beam, Vector start, Vector end)
    {
        if (beam == null || !beam.IsValid) return;
        beam.Teleport(start, new QAngle(0, 0, 0), new Vector(0, 0, 0));
        beam.EndPos.X = end.X;
        beam.EndPos.Y = end.Y;
        beam.EndPos.Z = end.Z;
        Utilities.SetStateChanged(beam, "CBeam", "m_vecEndPos");
    }

    public static void SetBeamColor(CBeam? beam, Color color)
    {
        if (beam == null || !beam.IsValid) return;
        beam.Render = color;
        Utilities.SetStateChanged(beam, "CBaseModelEntity", "m_clrRender");
    }

    /// <summary>A flat ring built from beam segments (replacement for TE_SetupBeamRingPoint).</summary>
    public static List<CBeam> Ring(Vector center, float radius, Color color, float width = 2f, float lifetime = 0f, int segments = 20)
    {
        var list = new List<CBeam>(segments);
        var pts = RingPoints(center, radius, segments);
        for (var i = 0; i < segments; i++)
        {
            var b = Beam(pts[i], pts[(i + 1) % segments], color, width, lifetime);
            if (b != null) list.Add(b);
        }

        return list;
    }

    public static void MoveRing(List<CBeam> ring, Vector center, float radius)
    {
        if (ring.Count == 0) return;
        var pts = RingPoints(center, radius, ring.Count);
        for (var i = 0; i < ring.Count; i++)
            MoveBeam(ring[i], pts[i], pts[(i + 1) % ring.Count]);
    }

    private static Vector[] RingPoints(Vector c, float r, int segments)
    {
        var pts = new Vector[segments];
        for (var i = 0; i < segments; i++)
        {
            var a = 2 * MathF.PI * i / segments;
            pts[i] = new Vector(c.X + r * MathF.Cos(a), c.Y + r * MathF.Sin(a), c.Z);
        }

        return pts;
    }

    /// <summary>
    /// SourceMod "beacon": a ring around the player's feet that expands from startRadius to endRadius over
    /// `duration` seconds, then disappears. Plays the beacon sound if given.
    /// </summary>
    public static void BeaconPulse(CCSPlayerController player, Color color, float startRadius = 10f, float endRadius = 375f,
        float duration = 0.5f, string? sound = null)
    {
        var origin = player.Origin();
        if (origin == null) return;
        PulseAt(new Vector(origin.X, origin.Y, origin.Z + 10f), color, startRadius, endRadius, duration);
        if (!string.IsNullOrEmpty(sound)) Sound.PlayAt(player, sound);
    }

    /// <summary>An expanding ring at a point (BeaconPulse without a player, e.g. where someone died).</summary>
    public static void PulseAt(Vector center, Color color, float startRadius = 10f, float endRadius = 375f, float duration = 0.5f, float width = 3f,
        Func<CCSPlayerController, bool>? visibleTo = null, int segments = 16)
    {
        var ring = Ring(center, startRadius, color, width, duration + 0.1f, segments);
        if (visibleTo != null)
            foreach (var b in ring) Visibility.OnlyFor(b, visibleTo);
        const int steps = 5;
        var plugin = JailbreakPlugin.Instance;
        if (plugin != null)
        {
            for (var s = 1; s <= steps; s++)
            {
                var r = startRadius + (endRadius - startRadius) * s / steps;
                plugin.AddTimer(duration * s / steps, () =>
                {
                    if (ring.All(b => b.IsValid)) MoveRing(ring, center, r);
                });
            }
        }
    }

    /// <summary>A model prop without collision (workshop pack models), optionally tinted. Null when it cannot be created.</summary>
    public static CBaseModelEntity? Prop(string model, Vector pos, QAngle angles, Color? tint = null)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        try
        {
            var prop = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
            if (prop == null) return null;
            prop.Spawnflags = 256; // start with collision disabled
            if (tint is { } c) prop.Render = c;
            SpawnWithModel(prop, model);
            prop.Teleport(pos, angles, new Vector(0, 0, 0));
            return prop;
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogWarning(ex, "Failed to create prop {Model}", model);
            return null;
        }
    }

    /// <summary>
    /// Spawn an entity created with CreateEntityByName with its model given as a spawn key value. Calling SetModel
    /// before DispatchSpawn sets up the skeleton of an entity that is still in the staging list, which fails the
    /// engine assertion "Assertion Failed in function SetupModel(): 0 == ( ... & EF_IN_STAGING_LIST )" and floods the
    /// server console (every trail piece, splat, hat and glow did it).
    /// </summary>
    public static void SpawnWithModel(CBaseEntity entity, string model)
    {
        var kv = new CEntityKeyValues();
        kv.SetString("model", model.Trim());
        entity.DispatchSpawn(kv);
    }

    public static void Tint(CBaseModelEntity? prop, Color color)
    {
        if (prop is not { IsValid: true } || prop.Render == color) return;
        prop.Render = color;
        Utilities.SetStateChanged(prop, "CBaseModelEntity", "m_clrRender");
    }

    public static void RemoveLater(CEntityInstance ent, float seconds)
    {
        // NativeEntity resolves its pointer from the entity handle on every access, so holding the wrapper is safe:
        // IsValid turns false once the entity is gone.
        JailbreakPlugin.Instance?.AddTimer(seconds, () =>
        {
            if (ent.IsValid) ent.Remove();
        });
    }

    public static void SafeRemove(CEntityInstance? ent)
    {
        if (ent is { IsValid: true }) ent.Remove();
    }

    // ------------------------------------------------------------------ particles

    /// <summary>Spawn an info_particle_system at a position (e.g. "particles/explosions_fx/explosion_c4_500.vpcf").</summary>
    public static CParticleSystem? Particle(string effect, Vector pos, float lifetime = 5f)
    {
        try
        {
            var p = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
            if (p == null) return null;
            p.EffectName = effect;
            p.StartActive = true;
            p.Teleport(pos, new QAngle(0, 0, 0), new Vector(0, 0, 0));
            p.DispatchSpawn();
            p.AcceptInput("Start");
            if (lifetime > 0) RemoveLater(p, lifetime);
            return p;
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogWarning(ex, "Failed to create particle {Effect}", effect);
            return null;
        }
    }

    // ------------------------------------------------------------------ screen effects (user messages)

    private static int _fadeId = -1;
    private static int _shakeId = -1;

    private static int FadeId
    {
        get
        {
            if (_fadeId > 0) return _fadeId;
            try { _fadeId = UserMessage.FindIdByName("CUserMessageFade"); }
            catch { _fadeId = 0; }
            if (_fadeId <= 0) _fadeId = 106;
            return _fadeId;
        }
    }

    private static int ShakeId
    {
        get
        {
            if (_shakeId > 0) return _shakeId;
            try { _shakeId = UserMessage.FindIdByName("CUserMessageShake"); }
            catch { _shakeId = 0; }
            if (_shakeId <= 0) _shakeId = 120;
            return _shakeId;
        }
    }

    public const int FadeIn = 0x0001;      // FFADE_IN
    public const int FadeOut = 0x0002;     // FFADE_OUT
    public const int FadeModulate = 0x0004;
    public const int FadeStayOut = 0x0008; // FFADE_STAYOUT
    public const int FadePurge = 0x0010;   // FFADE_PURGE

    /// <summary>Screen fade. durationSec/holdSec use the engine's 1/512s fixed point internally.</summary>
    public static void Fade(CCSPlayerController player, Color color, float durationSec, float holdSec, int flags = FadeIn | FadePurge)
    {
        if (!player.IsValid || player.IsBot) return;
        try
        {
            using var msg = UserMessage.FromId(FadeId);
            msg.SetInt("duration", (int)(durationSec * 512));
            msg.SetInt("hold_time", (int)(holdSec * 512));
            msg.SetInt("flags", flags);
            msg.SetInt("color", color.R | (color.G << 8) | (color.B << 16) | (color.A << 24));
            msg.Recipients.Add(player);
            msg.Send();
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogDebug(ex, "Fade usermessage failed");
        }
    }

    public static void ClearFade(CCSPlayerController player) =>
        Fade(player, Color.FromArgb(0, 0, 0, 0), 0f, 0f, FadeIn | FadePurge);

    /// <summary>Screen shake (command 0 = start, 1 = stop).</summary>
    public static void Shake(CCSPlayerController player, float amplitude, float frequency, float durationSec, int command = 0)
    {
        if (!player.IsValid || player.IsBot) return;
        try
        {
            using var msg = UserMessage.FromId(ShakeId);
            msg.SetInt("command", command);
            msg.SetFloat("amplitude", amplitude);
            msg.SetFloat("frequency", frequency);
            msg.SetFloat("duration", durationSec);
            msg.Recipients.Add(player);
            msg.Send();
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogDebug(ex, "Shake usermessage failed");
        }
    }

    /// <summary>Set the player's field of view (0 = default).</summary>
    public static void SetFov(CCSPlayerController player, int fov)
    {
        if (!player.IsValid) return;
        player.DesiredFOV = (uint)Math.Max(0, fov);
        Utilities.SetStateChanged(player, "CBasePlayerController", "m_iDesiredFOV");
    }
}

/// <summary>Sound playback. Names ending in .vsnd are played client-side with "play"; anything else is a sound event.</summary>
public static class Sound
{
    public static void Play(CCSPlayerController? player, string sound, float volume = 1f)
    {
        if (player == null || !player.IsValid || player.IsBot || string.IsNullOrWhiteSpace(sound)) return;
        try
        {
            if (sound.EndsWith(".vsnd", StringComparison.OrdinalIgnoreCase) || sound.Contains('/'))
            {
                player.ExecuteClientCommand($"play {sound}");
                return;
            }

            var pawn = player.PawnOrNull();
            var filter = new RecipientFilter { player };
            if (pawn != null) pawn.EmitSound(sound, filter, volume);
            else player.EmitSound(sound, filter, volume);
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogDebug(ex, "Sound {Sound} failed", sound);
        }
    }

    public static void PlayAll(string sound, float volume = 1f)
    {
        foreach (var p in Players.Humans()) Play(p, sound, volume);
    }

    /// <summary>Positional sound emitted from the player (everyone nearby hears it).</summary>
    public static void PlayAt(CCSPlayerController player, string sound, float volume = 1f)
    {
        if (!player.IsValid || string.IsNullOrWhiteSpace(sound)) return;
        try
        {
            if (sound.EndsWith(".vsnd", StringComparison.OrdinalIgnoreCase) || sound.Contains('/'))
            {
                PlayAll(sound, volume);
                return;
            }

            var pawn = player.PawnOrNull();
            if (pawn != null) pawn.EmitSound(sound, null, volume);
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogDebug(ex, "Sound {Sound} failed", sound);
        }
    }
}
