using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace Swbs.Common;

/// <summary>
/// World menus for every SWBS server: the game mode's menus (help, admin, store, guns, zones, votes ...) are drawn as a
/// themed panel in front of the player instead of the centre-screen HTML. The menu system itself (items, numbers,
/// W/S/E/R, pages) is unchanged; only the drawing is. Dead players, spectators and anyone who prefers it get the classic
/// centre menu. Players choose their style, theme, position and size with !menustyle (changes preview live and are kept
/// with Save); !menutest shows a sample panel and says what happened.
/// </summary>
public sealed class WorldMenuModule : SwbsModule
{
    internal const string Ns = "menustyle";

    public static WorldMenuModule? Instance { get; private set; }

    internal WorldMenuSettings Cfg => Section<WorldMenuSettings>("WorldMenu");

    public override string Name => "WorldMenu";
    public override int Order => 5;
    public override bool Enabled => Cfg.Enabled;

    private readonly WorldMenuPrefs?[] _saved = new WorldMenuPrefs?[65];

    /// <summary>Unsaved changes while !menustyle is open (they already show; Save keeps them).</summary>
    private readonly WorldMenuPrefs?[] _draft = new WorldMenuPrefs?[65];

    private readonly bool[] _toldFailure = new bool[65];
    private string _autoTheme = "gold";

    public override void OnLoad(bool hotReload)
    {
        Instance = this;
        WorldMenuSettingsHolder.Current = Cfg;
        WorldMenuRenderer.LogError = ex => Logger.LogWarning(ex, "[WorldMenu] a world menu could not be drawn (the classic menu is shown instead)");
        _autoTheme = WorldMenuThemes.ForPlugin(Plugin.ModuleName);
        Plugin.RegisterListener<Listeners.CheckTransmit>(WorldMenuRenderer.FilterTransmit);
        Plugin.RegisterListener<Listeners.OnTick>(WorldMenuRenderer.Tick);
        if (hotReload)
            foreach (var p in Players.Connected().ToList()) OnPlayerConnected(p);
    }

    public override void OnUnload(bool hotReload)
    {
        WorldMenuRenderer.HideAll();
        WorldMenuRenderer.LogError = null;
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    public override void OnMapEnd() => WorldMenuRenderer.Forget();

    public override void OnPlayerConnected(SwbsPlayer player)
    {
        var c = player.Controller;
        if (c == null || c.IsBot || c.SteamID == 0 || player.Slot is < 0 or >= 65) return;
        _saved[player.Slot] = Storage.Get<WorldMenuPrefs>(Ns, c.SteamID.ToString()) ?? new WorldMenuPrefs();
        _draft[player.Slot] = null;
        _toldFailure[player.Slot] = false;
    }

    public override void OnPlayerDisconnect(SwbsPlayer player)
    {
        WorldMenuRenderer.Hide(player.Slot);
        if (player.Slot is < 0 or >= 65) return;
        _saved[player.Slot] = null;
        _draft[player.Slot] = null;
    }

    // The menu cores re-draw on the next tick: dead players fall back to the centre menu.
    public override void OnPlayerDeath(SwbsPlayer victim, SwbsPlayer? attacker, EventPlayerDeath ev) => WorldMenuRenderer.Hide(victim.Slot);

    // ---- preferences (the draft while !menustyle is open, else the saved ones)

    internal WorldMenuPrefs Prefs(CCSPlayerController c)
    {
        if (c.Slot is < 0 or >= 65) return new WorldMenuPrefs();
        return _draft[c.Slot] ?? (_saved[c.Slot] ??= new WorldMenuPrefs());
    }

    private WorldMenuPrefs Draft(CCSPlayerController c) => _draft[c.Slot] ??= Copy(_saved[c.Slot] ??= new WorldMenuPrefs());

    private static WorldMenuPrefs Copy(WorldMenuPrefs p) => new() { Style = p.Style, Theme = p.Theme, Position = p.Position, Size = p.Size, Details = p.Details };

    private static bool Same(WorldMenuPrefs a, WorldMenuPrefs b) =>
        a.Style == b.Style && a.Theme == b.Theme && a.Position == b.Position && a.Size == b.Size && a.Details == b.Details;

    private bool Dirty(CCSPlayerController c) => _draft[c.Slot] is { } d && !Same(d, _saved[c.Slot] ?? new WorldMenuPrefs());

    private void SaveDraft(CCSPlayerController c)
    {
        if (_draft[c.Slot] is not { } d) return;
        _saved[c.Slot] = d;
        _draft[c.Slot] = null;
        if (c.SteamID != 0) Storage.Set(Ns, c.SteamID.ToString(), d);
    }

    private void Discard(CCSPlayerController c) => _draft[c.Slot] = null;

    internal string StyleOf(CCSPlayerController c) => Norm(Prefs(c).Style, Cfg.DefaultStyle, "world", "center");
    internal string ThemeOf(CCSPlayerController c)
    {
        var t = Prefs(c).Theme ?? Cfg.DefaultTheme;
        return string.IsNullOrWhiteSpace(t) || t.Equals("auto", StringComparison.OrdinalIgnoreCase) ? _autoTheme : WorldMenuThemes.Get(t).Key;
    }

    internal string PositionOf(CCSPlayerController c) => Norm(Prefs(c).Position, Cfg.DefaultPosition, "left", "center", "right");
    internal string SizeOf(CCSPlayerController c) => Norm(Prefs(c).Size, Cfg.DefaultSize, "normal", "small", "large");
    internal bool DetailsOf(CCSPlayerController c) => Prefs(c).Details ?? Cfg.DefaultDetails;

    private static string Norm(string? value, string fallback, params string[] allowed)
    {
        foreach (var v in new[] { value, fallback })
            if (v != null && allowed.Contains(v.Trim().ToLowerInvariant())) return v.Trim().ToLowerInvariant();
        return allowed[0];
    }

    internal static float SizeScale(string size) => size switch { "small" => 0.85f, "large" => 1.2f, _ => 1f };

    private static readonly string[] Styles = { "world", "center" };
    private static readonly string[] Positions = { "left", "center", "right" };
    private static readonly string[] Sizes = { "small", "normal", "large" };

    private static string Cycle(string[] values, string current, int step)
    {
        var i = Math.Max(0, Array.IndexOf(values, current));
        return values[((i + step) % values.Length + values.Length) % values.Length];
    }

    /// <summary>The first time a world menu cannot be drawn for a living player, say so (the classic menu is shown).</summary>
    internal void TellFailure(CCSPlayerController c)
    {
        if (c.Slot is < 0 or >= 65 || _toldFailure[c.Slot] || WorldMenuRenderer.LastError == null) return;
        _toldFailure[c.Slot] = true;
        Chat.Tell(c, "worldmenu.failed", WorldMenuRenderer.LastError);
    }

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
            Draft(caller).Style = arg == "world" ? "world" : "center";
            SaveDraft(caller);
            Chat.Tell(caller, "worldmenu.style_set", Lang.T("worldmenu.style." + StyleOf(caller)));
            return;
        }

        if (arg.Length > 0 && WorldMenuThemes.All.FirstOrDefault(t => t.Key == arg) is { } theme)
        {
            Draft(caller).Theme = theme.Key;
            SaveDraft(caller);
            Chat.Tell(caller, "worldmenu.theme_set", theme.Name);
            return;
        }

        Draft(caller);
        OpenSettings(caller, 0);
    }

    private static string Choice(string value, string color) => $"<font color='{color}'>◄ {value} ►</font>";

    /// <summary>
    /// The settings menu: every row is a ◄ value ► setting (A/D or E changes it, and it shows at once), then Save /
    /// Reset / Discard. Closing without saving discards the changes.
    /// </summary>
    public void OpenSettings(CCSPlayerController c, int cursor)
    {
        var theme = WorldMenuThemes.Get(ThemeOf(c));
        var style = StyleOf(c);
        var world = style == "world";
        var dirty = Dirty(c);
        var menu = new SwbsMenu(Lang.T("worldmenu.title"))
        {
            Subtitle = Lang.T(dirty ? "worldmenu.subtitle_dirty" : "worldmenu.subtitle"),
            StartCursor = cursor,
            OnCancel = p => { if (p.IsValid) Discard(p); }
        };

        void Setting(int row, string label, string value, Action<CCSPlayerController, int> change, bool enabled, WorldMenuDetails details)
        {
            var item = menu.Add($"{label} {Choice(value, theme.Accent)}", p =>
            {
                change(p, 1);
                OpenSettings(p, row);
            }, enabled);
            item.OnAdjust = (p, dir) =>
            {
                change(p, dir);
                OpenSettings(p, row);
            };
            item.Details = details;
        }

        Setting(0, Lang.T("worldmenu.item.style"), Lang.T("worldmenu.style." + style),
            (p, d) => Draft(p).Style = Cycle(Styles, StyleOf(p), d), true,
            new WorldMenuDetails { Title = Lang.T("worldmenu.item.style"), Body = Lang.T("worldmenu.help.style"), Actions = Lang.T("worldmenu.help.keys") });
        Setting(1, Lang.T("worldmenu.item.theme"), theme.Name,
            (p, d) => Draft(p).Theme = WorldMenuThemes.Cycle(ThemeOf(p), d).Key, true,
            new WorldMenuDetails { Title = theme.Name, Subtitle = Lang.T("worldmenu.item.theme"), Body = Lang.T("worldmenu.help.theme"), Actions = Lang.T("worldmenu.help.keys") }
                .Stat(Lang.T("worldmenu.help.themes"), WorldMenuThemes.All.Count));
        Setting(2, Lang.T("worldmenu.item.position"), Lang.T("worldmenu.position." + PositionOf(c)),
            (p, d) => Draft(p).Position = Cycle(Positions, PositionOf(p), d), world,
            new WorldMenuDetails { Title = Lang.T("worldmenu.item.position"), Body = Lang.T("worldmenu.help.position"), Actions = Lang.T("worldmenu.help.keys") });
        Setting(3, Lang.T("worldmenu.item.size"), Lang.T("worldmenu.size." + SizeOf(c)),
            (p, d) => Draft(p).Size = Cycle(Sizes, SizeOf(p), d), world,
            new WorldMenuDetails { Title = Lang.T("worldmenu.item.size"), Body = Lang.T("worldmenu.help.size"), Actions = Lang.T("worldmenu.help.keys") });
        Setting(4, Lang.T("worldmenu.item.details"), Lang.T(DetailsOf(c) ? "worldmenu.on" : "worldmenu.off"),
            (p, _) => Draft(p).Details = !DetailsOf(p), world,
            new WorldMenuDetails { Title = Lang.T("worldmenu.item.details"), Body = Lang.T("worldmenu.help.details"), Actions = Lang.T("worldmenu.help.keys") });

        var save = menu.Add(Lang.T(dirty ? "worldmenu.item.save_dirty" : "worldmenu.item.save"), p =>
        {
            var changed = Dirty(p);
            SaveDraft(p);
            Chat.Tell(p, changed ? "worldmenu.saved" : "worldmenu.nothing_to_save");
        }, dirty);
        save.Color = dirty ? theme.Good : null;
        save.Details = new WorldMenuDetails { Title = Lang.T("worldmenu.item.save"), Body = Lang.T("worldmenu.help.save"), Actions = Lang.T("worldmenu.help.save_keys") };

        var reset = menu.Add(Lang.T("worldmenu.item.reset"), p =>
        {
            _draft[p.Slot] = new WorldMenuPrefs();
            OpenSettings(p, 6);
        });
        reset.Details = new WorldMenuDetails { Title = Lang.T("worldmenu.item.reset"), Body = Lang.T("worldmenu.help.reset"), Actions = Lang.T("worldmenu.help.save_keys") };

        var discard = menu.Add(Lang.T("worldmenu.item.discard"), p =>
        {
            Discard(p);
            Chat.Tell(p, "worldmenu.discarded");
        }, dirty);
        discard.Details = new WorldMenuDetails { Title = Lang.T("worldmenu.item.discard"), Body = Lang.T("worldmenu.help.discard") };

        var test = menu.Add(Lang.T("worldmenu.item.test"), p =>
        {
            SaveDraft(p);
            OpenTest(p);
        });
        test.Details = new WorldMenuDetails { Title = Lang.T("worldmenu.item.test"), Body = Lang.T("worldmenu.help.test") };
        menu.Open(c);
    }

    // ---- !menutest

    [ConsoleCommand("css_menutest", "sm_menutest - show a sample world menu and report whether it could be drawn")]
    public void CmdMenuTest(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid || caller.IsBot) return;
        OpenTest(caller);
    }

    private void OpenTest(CCSPlayerController c)
    {
        var menu = new SwbsMenu(Lang.T("worldmenu.test.title")) { Subtitle = Lang.T("worldmenu.test.subtitle") };
        menu.WithTabs(new[] { "Tab 1", "Tab 2", "Tab 3" }, 0, (p, i) => TestMenu(p, i));
        FillTest(menu, 0);
        menu.Open(c);
        var slot = c.Slot;
        Plugin.AddTimer(0.6f, () =>
        {
            if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } p) return;
            var alive = p.PlayerPawn.Value is { IsValid: true } pawn && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE;
            var layers = WorldMenuRenderer.LayerCount(slot);
            Chat.Tell(p, "worldmenu.test.result", Lang.T("worldmenu.style." + StyleOf(p)), Cfg.Follow, alive ? "yes" : "no", layers,
                WorldMenuRenderer.LastError ?? "-");
        });
    }

    private SwbsMenu TestMenu(CCSPlayerController c, int tab)
    {
        var menu = new SwbsMenu(Lang.T("worldmenu.test.title")) { Subtitle = Lang.T("worldmenu.test.subtitle") };
        FillTest(menu, tab);
        return menu;
    }

    private static void FillTest(SwbsMenu menu, int tab)
    {
        menu.AddHeader($"Tab {tab + 1}");
        for (var i = 1; i <= 4; i++)
        {
            var item = menu.Add($"Sample item {tab * 4 + i}", _ => { }, i != 3, true);
            item.Tag = i == 1 ? "✓ owned" : i == 2 ? "750" : i == 3 ? "Lv 10" : "ON";
            item.Details = new WorldMenuDetails
            {
                Title = $"Sample item {tab * 4 + i}", Subtitle = "Test · details pane",
                Body = "This pane follows the cursor. If you can read this, world menus work on this server.", Actions = "E select · A/D tab"
            }.Stat("Price", "750 pts").Stat("You have", "2,450 pts");
        }

        menu.Add("Toggle sample: ON", _ => { }, true, true);
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
        if (!m.DetailsOf(c)) view.Details = null;
        var theme = WorldMenuThemes.Get(m.ThemeOf(c));
        var pulse = s.Animations && s.PulseSeconds > 0 && (int)(Server.CurrentTime / s.PulseSeconds) % 2 == 1;
        var panel = WorldMenuLayout.Build(view, theme, s, pulse);
        var ok = WorldMenuRenderer.Show(c, panel, s, m.PositionOf(c), WorldMenuModule.SizeScale(m.SizeOf(c)));
        if (!ok) m.TellFailure(c);
        return ok;
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
