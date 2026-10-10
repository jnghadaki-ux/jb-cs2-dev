using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.LastGuard;

/// <summary>Config section "LastGuard" (UltJB_LastGuard.sp v1.9). Defaults = original cvars.</summary>
public sealed class LastGuardConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>ultjb_lastguard_guards_needed (min 1): guards on the team at round start needed to enable LG.</summary>
    public int GuardsNeeded { get; set; } = 3;

    /// <summary>ultjb_lastguard_hp_per_prisoner: bonus HP per alive prisoner at activation.</summary>
    public int HpPerPrisoner { get; set; } = 17;

    /// <summary>ultjb_lastguard_hp_per_kill: HP gained per prisoner the last guard kills.</summary>
    public int HpPerKill { get; set; } = 5;

    /// <summary>ultjb_lastguard_damage_wait: seconds to damage someone before the punishment starts.</summary>
    public int DamageWait { get; set; } = 20;

    /// <summary>ultjb_lastguard_damage_interval (min 1): seconds between punishment ticks.</summary>
    public int DamageInterval { get; set; } = 3;

    /// <summary>ultjb_lastguard_damage_amount (min 1): slap damage per tick.</summary>
    public int DamageAmount { get; set; } = 10;

    /// <summary>Last guard model (original ctm_heavy.mdl). Empty = do not change the model.</summary>
    public string LastGuardModel { get; set; } = "characters/models/ctm_heavy/ctm_heavy.vmdl";

    public int BeaconSegments { get; set; } = 16;
    public float BeaconWidth { get; set; } = 5.5f;

    /// <summary>Original TE amplitude 10 (jagged ring). Set 0 for a smooth ring.</summary>
    public float BeaconAmplitude { get; set; } = 10f;

    /// <summary>".vsnd" fallback for buttons/blip1.wav (played to players within BeaconAudibleRadius; fixed pitch).</summary>
    public string BeaconSound { get; set; } = "sounds/tools/sfm/beep.vsnd";

    /// <summary>Sound event name; when set it is emitted from the player with volume 0.7 and the distance pitch.</summary>
    public string BeaconSoundEvent { get; set; } = "";

    public float BeaconAudibleRadius { get; set; } = 1000f;

    /// <summary>Slap sound (SlapPlayer sound=true). ".vsnd" or sound event; empty = silent.</summary>
    public string SlapSound { get; set; } = "";
}

/// <summary>
/// Port of UltJB_LastGuard.sp (v1.9, hlstriker). When enough guards started the round and only one guard is left
/// alive (no non-freeday LR initialised, no day), that guard becomes the last guard: bonus HP per prisoner, heavy
/// model, beacons on every alive player and a punishment timer that slaps/kills the last guard if they do not
/// damage anyone.
/// </summary>
public sealed class LastGuardModule : JbModule, ILastGuardService
{
    private const float BeaconDistanceMin = 550f;
    private const float BeaconDistanceMax = 2200f;
    private const float BeaconDelayMin = 0.3f;
    private const float BeaconDelayMax = 1.0f;
    private const float BeaconPitchMin = 91f;
    private const float BeaconPitchMax = 120f;
    private const string HintKey = "lg.hint";

    private static readonly Color BeaconColorGuard = Color.FromArgb(200, 0, 178, 255);
    private static readonly Color BeaconColorPrisoner = Color.FromArgb(200, 255, 77, 0);

    private LastGuardConfig Cfg => Section<LastGuardConfig>("LastGuard");

    public override int Order => 22;
    public override bool Enabled => Cfg.Enabled;

    private JbPlayer? _lastGuard;
    private bool _canLastGuard;
    private Timer? _timer;
    private int _counterWait;
    private int _counterInterval;
    private readonly float[] _nextBeacon = new float[Players.MaxSlots];
    private readonly HashSet<int> _beaconed = new();
    private readonly LgPulses _pulses = new();

    private int GuardsNeededValue => Math.Max(1, Cfg.GuardsNeeded);
    private int DamageIntervalValue => Math.Max(1, Cfg.DamageInterval);
    private int DamageWaitValue => Math.Max(0, Cfg.DamageWait);
    private int DamageAmountValue => Math.Max(1, Cfg.DamageAmount);

    // ================================================================= ILastGuardService

    public JbPlayer? LastGuard => _lastGuard is { Connected: true } lg ? lg : null;

    public bool IsActive => LastGuard != null;

    /// <summary>UltJB_LastGuard_GetNumNeeded.</summary>
    public int NumNeeded => GuardsNeededValue;

    /// <summary>UltJB_LastGuard_ResetDamageCounters (used by the RPG warday).</summary>
    public void ResetDamageCounters()
    {
        _counterWait = 0;
        _counterInterval = DamageIntervalValue;
        var lg = LastGuard;
        if (lg != null) ShowWaitHint(lg);
    }

    // ================================================================= lifecycle

    public override void OnLoad(bool hotReload)
    {
        Jb.LastGuard = this;
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        Plugin.RegisterListener<Listeners.OnServerPrecacheResources>(OnPrecache);
    }

    public override void OnAllModulesLoaded(bool hotReload)
    {
        JbEvents.LastRequestInitialized += OnLastRequestInitialized;
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.LastRequestInitialized -= OnLastRequestInitialized;
        RemoveLastGuard();
        _pulses.Clear();
        Hud.ClearPrefix("lg.");
    }

    public override void OnMapStart(string mapName)
    {
        _lastGuard = null;
        _beaconed.Clear();
        _pulses.Forget();
    }

    public override void OnMapEnd()
    {
        RemoveLastGuard();
        _pulses.Clear();
    }

    private void OnPrecache(ResourceManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(Cfg.LastGuardModel)) manifest.AddResource(Cfg.LastGuardModel);
    }

    // ================================================================= events

    public override void OnRoundStart()
    {
        RemoveLastGuard();
        var guards = Players.Count(CsTeam.CounterTerrorist); // in game, alive or not
        if (guards == 0) return; // original: canLastGuard keeps its previous value (harmless quirk kept)
        var plural = guards == 1 ? "" : "s";
        if (guards >= GuardsNeededValue)
        {
            _canLastGuard = true;
            Chat.PrintAll(LgText.T("lg.enabled", guards, plural));
        }
        else
        {
            _canLastGuard = false;
            Chat.PrintAll(LgText.T("lg.disabled", guards, plural));
        }
    }

    public override void OnRoundEnd(CsTeam winner, int reason) => RemoveLastGuard();

    [GameEventHandler]
    public HookResult OnMatchEndRestart(EventCsMatchEndRestart ev, GameEventInfo info)
    {
        RemoveLastGuard();
        return HookResult.Continue;
    }

    public override void OnPlayerTeam(JbPlayer player, CsTeam oldTeam, CsTeam newTeam, bool disconnect)
    {
        if (newTeam != CsTeam.CounterTerrorist) TryRemoveLastGuard(player);
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        TryRemoveLastGuard(player);
        _beaconed.Remove(player.Slot);
    }

    private void OnLastRequestInitialized(JbPlayer prisoner) => RemoveLastGuard();

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        _beaconed.Remove(victim.Slot);
        switch (victim.Team)
        {
            case CsTeam.CounterTerrorist:
                TryRemoveLastGuard(victim);
                CheckForLastGuard(victim);
                break;
            case CsTeam.Terrorist:
                if (attacker != null && attacker.IsGuard && ReferenceEquals(attacker, LastGuard))
                {
                    var c = attacker.Controller;
                    if (c != null) Jb.LastRequests.SetHealth(attacker, c.GetHp() + Math.Max(0, Cfg.HpPerKill));
                }

                break;
        }
    }

    /// <summary>Any damage dealt by the last guard (even to himself - faithful quirk) resets the punishment counters.</summary>
    [GameEventHandler]
    public HookResult OnPlayerHurt(EventPlayerHurt ev, GameEventInfo info)
    {
        var lg = LastGuard;
        if (lg == null) return HookResult.Continue;
        var attacker = Players.Get(ev.Attacker);
        if (attacker == null || !ReferenceEquals(attacker, lg)) return HookResult.Continue;
        if (Players.Get(ev.Userid) == null) return HookResult.Continue;
        ResetDamageCounters();
        return HookResult.Continue;
    }

    // ================================================================= activation

    /// <summary>
    /// CheckForLastGuard. Runs from the post death handler; in CS2 the victim can still read as alive during
    /// player_death, so the victim is excluded explicitly.
    /// </summary>
    private void CheckForLastGuard(JbPlayer? victim)
    {
        if (!_canLastGuard) return;
        if (Jb.Days.IsInProgress) return;

        JbPlayer? last = null;
        var alive = 0;
        foreach (var c in Players.AliveGuards())
        {
            var p = Players.Get(c);
            if (p == null || ReferenceEquals(p, victim)) continue;
            alive++;
            last = p;
        }

        if (alive != 1 || last == null) return;
        SetLastGuard(last);
    }

    private static bool HasStartedFreedayLr(JbPlayer p) =>
        Jb.LastRequests.HasStartedLastRequest(p) && (Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) != 0;

    private void SetLastGuard(JbPlayer lg)
    {
        // No last guard while a non-freeday LR is initialised.
        var numInitialized = Jb.LastRequests.NumInitialized - Jb.LastRequests.NumStartedContains(LrFlags.Freeday);
        if (numInitialized > 0) return;

        // UltJB_LastGuard_OnActivated_Pre (the Warden module removes the warden here). Handlers may veto.
        if (!JbEvents.RaiseLastGuardActivatingPre(lg)) return;

        StopTimer();
        _lastGuard = lg;

        var now = Server.CurrentTime;
        var prisoners = 0;
        var hint = LgText.T("lg.kill_hint", Hud.Escape(lg.Name));
        foreach (var c in Players.Alive())
        {
            var p = Players.Get(c);
            if (p == null || HasStartedFreedayLr(p)) continue;
            _nextBeacon[p.Slot] = now + (float)Random.Shared.NextDouble() * BeaconDelayMax;
            _beaconed.Add(p.Slot);
            if (c.Team != CsTeam.Terrorist) continue;
            Hud.Set(c, HintKey, hint, 100, 5);
            prisoners++;
        }

        Chat.PrintAll(LgText.T("lg.is_last_guard", lg.Name));

        var ctrl = lg.Controller;
        if (ctrl != null)
            Jb.LastRequests.SetHealth(lg, ctrl.GetHp() + Math.Max(0, Cfg.HpPerPrisoner) * prisoners);

        var pawn = lg.Pawn;
        if (pawn != null && !string.IsNullOrWhiteSpace(Cfg.LastGuardModel))
        {
            try { pawn.SetModel(Cfg.LastGuardModel); }
            catch (Exception ex) { Logger.LogWarning(ex, "Last guard model {Model} could not be applied", Cfg.LastGuardModel); }
        }

        StartTimer(lg);
        JbEvents.RaiseLastGuardActivated(lg);
    }

    private void TryRemoveLastGuard(JbPlayer p)
    {
        if (_lastGuard == null || !ReferenceEquals(_lastGuard, p)) return;
        RemoveLastGuard();
    }

    /// <summary>Stop the timer, clear the last guard and every beacon. The last guard keeps HP and model.</summary>
    private void RemoveLastGuard()
    {
        StopTimer();
        var lg = _lastGuard;
        _lastGuard = null;
        _beaconed.Clear();
        if (lg != null) Hud.Clear(lg.Slot, HintKey);
    }

    // ================================================================= punishment timer

    private void StartTimer(JbPlayer lg)
    {
        _counterWait = 0;
        _counterInterval = DamageIntervalValue;
        ShowWaitHint(lg);
        _timer = Plugin.AddTimer(1.0f, TimerTick, TimerFlags.REPEAT);
    }

    /// <summary>Kill the punishment timer exactly once (owned here, killed on removal / round end / map end).</summary>
    private void StopTimer()
    {
        var t = _timer;
        _timer = null;
        if (t == null) return;
        try { t.Kill(); }
        catch (Exception ex) { Logger.LogDebug(ex, "Last guard timer kill"); }
    }

    private void TimerTick()
    {
        if (_timer == null) return;
        var lg = LastGuard;
        var c = lg?.Controller;
        if (lg == null || c == null)
        {
            RemoveLastGuard();
            return;
        }

        _counterWait++;
        if (_counterWait < DamageWaitValue)
        {
            ShowWaitHint(lg);
            return;
        }

        // interval starts at DamageInterval, so the first tick fires as soon as the wait expires (original).
        _counterInterval++;
        if (_counterInterval < DamageIntervalValue)
        {
            ShowIntervalHint(lg);
            return;
        }

        _counterInterval = 0;
        if (!c.IsAliveP()) return;
        if (c.GetHp() > DamageAmountValue)
        {
            c.Slap(DamageAmountValue);
            if (!string.IsNullOrWhiteSpace(Cfg.SlapSound)) Sound.PlayAt(c, Cfg.SlapSound);
            ShowIntervalHint(lg);
        }
        else
        {
            c.CommitSuicide(false, true);
        }
    }

    private void ShowWaitHint(JbPlayer lg) =>
        Hud.Set(lg.Controller, HintKey, LgText.T("lg.wait_hint", DamageWaitValue - _counterWait), 100, 2);

    private void ShowIntervalHint(JbPlayer lg) =>
        Hud.Set(lg.Controller, HintKey, LgText.T("lg.interval_hint", DamageIntervalValue - _counterInterval), 100, 2);

    // ================================================================= beacons

    private void OnTick()
    {
        _pulses.Tick();
        if (_beaconed.Count == 0) return;
        var now = Server.CurrentTime;
        foreach (var slot in _beaconed.ToArray())
        {
            if (now < _nextBeacon[slot]) continue;
            var p = Players.Get(slot);
            var c = p.Controller;
            if (c == null || !p.IsAlive)
            {
                _beaconed.Remove(slot);
                continue;
            }

            Beacon(c, now);
        }
    }

    private void Beacon(CCSPlayerController c, float now)
    {
        var percent = GetBeaconPercent(c);
        var delay = (BeaconDelayMax - BeaconDelayMin) * percent + BeaconDelayMin;
        _nextBeacon[c.Slot] = now + delay + 0.2f;

        Color color;
        switch (c.Team)
        {
            case CsTeam.CounterTerrorist: color = BeaconColorGuard; break;
            case CsTeam.Terrorist: color = BeaconColorPrisoner; break;
            default: return;
        }

        var o = c.Origin();
        if (o == null) return;
        // TE BeamRingPoint diameter 10 -> 220 (radius 5 -> 110) over `delay`, width 5.5, amplitude 10.
        _pulses.Spawn(new Vector(o.X, o.Y, o.Z + 10f), 5f, 110f, delay, color, Math.Max(0.5f, Cfg.BeaconWidth),
            Math.Clamp(Cfg.BeaconSegments, 3, 64), Math.Max(0f, Cfg.BeaconAmplitude));

        var pitch = MathF.Round((BeaconPitchMax - BeaconPitchMin) * (1f - percent) + BeaconPitchMin);
        PlayBeaconSound(c, o, pitch);
    }

    /// <summary>(clamp(distance to nearest alive enemy, 550, 2200) - 550) / 1650; 1.0 when there is no enemy.</summary>
    private static float GetBeaconPercent(CCSPlayerController c)
    {
        var o = c.Origin();
        var min = BeaconDistanceMax;
        if (o != null)
        {
            foreach (var other in Players.Alive())
            {
                if (other.Team == c.Team) continue;
                var p = Players.Get(other);
                if (p != null && HasStartedFreedayLr(p)) continue;
                var oo = other.Origin();
                if (oo == null) continue;
                var d = VectorUtil.Distance(o, oo);
                if (d < min) min = d;
            }
        }

        if (min < BeaconDistanceMin) min = BeaconDistanceMin;
        return (min - BeaconDistanceMin) / (BeaconDistanceMax - BeaconDistanceMin);
    }

    private void PlayBeaconSound(CCSPlayerController c, Vector origin, float pitch)
    {
        if (!string.IsNullOrWhiteSpace(Cfg.BeaconSoundEvent))
        {
            try
            {
                var pawn = c.PawnOrNull();
                // CSS EmitSound pitch: 0 = default; a multiplier scale is assumed (unverified in-game).
                pawn?.EmitSound(Cfg.BeaconSoundEvent, null, 0.7f, pitch / 100f);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Beacon sound event failed");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(Cfg.BeaconSound)) return;
        foreach (var h in Players.Humans())
        {
            var ho = h.Origin();
            if (ho != null && VectorUtil.Distance(ho, origin) <= Cfg.BeaconAudibleRadius) Sound.Play(h, Cfg.BeaconSound);
        }
    }
}

/// <summary>Phrase helper (expands colour tags before formatting; see Warden's WText for the reason).</summary>
internal static class LgText
{
    public static string T(string key, params object?[] args)
    {
        var raw = Lang.T(key);
        if (args.Length == 0) return raw;
        var coloured = Colors.Apply(raw);
        try
        {
            return string.Format(coloured, args);
        }
        catch (FormatException)
        {
            return coloured;
        }
    }
}

/// <summary>Expanding beacon rings (beam polygons re-interpolated every 0.05 s and removed after their life).</summary>
internal sealed class LgPulses
{
    private sealed class Pulse
    {
        public required List<CBeam> Beams;
        public required Vector Center;
        public float R0;
        public float R1;
        public float Start;
        public float Life;
    }

    private readonly List<Pulse> _pulses = new();
    private float _nextStep;

    public void Spawn(Vector center, float r0, float r1, float life, Color color, float width, int segments, float amplitude)
    {
        if (life <= 0) return;
        var beams = new List<CBeam>(segments);
        var pts = Points(center, r0, segments);
        for (var i = 0; i < segments; i++)
        {
            var b = CreateBeam(pts[i], pts[(i + 1) % segments], color, width, amplitude);
            if (b != null) beams.Add(b);
        }

        if (beams.Count == 0) return;
        _pulses.Add(new Pulse { Beams = beams, Center = center, R0 = r0, R1 = r1, Start = Server.CurrentTime, Life = life });
    }

    public void Tick()
    {
        if (_pulses.Count == 0) return;
        var now = Server.CurrentTime;
        if (now < _nextStep) return;
        _nextStep = now + 0.05f;
        for (var i = _pulses.Count - 1; i >= 0; i--)
        {
            var p = _pulses[i];
            var t = (now - p.Start) / p.Life;
            if (t >= 1f || p.Beams.Any(b => !b.IsValid))
            {
                foreach (var b in p.Beams) Fx.SafeRemove(b);
                _pulses.RemoveAt(i);
                continue;
            }

            var pts = Points(p.Center, p.R0 + (p.R1 - p.R0) * t, p.Beams.Count);
            for (var s = 0; s < p.Beams.Count; s++) Fx.MoveBeam(p.Beams[s], pts[s], pts[(s + 1) % p.Beams.Count]);
        }
    }

    public void Clear()
    {
        foreach (var p in _pulses)
            foreach (var b in p.Beams)
                Fx.SafeRemove(b);
        _pulses.Clear();
    }

    /// <summary>Map change: the entities are already gone, just drop the references.</summary>
    public void Forget() => _pulses.Clear();

    private static Vector[] Points(Vector c, float r, int n)
    {
        var pts = new Vector[n];
        for (var i = 0; i < n; i++)
        {
            var a = 2 * MathF.PI * i / n;
            pts[i] = new Vector(c.X + r * MathF.Cos(a), c.Y + r * MathF.Sin(a), c.Z);
        }

        return pts;
    }

    private static CBeam? CreateBeam(Vector start, Vector end, Color color, float width, float amplitude)
    {
        try
        {
            var beam = Utilities.CreateEntityByName<CBeam>("beam");
            if (beam == null) return null;
            beam.RenderMode = color.A < 255 ? RenderMode_t.kRenderTransAlpha : RenderMode_t.kRenderNormal;
            beam.Render = color;
            beam.Width = width;
            beam.EndWidth = width;
            if (amplitude > 0) beam.Amplitude = amplitude;
            beam.Teleport(start, new QAngle(0, 0, 0), new Vector(0, 0, 0));
            beam.EndPos.X = end.X;
            beam.EndPos.Y = end.Y;
            beam.EndPos.Z = end.Z;
            beam.DispatchSpawn();
            return beam;
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogWarning(ex, "Last guard: failed to create beacon beam");
            return null;
        }
    }
}
