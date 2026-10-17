using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace Swbs.Common;

/// <summary>
/// Round variations: the SWBS Jailbreak days turned into a rounds system for the deathmatch modes. The mode's endless
/// round becomes timed rounds (Rounds.RoundMinutes). Normal rounds alternate with variation rounds; players vote for
/// the next variation near the end of a round. A variation can replace the loadout (RPG Arena, Throwing Knives,
/// Dodgeball, Gun Game ...), add a mutator on top of the mode (Low Gravity, Vampire ...) or change the goal (Bounty
/// Hunter, Juggernaut, Last Man Standing). Every variation round has its own scoreboard: the top three earn points and
/// the MVP is counted in !stats. Only plugins implementing <see cref="IRoundsHost"/> load it.
/// </summary>
public sealed partial class RoundsModule : SwbsModule
{
    public static RoundsModule? Instance { get; private set; }

    internal RoundsConfig Cfg => Section<RoundsConfig>("Rounds");
    internal RoundTuning Tuning => Section<RoundTuning>("RoundTuning");

    public override string Name => "Rounds";
    public override int Order => 60;

    // Short-circuits before Cfg so plugins without rounds never get a Rounds section in their config. (object): the
    // jailbreak plugin is sealed and never a host, which the compiler would otherwise reject.
    public override bool Enabled => (object)Plugin is IRoundsHost { RoundTags.Count: > 0 } && Cfg.Enabled;

    private IRoundsHost Host => (IRoundsHost)(object)Plugin;

    private readonly List<RoundVariation> _all = new();

    /// <summary>The variation of the round being played (null = a normal round).</summary>
    public RoundVariation? Active { get; private set; }

    public IReadOnlyList<RoundVariation> All => _all;

    private RoundVariation? _next;          // decided for the next round (null + _nextNormal = forced normal round)
    private bool _nextNormal;
    private string? _lastKey;
    private int _normalSince = int.MaxValue / 2; // the first round after the warmup may be a variation
    private int _roundNumber;
    private readonly Dictionary<string, int> _playedAtRound = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<int, (int Score, long Order)> _scores = new();
    private long _scoreOrder;
    private bool _ending;
    private float _introUntil;

    internal RocketLauncher Rockets { get; private set; } = null!;
    internal ThrowingKnives Knives { get; private set; } = null!;

    public override void OnLoad(bool hotReload)
    {
        Instance = this;
        Rockets = new RocketLauncher(this);
        Knives = new ThrowingKnives(this);
        RegisterBuiltIns();

        // Every known variation gets its switches in the config (new ones appear after an update).
        var missing = _all.Where(v => !Cfg.Variations.ContainsKey(v.Key)).ToList();
        if (missing.Count > 0)
        {
            Plugin.Config.Update<RoundsConfig>("Rounds", c =>
            {
                foreach (var v in missing) c.Variations[v.Key] = new VariationSettings();
            });
            Plugin.Config.SaveIfDirty(Logger);
        }

        DamageService.Register("Rounds", 95, OnDamage);
        Plugin.RegisterListener<Listeners.OnTick>(OnTickListener);
        Plugin.RegisterListener<Listeners.OnServerPrecacheResources>(m =>
        {
            if (!string.IsNullOrWhiteSpace(Tuning.KnifeModel)) m.AddResource(Tuning.KnifeModel);
        });
        Plugin.RegisterEventHandler<EventRoundPrestart>(OnRoundPrestart);
        Plugin.RegisterEventHandler<EventGrenadeThrown>((ev, _) =>
        {
            if (ev.Userid is { IsValid: true } c) Safe(v => v.GrenadeThrown(c, ev.Weapon ?? ""));
            return HookResult.Continue;
        });
        Plugin.RegisterEventHandler<EventWeaponFire>((ev, _) =>
        {
            if (ev.Userid is { IsValid: true } c) Safe(v => v.WeaponFire(c, ev.Weapon ?? ""));
            return HookResult.Continue;
        });
        Plugin.RegisterEventHandler<EventDecoyStarted>((ev, _) =>
        {
            if (Active is DodgeballVariation && Utilities.GetEntityFromIndex<CBaseEntity>(ev.Entityid) is { IsValid: true } e) e.AcceptInput("Kill");
            return HookResult.Continue;
        });
        Plugin.AddTimer(1f, EverySecond, TimerFlags.REPEAT);
    }

    public override void OnUnload(bool hotReload)
    {
        StopVariation();
        DamageService.Unregister(OnDamage);
        Instance = null;
    }

    /// <summary>Add a game mode's own variation (call in OnAllModulesLoaded or earlier).</summary>
    public void Register(RoundVariation v)
    {
        if (_all.Any(x => x.Key.Equals(v.Key, StringComparison.OrdinalIgnoreCase))) return;
        v.Rounds = this;
        _all.Add(v);
    }

    private void RegisterBuiltIns()
    {
        foreach (var v in new RoundVariation[]
                 {
                     new RpgArenaVariation(), new RocketRainVariation(), new ThrowingKnivesVariation(), new DodgeballVariation(),
                     new ScoutzKnivezVariation(), new OneInTheChamberVariation(), new GunGameVariation(), new HePartyVariation(),
                     new DeagleHeadshotsVariation(), new LowGravityVariation(), new VampireVariation(), new SpeedVariation(),
                     new GlassCannonVariation(), new BountyHunterVariation(), new JuggernautVariation(), new LastManStandingVariation()
                 })
            Register(v);
    }

    internal VariationSettings SettingsOf(RoundVariation v) =>
        Cfg.Variations.TryGetValue(v.Key, out var s) ? s : new VariationSettings();

    private bool Suits(RoundVariation v) => v.Tags.Any(t => Host.RoundTags.Contains(t, StringComparer.OrdinalIgnoreCase));

    /// <summary>Could this variation be played now (mode, switches, players, its own conditions)?</summary>
    internal bool Playable(RoundVariation v, bool forVote)
    {
        if (!Suits(v)) return false;
        var s = SettingsOf(v);
        if (!s.Enabled || forVote && s.Weight <= 0) return false;
        var min = s.MinPlayers > 0 ? s.MinPlayers : v.MinPlayers;
        if (Players.All().Count(c => c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist) < min) return false;
        try { return v.CanRun(); }
        catch { return false; }
    }

    internal bool OnCooldown(RoundVariation v) =>
        _playedAtRound.TryGetValue(v.Key, out var at) && _roundNumber - at <= Math.Max(0, Cfg.RepeatCooldownRounds);

    // ================================================================= round flow

    private HookResult OnRoundPrestart(EventRoundPrestart ev, GameEventInfo info)
    {
        StopVariation();
        CloseVote(false);
        _scores.Clear();
        _ending = false;
        if (RoundClock.IsWarmup) return HookResult.Continue;

        _roundNumber++;
        var next = DecideNext();
        _next = null;
        _nextNormal = false;
        if (next == null)
        {
            _normalSince++;
            return HookResult.Continue;
        }

        // Before the players spawn: their spawn handlers already see the variation (loadout handover).
        _normalSince = 0;
        _lastKey = next.Key;
        _playedAtRound[next.Key] = _roundNumber;
        Active = next;
        try { next.Start(); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Rounds] {Key} failed to start", next.Key);
            Active = null;
        }

        return HookResult.Continue;
    }

    /// <summary>The variation the coming round plays, or null for a normal round.</summary>
    private RoundVariation? DecideNext()
    {
        if (_nextNormal) return null;
        if (_next != null && Playable(_next, false)) return _next;
        if (!RoundLogic.NextIsVariation(Cfg.NormalRoundsBetween, _normalSince)) return null;
        var options = _all.Where(v => Playable(v, true) && !OnCooldown(v)).ToList();
        if (options.Count == 0) options = _all.Where(v => Playable(v, true)).ToList();
        if (options.Count == 0) return null;
        if (Cfg.Schedule == RoundSchedule.Rotation)
        {
            var key = RoundLogic.NextInRotation(_all.Where(v => Playable(v, true)).Select(v => v.Key).ToList(), _lastKey);
            return _all.FirstOrDefault(v => v.Key == key);
        }

        // Random, or a vote nobody answered.
        var pick = RoundLogic.PickWeighted(options.Select(v => (v.Key, SettingsOf(v).Weight)).ToList(), Random.Shared.Next);
        return _all.FirstOrDefault(v => v.Key == pick);
    }

    public override void OnRoundStart()
    {
        if (RoundClock.IsWarmup) return;
        var minutes = Active != null && SettingsOf(Active).Minutes > 0 ? SettingsOf(Active).Minutes : Cfg.RoundMinutes;
        Server.NextFrame(() => RoundClock.SetTimeLeft((float)Math.Max(0.5, minutes) * 60f));
        if (Active != null) Intro(Active);
        else Chat.TellAll("rounds.normal", NextVariationIn());
    }

    private int NextVariationIn() => Math.Max(0, Cfg.NormalRoundsBetween - _normalSince);

    private void Intro(RoundVariation v)
    {
        _introUntil = Server.CurrentTime + Math.Max(1f, Cfg.IntroSeconds);
        var kind = Lang.T("rounds.kind." + v.Kind.ToString().ToLowerInvariant());
        Chat.TellAll("rounds.intro", v.Name, kind);
        Chat.TellAll("rounds.intro_desc", v.Description);
        Chat.AlertAll(Colors.Strip(Lang.T("rounds.alert", v.Name)));
        Hud.SetGlobal("rounds.intro", Lang.T("rounds.hud.intro", Hud.Escape(v.Name), kind, Hud.Escape(v.Description)), 90, Cfg.IntroSeconds);
        if (Cfg.IntroSound.Length > 0) Sound.PlayAll(Cfg.IntroSound);
    }

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        CloseVote(true);
        if (Active == null) return;
        Results(Active);
    }

    private void StopVariation()
    {
        var v = Active;
        Active = null;
        if (v != null)
        {
            try { v.End(); }
            catch (Exception ex) { Logger.LogError(ex, "[Rounds] {Key} failed to end", v.Key); }
        }

        Rockets.Clear();
        Knives.Clear();
        Hud.ClearPrefix("rounds.");
    }

    /// <summary>A variation decided the round early (Gun Game finished, the last man standing).</summary>
    public void Finish(string reasonKey, params object?[] args)
    {
        if (_ending) return;
        _ending = true;
        Chat.TellAll(reasonKey, args);
        RoundClock.End(Math.Max(1f, Cfg.RoundEndDelay));
    }

    // ================================================================= scoring

    public void AddScore(SwbsPlayer p, int amount)
    {
        if (amount == 0 || Active == null) return;
        var cur = _scores.TryGetValue(p.Slot, out var s) ? s.Score : 0;
        _scores[p.Slot] = (Math.Max(0, cur + amount), ++_scoreOrder);
    }

    /// <summary>Outline a player for everyone (null removes it) - the mode decides how (glow or tint).</summary>
    public void Highlight(CCSPlayerController c, System.Drawing.Color? color)
    {
        try { Host.HighlightPlayer(c, color); }
        catch { /* cosmetic */ }
    }

    /// <summary>Run later, only while the same variation is still the current round.</summary>
    public void After(float seconds, RoundVariation owner, Action action) =>
        Plugin.AddTimer(Math.Max(0.05f, seconds), () =>
        {
            if (!ReferenceEquals(Active, owner)) return;
            try { action(); }
            catch (Exception ex) { Logger.LogError(ex, "[Rounds] {Key} timer failed", owner.Key); }
        });

    public int ScoreOf(int slot) => _scores.TryGetValue(slot, out var s) ? s.Score : 0;

    private List<int> Placings() => RoundLogic.Placings(_scores).Where(s => Players.Get(s).Connected).ToList();

    private void Results(RoundVariation v)
    {
        var places = Placings();
        if (places.Count == 0) { Chat.TellAll("rounds.results_none", v.Name); return; }
        Chat.TellAll("rounds.results_title", v.Name);
        for (var i = 0; i < Math.Min(3, places.Count); i++)
        {
            var p = Players.Get(places[i]);
            var points = i < Cfg.PlacePoints.Count ? Cfg.PlacePoints[i] : 0;
            Chat.TellAll("rounds.results_line", i + 1, p.Name, ScoreOf(p.Slot), points);
            if (points > 0 && !p.IsBot) CommonHost.AddPoints(p, points, $"round {v.Key} place {i + 1}");
        }

        var mvp = Players.Get(places[0]);
        Host.AddRoundStat(mvp, "round_mvps", 1);
        Chat.AlertAll(Colors.Strip(Lang.T("rounds.mvp_alert", mvp.Name, v.Name)));
        if (Cfg.MvpSound.Length > 0) Sound.PlayAll(Cfg.MvpSound);
    }

    public override void OnPlayerSpawn(SwbsPlayer player)
    {
        var v = Active;
        if (v == null) return;
        var slot = player.Slot;
        // Two frames: the mode's own spawn loadout (given on the next frame) has run.
        Server.NextFrame(() => Server.NextFrame(() =>
        {
            if (!ReferenceEquals(Active, v) || Players.Get(slot).Controller is not { } c || !c.IsAliveP()) return;
            if (v.OwnsLoadout) Strip(c);
            try { v.Spawn(c); }
            catch (Exception ex) { Logger.LogError(ex, "[Rounds] {Key} spawn failed", v.Key); }
        }));
    }

    private static void Strip(CCSPlayerController c)
    {
        try { c.RemoveWeapons(); }
        catch { /* pawn gone */ }
    }

    public override void OnPlayerDeath(SwbsPlayer victim, SwbsPlayer? attacker, EventPlayerDeath ev)
    {
        var v = Active;
        if (v == null) return;
        try
        {
            if (attacker != null && attacker.Slot != victim.Slot) AddScore(attacker, v.KillScore(victim, attacker, ev));
            v.Death(victim, attacker, ev);
        }
        catch (Exception ex) { Logger.LogError(ex, "[Rounds] {Key} death failed", v.Key); }
    }

    private HookResult OnDamage(DamageContext ctx)
    {
        var v = Active;
        if (v == null) return HookResult.Continue;
        try { return v.Damage(ctx); }
        catch { return HookResult.Continue; }
    }

    private void Safe(Action<RoundVariation> action)
    {
        var v = Active;
        if (v == null) return;
        try { action(v); }
        catch (Exception ex) { Logger.LogError(ex, "[Rounds] {Key} hook failed", v.Key); }
    }

    private void OnTickListener()
    {
        var now = (float)Server.EngineTime;
        Rockets.Tick(now);
        Knives.Tick(now);
        if (Active != null) Safe(v => v.Tick(now));
    }

    private void EverySecond()
    {
        if (RoundClock.IsWarmup) return;
        var left = RoundClock.TimeLeft;
        if (Active != null) Safe(v => v.Second());

        // The mode's round never ends by itself (mp_ignore_round_win_conditions): end it on time.
        if (!_ending && left <= 0 && Players.All().Any())
        {
            _ending = true;
            RoundClock.End(Math.Max(1f, Cfg.RoundEndDelay));
            return;
        }

        if (!_voteOpen && !_voteDone && left > 0 && left <= Cfg.VoteAtSecondsLeft) OpenVote();
        if (_voteOpen && Server.CurrentTime >= _voteEnds) CloseVote(true);
        ShowHud(left);
    }

    private void ShowHud(float left)
    {
        var v = Active;
        if (v == null || Server.CurrentTime < _introUntil) return;
        var places = Placings();
        foreach (var c in Players.Humans().ToList())
        {
            var line = Lang.T("rounds.hud.title", Hud.Escape(v.Name), RoundLogic.Clock(left));
            string? extra = null;
            try { extra = v.HudLine(c); }
            catch { /* objective line is cosmetic */ }
            if (!string.IsNullOrEmpty(extra)) line += "<br>" + extra;
            var rank = places.IndexOf(c.Slot);
            line += "<br>" + Lang.T("rounds.hud.score", ScoreOf(c.Slot), rank >= 0 ? $"#{rank + 1}" : "-",
                places.Count > 0 ? Hud.Escape(Players.Get(places[0]).Name) : "-");
            Hud.Set(c, "rounds.panel", line, 15, 1.5f);
        }
    }
}
