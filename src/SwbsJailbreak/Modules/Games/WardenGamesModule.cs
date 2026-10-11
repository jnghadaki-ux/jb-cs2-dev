using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Games;

public sealed class GamesConfig
{
    public bool Enabled { get; set; } = true;
    public int MinPrisoners { get; set; } = 2;
    public int TimeLimitSeconds { get; set; } = 300;
    /// <summary>None, Points or CrewRespect: preselected in the warden's reward menu and used when a prisoner picks.</summary>
    public string DefaultReward { get; set; } = "Points";
    public int RewardPoints { get; set; } = 10;
    public int RewardRespect { get; set; } = 3;
    public bool LetPrisonersPick { get; set; } = true;
    /// <summary>MultigamesGameSelect's sv_multigame_auto_select_time: the picker's time before a game is chosen for them.</summary>
    public int PickSeconds { get; set; } = 45;
    public bool GlowParticipantsForGuards { get; set; } = true;
    /// <summary>UserPointsForMinigamesWinner: a Named zone with this name is the map's win zone.</summary>
    public string WinZoneName { get; set; } = "mgwin";
    public string MusicZonePrefix { get; set; } = "music";
    public float SimonGraceSeconds { get; set; } = 0.3f;
    public float SimonReactSeconds { get; set; } = 2.5f;
    public float RedLightGraceSeconds { get; set; } = 0.4f;
    public float RedLightSpeed { get; set; } = 15f;
    public float MusicGraceSeconds { get; set; } = 3f;
    public float MusicCampSeconds { get; set; } = 3f;
    /// <summary>Optional stock sound events (empty = none): played on music start / stop and on a light change.</summary>
    public string MusicStartSound { get; set; } = "";
    public string MusicStopSound { get; set; } = "";
    public string LightSound { get; set; } = "";
}

/// <summary>A running game: participants, area, reward. Owned by <see cref="WardenGamesModule"/>.</summary>
internal sealed class GameSession
{
    public required WardenGame Game { get; init; }
    public required WardenGamesModule Module { get; init; }
    public int WardenSlot { get; set; }
    public HashSet<int> Players { get; } = new();
    public Zone? Area { get; init; }
    public GameButtonRecord? Button { get; init; }
    public GameReward Reward { get; init; }
    public float StartedAt { get; init; }
    public bool Over { get; set; }

    public GamesConfig Cfg => Module.Cfg;
    public static CCSPlayerController? C(int slot) => Core.Players.Get(slot).Controller;
    public IEnumerable<CCSPlayerController> Alive() =>
        Players.Select(C).Where(c => c != null && c.IsValid && c.IsAliveP())!;
    public void Eliminate(int slot, string reasonKey) => Module.Eliminate(this, slot, reasonKey);
    public void Win(IReadOnlyCollection<int> slots) => Module.Win(this, slots);
}

/// <summary>Base class for warden games (design §4.1).</summary>
internal abstract class WardenGame
{
    public abstract string Key { get; }
    public abstract string NameKey { get; }
    public abstract string RulesKey { get; }
    /// <summary>The warden chooses a Named zone for the game (area or finish line).</summary>
    public virtual bool UsesArea => false;
    /// <summary>Prisoners may choose this game from the picker menu (no area / button to choose).</summary>
    public virtual bool PrisonerPickable => !UsesArea;
    public virtual bool CanStart(WardenGamesModule m, out string whyKey) { whyKey = ""; return true; }
    public virtual void Start(GameSession s) { }
    public virtual void Tick(GameSession s) { }
    public virtual void AddWardenItems(GameSession s, JbMenu m) { }
    public virtual void OnEntered(GameSession s, Zone zone, CCSPlayerController c) { }
    public virtual void OnOut(GameSession s, int slot) { }
    public virtual void End(GameSession s) { }
    public virtual string Status(GameSession s) => "";
}

/// <summary>
/// Warden Games: the warden (or the next prisoner in the picker queue) starts a game; the plugin runs it, judges it from real
/// engine input and rewards the winner once per round. Based on SMPlugins by hlstriker (MultigamesGameSelect,
/// MultigamesQueue, UserPointsForMinigamesWinner). Design: docs/design/MINIGAMES_SUITE_DESIGN.md §4.
/// </summary>
public sealed partial class WardenGamesModule : JbModule
{
    internal GamesConfig Cfg => Section<GamesConfig>("Games");

    public override string Name => "Games";
    public override int Order => 15;
    public override bool Enabled => Cfg.Enabled;

    private readonly List<WardenGame> _games = new();
    private readonly Dictionary<int, GlowHandle> _glows = new();
    private readonly PickerQueue _queue = new();
    private GameSession? _session;
    private bool _rewardGiven;
    private int _tick;
    private int? _pickerSlot;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _pickTimer;
    private JbMenu? _controlMenu;

    internal IReadOnlyList<WardenGame> Games => _games;
    internal bool Running => _session is { Over: false };

    /// <summary>True while the player takes part in the running game (used by !hide).</summary>
    internal bool IsPlaying(int slot) => _session is { Over: false } s && s.Players.Contains(slot);

    public override void OnLoad(bool hotReload)
    {
        _games.AddRange(new WardenGame[]
        {
            new WinZoneGame(), new LastStandingGame(), new SimonSaysGame(), new RedLightGame(), new MusicalZonesGame(), new MapGame()
        });
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        LoadButtons();
        foreach (var c in Players.Prisoners()) _queue.Add(c.Slot);
    }

    public override void OnAllModulesLoaded(bool hotReload) => Jb.Zones.Entered += OnZoneEntered;

    public override void OnUnload(bool hotReload)
    {
        Jb.Zones.Entered -= OnZoneEntered;
        EndGame(GameEndReason.Unloaded);
        CancelPick();
    }

    public override void OnMapStart(string mapName)
    {
        EndGame(GameEndReason.RoundEnded);
        CancelPick();
        LoadButtons();
    }

    public override void OnRoundStart()
    {
        EndGame(GameEndReason.RoundEnded);
        CancelPick();
        _rewardGiven = false;
    }

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        EndGame(GameEndReason.RoundEnded);
        CancelPick();
    }

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev) => PlayerGone(victim.Slot, "games.out.died");

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        _queue.Remove(player.Slot);
        PlayerGone(player.Slot, "games.out.left");
        if (_pickerSlot == player.Slot) CancelPick();
    }

    public override void OnPlayerTeam(JbPlayer player, CsTeam oldTeam, CsTeam newTeam, bool disconnect)
    {
        if (newTeam == CsTeam.Terrorist && !disconnect) _queue.Add(player.Slot);
        else _queue.Remove(player.Slot);
        if (oldTeam != newTeam) PlayerGone(player.Slot, "games.out.left");
    }

    // ====================================================================== commands

    // Wardens are ordinary players: the !commands catalogue reads the first capability *literal* as the command's gate, so the
    // admin override is a field, not a literal, and !games is listed as open (the command still requires warden or admin).
    private static readonly string AdminOverride = "games.admin";

    [ConsoleCommand("css_games", "sm_games - (warden) start, control or end a warden game")]
    public void CmdGames(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid) { info.ReplyToCommand("[Games] in-game only"); return; }
        if (!Jb.Warden.IsWarden(caller) && !Perms.Has(caller, AdminOverride)) { Chat.Tell(caller, "games.err.warden_only"); return; }
        if (Running) ShowControl(caller);
        else ShowGameList(caller);
    }

    // ====================================================================== menus

    private void ShowGameList(CCSPlayerController c)
    {
        if (!CanRunGames(c, out var why)) { Chat.Tell(c, why); return; }
        var m = new JbMenu(Lang.T("games.menu.title"));
        foreach (var g in _games)
        {
            var game = g;
            var ok = game.CanStart(this, out var gameWhy);
            var label = Lang.T(game.NameKey) + (ok ? "" : $" ({Lang.T(gameWhy)})");
            m.Add(Hud.Escape(label), p => ChooseArea(p, game), ok);
        }

        if (Cfg.LetPrisonersPick) m.Add(Lang.T("games.menu.let_pick"), HandToPicker);
        m.Open(c);
    }

    private void ChooseArea(CCSPlayerController c, WardenGame game)
    {
        if (game is MapGame) { ChooseButton(c); return; }
        if (!game.UsesArea) { ChooseReward(c, game, null, null, false); return; }
        var m = new JbMenu(Lang.T("games.menu.area_title", Lang.T(game.NameKey)));
        foreach (var z in NamedZones())
        {
            var zone = z;
            m.Add(Hud.Escape(zone.Name), p => ChooseParticipants(p, game, zone));
        }

        if (m.Items.Count == 0) m.AddInfo(Lang.T("games.err.no_named_zones"));
        m.Add(Lang.T("games.menu.back"), ShowGameList);
        m.Open(c);
    }

    private void ChooseParticipants(CCSPlayerController c, WardenGame game, Zone area)
    {
        // A finish line (Red Light) is never where the players start: everyone plays. Last One Standing is played by the
        // prisoners inside the arena (with "all prisoners" everyone outside would be out at the first check).
        if (game is RedLightGame) { ChooseReward(c, game, area, null, false); return; }
        if (game is LastStandingGame) { ChooseReward(c, game, area, null, true); return; }
        var m = new JbMenu(Lang.T("games.menu.who_title"));
        m.Add(Lang.T("games.menu.who_all"), p => ChooseReward(p, game, area, null, false));
        m.Add(Lang.T("games.menu.who_area", Hud.Escape(area.Name)), p => ChooseReward(p, game, area, null, true));
        m.Add(Lang.T("games.menu.back"), p => ChooseArea(p, game));
        m.Open(c);
    }

    internal void ChooseReward(CCSPlayerController c, WardenGame game, Zone? area, GameButtonRecord? button, bool onlyInArea)
    {
        var m = new JbMenu(Lang.T("games.menu.reward_title"));
        foreach (var r in Enum.GetValues<GameReward>())
        {
            var reward = r;
            if (reward == GameReward.CrewRespect && !Jb.Crews.Available) continue;
            m.Add(Lang.T(RewardKey(reward), Cfg.RewardPoints, Cfg.RewardRespect), p => TryStart(p, game, area, button, onlyInArea, reward));
        }

        m.Add(Lang.T("games.menu.back"), ShowGameList);
        m.Open(c);
    }

    private void ShowControl(CCSPlayerController c)
    {
        var s = _session;
        if (s == null || s.Over) return;
        var m = new JbMenu(Lang.T(s.Game.NameKey));
        m.Subtitle = Hud.Escape(Lang.T("games.menu.players_left", s.Players.Count));
        s.Game.AddWardenItems(s, m);
        if (Jb.Warden.IsWarden(c)) m.Add(Lang.T("games.menu.declare_winner"), ShowDeclareWinner);
        m.Add(Lang.T("games.menu.end"), p =>
        {
            Chat.TellAll("games.ended_by", Chat.Name(p));
            EndGame(GameEndReason.WardenEnded);
        });
        // A menu can outlive the game or the player's warden job: every item checks both when it is pressed.
        foreach (var item in m.Items)
        {
            if (item.OnSelect is not { } action) continue;
            item.OnSelect = p =>
            {
                if (_session != s || s.Over || !CanControl(p)) { Chat.Tell(p, "games.err.stale"); return; }
                action(p);
            };
        }

        _controlMenu = m;
        m.Open(c);
    }

    /// <summary>The current warden, or an admin with the games override.</summary>
    internal static bool CanControl(CCSPlayerController? c) =>
        c != null && c.IsValid && (Jb.Warden.IsWarden(c) || Perms.Has(c, AdminOverride));

    private void ShowDeclareWinner(CCSPlayerController c)
    {
        var s = _session;
        if (s == null || s.Over) return;
        var m = new JbMenu(Lang.T("games.menu.declare_winner"));
        foreach (var p in s.Alive().OrderBy(x => x.PlayerName))
        {
            var slot = p.Slot;
            m.Add(Hud.Escape(p.PlayerName), w =>
            {
                if (_session != s || s.Over || !Jb.Warden.IsWarden(w) || w.Slot == slot) return;
                if (!s.Players.Contains(slot) || !(GameSession.C(slot)?.IsAliveP() ?? false)) { Chat.Tell(w, "games.err.not_playing"); return; }
                Logger.LogInformation("[Games] \"{Warden}\" declared {Winner} winner of {Game}", w.PlayerName, p.PlayerName, s.Game.Key);
                Win(s, new[] { slot });
            });
        }

        m.Add(Lang.T("games.menu.back"), ShowControl);
        m.Open(c);
    }

    /// <summary>Refreshes the warden's control menu, but only while it is the menu on screen (never replaces another menu).</summary>
    internal void ReopenControl(CCSPlayerController c)
    {
        if (Running && _controlMenu != null && MenuService.Current(c) == _controlMenu) ShowControl(c);
    }

    /// <summary>A control item was pressed: rebuild the menu so enabled items match the new state.</summary>
    internal void RefreshControl(CCSPlayerController c)
    {
        if (Running) ShowControl(c);
    }

    // ====================================================================== start / end

    private bool CanRunGames(CCSPlayerController c, out string whyKey)
    {
        whyKey = "";
        if (Running) { whyKey = "games.err.running"; return false; }
        if (!Round.IsActive) { whyKey = "games.err.round"; return false; }
        if (Jb.Days.IsInProgress) { whyKey = "games.err.day"; return false; }
        if (LastRequestRunning) { whyKey = "games.err.lr"; return false; }
        if (Eligible().Count() < Math.Max(2, Cfg.MinPrisoners)) { whyKey = "games.err.players"; return false; }
        return true;
    }

    private static bool LastRequestRunning => Jb.LastRequests.NumStartedIgnore(LrFlags.None) > 0;

    /// <summary>Prisoners who may play: alive, not rebels, not in a last request.</summary>
    private static IEnumerable<CCSPlayerController> Eligible() =>
        Players.AlivePrisoners().Where(c => c.Jb() is { } p && !Jb.Rebels.IsRebel(p) && !Jb.LastRequests.IsInLastRequest(p));

    internal void TryStart(CCSPlayerController starter, WardenGame game, Zone? area, GameButtonRecord? button, bool onlyInArea, GameReward reward)
    {
        if (!CanControl(starter)) { Chat.Tell(starter, "games.err.warden_only"); return; }
        if (!CanRunGames(starter, out var why)) { Chat.Tell(starter, why); return; }
        if (!game.CanStart(this, out var gameWhy)) { Chat.Tell(starter, gameWhy); return; }
        var warden = Jb.Warden.Warden?.Controller;
        if (warden == null || !warden.IsValid) { Chat.Tell(starter, "games.err.no_warden"); return; }

        var s = new GameSession
        {
            Game = game, Module = this, WardenSlot = warden.Slot, Area = area, Button = button, Reward = reward, StartedAt = Server.CurrentTime
        };
        foreach (var c in Eligible())
            if (!onlyInArea || area == null || Jb.Zones.IsInside(c, area)) s.Players.Add(c.Slot);
        if (s.Players.Count < Math.Max(2, Cfg.MinPrisoners)) { Chat.Tell(starter, "games.err.players"); return; }

        _session = s;
        CancelPick();
        foreach (var slot in s.Players)
            if (Cfg.GlowParticipantsForGuards && GameSession.C(slot) is { } pc && Glow.Attach(pc, Color.FromArgb(255, 80, 200, 255), v => v.Team == CsTeam.CounterTerrorist) is { } h)
                _glows[slot] = h;

        Chat.TellAll("games.started", Chat.Name(starter), Lang.T(game.NameKey), s.Players.Count, Lang.T(RewardKey(reward), Cfg.RewardPoints, Cfg.RewardRespect));
        Chat.TellAll(game.RulesKey, area != null ? area.Name : "", Cfg.WinZoneName);
        Logger.LogInformation("[Games] \"{Starter}\" started {Game} ({Players} players, reward {Reward}, area {Area})",
            starter.PlayerName, game.Key, s.Players.Count, reward, area?.Name ?? "-");
        SafeCall(() => game.Start(s));
        if (Jb.Warden.IsWarden(starter) || Perms.Has(starter, "games.admin")) ShowControl(starter);
        else if (warden.IsValid) ShowControl(warden);
    }

    internal void Eliminate(GameSession s, int slot, string reasonKey)
    {
        if (s.Over || !s.Players.Remove(slot)) return;
        if (_glows.Remove(slot, out var h)) Glow.Remove(h);
        var c = GameSession.C(slot);
        if (c != null && c.IsValid)
        {
            Chat.TellAll("games.out", Chat.Name(c), Lang.T(reasonKey), s.Players.Count);
            Hud.Clear(c, "games.order");
        }

        SafeCall(() => s.Game.OnOut(s, slot));
    }

    internal void Win(GameSession s, IReadOnlyCollection<int> slots)
    {
        if (s.Over || _session != s) return;
        var winners = slots.Select(GameSession.C)
            .Where(c => c != null && c.IsValid && c.Team == CsTeam.Terrorist && !(c.Jb() is { } p && Jb.Rebels.IsRebel(p))).ToList();
        var names = string.Join(", ", winners.Select(c => Chat.Name(c)));
        if (winners.Count == 0) { EndGame(GameEndReason.NoPlayers); return; }
        Chat.TellAll("games.won", names, Lang.T(s.Game.NameKey));
        Logger.LogInformation("[Games] {Game} won by {Winners}", s.Game.Key, string.Join(", ", winners.Select(c => c!.PlayerName)));
        if (!_rewardGiven && s.Reward != GameReward.None)
        {
            _rewardGiven = true; // UserPointsForMinigamesWinner: one winner reward per round
            foreach (var w in winners) GiveReward(w!, s.Reward);
        }
        else if (_rewardGiven && s.Reward != GameReward.None) Chat.TellAll("games.reward_used");

        EndGame(GameEndReason.Won);
    }

    private void GiveReward(CCSPlayerController c, GameReward reward)
    {
        switch (reward)
        {
            case GameReward.Points when c.Jb() is { } p:
                Jb.Points.AddPoints(p, Cfg.RewardPoints, "games");
                Chat.Tell(c, "games.reward.points_got", Cfg.RewardPoints);
                break;
            case GameReward.CrewRespect when Jb.Crews is Crews.CrewsModule crews:
                crews.Grant(c, Cfg.RewardRespect, "games.reason.win");
                break;
        }
    }

    internal void EndGame(GameEndReason why)
    {
        var s = _session;
        if (s == null) return;
        _session = null;
        if (!s.Over)
        {
            s.Over = true;
            SafeCall(() => s.Game.End(s));
            if (why is GameEndReason.TimeUp or GameEndReason.WardenLost or GameEndReason.DayStarted or GameEndReason.LastRequest or GameEndReason.NoPlayers)
                Chat.TellAll("games.end." + why.ToString().ToLowerInvariant(), Lang.T(s.Game.NameKey));
            Logger.LogInformation("[Games] {Game} ended: {Why}", s.Game.Key, why);
        }

        foreach (var h in _glows.Values) Glow.Remove(h);
        _glows.Clear();
        Hud.ClearGlobal("games");
        Hud.ClearPrefix("games.");
    }

    private void PlayerGone(int slot, string reasonKey)
    {
        var s = _session;
        if (s == null || s.Over) return;
        if (slot == s.WardenSlot && !Jb.Warden.IsWarden(GameSession.C(slot)))
        {
            // The warden left the job: the next warden inherits the game; without one it ends.
            if (Jb.Warden.Warden?.Controller is { IsValid: true } nw) s.WardenSlot = nw.Slot;
        }

        if (s.Players.Contains(slot)) Eliminate(s, slot, reasonKey);
    }

    // ====================================================================== tick

    private void OnTick()
    {
        var s = _session;
        if (s == null || s.Over) return;
        _tick++;
        if (_tick % 8 == 0 && !CheckState(s)) return;
        SafeCall(() => s.Game.Tick(s));
        // After the game's own check (the last two going out together may share the win).
        if (_session == s && !s.Over && s.Players.Count == 0) { EndGame(GameEndReason.NoPlayers); return; }
        if (_tick % 32 == 0 && _session == s && !s.Over) DrawHud(s);
    }

    /// <summary>Jailbreak first: a day, a last request, the warden's loss or the time limit end the game; rebels are out.</summary>
    private bool CheckState(GameSession s)
    {
        if (Jb.Days.IsInProgress) { EndGame(GameEndReason.DayStarted); return false; }
        if (LastRequestRunning) { EndGame(GameEndReason.LastRequest); return false; }
        var w = Jb.Warden.Warden?.Controller;
        if (w == null || !w.IsValid || !w.IsAliveP()) { EndGame(GameEndReason.WardenLost); return false; }
        s.WardenSlot = w.Slot;
        if (Cfg.TimeLimitSeconds > 0 && Server.CurrentTime - s.StartedAt > Cfg.TimeLimitSeconds) { EndGame(GameEndReason.TimeUp); return false; }
        foreach (var slot in s.Players.ToList())
        {
            var c = GameSession.C(slot);
            if (c == null || !c.IsValid || !c.IsAliveP() || c.Team != CsTeam.Terrorist) { Eliminate(s, slot, "games.out.left"); continue; }
            if (c.Jb() is { } p && Jb.Rebels.IsRebel(p)) Eliminate(s, slot, "games.out.rebel");
        }

        return !s.Over;
    }

    private void DrawHud(GameSession s)
    {
        var status = s.Game.Status(s);
        var html = Hud.Bold(Hud.Color(Hud.Escape(Lang.T(s.Game.NameKey)), "#50C8FF")) + "<br>" +
                   Hud.Small(Hud.Escape(Lang.T("games.menu.players_left", s.Players.Count)) + (status.Length > 0 ? "<br>" + status : ""));
        var players = s.Players;
        Hud.SetGlobal("games", html, 30, 1.2f, v => players.Contains(v.Slot) || v.Team == CsTeam.CounterTerrorist || !v.IsAliveP());
    }

    private void OnZoneEntered(Zone zone, CCSPlayerController c)
    {
        var s = _session;
        if (s == null || s.Over || !s.Players.Contains(c.Slot)) return;
        SafeCall(() => s.Game.OnEntered(s, zone, c));
    }

    // ====================================================================== picker queue (MultigamesQueue)

    private void HandToPicker(CCSPlayerController warden)
    {
        if (!CanControl(warden)) { Chat.Tell(warden, "games.err.warden_only"); return; }
        if (!CanRunGames(warden, out var why)) { Chat.Tell(warden, why); return; }
        var eligible = Eligible().Select(c => c.Slot).ToHashSet();
        foreach (var slot in eligible) _queue.Add(slot);
        var pick = _queue.Next(eligible.Contains);
        if (pick is not { } slot0 || GameSession.C(slot0) is not { } picker) { Chat.Tell(warden, "games.err.players"); return; }
        _queue.Picked(slot0);
        _pickerSlot = slot0;
        Chat.TellAll("games.picker", Chat.Name(picker), Cfg.PickSeconds);
        ShowPickerMenu(picker);
        CancelPickTimer();
        _pickTimer = Round.Timer(Math.Max(5, Cfg.PickSeconds), () =>
        {
            _pickTimer = null;
            if (_pickerSlot != slot0 || Running) return;
            _pickerSlot = null;
            var options = PickableGames();
            if (options.Count == 0 || Jb.Warden.Warden?.Controller is not { IsValid: true } w) return;
            Chat.TellAll("games.picker_timeout");
            StartPicked(w, options[Random.Shared.Next(options.Count)], null);
        });
    }

    private List<WardenGame> PickableGames() => _games.Where(g => g.PrisonerPickable && g is not MapGame && g.CanStart(this, out _)).ToList();

    private void ShowPickerMenu(CCSPlayerController picker)
    {
        var m = new JbMenu(Lang.T("games.menu.pick_title"));
        foreach (var g in PickableGames())
        {
            var game = g;
            m.Add(Hud.Escape(Lang.T(game.NameKey)), p => OnPicked(p, game, null));
        }

        foreach (var b in _buttons.Buttons)
        {
            var button = b;
            m.Add(Hud.Escape(Lang.T("games.menu.map_game", button.Label)), p => OnPicked(p, _games.OfType<MapGame>().First(), button));
        }

        m.Open(picker);
    }

    private void OnPicked(CCSPlayerController picker, WardenGame game, GameButtonRecord? button)
    {
        if (_pickerSlot != picker.Slot || Running) return;
        if (!Eligible().Any(x => x.Slot == picker.Slot)) { Chat.Tell(picker, "games.err.not_playing"); return; }
        _pickerSlot = null;
        CancelPickTimer();
        if (Jb.Warden.Warden?.Controller is not { IsValid: true } w) { Chat.Tell(picker, "games.err.no_warden"); return; }
        Chat.TellAll("games.picked", Chat.Name(picker), button != null ? button.Label : Lang.T(game.NameKey));
        StartPicked(w, game, button);
    }

    private void StartPicked(CCSPlayerController warden, WardenGame game, GameButtonRecord? button) =>
        TryStart(warden, game, null, button, false, ParseReward(Cfg.DefaultReward));

    private void CancelPick()
    {
        _pickerSlot = null;
        CancelPickTimer();
    }

    private void CancelPickTimer()
    {
        _pickTimer?.Kill();
        _pickTimer = null;
    }

    // ====================================================================== helpers

    internal IEnumerable<Zone> NamedZones() =>
        Jb.Zones.OfType(ZoneTypeKeys.Named).Where(z => z.Name.Length > 0).OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase);

    internal static string RewardKey(GameReward r) => r switch
    {
        GameReward.Points => "games.reward.points",
        GameReward.CrewRespect => "games.reward.respect",
        _ => "games.reward.none"
    };

    internal static GameReward ParseReward(string s) => Enum.TryParse<GameReward>(s, true, out var r) ? r : GameReward.Points;

    internal void SafeCall(Action a)
    {
        try { a(); }
        catch (Exception ex) { Logger.LogError(ex, "[Games] game callback failed"); }
    }

    /// <summary>Warden menu entry: the same as !games.</summary>
    internal void OpenFor(CCSPlayerController c)
    {
        if (Running) ShowControl(c);
        else ShowGameList(c);
    }
}
