using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace Swbs.Common;

/// <summary>Where a trail's pieces go.</summary>
public enum TrailPlacement
{
    /// <summary>Flat on the floor, pointing the way the player walks, left and right alternately (footprints only).</summary>
    Footsteps,

    /// <summary>Behind the player's back, MinZ..MaxZ above the feet, randomly turned (every other trail).</summary>
    Back
}

/// <summary>A model trail (store Trail item whose Data names it). Pieces are models/swbs/trails/&lt;piece&gt;.vmdl.</summary>
public sealed record TrailStyle(string[] Pieces, TrailPlacement Placement, float Spacing, float MinZ, float MaxZ, float Seconds,
    bool Tinted, string FallbackColor);

/// <summary>
/// The SWBS model trails (scripts/workshop/trails.py draws the pieces). A Trail item whose Data is one of these names
/// drops that style's pieces behind the player; any other Data is the classic beam trail. Without the workshop pack
/// (or with WorkshopPack.UseModels off) model trails fall back to a beam trail in the item colour (FallbackColor for
/// the untinted fire / ice / galaxy).
/// </summary>
public static class StoreTrails
{
    public const string Folder = "models/swbs/trails";

    public static readonly IReadOnlyDictionary<string, TrailStyle> Styles = new Dictionary<string, TrailStyle>(StringComparer.OrdinalIgnoreCase)
    {
        ["footprints"] = new(new[] { "footprint_l", "footprint_r" }, TrailPlacement.Footsteps, 24f, 0.5f, 0.5f, 3f, true, "40,40,40"),
        ["stars"] = new(new[] { "star" }, TrailPlacement.Back, 30f, 38f, 54f, 1.5f, true, "255,200,40"),
        ["hearts"] = new(new[] { "heart" }, TrailPlacement.Back, 30f, 38f, 54f, 1.5f, true, "255,80,160"),
        ["skulls"] = new(new[] { "skull" }, TrailPlacement.Back, 30f, 38f, 54f, 1.5f, true, "235,235,235"),
        ["lightning"] = new(new[] { "lightning" }, TrailPlacement.Back, 30f, 36f, 56f, 1.2f, true, "90,170,255"),
        ["snowflakes"] = new(new[] { "snowflake" }, TrailPlacement.Back, 30f, 36f, 56f, 1.8f, true, "200,240,255"),
        ["notes"] = new(new[] { "note" }, TrailPlacement.Back, 30f, 38f, 56f, 1.5f, true, "170,90,230"),
        ["coins"] = new(new[] { "coin" }, TrailPlacement.Back, 30f, 38f, 52f, 1.5f, true, "255,190,40"),
        ["orbs"] = new(new[] { "orb" }, TrailPlacement.Back, 26f, 38f, 56f, 1.5f, true, "170,90,230"),
        ["bubbles"] = new(new[] { "bubble" }, TrailPlacement.Back, 26f, 34f, 60f, 1.8f, true, "180,230,255"),
        ["fire"] = new(new[] { "flame_1", "flame_2", "flame_3", "flame_4" }, TrailPlacement.Back, 18f, 36f, 50f, 0.8f, false, "255,120,0"),
        ["ice"] = new(new[] { "ice_1", "ice_2", "ice_3" }, TrailPlacement.Back, 26f, 34f, 46f, 2.0f, false, "150,220,255"),
        ["galaxy"] = new(new[] { "galaxy", "galaxy_stars" }, TrailPlacement.Back, 26f, 38f, 56f, 1.6f, false, "150,80,255")
    };

    /// <summary>Every piece model (precached with the pack).</summary>
    public static IEnumerable<string> AllModels() =>
        Styles.Values.SelectMany(s => s.Pieces).Distinct().Select(ModelOf);

    public static string ModelOf(string piece) => $"{Folder}/{piece}.vmdl";

    public static TrailStyle? StyleOf(StoreItem item) =>
        item.Data.Length > 0 && Styles.TryGetValue(item.Data.Trim(), out var s) ? s : null;

    /// <summary>
    /// Engine-free (unit-tested): the drop points along a move from <paramref name="from"/> (the last drop) to
    /// <paramref name="to"/>, every <paramref name="spacing"/> units, at most <paramref name="max"/>. Returns them and
    /// the new last-drop point (unchanged when nothing was dropped).
    /// </summary>
    internal static (List<(float X, float Y, float Z)> Points, (float X, float Y, float Z) Last) DropPoints(
        (float X, float Y, float Z) from, (float X, float Y, float Z) to, float spacing, int max = 3)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dz = to.Z - from.Z;
        var len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        var pts = new List<(float, float, float)>();
        if (spacing <= 0 || len < spacing) return (pts, from);
        var n = Math.Min(max, (int)(len / spacing));
        for (var i = 1; i <= n; i++)
        {
            var t = i * spacing / len;
            pts.Add((from.X + dx * t, from.Y + dy * t, from.Z + dz * t));
        }

        // Too far behind (teleport, respawn, or more than max steps in one tick): restart from here.
        return (pts, n < (int)(len / spacing) ? to : pts[^1]);
    }

    /// <summary>Engine-free: yaw (degrees) of a horizontal move, or null when standing still.</summary>
    internal static float? Yaw(float dx, float dy) =>
        dx * dx + dy * dy < 0.01f ? null : MathF.Atan2(dy, dx) * 180f / MathF.PI;
}

public sealed partial class StoreModule
{
    private readonly (float X, float Y, float Z)?[] _dropLast = new (float, float, float)?[Players.MaxSlots];
    private readonly float[] _dropYaw = new float[Players.MaxSlots];
    private readonly bool[] _leftFoot = new bool[Players.MaxSlots];
    private readonly double[] _trailHue = new double[Players.MaxSlots];
    private readonly Queue<CEntityInstance>[] _pieces = Enumerable.Range(0, Players.MaxSlots).Select(_ => new Queue<CEntityInstance>()).ToArray();

    /// <summary>Units behind the player's back that a back trail's pieces appear.</summary>
    private const float BackOffset = 10f;

    /// <summary>Most trail pieces one player has at once (oldest removed first).</summary>
    private const int MaxPiecesPerPlayer = 16;

    /// <summary>Trail colour: "rainbow" walks round the colour wheel piece by piece instead of jumping at random.</summary>
    private Color TrailColor(int slot, string spec)
    {
        if (!spec.Trim().Equals("rainbow", StringComparison.OrdinalIgnoreCase)) return ColorOf(spec, Random.Shared);
        _trailHue[slot] = (_trailHue[slot] + 23) % 360;
        return FromHue(_trailHue[slot]);
    }

    /// <summary>Drops a model trail's pieces behind the player. False when the pack's models are not available.</summary>
    private bool ModelTrail(CCSPlayerController c, Vector o, TrailStyle style, StoreItem item)
    {
        if (!CommonHost.PackModelsOn) return false;
        var here = (o.X, o.Y, o.Z);
        if (_dropLast[c.Slot] is not { } last)
        {
            _dropLast[c.Slot] = here;
            return true;
        }

        var (points, newLast) = StoreTrails.DropPoints(last, here, style.Spacing);
        _dropLast[c.Slot] = newLast;
        if (points.Count == 0) return true;
        if (StoreTrails.Yaw(here.X - last.X, here.Y - last.Y) is { } yaw) _dropYaw[c.Slot] = yaw;

        var pawn = c.PawnOrNull();
        var onGround = pawn != null && (pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) != 0;
        if (style.Placement == TrailPlacement.Footsteps && !onGround) return true; // no footprints in the air

        var filter = ViewFilter(c);
        foreach (var p in points)
        {
            var (model, pos, angles) = PlacePiece(c.Slot, style, p);
            Color? tint = style.Tinted ? TrailColor(c.Slot, item.Color) : null;
            if (Fx.Prop(model, pos, angles, tint) is not { } piece) continue;
            Restrict(piece, filter);
            Fx.RemoveLater(piece, style.Seconds);
            var q = _pieces[c.Slot];
            q.Enqueue(piece);
            while (q.Count > MaxPiecesPerPlayer) Fx.SafeRemove(q.Dequeue());
        }

        return true;
    }

    private (string Model, Vector Pos, QAngle Angles) PlacePiece(int slot, TrailStyle style, (float X, float Y, float Z) p)
    {
        var rng = Random.Shared;
        var yaw = _dropYaw[slot];
        switch (style.Placement)
        {
            case TrailPlacement.Footsteps:
            {
                var left = _leftFoot[slot] = !_leftFoot[slot];
                var side = (left ? 1f : -1f) * 4f; // feet apart, to the left / right of the walking line
                var rad = (yaw + 90f) * MathF.PI / 180f;
                var piece = style.Pieces[left ? 0 : Math.Min(1, style.Pieces.Length - 1)];
                return (StoreTrails.ModelOf(piece), new Vector(p.X + MathF.Cos(rad) * side, p.Y + MathF.Sin(rad) * side, p.Z + style.MinZ),
                    new QAngle(0, yaw, 0));
            }
            default:
            {
                // Behind the back: a step back from where the player was, a little to either side.
                var fwd = yaw * MathF.PI / 180f;
                var back = BackOffset + rng.NextSingle() * 4f;
                var side = rng.NextSingle() * 8f - 4f;
                var x = p.X - MathF.Cos(fwd) * back - MathF.Sin(fwd) * side;
                var y = p.Y - MathF.Sin(fwd) * back + MathF.Cos(fwd) * side;
                return (StoreTrails.ModelOf(style.Pieces[rng.Next(style.Pieces.Length)]),
                    new Vector(x, y, p.Z + style.MinZ + rng.NextSingle() * (style.MaxZ - style.MinZ)),
                    new QAngle(rng.Next(-15, 16), rng.Next(360), rng.Next(-25, 26)));
            }
        }
    }

    private void ClearPieces(int slot)
    {
        _dropLast[slot] = null;
        var q = _pieces[slot];
        while (q.Count > 0) Fx.SafeRemove(q.Dequeue());
    }
}
