using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.Cells;

namespace SwbsJailbreak.Modules.Games;

/// <summary>
/// Map game buttons (SMPlugins MultigamesGameSelect, hlstriker): admins mark the func_buttons that start a map's built-in
/// games; the warden (or the picker) chooses one by name and the plugin presses it. Stored in maps/game_buttons/&lt;map&gt;.json.
/// </summary>
public sealed partial class WardenGamesModule
{
    private const float PingPickSeconds = 15f;
    private static readonly string[] ButtonClasses = { "func_button", "func_rot_button", "momentary_rot_button" };

    private GameButtonsDoc _buttons = new();
    private string _map = "";

    internal IReadOnlyList<GameButtonRecord> Buttons => _buttons.Buttons;

    private void LoadButtons()
    {
        _map = (Server.MapName ?? "").ToLowerInvariant();
        _buttons = _map.Length == 0 ? new GameButtonsDoc() : Storage.LoadMapDoc<GameButtonsDoc>("game_buttons", _map);
        _buttons.Buttons ??= new List<GameButtonRecord>();
        _buttons.Buttons.RemoveAll(b => b == null);
    }

    private void SaveButtons()
    {
        if (_map.Length > 0) Storage.SaveMapDoc("game_buttons", _map, _buttons);
    }

    [ConsoleCommand("css_gamebuttons", "sm_gamebuttons - (admin) mark the map buttons that start its games")]
    public void CmdGameButtons(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid) { info.ReplyToCommand("[Games] in-game only"); return; }
        if (!Perms.Has(caller, "games.admin")) { Chat.Tell(caller, "core.no_access"); return; }
        ShowButtonAdmin(caller);
    }

    private void ShowButtonAdmin(CCSPlayerController c)
    {
        var m = new JbMenu(Lang.T("games.buttons.title"));
        m.Subtitle = Hud.Escape(Lang.T("games.buttons.hint"));
        m.Add(Lang.T("games.buttons.add"), AddPingedButton);
        for (var i = 0; i < _buttons.Buttons.Count; i++)
        {
            var index = i;
            var b = _buttons.Buttons[i];
            m.Add(Hud.Escape($"{b.Label} ({(b.Name.Length > 0 ? b.Name : "#" + b.HammerId)})"), p => ShowButtonActions(p, index));
        }

        m.Open(c);
    }

    private void ShowButtonActions(CCSPlayerController c, int index)
    {
        if (index < 0 || index >= _buttons.Buttons.Count) { ShowButtonAdmin(c); return; }
        var b = _buttons.Buttons[index];
        var m = new JbMenu(Hud.Escape(b.Label));
        m.Add(Lang.T("games.buttons.press"), p =>
        {
            Chat.Tell(p, Press(b, p) ? "games.buttons.pressed" : "games.buttons.missing", b.Label);
            ShowButtonActions(p, index);
        });
        m.Add(Lang.T("games.buttons.delete"), p =>
        {
            if (index < _buttons.Buttons.Count && ReferenceEquals(_buttons.Buttons[index], b))
            {
                _buttons.Buttons.RemoveAt(index);
                SaveButtons();
                Logger.LogInformation("[Games] \"{Admin}\" removed game button {Label} on {Map}", p.PlayerName, b.Label, _map);
            }

            ShowButtonAdmin(p);
        });
        m.Add(Lang.T("games.menu.back"), ShowButtonAdmin);
        m.Open(c);
    }

    /// <summary>Uses the admin's last ping (the engine's crosshair trace) to pick the button, like the cell editor's doors.</summary>
    private void AddPingedButton(CCSPlayerController c)
    {
        if (!Aim.TryGetPing(c, PingPickSeconds, out _, out var entity) || Aim.Entity<CBaseEntity>(entity) is not { } e ||
            !ButtonClasses.Contains(e.DesignerName))
        {
            Chat.Tell(c, "games.buttons.ping_first");
            ShowButtonAdmin(c);
            return;
        }

        var rec = new GameButtonRecord
        {
            Name = e.Entity?.Name ?? "", HammerId = CellAutoDetect.SafeHammerId(e), Class = e.DesignerName,
            Origin = e.AbsOrigin is { } o ? new[] { o.X, o.Y, o.Z } : new float[3]
        };
        if (rec.Name.Length == 0 && rec.HammerId.Length == 0) { Chat.Tell(c, "games.buttons.unnamed"); ShowButtonAdmin(c); return; }
        if (_buttons.Buttons.Any(b => Same(b, rec))) { Chat.Tell(c, "games.buttons.already"); ShowButtonAdmin(c); return; }

        ChatInput.Ask(c, Lang.T("games.buttons.ask_label"), (p, text) =>
        {
            var label = text.Trim().Replace('{', '(').Replace('}', ')');
            if (label.Length is 0 or > 32) { Chat.Tell(p, "games.buttons.bad_label"); ShowButtonAdmin(p); return; }
            rec.Label = label;
            _buttons.Buttons.Add(rec);
            SaveButtons();
            Logger.LogInformation("[Games] \"{Admin}\" marked game button {Label} ({Name}/{Hid}) on {Map}", p.PlayerName, label, rec.Name, rec.HammerId, _map);
            Chat.Tell(p, "games.buttons.added", label);
            ShowButtonAdmin(p);
        }, ShowButtonAdmin);
    }

    private static bool Same(GameButtonRecord a, GameButtonRecord b) =>
        (a.HammerId.Length > 0 && a.HammerId == b.HammerId) || (a.HammerId.Length == 0 && b.HammerId.Length == 0 && a.Name == b.Name);

    private static CBaseEntity? Resolve(GameButtonRecord rec)
    {
        foreach (var cls in ButtonClasses)
            foreach (var e in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(cls))
            {
                if (!e.IsValid) continue;
                if (rec.HammerId.Length > 0 && CellAutoDetect.SafeHammerId(e) == rec.HammerId) return e;
                if (rec.HammerId.Length == 0 && rec.Name.Length > 0 && (e.Entity?.Name ?? "") == rec.Name) return e;
            }

        return null;
    }

    /// <summary>Unlock + Press with the presser's pawn as activator (same inputs the cell doors use).</summary>
    internal bool Press(GameButtonRecord rec, CCSPlayerController? by)
    {
        var e = Resolve(rec);
        if (e == null) return false;
        var activator = by.PawnOrNull();
        try
        {
            e.AcceptInput("Unlock", activator, activator);
            e.AcceptInput("Press", activator, activator);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Games] pressing game button {Label} failed", rec.Label);
            return false;
        }
    }

    private void ChooseButton(CCSPlayerController c)
    {
        var m = new JbMenu(Lang.T("games.menu.map_games"));
        foreach (var b in _buttons.Buttons)
        {
            var button = b;
            m.Add(Hud.Escape(button.Label + PlayerExtras.PoolsModule.Tag(PlayerExtras.PoolCategory.MapGame, button.Label)), p => ChooseReward(p, _games.OfType<MapGame>().First(), null, button, false));
        }

        if (m.Items.Count == 0) m.AddInfo(Lang.T("games.err.no_buttons"));
        m.Add(Lang.T("games.menu.back"), ShowGameList);
        m.Open(c);
    }
}
