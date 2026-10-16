using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API.Core;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.PlayerExtras;

/// <summary>
/// Equipment effects (SMPlugins Item_EquipmentEffects): one effect, drawn from the emitter points of every hat, face and
/// back item the player wears - jetpack nozzles, wing tips, crown points, glasses lenses, along the katana blade.
/// StoreItem.Data is the style: flames, sparks, electric or twinkle. Beam-drawn, so it always renders.
/// </summary>
public sealed partial class StoreModule
{
    internal static readonly string[] GearEffectStyles = { "flames", "sparks", "electric", "twinkle" };

    /// <summary>An emitter in the model's own space (+X forward, +Y left, +Z up) and the way its effect points.</summary>
    internal readonly record struct Emitter(Vector3 At, Vector3 Dir);

    private static Emitter E(float x, float y, float z, float dx = 0, float dy = 0, float dz = 1) =>
        new(new Vector3(x, y, z), Vector3.Normalize(new Vector3(dx, dy, dz)));

    private static Emitter[] Both(float x, float y, float z, float dx = 0, float dy = 0, float dz = 1) =>
        new[] { E(x, y, z, dx, dy, dz), E(x, -y, z, dx, -dy, dz) };

    /// <summary>
    /// Emitters of the pack models by file name (tools/workshop-hats/hats.py, tools/store-pack-2/equipment.py write the
    /// same models). A model not listed (an admin's own) emits from its origin.
    /// </summary>
    internal static readonly Dictionary<string, Emitter[]> GearEmitters = new(StringComparer.OrdinalIgnoreCase)
    {
        // hats: origin on top of the head
        ["top_hat"] = new[] { E(0, 0, 7.9f) },
        ["crown"] = Enumerable.Range(0, 4).Select(i => (i * 2 + 0.5f) * MathF.PI / 4)
            .Select(a => E(5.1f * MathF.Cos(a), 5.1f * MathF.Sin(a), 5.1f)).ToArray(),
        ["party_hat"] = new[] { E(0, 0, 11.5f) },
        ["halo"] = Enumerable.Range(0, 6).Select(i => i * MathF.PI / 3)
            .Select(a => E(4.6f * MathF.Cos(a), 4.6f * MathF.Sin(a), 5f)).ToArray(),
        ["beanie"] = new[] { E(0, 0, 6.8f) },
        ["cowboy_hat"] = Both(0, 9.4f, 1.4f, 0, 0.3f, 1).Append(E(0, 0, 6.2f)).ToArray(),
        // face: origin between the eyes
        ["sunglasses"] = Both(0.2f, 1.7f, -0.2f, 1, 0, 0),
        ["round_glasses"] = Both(0.2f, 1.65f, 0, 1, 0, 0),
        ["bandit_mask"] = Both(0.2f, 1.78f, 0, 1, 0, 0),
        ["mustache"] = Both(-0.05f, 2.35f, -1.25f, 0, 0.5f, 1),
        ["clown_nose"] = new[] { E(2.15f, 0, -1.25f, 1, 0, 0) },
        ["eyepatch"] = new[] { E(0.4f, -1.65f, 0, 1, 0, 0) },
        // back: origin on the upper back
        ["angel_wings"] = Both(-6.3f, 20.4f, -0.6f, -0.3f, 0.3f, 1).Concat(Both(-6.1f, 16.4f, 11.6f, -0.3f, 0.3f, 1))
            .Concat(Both(-4.7f, 4.9f, 13.6f, -0.3f, 0, 1)).ToArray(),
        ["bat_wings"] = Both(-4.0f, 16f, 10.5f, 0, 0.4f, 1).Concat(Both(-4.2f, 17.5f, 4f, 0, 1, 0.3f))
            .Concat(Both(-3.6f, 9f, 13f, 0, 0, 1)).ToArray(),
        ["jetpack"] = Both(-3.9f, 2.3f, -8.2f, 0, 0, -1),
        ["backpack"] = new[] { E(-3.3f, 0, 6.8f), E(-6.1f, 0, -1.6f, -1, 0, 0.3f) },
        // handle over the right shoulder (-3.2, -5.5, 9), the blade runs along (0, 0.6, -0.8) to the left hip
        ["katana"] = new[] { 12f, 19f, 26f, 33f }.Select(d => E(-3.2f, -5.5f + 0.6f * d, 9f - 0.8f * d)).ToArray()
    };

    private static readonly Emitter[] OriginOnly = { E(0, 0, 0) };

    internal static Emitter[] EmittersOf(string modelPath) =>
        GearEmitters.TryGetValue(Path.GetFileNameWithoutExtension(modelPath.Trim()), out var e) ? e : OriginOnly;

    internal static string GearEffectStyle(StoreItem item)
    {
        var s = item.Data.Trim().ToLowerInvariant();
        return GearEffectStyles.Contains(s) ? s : "sparks";
    }

    /// <summary>
    /// Engine-free: a model-space point in the world for an entity at <paramref name="origin"/> with Source angles
    /// (pitch down-positive, yaw, roll), the same rotation as the engine (R = Rz(yaw) Ry(pitch) Rx(roll)).
    /// </summary>
    internal static Vector3 ToWorld(Vector3 origin, float pitch, float yaw, float roll, Vector3 local) =>
        origin + Rotate(pitch, yaw, roll, local);

    internal static Vector3 Rotate(float pitch, float yaw, float roll, Vector3 v)
    {
        float p = pitch * MathF.PI / 180f, y = yaw * MathF.PI / 180f, r = roll * MathF.PI / 180f;
        // Rx(roll)
        v = new Vector3(v.X, v.Y * MathF.Cos(r) - v.Z * MathF.Sin(r), v.Y * MathF.Sin(r) + v.Z * MathF.Cos(r));
        // Ry(pitch): positive pitch tips +X downwards
        v = new Vector3(v.X * MathF.Cos(p) + v.Z * MathF.Sin(p), v.Y, -v.X * MathF.Sin(p) + v.Z * MathF.Cos(p));
        // Rz(yaw)
        return new Vector3(v.X * MathF.Cos(y) - v.Y * MathF.Sin(y), v.X * MathF.Sin(y) + v.Y * MathF.Cos(y), v.Z);
    }

    /// <summary>Effects tick: the equipped equipment effect on every worn hat / face / back piece.</summary>
    private void GearEffectTick(CCSPlayerController c, StoreItem fx)
    {
        var style = GearEffectStyle(fx);
        var every = style switch { "flames" => 2, "electric" => 3, "twinkle" => 3, _ => 2 };
        if (_tick % every != 0) return;
        foreach (var kind in GearTypes)
        {
            if (_gear[kind][c.Slot] is not { IsValid: true } gear || Find(_gearId[kind][c.Slot] ?? "") is not { } item) continue;
            if (gear.AbsOrigin is not { } go || gear.AbsRotation is not { } ga) continue;
            var origin = new Vector3(go.X, go.Y, go.Z);
            // Hats and face pieces sit in front of their wearer's eyes: hidden from the wearer like the pieces themselves.
            var filter = ViewFilter(c, kind != StoreItemType.Back);
            var emitters = EmittersOf(item.Data);
            // At most three emitters per piece and pulse (wings have six): less clutter, fewer beams.
            foreach (var em in emitters.Length <= 3 ? emitters : emitters.OrderBy(_ => Random.Shared.Next()).Take(3))
            {
                var at = ToWorld(origin, ga.X, ga.Y, ga.Z, em.At);
                var dir = Rotate(ga.X, ga.Y, ga.Z, em.Dir);
                GearEffect(style, fx, V(at), dir, filter);
            }
        }
    }

    private static Vector V(Vector3 v) => new(v.X, v.Y, v.Z);

    private static Vector Along(Vector p, Vector3 d, float k, float jitter = 0) =>
        new(p.X + d.X * k + Jit(jitter), p.Y + d.Y * k + Jit(jitter), p.Z + d.Z * k + Jit(jitter));

    private static float Jit(float j) => j <= 0 ? 0 : ((float)Random.Shared.NextDouble() * 2 - 1) * j;

    private void GearEffect(string style, StoreItem fx, Vector at, Vector3 dir, Func<CCSPlayerController, bool>? filter)
    {
        var color = ColorOf(fx.Color, Random.Shared);
        switch (style)
        {
            case "flames":
            {
                // Flames follow the emitter (jetpack nozzles blow down) and lick slightly upward.
                var fire = fx.Color == "255,255,255";
                for (var i = 0; i < 2; i++)
                {
                    var len = 6f + (float)Random.Shared.NextDouble() * 8f;
                    var tip = Along(at, dir, len, 1.5f);
                    tip.Z += len * 0.25f;
                    Restrict(Fx.Beam(at, tip, fire ? FireColor() : Mix(color, Color.White, (float)Random.Shared.NextDouble() * 0.4f), 2.6f, 0.3f, 0.3f), filter);
                }
                break;
            }
            case "electric":
            {
                Vector prev = at;
                var light = Mix(color, Color.White, 0.6f);
                for (var s = 1; s <= 3; s++)
                {
                    var next = Along(at, dir, s * 3.2f, 2.5f);
                    Restrict(Fx.Beam(prev, next, light, 0.8f, 0.2f), filter);
                    Restrict(Fx.Beam(prev, next, color, 2.2f, 0.2f), filter);
                    prev = next;
                }
                break;
            }
            case "twinkle":
            {
                var p = Along(at, dir, 1.5f, 1.5f);
                var s = 1.5f + (float)Random.Shared.NextDouble() * 1.5f;
                Restrict(Fx.Beam(At(p, 0, 0, -s), At(p, 0, 0, s), Mix(color, Color.White, 0.4f), 0.8f, 0.3f), filter);
                Restrict(Fx.Beam(At(p, -s, 0, 0), At(p, s, 0, 0), color, 0.8f, 0.3f), filter);
                Restrict(Fx.Beam(At(p, 0, -s, 0), At(p, 0, s, 0), color, 0.8f, 0.3f), filter);
                break;
            }
            default: // sparks
                for (var i = 0; i < 2; i++)
                {
                    var tip = Along(at, dir, 3f + (float)Random.Shared.NextDouble() * 5f, 4f);
                    Restrict(Fx.Beam(at, tip, Mix(color, Color.White, 0.3f), 1f, 0.1f, 0.2f), filter);
                }
                break;
        }
    }
}
