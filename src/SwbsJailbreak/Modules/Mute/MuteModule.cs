using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Modules.Mute;

/// <summary>Why a player is server-muted. Each module owns its own bit so nobody can lift another module's mute.</summary>
[Flags]
public enum MuteReason
{
    None = 0,

    /// <summary>Admin sm_mute / sm_silence (admin comm module).</summary>
    Admin = 1,

    /// <summary>Timed punishment mute (admin comm module).</summary>
    Timed = 2,

    /// <summary>UltJB_VoiceChat rules: prisoners at round start, dead players, map end, connecting.</summary>
    VoiceChat = 4,

    /// <summary>UltJB_MuteManager: the warden is talking.</summary>
    WardenMute = 8
}

public sealed class MuteConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>ultjb_voice_mute_prisoner_time (min 0): seconds prisoners are muted at round start.</summary>
    public int MutePrisonerTime { get; set; } = 60;

    /// <summary>NEW (default off = original): players with sm_say/@css/chat never get the voice-chat (dead/T) mute.</summary>
    public bool AdminImmunity { get; set; } = false;

    /// <summary>Played to a muted player who tries to talk (original buttons/button11.wav). Client "play" path or a sound event.</summary>
    public string MutedSound { get; set; } = "sounds/buttons/button11.vsnd";

    /// <summary>Seconds between "your microphone is muted" notices (original 4).</summary>
    public float MutedNoticeInterval { get; set; } = 4f;

    /// <summary>Everyone hears alive guards and guards always hear each other (original SquelchManager part of MuteManager).</summary>
    public bool GuardsAlwaysAudible { get; set; } = true;

    /// <summary>Warden "mute power" (UltJB_MuteManager).</summary>
    public bool WardenMuteEnabled { get; set; } = true;

    /// <summary>Default flag for the sm_ultjb_ignore_wardenmute override (original ADMFLAG_BAN).</summary>
    public string WardenMuteImmunityFlag { get; set; } = "@css/ban";
}

/// <summary>
/// Port of UltJB_MuteManager.sp (v1.21, warden mute power + guard audibility) and UltJB_VoiceChat.sp (v1.18, prisoner
/// round-start mute, dead mute, map-end mute, muted notices) on CS2 voice flags (CCSPlayerController.VoiceFlags) and
/// listen overrides (SetListenOverride). Voice activity comes from CSS's Listeners.OnClientVoice.
/// </summary>
public sealed class MuteModule : JbModule, IMuteService
{
    // ---- original constants (UltJB_MuteManager.sp) ----
    private const float VoiceHoldTime = 0.15f;
    private const float VoiceMaxSpeakTime = 15.0f;
    private const float VoiceMinSpeakTime = 9.0f;
    private const int VoiceMinPlayers = 10;

    // SquelchManager VOICE_END_DELAY
    private const float VoiceEndDelay = 0.3f;

    private readonly MuteReason[] _reasons = new MuteReason[Players.MaxSlots];
    private readonly bool[] _appliedMute = new bool[Players.MaxSlots];
    private readonly bool[] _speaking = new bool[Players.MaxSlots];
    private readonly float[] _lastVoice = new float[Players.MaxSlots];
    private readonly double[] _nextNotice = new double[Players.MaxSlots];

    // ---- voice chat rules ----
    private bool _roundStarted;
    private bool _mapEnded;
    private Timer? _muteTimer;

    // ---- warden mute power ----
    private float _voiceHoldUntil;
    private float _totalSpeakTime;
    private float _stoppedSpeakingTime;
    private float _lastCheckTime;
    private bool _wardenSpeaking;
    private bool _playersMuted;
    private bool _wardenMuteActive = true;
    private Timer? _hudTimer;
    private int _hudSlot = -1;

    private MuteConfig Cfg => Section<MuteConfig>("Mute");

    public override int Order => 30;
    public override bool Enabled => Cfg.Enabled;

    public override void OnLoad(bool hotReload)
    {
        Jb.Mute = this;
        Plugin.RegisterListener<Listeners.OnClientVoice>(OnClientVoice);
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        if (hotReload) _stoppedSpeakingTime = Server.CurrentTime; // Cold start: no map yet, and Server.* reads the engine globals natively (crashes the server); OnMapStart covers it.
        if (hotReload)
        {
            // Round state is unknown after a hot reload: treat the round as started if there are guards.
            _roundStarted = Players.Count(CsTeam.CounterTerrorist) > 0;
        }
    }

    public override void OnAllModulesLoaded(bool hotReload)
    {
        JbEvents.WardenRemoved += OnWardenRemoved;
        JbEvents.WardenDeath += OnWardenDeath;
        JbEvents.DayStarted += OnDayStarted;
        // Hot reload: the core re-dispatches OnPlayerConnected (which mutes like OnClientPutInServer); re-evaluate
        // everyone one frame later with the normal rules.
        if (hotReload) Server.NextFrame(RefreshAll);
    }

    public override void OnUnload(bool hotReload)
    {
        KillMuteTimer();
        StopHud();
        for (var slot = 0; slot < Players.MaxSlots; slot++)
        {
            _reasons[slot] = MuteReason.None;
            Apply(Players.Get(slot));
        }

        if (Cfg.GuardsAlwaysAudible)
        {
            var humans = Players.Humans().ToList();
            foreach (var listener in humans)
                foreach (var sender in Players.All())
                    if (listener.Slot != sender.Slot)
                        TrySetListen(listener, sender, ListenOverride.Default);
        }
    }

    // ================================================================== IMuteService + reason model

    public bool IsMuted(JbPlayer player)
    {
        if (player.Slot is < 0 or >= Players.MaxSlots) return false;
        if (_reasons[player.Slot] != MuteReason.None) return true;
        var c = player.Controller;
        // A mute set by something else (another plugin / admin tool writing VoiceFlags) also counts.
        return c != null && (c.VoiceFlags & VoiceFlags.Muted) != 0;
    }

    public void Refresh(JbPlayer player)
    {
        CheckToMutePlayer(player);
        Apply(player);
    }

    public void RefreshAll()
    {
        foreach (var p in Players.Connected()) Refresh(p);
    }

    /// <summary>Set or clear one mute reason (for the admin comm / timed punishment modules). Not yet on IMuteService.</summary>
    public void SetReason(JbPlayer player, MuteReason reason, bool on)
    {
        if (player.Slot is < 0 or >= Players.MaxSlots) return;
        var before = _reasons[player.Slot];
        _reasons[player.Slot] = on ? before | reason : before & ~reason;
        Apply(player);
    }

    public bool HasReason(JbPlayer player, MuteReason reason) =>
        player.Slot is >= 0 and < Players.MaxSlots && (_reasons[player.Slot] & reason) != 0;

    public MuteReason GetReasons(JbPlayer player) =>
        player.Slot is >= 0 and < Players.MaxSlots ? _reasons[player.Slot] : MuteReason.None;

    /// <summary>Write the Muted voice flag. Only clears the flag if this module set it, so foreign mutes survive.</summary>
    private void Apply(JbPlayer player)
    {
        var slot = player.Slot;
        if (slot is < 0 or >= Players.MaxSlots) return;
        var c = player.Controller;
        if (c == null || c.IsBot) return;
        try
        {
            var flags = c.VoiceFlags;
            if (_reasons[slot] != MuteReason.None)
            {
                if ((flags & VoiceFlags.Muted) == 0) c.VoiceFlags = flags | VoiceFlags.Muted;
                _appliedMute[slot] = true;
            }
            else if (_appliedMute[slot])
            {
                if ((flags & VoiceFlags.Muted) != 0) c.VoiceFlags = flags & ~VoiceFlags.Muted;
                _appliedMute[slot] = false;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[Mute] VoiceFlags write failed");
        }
    }

    private void MutePlayer(JbPlayer p)
    {
        if (Cfg.AdminImmunity && OverrideAccess.Check(p.Controller, "sm_say", "@css/chat")) return;
        SetReason(p, MuteReason.VoiceChat, true);
    }

    private void UnmutePlayer(JbPlayer p) => SetReason(p, MuteReason.VoiceChat, false);

    private static string T(string key, params object?[] args)
    {
        var text = Lang.T(key);
        for (var i = 0; i < args.Length; i++) text = text.Replace("{" + i + "}", args[i]?.ToString() ?? "");
        return text;
    }

    // ================================================================== voice activity

    private void OnClientVoice(int slot)
    {
        if (slot is < 0 or >= Players.MaxSlots) return;
        var now = Server.CurrentTime;
        var p = Players.Get(slot);
        if (!p.Connected) return;

        if (!_speaking[slot] || now > _lastVoice[slot] + VoiceEndDelay)
        {
            _speaking[slot] = true;
            _lastVoice[slot] = now;
            OnStartedSpeaking(p);
        }

        _lastVoice[slot] = now;
        OnSpeaking(p, now);
    }

    private void OnTick()
    {
        var now = Server.CurrentTime;
        for (var slot = 0; slot < Players.MaxSlots; slot++)
            if (_speaking[slot] && now > _lastVoice[slot] + VoiceEndDelay)
                _speaking[slot] = false;

        if (!_wardenSpeaking) return;

        // OnGameFrame of the original.
        if (_totalSpeakTime >= MaxSpeakTime() && _playersMuted)
        {
            _playersMuted = false;
            UnmuteAllWardenMute();
        }

        if (now >= _voiceHoldUntil) WardenStopSpeaking(now);
    }

    /// <summary>SquelchManager_OnClientStartSpeaking in UltJB_VoiceChat: tell muted players why they are muted.</summary>
    private void OnStartedSpeaking(JbPlayer p)
    {
        var c = p.Controller;
        if (c == null || c.IsBot || !IsMuted(p)) return;
        var now = Server.EngineTime;
        if (_nextNotice[p.Slot] > now) return;
        _nextNotice[p.Slot] = now + Math.Max(0.5f, Cfg.MutedNoticeInterval);

        Chat.Print(c, Lang.T(_muteTimer != null ? "mute.notice_prisoner_time" : "mute.notice_generic"));
        if (!string.IsNullOrWhiteSpace(Cfg.MutedSound)) Sound.Play(c, Cfg.MutedSound);
    }

    // ================================================================== warden mute power (UltJB_MuteManager)

    private void OnSpeaking(JbPlayer p, float now)
    {
        if (!Cfg.WardenMuteEnabled || !_wardenMuteActive) return;
        var c = p.Controller;
        if (c == null || !Jb.Warden.IsWarden(c) || !p.IsAlive) return;

        _voiceHoldUntil = now + VoiceHoldTime;
        if (_wardenSpeaking)
        {
            AddToSpeakTime(now);
            return;
        }

        SubtractFromSpeakTime(now);
        WardenStartSpeaking(p, now);
    }

    private void AddToSpeakTime(float now)
    {
        var dt = now - _lastCheckTime;
        _totalSpeakTime += _playersMuted ? dt : dt * 0.2f;
        _lastCheckTime = now;
    }

    private void SubtractFromSpeakTime(float now)
    {
        _totalSpeakTime -= now - _stoppedSpeakingTime;
        _stoppedSpeakingTime = now;
        if (_totalSpeakTime < 0) _totalSpeakTime = 0;
    }

    private void WardenStartSpeaking(JbPlayer warden, float now)
    {
        _lastCheckTime = now;
        foreach (var c in Players.Alive())
        {
            if (c.Slot == warden.Slot) continue;
            if (OverrideAccess.Check(c, "sm_ultjb_ignore_wardenmute", Cfg.WardenMuteImmunityFlag)) continue;
            var p = Players.Get(c);
            if (p != null) SetReason(p, MuteReason.WardenMute, true);
        }

        _wardenSpeaking = true;
        _playersMuted = true;
        StartHud();
    }

    private void WardenStopSpeaking(float now)
    {
        _stoppedSpeakingTime = now;
        _wardenSpeaking = false;
        UnmuteAllWardenMute();
    }

    private void UnmuteAllWardenMute()
    {
        for (var slot = 0; slot < Players.MaxSlots; slot++)
        {
            if ((_reasons[slot] & MuteReason.WardenMute) == 0) continue;
            SetReason(Players.Get(slot), MuteReason.WardenMute, false);
        }
    }

    /// <summary>9 s with 10 or fewer players, scaling linearly to 15 s on a full server.</summary>
    private static float MaxSpeakTime()
    {
        var n = Math.Max(VoiceMinPlayers, Players.All().Count(c => c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist));
        var maxPlayers = Server.MaxPlayers;
        if (maxPlayers <= VoiceMinPlayers) maxPlayers = VoiceMinPlayers + 1;
        var percent = (float)(n - VoiceMinPlayers) / (maxPlayers - VoiceMinPlayers);
        return (VoiceMaxSpeakTime - VoiceMinSpeakTime) * percent + VoiceMinSpeakTime;
    }

    private void StartHud()
    {
        if (_hudTimer != null) return;
        _hudTimer = Plugin.AddTimer(0.1f, HudTick, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StopHud()
    {
        try { _hudTimer?.Kill(); }
        catch { /* already dead */ }
        _hudTimer = null;
        if (_hudSlot >= 0) Hud.Clear(_hudSlot, "mute.power");
        _hudSlot = -1;
    }

    private void HudTick()
    {
        var warden = Jb.Warden.Warden;
        var c = warden?.Controller;
        if (warden == null || c == null)
        {
            StopHud();
            return;
        }

        if (_hudSlot >= 0 && _hudSlot != c.Slot) Hud.Clear(_hudSlot, "mute.power");
        _hudSlot = c.Slot;

        if (!_wardenSpeaking) SubtractFromSpeakTime(Server.CurrentTime);
        var percent = 100 - (int)MathF.Round(_totalSpeakTime / MaxSpeakTime() * 100f);
        if (percent < 0) percent = 0;
        var color = percent < 1 ? "#DE2626" : "#26D2DE";
        Hud.Set(c, "mute.power", T("mute.hud", color, percent), 60, 0.35f);

        if (percent >= 100) StopHud();
    }

    private void OnWardenRemoved(JbPlayer warden)
    {
        _totalSpeakTime = 0;
        if (_wardenSpeaking) WardenStopSpeaking(Server.CurrentTime);
        else UnmuteAllWardenMute();
        _wardenMuteActive = true;
    }

    private void OnWardenDeath(JbPlayer warden) => _wardenMuteActive = true;

    [ConsoleCommand("css_wmute", "Toggle Warden mute powers.")]
    [ConsoleCommand("sm_wmute", "Toggle Warden mute powers.")]
    public void OnWardenMuteCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        if (!Jb.Warden.IsWarden(player))
        {
            Chat.Print(player, Lang.T("mute.must_be_warden"));
            return;
        }

        _wardenMuteActive = !_wardenMuteActive;
        Chat.Print(player, Lang.T(_wardenMuteActive ? "mute.wmute_active" : "mute.wmute_inactive"));
    }

    // ================================================================== voice chat rules (UltJB_VoiceChat)

    public override void OnMapStart(string mapName)
    {
        _roundStarted = false;
        _mapEnded = false;
        _muteTimer = null; // STOP_ON_MAPCHANGE already killed it
        _hudTimer = null;
        if (_hudSlot >= 0) Hud.Clear(_hudSlot, "mute.power");
        _hudSlot = -1;
        _wardenMuteActive = true;
        _wardenSpeaking = false;
        _playersMuted = false;
        _totalSpeakTime = 0;
        _stoppedSpeakingTime = Server.CurrentTime;

        // Players carried over a map change behave like freshly connected players (OnClientPutInServer mute);
        // the map-end mute and warden mute are lifted.
        foreach (var p in Players.Connected())
        {
            SetReason(p, MuteReason.WardenMute, false);
            MutePlayer(p);
        }
    }

    public override void OnRoundStart()
    {
        if (Players.Count(CsTeam.CounterTerrorist) == 0) return; // round counts as not started: nobody is muted

        _roundStarted = true;
        foreach (var c in Players.Prisoners())
            if (Players.Get(c) is { } p)
                MutePlayer(p);

        KillMuteTimer();
        var seconds = Math.Max(0, Cfg.MutePrisonerTime);
        if (seconds > 0) _muteTimer = Plugin.AddTimer(seconds, OnUnmuteTimer, TimerFlags.STOP_ON_MAPCHANGE);
        Chat.PrintAll(T("mute.prisoners_muted", seconds));
    }

    private void OnUnmuteTimer()
    {
        _muteTimer = null;
        foreach (var c in Players.Alive())
            if (Players.Get(c) is { } p)
                UnmutePlayer(p);
        Chat.PrintAll(Lang.T("mute.prisoners_may_speak"));
    }

    private void OnDayStarted(JbPlayer? starter, DayType type, bool freeForAll)
    {
        KillMuteTimer();
        foreach (var c in Players.Alive())
            if (Players.Get(c) is { } p)
                UnmutePlayer(p);
    }

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        KillMuteTimer();
        _roundStarted = false;
        foreach (var p in Players.Connected()) UnmutePlayer(p);
    }

    [GameEventHandler]
    public HookResult OnCsIntermission(EventCsIntermission ev, GameEventInfo info)
    {
        OnMapEnded();
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnCsWinPanelMatch(EventCsWinPanelMatch ev, GameEventInfo info)
    {
        OnMapEnded();
        return HookResult.Continue;
    }

    private void OnMapEnded()
    {
        if (_mapEnded) return;
        _mapEnded = true;
        KillMuteTimer();
        foreach (var p in Players.Connected())
        {
            if (OverrideAccess.Check(p.Controller, "sm_say", "@css/chat")) continue;
            SetReason(p, MuteReason.VoiceChat, true); // admin immunity setting does not apply: original exempted only these admins
        }
    }

    public override void OnPlayerConnected(JbPlayer player)
    {
        var slot = player.Slot;
        _reasons[slot] = MuteReason.None;
        _appliedMute[slot] = false;
        _speaking[slot] = false;
        _nextNotice[slot] = 0;
        MutePlayer(player);
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        var slot = player.Slot;
        _reasons[slot] = MuteReason.None;
        _appliedMute[slot] = false;
        _speaking[slot] = false;
    }

    public override void OnPlayerSpawn(JbPlayer player)
    {
        CheckToMutePlayer(player);
        Apply(player); // CS2 can reset voice flags on reconnect / respawn

        if (Cfg.GuardsAlwaysAudible && player.IsGuard)
        {
            var guard = player.Controller;
            if (guard == null) return;
            foreach (var listener in Players.Humans())
                if (listener.Slot != guard.Slot)
                    TrySetListen(listener, guard, ListenOverride.Hear);
        }
    }

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        if (_roundStarted) MutePlayer(victim);
        SetReason(victim, MuteReason.WardenMute, false);

        if (!Cfg.GuardsAlwaysAudible || !victim.IsGuard) return;
        var guard = victim.Controller;
        if (guard == null) return;
        foreach (var listener in Players.Humans())
            if (listener.Slot != guard.Slot)
                TrySetListen(listener, guard, ListenOverride.Default);
    }

    public override void OnPlayerTeam(JbPlayer player, CsTeam oldTeam, CsTeam newTeam, bool disconnect)
    {
        if (disconnect) return;
        var slot = player.Slot;
        // Let the team change settle (controller team / life state) before applying the original checks.
        Server.NextFrame(() =>
        {
            var p = Players.Get(slot);
            if (!p.Connected) return;
            CheckToMutePlayer(p, newTeam);

            if (!Cfg.GuardsAlwaysAudible || oldTeam == newTeam) return;
            var mover = p.Controller;
            if (mover == null) return;
            foreach (var other in Players.All())
            {
                if (other.Slot == mover.Slot) continue;
                if (!other.IsBot)
                    TrySetListen(other, mover, newTeam == CsTeam.CounterTerrorist ? ListenOverride.Hear : ListenOverride.Default);
                if (!mover.IsBot)
                    TrySetListen(mover, other, other.Team == CsTeam.CounterTerrorist ? ListenOverride.Hear : ListenOverride.Default);
            }
        });
    }

    /// <summary>Original CheckToMutePlayer (SpawnPost and player_team).</summary>
    private void CheckToMutePlayer(JbPlayer p, CsTeam? teamOverride = null)
    {
        var c = p.Controller;
        if (c == null) return;
        var team = teamOverride ?? c.Team;

        if (_mapEnded)
        {
            if (!OverrideAccess.Check(c, "sm_say", "@css/chat")) SetReason(p, MuteReason.VoiceChat, true);
            return;
        }

        if (Players.Count(CsTeam.CounterTerrorist) == 0 || !_roundStarted)
        {
            UnmutePlayer(p);
            return;
        }

        var observer = team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist);
        if (observer || !c.IsAliveP())
        {
            MutePlayer(p);
            return;
        }

        if (_muteTimer != null && team == CsTeam.Terrorist)
        {
            MutePlayer(p);
            return;
        }

        UnmutePlayer(p);
    }

    private void KillMuteTimer()
    {
        try { _muteTimer?.Kill(); }
        catch { /* already dead */ }
        _muteTimer = null;
    }

    private void TrySetListen(CCSPlayerController listener, CCSPlayerController sender, ListenOverride value)
    {
        try
        {
            if (!listener.IsValid || !sender.IsValid || listener.IsBot) return;
            listener.SetListenOverride(sender, value);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[Mute] SetListenOverride failed");
        }
    }
}

/// <summary>SourceMod CheckCommandAccess(client, "override", FLAG) on top of CSS admin_overrides.json.</summary>
internal static class OverrideAccess
{
    public static bool Check(CCSPlayerController? player, string overrideName, string defaultFlag)
    {
        if (player == null || !player.IsValid || player.IsBot) return false;
        try
        {
            if (AdminManager.CommandIsOverriden(overrideName))
            {
                var perms = AdminManager.GetPermissionOverrides(overrideName);
                if (perms.Length == 0) return true;
                return perms.Any(f => f.StartsWith('#') ? AdminManager.PlayerInGroup(player, f) : AdminManager.PlayerHasPermissions(player, f));
            }

            return AdminManager.PlayerHasPermissions(player, defaultFlag) || AdminManager.PlayerHasPermissions(player, "@css/root");
        }
        catch
        {
            return false;
        }
    }
}
