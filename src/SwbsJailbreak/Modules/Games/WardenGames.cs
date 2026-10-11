using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using CssVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Games;

// The built-in warden games (design §4.3). Judging uses real engine input only: buttons, velocity, eye pitch and zones.

internal static class GameInput
{
    public static InputSample Sample(CCSPlayerController c)
    {
        var pawn = c.PawnOrNull();
        if (pawn == null) return new InputSample(false, false, 0f, 0f); // c.Buttons reads the pawn's movement services
        var v = pawn.AbsVelocity;
        var speed = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        var buttons = c.Buttons;
        // A mouse-wheel jump can press and release inside one tick: a take-off (airborne, moving up) counts as a jump too.
        var jumped = (buttons & PlayerButtons.Jump) != 0 || (!c.OnGround() && v.Z > 50f);
        return new InputSample(jumped, (buttons & PlayerButtons.Duck) != 0, speed, pawn.EyeAngles.X);
    }

    /// <summary>Shared "last one standing" check: call once per tick with the players who went out this tick.</summary>
    public static void CheckLastStanding(GameSession s, List<int> outThisTick)
    {
        var winners = GameJudge.LastStanding(s.Players, outThisTick);
        outThisTick.Clear();
        if (winners != null) s.Win(winners);
    }
}

/// <summary>UserPointsForMinigamesWinner (hlstriker): the first player to reach the map's "mgwin" Named zone wins.</summary>
internal sealed class WinZoneGame : WardenGame
{
    public override string Key => "win_zone";
    public override string NameKey => "games.win_zone.name";
    public override string RulesKey => "games.win_zone.rules";

    public override bool CanStart(WardenGamesModule m, out string whyKey)
    {
        whyKey = "games.err.no_win_zone";
        return Jb.Zones.FindByName(ZoneTypeKeys.Named, m.Cfg.WinZoneName) != null;
    }

    public override void OnEntered(GameSession s, Zone zone, CCSPlayerController c)
    {
        if (zone.TypeKey == ZoneTypeKeys.Named && zone.Name.Equals(s.Cfg.WinZoneName, StringComparison.OrdinalIgnoreCase) && c.IsAliveP())
            s.Win(new[] { c.Slot });
    }
}

/// <summary>Last One Standing: stay inside the chosen zone; leaving it or dying is out; the last player wins.</summary>
internal sealed class LastStandingGame : WardenGame
{
    private readonly List<int> _outThisTick = new();

    public override string Key => "last_standing";
    public override string NameKey => "games.last_standing.name";
    public override string RulesKey => "games.last_standing.rules";
    public override bool UsesArea => true;

    public override void Start(GameSession s) => _outThisTick.Clear();

    public override void OnOut(GameSession s, int slot) => _outThisTick.Add(slot);

    public override void Tick(GameSession s)
    {
        if (s.Area is { } area && Server.TickCount % 4 == 0)
            foreach (var slot in s.Players.ToList())
                if (GameSession.C(slot) is { } c && !Jb.Zones.IsInside(c, area)) s.Eliminate(slot, "games.out.left_area");
        if (!s.Over) GameInput.CheckLastStanding(s, _outThisTick);
    }
}

/// <summary>Simon Says, judged by the plugin from real jump / crouch buttons, movement and eye pitch.</summary>
internal sealed class SimonSaysGame : WardenGame
{
    private SimonOrder _order;
    private bool _simon;
    private float _givenAt;
    private bool _active;
    private readonly HashSet<int> _passed = new();
    private readonly List<int> _outThisTick = new();

    public override string Key => "simon_says";
    public override string NameKey => "games.simon.name";
    public override string RulesKey => "games.simon.rules";

    public override void Start(GameSession s)
    {
        _active = false;
        _passed.Clear();
        _outThisTick.Clear();
    }

    public override void OnOut(GameSession s, int slot) => _outThisTick.Add(slot);

    public override void AddWardenItems(GameSession s, JbMenu m)
    {
        foreach (var order in Enum.GetValues<SimonOrder>())
        {
            var o = order;
            m.Add(Lang.T("games.simon.give_simon", Lang.T(OrderKey(o))), p => Give(s, p, o, true), !_active);
            if (o != SimonOrder.StandStill) m.Add(Lang.T("games.simon.give_plain", Lang.T(OrderKey(o))), p => Give(s, p, o, false), !_active);
        }
    }

    private void Give(GameSession s, CCSPlayerController warden, SimonOrder order, bool simon)
    {
        if (s.Over || _active) return;
        _order = order;
        _simon = simon;
        _givenAt = Server.CurrentTime;
        _active = true;
        _passed.Clear();
        var text = simon ? Lang.T("games.simon.order_simon", Lang.T(OrderKey(order))) : Lang.T("games.simon.order_plain", Lang.T(OrderKey(order)));
        Chat.PrintAll(text);
        var html = Hud.Big(Hud.Bold(Hud.Color(Hud.Escape(text), simon ? "#55FF55" : "#FFAA00")));
        foreach (var c in s.Alive()) Hud.Set(c, "games.order", html, 40, s.Cfg.SimonReactSeconds + 0.5f);
        s.Module.RefreshControl(warden);
    }

    public override void Tick(GameSession s)
    {
        if (!_active)
        {
            GameInput.CheckLastStanding(s, _outThisTick); // deaths between orders
            return;
        }

        var elapsed = Server.CurrentTime - _givenAt;
        foreach (var slot in s.Players.ToList())
        {
            if (_passed.Contains(slot) || GameSession.C(slot) is not { } c) continue;
            var v = GameJudge.Simon(_order, _simon, GameInput.Sample(c), elapsed, s.Cfg.SimonGraceSeconds, s.Cfg.SimonReactSeconds, false);
            if (v == Verdict.Pass && (_simon && _order != SimonOrder.StandStill)) _passed.Add(slot);
            else if (v == Verdict.Fail) s.Eliminate(slot, _simon ? "games.out.too_slow" : "games.out.no_simon");
        }

        if (!s.Over && elapsed >= s.Cfg.SimonReactSeconds + 0.1f)
        {
            _active = false;
            if (s.Players.Count > 1) Chat.TellAll("games.simon.round_over", s.Players.Count);
            if (Jb.Warden.Warden?.Controller is { IsValid: true } w) s.Module.ReopenControl(w);
        }

        if (!s.Over) GameInput.CheckLastStanding(s, _outThisTick);
    }

    public override string Status(GameSession s) =>
        _active ? Hud.Escape(_simon ? Lang.T("games.simon.order_simon", Lang.T(OrderKey(_order))) : Lang.T("games.simon.order_plain", Lang.T(OrderKey(_order)))) : "";

    private static string OrderKey(SimonOrder o) => "games.simon.order." + o.ToString().ToLowerInvariant();
}

/// <summary>Red Light / Green Light: move on green, freeze on red (real velocity); first into the finish zone wins.</summary>
internal sealed class RedLightGame : WardenGame
{
    private bool _red = true;
    private float _redSince;
    private bool _auto;
    private float _nextSwitch;
    private readonly List<int> _outThisTick = new();

    public override string Key => "red_light";
    public override string NameKey => "games.red_light.name";
    public override string RulesKey => "games.red_light.rules";
    public override bool UsesArea => true;

    public override void Start(GameSession s)
    {
        _red = true;
        _redSince = Server.CurrentTime + 3f; // opening red: 3 s to stop before the light is judged
        _auto = false;
        _outThisTick.Clear();
        Announce(s);
    }

    public override void OnOut(GameSession s, int slot) => _outThisTick.Add(slot);

    public override void AddWardenItems(GameSession s, JbMenu m)
    {
        m.Add(Lang.T("games.red_light.green"), p => { Set(s, false); s.Module.RefreshControl(p); }, _red);
        m.Add(Lang.T("games.red_light.red"), p => { Set(s, true); s.Module.RefreshControl(p); }, !_red);
        m.Add(Lang.T(_auto ? "games.red_light.auto_off" : "games.red_light.auto_on"), p =>
        {
            _auto = !_auto;
            _nextSwitch = Server.CurrentTime + (float)GameJudge.LightPhaseSeconds(Random.Shared.NextDouble());
            s.Module.RefreshControl(p);
        });
    }

    private void Set(GameSession s, bool red)
    {
        if (s.Over || _red == red) return;
        _red = red;
        if (red) _redSince = Server.CurrentTime;
        if (s.Cfg.LightSound.Length > 0) Sound.PlayAll(s.Cfg.LightSound);
        Announce(s);
    }

    private void Announce(GameSession s)
    {
        var text = Lang.T(_red ? "games.red_light.is_red" : "games.red_light.is_green");
        Chat.PrintAll(text);
        var html = Hud.Big(Hud.Bold(Hud.Color(Hud.Escape(text), _red ? "#FF4444" : "#55FF55")));
        foreach (var c in s.Alive()) Hud.Set(c, "games.order", html, 40, 2f);
    }

    public override void Tick(GameSession s)
    {
        var now = Server.CurrentTime;
        if (_auto && now >= _nextSwitch)
        {
            Set(s, !_red);
            _nextSwitch = now + (float)GameJudge.LightPhaseSeconds(Random.Shared.NextDouble());
        }

        if (_red)
            foreach (var slot in s.Players.ToList())
                if (GameSession.C(slot) is { } c && GameJudge.RedLightOut(GameInput.Sample(c).Speed2D, now - _redSince, s.Cfg.RedLightGraceSeconds, s.Cfg.RedLightSpeed))
                    s.Eliminate(slot, "games.out.moved_on_red");
        if (!s.Over) GameInput.CheckLastStanding(s, _outThisTick);
    }

    public override void OnEntered(GameSession s, Zone zone, CCSPlayerController c)
    {
        if (s.Area != null && zone.Id == s.Area.Id && c.IsAliveP()) s.Win(new[] { c.Slot });
    }

    public override string Status(GameSession s) =>
        Hud.Color(Hud.Escape(Lang.T(_red ? "games.red_light.is_red" : "games.red_light.is_green")), _red ? "#FF4444" : "#55FF55");
}

/// <summary>
/// Musical Zones (musical chairs on Named zones "music*"): no camping in a spot while the music plays; when it stops each
/// spot is claimed by the first player to enter it, everyone else is out, and one spot fewer remains next time.
/// </summary>
internal sealed class MusicalZonesGame : WardenGame
{
    private readonly List<Zone> _active = new();
    private readonly Dictionary<(int Slot, int Zone), long> _entered = new();
    private readonly Dictionary<int, float> _campSince = new();
    private readonly Dictionary<int, List<CBeam>> _rings = new();
    private readonly List<int> _outThisTick = new();
    private bool _music;
    private float _stoppedAt;
    private bool _resolving;

    public override string Key => "musical_zones";
    public override string NameKey => "games.musical.name";
    public override string RulesKey => "games.musical.rules";

    private static List<Zone> Spots(GamesConfig cfg) =>
        Jb.Zones.OfType(ZoneTypeKeys.Named).Where(z => z.Name.StartsWith(cfg.MusicZonePrefix, StringComparison.OrdinalIgnoreCase)).ToList();

    public override bool CanStart(WardenGamesModule m, out string whyKey)
    {
        whyKey = "games.err.no_music_zones";
        return Spots(m.Cfg).Count > 0;
    }

    public override void Start(GameSession s)
    {
        _music = false;
        _resolving = false;
        _entered.Clear();
        _campSince.Clear();
        _outThisTick.Clear();
        var spots = Spots(s.Cfg).OrderBy(_ => Random.Shared.Next()).ToList();
        SetActive(spots.Take(GameJudge.MusicalZoneCount(s.Players.Count, spots.Count)).ToList());
    }

    public override void End(GameSession s) => SetActive(new List<Zone>());

    public override void OnOut(GameSession s, int slot) => _outThisTick.Add(slot);

    public override void AddWardenItems(GameSession s, JbMenu m)
    {
        m.Add(Lang.T("games.musical.start"), p => { Music(s, true); s.Module.RefreshControl(p); }, !_music && !_resolving);
        m.Add(Lang.T("games.musical.stop"), p => { Music(s, false); s.Module.RefreshControl(p); }, _music);
    }

    private void Music(GameSession s, bool on)
    {
        if (s.Over) return;
        _music = on;
        _campSince.Clear();
        if (on)
        {
            _entered.Clear();
            if (s.Cfg.MusicStartSound.Length > 0) Sound.PlayAll(s.Cfg.MusicStartSound);
        }
        else
        {
            _stoppedAt = Server.CurrentTime;
            _resolving = true;
            if (s.Cfg.MusicStopSound.Length > 0) Sound.PlayAll(s.Cfg.MusicStopSound);
        }

        var text = Lang.T(on ? "games.musical.playing" : "games.musical.stopped", _active.Count);
        Chat.PrintAll(text);
        foreach (var c in s.Alive()) Hud.Set(c, "games.order", Hud.Big(Hud.Bold(Hud.Color(Hud.Escape(text), on ? "#55FF55" : "#FF4444"))), 40, 3f);
    }

    public override void OnEntered(GameSession s, Zone zone, CCSPlayerController c)
    {
        if (!_music && _active.Any(z => z.Id == zone.Id)) _entered.TryAdd((c.Slot, zone.Id), Server.TickCount);
    }

    public override void Tick(GameSession s)
    {
        var now = Server.CurrentTime;
        if (_music && Server.TickCount % 8 == 0)
        {
            // No camping on a spot while the music plays.
            foreach (var slot in s.Players.ToList())
            {
                if (GameSession.C(slot) is not { } c) continue;
                if (!_active.Any(z => Jb.Zones.IsInside(c, z))) { _campSince.Remove(slot); continue; }
                if (!_campSince.TryGetValue(slot, out var since)) { _campSince[slot] = now; Chat.Tell(c, "games.musical.no_camping"); continue; }
                if (now - since >= s.Cfg.MusicCampSeconds) s.Eliminate(slot, "games.out.camping");
            }
        }

        if (_resolving && now - _stoppedAt >= s.Cfg.MusicGraceSeconds)
        {
            _resolving = false;
            var samples = new List<(int, int?, long)>();
            foreach (var slot in s.Players)
            {
                if (GameSession.C(slot) is not { } c) continue;
                var zone = _active.Where(z => Jb.Zones.IsInside(c, z))
                    .OrderBy(z => _entered.TryGetValue((slot, z.Id), out var t) ? t : long.MaxValue).FirstOrDefault();
                var tick = zone != null && _entered.TryGetValue((slot, zone.Id), out var et) ? et : long.MaxValue;
                samples.Add((slot, zone?.Id, tick));
            }

            var (_, outSlots) = GameJudge.Musical(samples, _active.Select(z => z.Id).ToHashSet());
            foreach (var slot in outSlots) s.Eliminate(slot, "games.out.no_spot");
            if (!s.Over) GameInput.CheckLastStanding(s, _outThisTick);
            if (!s.Over)
            {
                var keep = GameJudge.MusicalZoneCount(s.Players.Count, Spots(s.Cfg).Count);
                SetActive(_active.OrderBy(_ => Random.Shared.Next()).Take(keep).ToList());
                Chat.TellAll("games.musical.next", s.Players.Count, _active.Count);
                if (Jb.Warden.Warden?.Controller is { IsValid: true } w) s.Module.ReopenControl(w); // "Start the music" is enabled again
            }

            return;
        }

        if (!s.Over) GameInput.CheckLastStanding(s, _outThisTick);
    }

    private void SetActive(List<Zone> zones)
    {
        foreach (var id in _rings.Keys.Where(id => zones.All(z => z.Id != id)).ToList())
        {
            foreach (var b in _rings[id]) Fx.SafeRemove(b);
            _rings.Remove(id);
        }

        _active.Clear();
        _active.AddRange(zones);
        foreach (var z in zones.Where(z => !_rings.ContainsKey(z.Id)))
        {
            var bc = z.Box.BottomCenter;
            var radius = Math.Max(16f, Math.Min(z.Box.Size.X, z.Box.Size.Y) * 0.5f);
            _rings[z.Id] = Fx.Ring(new CssVector(bc.X, bc.Y, bc.Z + 3f), radius, Color.FromArgb(230, 255, 200, 40), 3f, 0f, 24);
        }
    }

    public override string Status(GameSession s) => Hud.Escape(Lang.T(_music ? "games.musical.playing" : "games.musical.waiting", _active.Count));
}

/// <summary>
/// A map's own game (MultigamesGameSelect): the plugin presses the marked button. If the map has a win zone the first player
/// to reach it wins; otherwise the warden declares the winner.
/// </summary>
internal sealed class MapGame : WardenGame
{
    public override string Key => "map_game";
    public override string NameKey => "games.map_game.name";
    public override string RulesKey => "games.map_game.rules";
    public override bool PrisonerPickable => false; // the picker menu lists the buttons directly

    public override bool CanStart(WardenGamesModule m, out string whyKey)
    {
        whyKey = "games.err.no_buttons";
        return m.Buttons.Count > 0;
    }

    public override void Start(GameSession s)
    {
        if (s.Button == null) return;
        var by = Jb.Warden.Warden?.Controller;
        if (!s.Module.Press(s.Button, by)) Chat.TellAll("games.buttons.missing", s.Button.Label);
        else Chat.TellAll("games.map_game.started", s.Button.Label);
    }

    public override void OnEntered(GameSession s, Zone zone, CCSPlayerController c)
    {
        if (zone.TypeKey == ZoneTypeKeys.Named && zone.Name.Equals(s.Cfg.WinZoneName, StringComparison.OrdinalIgnoreCase) && c.IsAliveP())
            s.Win(new[] { c.Slot });
    }

    public override string Status(GameSession s) => s.Button != null ? Hud.Escape(s.Button.Label) : "";
}
