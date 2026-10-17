using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;

namespace Swbs.Common;

/// <summary>
/// World menus for every SWBS server: the game mode's menus (help, admin, store, guns, zones, votes ...) are drawn as a
/// themed panel in front of the player instead of the centre-screen HTML. The menu system itself (items, numbers,
/// W/S/E/R, pages) is unchanged; only the drawing is. Dead players, spectators and anyone who prefers it get the classic
/// centre menu. Players choose their style, theme, position and size with !menustyle.
/// </summary>
public sealed class WorldMenuModule : SwbsModule
{
    internal const string Ns = "menustyle";

    public static WorldMenuModule? Instance { get; private set; }

    internal WorldMenuSettings Cfg => Section<WorldMenuSettings>("WorldMenu");

    public override string Name => "WorldMenu";
    public override int Order => 5;
    public override bool Enabled => Cfg.Enabled;

    private readonly WorldMenuPrefs?[] _prefs = new WorldMenuPrefs?[65];
    private string _autoTheme = "gold";

    public override void OnLoad(bool hotReload)
    {
        Instance = this;
        WorldMenuSettingsHolder.Current = Cfg;
        _autoTheme = WorldMenuThemes.ForPlugin(Plugin.ModuleName);
        Plugin.RegisterListener<Listeners.CheckTransmit>(WorldMenuRenderer.FilterTransmit);
        Plugin.RegisterListener<Listeners.OnTick>(WorldMenuRenderer.Tick);
        if (hotReload)
            foreach (var p in Players.Connected().ToList()) OnPlayerConnected(p);
    }

    public override void OnUnload(bool hotReload)
    {
        WorldMenuRenderer.HideAll();
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    public override void OnMapEnd() => WorldMenuRenderer.Forget();

    public override void OnPlayerConnected(SwbsPlayer player)
    {
        var c = player.Controller;
        if (c == null || c.IsBot || c.SteamID == 0 || player.Slot is < 0 or >= 65) return;
        _prefs[player.Slot] = Storage.Get<WorldMenuPrefs>(Ns, c.SteamID.ToString()) ?? new WorldMenuPrefs();
    }

    public override void OnPlayerDisconnect(SwbsPlayer player)
    {
        WorldMenuRenderer.Hide(player.Slot);
        if (player.Slot is >= 0 and < 65) _prefs[player.Slot] = null;
    }

    // The menu cores re-draw on the next tick: dead players fall back to the centre menu.
    public override void OnPlayerDeath(SwbsPlayer victim, SwbsPlayer? attacker, EventPlayerDeath ev) => WorldMenuRenderer.Hide(victim.Slot);

    // ---- preferences

    internal WorldMenuPrefs Prefs(CCSPlayerController c) =>
        c.Slot is >= 0 and < 65 ? _prefs[c.Slot] ??= new WorldMenuPrefs() : new WorldMenuPrefs();

    private void Save(CCSPlayerController c)
    {
        if (c.SteamID == 0) return;
        Storage.Set(Ns, c.SteamID.ToString(), Prefs(c));
    }

    internal string StyleOf(CCSPlayerController c) => Norm(Prefs(c).Style, Cfg.DefaultStyle, "world", "center");
    internal string ThemeOf(CCSPlayerController c)
    {
        var t = Prefs(c).Theme ?? Cfg.DefaultTheme;
        return string.IsNullOrWhiteSpace(t) || t.Equals("auto", StringComparison.OrdinalIgnoreCase) ? _autoTheme : WorldMenuThemes.Get(t).Key;
    }

    internal string PositionOf(CCSPlayerController c) => Norm(Prefs(c).Position, Cfg.DefaultPosition, "left", "center", "right");
    internal string SizeOf(CCSPlayerController c) => Norm(Prefs(c).Size, Cfg.DefaultSize, "normal", "small", "large");

    private static string Norm(string? value, string fallback, params string[] allowed)
    {
        foreach (var v in new[] { value, fallback })
            if (v != null && allowed.Contains(v.Trim().ToLowerInvariant())) return v.Trim().ToLowerInvariant();
        return allowed[0];
    }

    internal static float SizeScale(string size) => size switch { "small" => 0.85f, "large" => 1.2f, _ => 1f };

    // ---- !menustyle

    [ConsoleCommand("css_menustyle", "sm_menustyle [world|center] - your menu style, theme, position and size")]
    [ConsoleCommand("css_menutheme", "sm_menutheme [theme] - your menu style, theme, position and size")]
    [ConsoleCommand("css_menusettings", "sm_menusettings - your menu style, theme, position and size")]
    public void CmdMenuStyle(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid || caller.IsBot) return;
        var arg = info.ArgCount > 1 ? info.GetArg(1).Trim().ToLowerInvariant() : "";
        if (arg is "world" or "center" or "centre" or "classic")
        {
            Prefs(caller).Style = arg == "world" ? "world" : "center";
            Save(caller);
            Chat.Tell(caller, "worldmenu.style_set", Lang.T("worldmenu.style." + Prefs(caller).Style));
            return;
        }

        if (arg.Length > 0 && WorldMenuThemes.All.FirstOrDefault(t => t.Key == arg) is { } theme)
        {
            Prefs(caller).Theme = theme.Key;
            Save(caller);
            Chat.Tell(caller, "worldmenu.theme_set", theme.Name);
            return;
        }

        OpenSettings(caller);
    }

    private static string Value(string text, string value, string color) => $"{text} <font color='{color}'>{value}</font>";

    public void OpenSettings(CCSPlayerController c)
    {
        var theme = WorldMenuThemes.Get(ThemeOf(c));
        var style = StyleOf(c);
        var menu = new SwbsMenu(Lang.T("worldmenu.title")) { Subtitle = Lang.T("worldmenu.subtitle") };
        menu.Add(Value(Lang.T("worldmenu.item.style"), Lang.T("worldmenu.style." + style), theme.Accent), p =>
        {
            Prefs(p).Style = StyleOf(p) == "world" ? "center" : "world";
            Save(p);
            OpenSettings(p);
        });
        menu.Add(Value(Lang.T("worldmenu.item.theme"), "◄ " + theme.Name + " ►", theme.Accent), p =>
        {
            Prefs(p).Theme = WorldMenuThemes.Cycle(ThemeOf(p), 1).Key;
            Save(p);
            OpenSettings(p);
        });
        menu.Add(Value(Lang.T("worldmenu.item.position"), Lang.T("worldmenu.position." + PositionOf(c)), theme.Accent), p =>
        {
            Prefs(p).Position = PositionOf(p) switch { "left" => "center", "center" => "right", _ => "left" };
            Save(p);
            OpenSettings(p);
        }, style == "world");
        menu.Add(Value(Lang.T("worldmenu.item.size"), Lang.T("worldmenu.size." + SizeOf(c)), theme.Accent), p =>
        {
            Prefs(p).Size = SizeOf(p) switch { "small" => "normal", "normal" => "large", _ => "small" };
            Save(p);
            OpenSettings(p);
        }, style == "world");
        menu.Add(Lang.T("worldmenu.item.reset"), p =>
        {
            _prefs[p.Slot] = new WorldMenuPrefs();
            Save(p);
            Chat.Tell(p, "worldmenu.reset_done");
            OpenSettings(p);
        });
        menu.Open(c);
    }
}

/// <summary>What the menu cores call: draw a menu in the world when the player wants it, else report false.</summary>
public static class WorldMenus
{
    /// <summary>The player has world menus on (the module is loaded and they did not pick the centre menu).</summary>
    public static bool Wants(CCSPlayerController? c) =>
        c is { IsValid: true, IsBot: false } && WorldMenuModule.Instance is { } m && m.StyleOf(c) == "world";

    /// <summary>Draw (or update) the menu. False: not possible right now (dead, spectating ...) - draw the centre menu.</summary>
    public static bool Draw(CCSPlayerController c, WorldMenuView view)
    {
        var m = WorldMenuModule.Instance;
        if (m == null || !Wants(c)) return false;
        var s = m.Cfg;
        var theme = WorldMenuThemes.Get(m.ThemeOf(c));
        var pulse = s.Animations && s.PulseSeconds > 0 && (int)(Server.CurrentTime / s.PulseSeconds) % 2 == 1;
        var panel = WorldMenuLayout.Build(view, theme, s, pulse);
        return WorldMenuRenderer.Show(c, panel, s, m.PositionOf(c), WorldMenuModule.SizeScale(m.SizeOf(c)));
    }

    public static void Hide(CCSPlayerController? c)
    {
        if (c != null && c.IsValid) WorldMenuRenderer.Hide(c.Slot);
    }

    public static bool IsOpen(CCSPlayerController? c) => c != null && c.IsValid && WorldMenuRenderer.IsOpen(c.Slot);

    /// <summary>Navigation feedback sound for world menus ("move", "select", "open").</summary>
    public static void Feedback(CCSPlayerController c, string what)
    {
        if (WorldMenuModule.Instance is not { } m || !IsOpen(c)) return;
        var sound = what switch { "move" => m.Cfg.MoveSound, "select" => m.Cfg.SelectSound, _ => m.Cfg.OpenSound };
        if (sound.Length > 0) Sound.Play(c, sound);
    }
}
