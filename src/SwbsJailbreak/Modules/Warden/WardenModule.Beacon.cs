using System.Drawing;
using System.Globalization;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Warden;

/// <summary>Warden pointer / beacon look settings that need no engine (unit-tested).</summary>
internal static class WardenBeaconRules
{
    public static readonly (string Name, int[] Rgba)[] Colors =
    {
        ("Orange-red", new[] { 240, 50, 15, 255 }), ("Red", new[] { 255, 30, 30, 255 }), ("Orange", new[] { 255, 128, 0, 255 }),
        ("Yellow", new[] { 255, 220, 0, 255 }), ("Green", new[] { 40, 255, 40, 255 }), ("Cyan", new[] { 0, 255, 255, 255 }),
        ("Blue", new[] { 0, 110, 255, 255 }), ("Purple", new[] { 170, 60, 255, 255 }), ("Pink", new[] { 255, 105, 180, 255 }),
        ("White", new[] { 255, 255, 255, 255 })
    };

    public static readonly float[] BeaconSizes = { 25f, 40f, 60f, 90f, 130f, 180f, 250f };
    public static readonly float[] BeaconWidths = { 2f, 3f, 5f, 8f };
    public static readonly float[] BeaconTimes = { 0.3f, 0.6f, 1f, 2f };
    public static readonly float[] TracerWidths = { 0.4f, 1f, 2f, 3f, 5f };

    /// <summary>Preset name, "custom R,G,B" for anything else, or "same as tracer" for an empty ring colour.</summary>
    public static string Name(int[]? rgba, string emptyName = "default")
    {
        if (rgba == null || rgba.Length < 3) return emptyName;
        foreach (var (name, c) in Colors)
            if (c[0] == rgba[0] && c[1] == rgba[1] && c[2] == rgba[2]) return name;
        return $"{rgba[0]},{rgba[1]},{rgba[2]}";
    }

    /// <summary>Next preset. With <paramref name="allowEmpty"/> the cycle also passes through "empty" (= same as tracer).</summary>
    public static int[] NextColor(int[]? current, bool allowEmpty)
    {
        var i = current is { Length: >= 3 } ? Array.FindIndex(Colors, c => c.Rgba[0] == current[0] && c.Rgba[1] == current[1] && c.Rgba[2] == current[2]) : -2;
        if (allowEmpty)
        {
            if (i == -2) return (int[])Colors[0].Rgba.Clone();          // empty -> first preset
            if (i == Colors.Length - 1) return Array.Empty<int>();      // last preset -> empty
        }

        return (int[])Colors[(i + 1 + Colors.Length) % Colors.Length].Rgba.Clone();
    }

    /// <summary>A preset name ("red") or "R G B" / "R,G,B" (0-255); null when neither.</summary>
    public static int[]? ParseColor(string text)
    {
        text = text.Trim();
        foreach (var (name, c) in Colors)
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase) || string.Equals(name.Replace("-", ""), text, StringComparison.OrdinalIgnoreCase))
                return (int[])c.Clone();
        var parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return null;
        var v = new int[4];
        v[3] = 255;
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]) || v[i] < 0 || v[i] > 255) return null;
        return v;
    }

    /// <summary>The next value in a cycle (the first when the current one is not in it).</summary>
    public static float Next(float[] values, float current)
    {
        var i = Array.FindIndex(values, v => Math.Abs(v - current) < 0.01f);
        return values[(i + 1) % values.Length];
    }
}

/// <summary>
/// Admin settings for the warden's pointer: the tracer (double-tap USE, or a ping) and the beacon ring at its end.
/// !wbeacon opens a menu (also in !admin, Jailbreak page); changes are saved to the config at once.
/// </summary>
public sealed partial class WardenModule
{
    private Color BeaconColor() => Cfg.PulseRingColor is { Length: >= 3 } rc
        ? WDraw.ToColor(rc, Color.OrangeRed)
        : WDraw.ToColor(Cfg.PointerColor, Color.OrangeRed);

    [ConsoleCommand("css_wbeacon", "sm_wbeacon [color|beaconcolor <name|r g b|same>, size|width|time|tracer <n>, on|off, reset] - warden pointer and beacon settings")]
    [ConsoleCommand("css_wardenbeacon", "sm_wardenbeacon - warden pointer and beacon settings")]
    public void CmdWardenBeacon(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "warden.beacon")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        if (info.ArgCount < 2)
        {
            if (caller == null) info.ReplyToCommand(Describe());
            else OpenBeaconMenu(caller, null);
            return;
        }

        var arg = info.GetArg(1).ToLowerInvariant();
        var rest = string.Join(' ', Enumerable.Range(2, Math.Max(0, info.ArgCount - 2)).Select(info.GetArg));
        string? usage = null;
        switch (arg)
        {
            case "on" or "off": ChangeBeacon(caller, c => c.PointerEnabled = arg == "on"); break;
            case "reset": ResetBeacon(caller); break;
            case "color" or "colour" or "tracercolor":
                if (WardenBeaconRules.ParseColor(rest) is { } tc) ChangeBeacon(caller, c => c.PointerColor = tc);
                else usage = "!wbeacon color <red|blue|...|r g b>";
                break;
            case "beaconcolor" or "beaconcolour" or "ringcolor":
                if (rest.Trim().Equals("same", StringComparison.OrdinalIgnoreCase)) ChangeBeacon(caller, c => c.PulseRingColor = Array.Empty<int>());
                else if (WardenBeaconRules.ParseColor(rest) is { } rc) ChangeBeacon(caller, c => c.PulseRingColor = rc);
                else usage = "!wbeacon beaconcolor <red|blue|...|r g b|same>";
                break;
            case "size" or "width" or "time" or "tracer":
                if (!float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0) { usage = $"!wbeacon {arg} <number>"; break; }
                ChangeBeacon(caller, c =>
                {
                    switch (arg)
                    {
                        case "size": c.PulseRingRadius = Math.Clamp(n, 10f, 500f); break;
                        case "width": c.PulseRingWidth = Math.Clamp(n, 1f, 20f); break;
                        case "time": c.PulseRingLife = Math.Clamp(n, 0.1f, 5f); break;
                        default: SetTracerWidth(c, Math.Clamp(n, 0.2f, 10f)); break;
                    }
                });
                break;
            default: usage = "!wbeacon [color|beaconcolor|size|width|time|tracer|on|off|reset]"; break;
        }

        if (usage != null) Chat.ReplyT(caller, info, "core.usage", usage);
        else Chat.ReplyT(caller, info, "warden.beacon.now", Describe());
    }

    /// <summary>The settings menu (also opened from the admin menu's Jailbreak page).</summary>
    internal void OpenBeaconMenu(CCSPlayerController admin, JbMenu? parent)
    {
        var cfg = Cfg;
        var m = new JbMenu(Lang.T("warden.beacon.title")) { Parent = parent, Subtitle = Hud.Escape(Describe()) };
        void Item(string text, Action<WardenConfig> change) => m.Add(text, a =>
        {
            ChangeBeacon(a, change);
            Preview(a);
            OpenBeaconMenu(a, parent);
        });
        Item(Lang.T("warden.beacon.item.tracer_color", WardenBeaconRules.Name(cfg.PointerColor)),
            c => c.PointerColor = WardenBeaconRules.NextColor(c.PointerColor, false));
        Item(Lang.T("warden.beacon.item.beacon_color", WardenBeaconRules.Name(cfg.PulseRingColor, Lang.T("warden.beacon.same"))),
            c => c.PulseRingColor = WardenBeaconRules.NextColor(c.PulseRingColor, true));
        Item(Lang.T("warden.beacon.item.size", (int)cfg.PulseRingRadius),
            c => c.PulseRingRadius = WardenBeaconRules.Next(WardenBeaconRules.BeaconSizes, c.PulseRingRadius));
        Item(Lang.T("warden.beacon.item.width", F(cfg.PulseRingWidth)),
            c => c.PulseRingWidth = WardenBeaconRules.Next(WardenBeaconRules.BeaconWidths, c.PulseRingWidth));
        Item(Lang.T("warden.beacon.item.tracer_width", F(cfg.PointerWidth)),
            c => SetTracerWidth(c, WardenBeaconRules.Next(WardenBeaconRules.TracerWidths, c.PointerWidth)));
        Item(Lang.T("warden.beacon.item.time", F(cfg.PulseRingLife)),
            c => c.PulseRingLife = WardenBeaconRules.Next(WardenBeaconRules.BeaconTimes, c.PulseRingLife));
        Item(Lang.T(cfg.PointerEnabled ? "warden.beacon.item.on" : "warden.beacon.item.off"), c => c.PointerEnabled = !c.PointerEnabled);
        m.Add(Lang.T("warden.beacon.item.test"), a => { Preview(a); OpenBeaconMenu(a, parent); });
        m.Add(Lang.T("warden.beacon.item.reset"), a => { ResetBeacon(a); Preview(a); OpenBeaconMenu(a, parent); });
        m.Open(admin);
    }

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The tracer starts thin at the warden and widens towards the beacon, as before.</summary>
    private static void SetTracerWidth(WardenConfig c, float w)
    {
        c.PointerWidth = w;
        c.PointerEndWidth = Math.Max(1f, w * 2.5f);
    }

    /// <summary>Show the admin the current look: a tracer from their eyes to a spot in front of them, with the beacon there.</summary>
    private void Preview(CCSPlayerController admin)
    {
        var o = admin.Origin();
        var pawn = admin.PawnOrNull();
        if (o == null || pawn == null || !admin.IsAliveP()) return;
        var yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
        var end = new Vector(o.X + MathF.Cos(yaw) * 160f, o.Y + MathF.Sin(yaw) * 160f, o.Z + 2f);
        var beam = WDraw.Beam(end, new Vector(o.X, o.Y, o.Z + 50f), WDraw.ToColor(Cfg.PointerColor, Color.OrangeRed), Cfg.PointerWidth, Cfg.PointerEndWidth);
        if (beam != null) Fx.RemoveLater(beam, 1.5f);
        _pulses.Spawn(end, 1f, Math.Max(1f, Cfg.PulseRingRadius), Cfg.PulseRingLife, BeaconColor(), Cfg.PulseRingWidth, 16);
    }

    private string Describe()
    {
        var c = Cfg;
        return Lang.T("warden.beacon.describe", Lang.T(c.PointerEnabled ? "warden.beacon.state.on" : "warden.beacon.state.off"),
            WardenBeaconRules.Name(c.PointerColor), WardenBeaconRules.Name(c.PulseRingColor, Lang.T("warden.beacon.same")), (int)c.PulseRingRadius);
    }

    private void ChangeBeacon(CCSPlayerController? admin, Action<WardenConfig> change)
    {
        Config.Update<WardenConfig>("Warden", change);
        Config.SaveIfDirty(Logger);
        var who = admin?.PlayerName ?? "Console";
        Logger.LogInformation("[Warden] {Admin} changed the warden pointer: {Settings}", who, Describe());
        Jb.Log.LogEvent($"{who} changed the warden pointer: {Describe()}", admin?.Jb(), null, LogType.Admin);
    }

    private void ResetBeacon(CCSPlayerController? admin)
    {
        var d = new WardenConfig();
        ChangeBeacon(admin, c =>
        {
            c.PointerEnabled = d.PointerEnabled;
            c.PointerColor = d.PointerColor;
            c.PointerWidth = d.PointerWidth;
            c.PointerEndWidth = d.PointerEndWidth;
            c.PulseRingRadius = d.PulseRingRadius;
            c.PulseRingLife = d.PulseRingLife;
            c.PulseRingWidth = d.PulseRingWidth;
            c.PulseRingColor = d.PulseRingColor;
        });
    }
}
