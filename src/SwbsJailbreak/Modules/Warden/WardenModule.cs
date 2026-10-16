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
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Warden;

/// <summary>Per-connection warden state (NOT round scoped; cleared on connect and map start).</summary>
internal sealed class WardenPlayerData
{
    /// <summary>UltJB_Warden_GetClientWardenCount: times this player has been warden this connection/map.</summary>
    public int WardenCount;

    public string? SavedModel;
    public bool HasGuardClanTag;
    public bool HasOriginalClanTag;
    public string OriginalClanTag = "";
    public string AppliedClanTag = "";
    public bool ColorApplied;

    // Warden bonus (few guards): what is currently granted, so it can be changed or taken back exactly.
    public int BonusSteps, BonusHp, BonusArmor, BonusPrevMaxHp;
}

/// <summary>
/// Port of UltJB_Warden.sp (v1.42, hlstriker). Wardens are chosen automatically by a countdown from two queues:
/// the primary queue (players who typed !w, only used for the first warden of a round) and the round-robin secondary
/// queue (everybody who joined a team). There is deliberately no claim/pass command (the original had none).
/// </summary>
public sealed partial class WardenModule : JbModule, IWardenService
{
    private WardenConfig Cfg => Section<WardenConfig>("Warden");

    public override int Order => 20;
    public override bool Enabled => Cfg.Enabled;

    private JbPlayer? _warden;
    private readonly List<JbPlayer> _queuePrimary = new();
    private readonly List<JbPlayer> _queueSecondary = new();

    private Timer? _selectTimer;
    private int _countdown;
    private int _countdownMax;
    private int _roundWardenCount;
    private bool _roundStarted;
    private JbPlayer? _lastForceRemoved;
    private double _nextKiss;

    private const string HintKey = "warden.hint";
    private const string HudKey = "warden.info";

    // ================================================================= IWardenService

    public JbPlayer? Warden => _warden is { Connected: true } w ? w : null;

    public bool HasWarden => Warden != null;

    public bool IsWarden(CCSPlayerController? player) =>
        player != null && player.IsValid && Warden is { } w && w.Slot == player.Slot;

    private bool IsWardenP(JbPlayer? p) => p != null && Warden is { } w && ReferenceEquals(w, p);

    public int GetWardenCount(JbPlayer player) => player.Data<WardenPlayerData>().WardenCount;

    public bool TrySetWarden(JbPlayer player, CCSPlayerController? admin = null)
    {
        if (!player.IsAlive || !player.IsGuard) return false;
        if (admin != null) return SetWardenByAdmin(player, admin, null);

        var w = Warden;
        if (w != null)
        {
            if (ReferenceEquals(w, player)) return false;
            if (!TryRemoveClientFromWarden(w)) return false;
        }

        HintNeeded(WText.T("warden.selected.hint", WText.HtmlName(player.Name)), 5);
        Chat.PrintAll(WText.T("warden.selected.chat", player.Name));
        SetWarden(player);
        StopWardenTimer();
        return true;
    }

    public void RemoveWarden(bool announce = true)
    {
        var w = Warden;
        if (w == null) return;
        var name = w.Name;
        if (TryRemoveClientFromWarden(w) && announce)
            Chat.PrintAll(WText.T("warden.rw.done", name));
    }

    public void CreateRing(JbPlayer owner, int ringIndex, Vector position, float radius) =>
        Plugin.GetModule<WardenMenuModule>()?.SetRingPosition(owner, ringIndex, position, true, radius);

    // ================================================================= lifecycle

    public override void OnLoad(bool hotReload)
    {
        Jb.Warden = this;
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        Plugin.RegisterListener<Listeners.OnPlayerButtonsChanged>(OnButtonsChanged);
        Aim.Pinged += OnPing;
        Plugin.RegisterListener<Listeners.OnServerPrecacheResources>(OnPrecache);
    }

    public override void OnAllModulesLoaded(bool hotReload)
    {
        JbEvents.LastGuardActivatingPre += OnLastGuardActivatingPre;
        JbEvents.LastRequestInitialized += OnLastRequestInitialized;
        JbEvents.DayStarted += OnDayStarted;
        JbEvents.SettingsSpawnPost += OnSpawnPost;

        if (!hotReload) return;
        // Late load: rebuild the round-robin queue from the current teams (the original started empty).
        foreach (var c in Players.All().OrderBy(c => c.Slot))
        {
            var p = Players.Get(c);
            if (p == null) continue;
            if (c.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist) AddToQueue(p, _queueSecondary);
            if (c.Team == CsTeam.CounterTerrorist) GiveGuardClanTag(p);
        }

        if (Round.IsActive && CountAliveGuards() > 0)
        {
            _roundStarted = true;
            StartWardenTimer();
        }
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.LastGuardActivatingPre -= OnLastGuardActivatingPre;
        JbEvents.LastRequestInitialized -= OnLastRequestInitialized;
        JbEvents.DayStarted -= OnDayStarted;
        JbEvents.SettingsSpawnPost -= OnSpawnPost;
        Aim.Pinged -= OnPing;
        StopWardenTimer();
        CleanupVisuals(true);
        Hud.ClearPrefix("warden.");
        _warden = null;
    }

    public override void OnMapStart(string mapName)
    {
        _queuePrimary.Clear();
        _queueSecondary.Clear();
        _warden = null;
        _lastForceRemoved = null;
        _roundStarted = false;
        _savedCount = 0;
        foreach (var p in Players.Connected()) p.Data<WardenPlayerData>().WardenCount = 0;
    }

    public override void OnMapEnd()
    {
        RoundEndCleanup();
        StopWardenTimer();
    }

    public override void OnRoundStart()
    {
        ClearPaint();
    }

    public override void OnRoundEnd(CsTeam winner, int reason) => RoundEndCleanup();

    private void OnPrecache(ResourceManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(Cfg.WardenModel)) manifest.AddResource(Cfg.WardenModel);
    }

    /// <summary>RoundEndCleanup: remove the warden (no model restore, no reselection), stop timers and the line.</summary>
    private void RoundEndCleanup()
    {
        var w = Warden;
        if (w != null) RemoveWardenInternal(w);
        _warden = null;
        _lastForceRemoved = null;
        StopWardenTimer();
        CleanupVisuals(false);
        _roundStarted = false;
        Hud.ClearGlobal(HudKey);
    }

    // ================================================================= events

    public override void OnRoundFreezeEnd()
    {
        if (CountAliveGuards() < 1) return;
        _roundWardenCount = 0;
        _roundStarted = true;
        StartWardenTimer(true);
        foreach (var c in Players.Humans().Where(c => c.Team == CsTeam.CounterTerrorist))
            Chat.Print(c, WText.T("warden.priority"));
    }

    /// <summary>player_death (pre): the warden's death forward must fire before anything else handles the death.</summary>
    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerDeathPre(EventPlayerDeath ev, GameEventInfo info)
    {
        var victim = Players.Get(ev.Userid);
        if (victim == null) return HookResult.Continue;
        if (IsWardenP(victim))
        {
            JbEvents.RaiseWardenDeath(victim);
            Chat.PrintAll(WText.T("warden.died"));
            if (!string.IsNullOrWhiteSpace(Cfg.DeathSound)) Sound.PlayAll(Cfg.DeathSound);
        }

        // The corpse keeps its model (no restore on death).
        TryRemoveClientFromWarden(victim, true, false);
        return HookResult.Continue;
    }

    public override void OnPlayerTeam(JbPlayer player, CsTeam oldTeam, CsTeam newTeam, bool disconnect)
    {
        switch (newTeam)
        {
            case CsTeam.CounterTerrorist:
                AddToQueue(player, _queueSecondary);
                GiveGuardClanTag(player);
                break;
            case CsTeam.Terrorist:
                _queuePrimary.Remove(player);
                AddToQueue(player, _queueSecondary);
                RemoveGuardClanTag(player);
                TryRemoveClientFromWarden(player);
                break;
            default:
                _queuePrimary.Remove(player);
                RemoveGuardClanTag(player);
                TryRemoveClientFromWarden(player, true, !disconnect);
                break;
        }
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        TryRemoveClientFromWarden(player, true, false);
        _queuePrimary.Remove(player);
        _queueSecondary.Remove(player);
        if (ReferenceEquals(_lastForceRemoved, player)) _lastForceRemoved = null;
    }

    public override void OnPlayerSpawn(JbPlayer player) => OnSpawnPost(player);

    /// <summary>UltJB_Settings_OnSpawnPost (also driven by the framework spawn hook; idempotent).</summary>
    private void OnSpawnPost(JbPlayer player)
    {
        // CS2 clients can re-send their clan tag; re-apply the guard tag on every spawn.
        if (player.IsGuard) GiveGuardClanTag(player);

        if (!_roundStarted) return;
        if (CountAliveGuards() < 1) return;
        if (Warden != null) return;
        if (_selectTimer != null) return;
        StartWardenTimer();
    }

    /// <summary>UltJB_LastGuard_OnActivated_Pre. Port fix: no 3 s reselection timer is started here (see spec 13).</summary>
    private bool OnLastGuardActivatingPre(JbPlayer lastGuard)
    {
        StopWardenTimer();
        TryRemoveClientFromWarden(Warden, false, true);
        return true;
    }

    private void OnLastRequestInitialized(JbPlayer prisoner)
    {
        StopWardenTimer();
        TryRemoveClientFromWarden(Warden);
    }

    private void OnDayStarted(JbPlayer? starter, DayType type, bool freeForAll)
    {
        StopWardenTimer();
        TryRemoveClientFromWarden(Warden);
    }

    // ================================================================= selection

    private static int CountAliveGuards() => Players.CountAlive(CsTeam.CounterTerrorist);

    private void StartWardenTimer(bool initial = false)
    {
        StopWardenTimer();

        // Never between round_end and the next round (port guard: the original could select during freeze time).
        if (Round.IsOver) return;
        if (Jb.LastRequests.CanLastRequest) return;
        if (Jb.LastGuard.LastGuard != null) return;
        if (Jb.Days.IsInProgress) return;

        _countdown = 0;
        _countdownMax = Math.Max(1, initial ? Cfg.SelectTimeInitial : Cfg.SelectTime);
        ShowWardenCountdown();
        _selectTimer = Plugin.AddTimer(1.0f, SelectTick, TimerFlags.REPEAT);
        Chat.PrintAll(WText.T("warden.timer.chat", _countdownMax));
    }

    /// <summary>Kill the selection timer exactly once (it is owned here, not by Round.Timer).</summary>
    private void StopWardenTimer()
    {
        var t = _selectTimer;
        _selectTimer = null;
        if (t == null) return;
        try { t.Kill(); }
        catch (Exception ex) { Logger.LogDebug(ex, "Warden select timer kill"); }
    }

    private void ShowWardenCountdown() =>
        HintNeeded(WText.T("warden.timer.hint", _countdownMax - _countdown), 2);

    /// <summary>PrintHintTextToNeeded: everyone in game who has not started a last request.</summary>
    private static void HintNeeded(string html, float seconds)
    {
        foreach (var c in Players.Humans())
        {
            var p = Players.Get(c);
            if (p != null && Jb.LastRequests.HasStartedLastRequest(p)) continue;
            Hud.Set(c, HintKey, html, 100, seconds);
        }
    }

    private void SelectTick()
    {
        if (_selectTimer == null) return;
        if (Jb.LastGuard.LastGuard != null)
        {
            StopWardenTimer();
            return;
        }

        _countdown++;
        if (_countdown < _countdownMax)
        {
            ShowWardenCountdown();
            return;
        }

        StopWardenTimer();

        // The !w queue decides the first warden of a round (every selection when auto warden is off). If nobody queued,
        // auto warden picks a guard so the round is never left without one.
        JbPlayer? w = null;
        var automatic = false;
        if (_roundWardenCount == 0 || !Cfg.AutoWarden) w = GetPrimaryWarden();
        if (w == null && Cfg.AutoWarden)
        {
            w = string.Equals(Cfg.AutoWardenMode, "random", StringComparison.OrdinalIgnoreCase) ? GetRandomWarden() : GetSecondaryWarden();
            automatic = w != null;
        }

        _lastForceRemoved = null;

        if (w == null)
        {
            HintNeeded(WText.T("warden.notfound.hint"), 5);
            Chat.PrintAll(WText.T("warden.notfound.chat"));
            // Original TODO "automatically go into a freeday" was never implemented; intentionally not added.
            return;
        }

        HintNeeded(WText.T("warden.selected.hint", WText.HtmlName(w.Name)), 5);
        Chat.PrintAll(WText.T(automatic && _roundWardenCount == 0 ? "warden.selected.auto" : "warden.selected.chat", w.Name));
        SetWarden(w);
    }

    private bool Eligible(JbPlayer p) =>
        p.Connected && p.Controller != null && p.IsAlive && (Cfg.AllowBotWarden || !p.IsBot);

    /// <summary>First valid entry of the !w queue (removed from the queue when chosen).</summary>
    private JbPlayer? GetPrimaryWarden()
    {
        // Entries leave the queue when a player leaves guards; the guard check is a defensive addition for
        // CS2 SwitchTeam calls that might not raise player_team.
        var p = _queuePrimary.FirstOrDefault(q => Eligible(q) && q.IsGuard);
        if (p != null) _queuePrimary.Remove(p);
        return p;
    }

    /// <summary>Round-robin queue; the force-removed warden is only used when nobody else is valid.</summary>
    private JbPlayer? GetSecondaryWarden()
    {
        var forceRemoved = _lastForceRemoved;
        JbPlayer? fallback = null;
        JbPlayer? chosen = null;
        foreach (var p in _queueSecondary)
        {
            if (!Eligible(p) || !p.IsGuard) continue;
            if (ReferenceEquals(p, forceRemoved))
            {
                fallback = p;
                continue;
            }

            chosen = p;
            break;
        }

        chosen ??= fallback;
        if (chosen == null) return null;
        _queueSecondary.Remove(chosen);
        _queueSecondary.Add(chosen);
        return chosen;
    }

    /// <summary>Auto warden "random" mode: any eligible guard (the force-removed warden only when alone).</summary>
    private JbPlayer? GetRandomWarden()
    {
        var pool = _queueSecondary.Where(p => Eligible(p) && p.IsGuard).ToList();
        if (pool.Count > 1) pool.RemoveAll(p => ReferenceEquals(p, _lastForceRemoved));
        if (pool.Count == 0) return null;
        var chosen = pool[Random.Shared.Next(pool.Count)];
        _queueSecondary.Remove(chosen);
        _queueSecondary.Add(chosen);
        return chosen;
    }

    // ================================================================= bonus while guards are few

    private int _bonusTick;

    /// <summary>Steps for the current number of living guards: 0 above the limit, else limit + 1 - alive (stacks as guards die).</summary>
    internal static int BonusSteps(int aliveGuards, int maxGuards) =>
        aliveGuards >= 1 && aliveGuards <= maxGuards ? maxGuards + 1 - aliveGuards : 0;

    /// <summary>~2 Hz from OnTick: cheap, and it also covers guards leaving, switching team or dying.</summary>
    private void BonusTick()
    {
        if (++_bonusTick < 32) return;
        _bonusTick = 0;
        RefreshBonus();
    }

    private void RefreshBonus()
    {
        var w = _warden;
        var c = w?.Controller;
        var pawn = w?.Pawn;
        if (w == null || c == null || pawn == null || !w.IsAlive || pawn.Health <= 0) return;

        var d = w.Data<WardenPlayerData>();
        var steps = Cfg.BonusEnabled ? BonusSteps(CountAliveGuards(), Math.Max(1, Cfg.BonusMaxGuards)) : 0;
        if (steps == d.BonusSteps) return;

        var wantHp = steps * Math.Max(0, Cfg.BonusHpPerStep);
        var wantArmor = steps * Math.Max(0, Cfg.BonusArmorPerStep);
        if (d.BonusSteps == 0) d.BonusPrevMaxHp = pawn.MaxHealth;

        var dHp = wantHp - d.BonusHp;
        if (dHp > 0) c.AddHp(dHp);
        else if (dHp < 0) c.SetHp(Math.Max(1, Math.Min(pawn.Health, Math.Max(d.BonusPrevMaxHp, pawn.Health + dHp))), false);

        var dArmor = wantArmor - d.BonusArmor;
        if (dArmor != 0) c.SetArmor(Math.Max(0, pawn.ArmorValue + dArmor));

        if (Cfg.BonusSpeedPerStep > 0f && steps > 0) Movement.SetSpeed(c, "warden.bonus.speed", 1f + steps * Cfg.BonusSpeedPerStep);
        else Movement.ClearSpeed(c, "warden.bonus.speed");

        if (steps > d.BonusSteps) Chat.Tell(c, "warden.bonus", wantHp, wantArmor, CountAliveGuards());
        d.BonusSteps = steps;
        d.BonusHp = wantHp;
        d.BonusArmor = wantArmor;
        if (steps == 0 && d.BonusPrevMaxHp > 0) c.SetMaxHp(d.BonusPrevMaxHp);
    }

    /// <summary>Warden ends (unwarden, LR, day, round end): take the bonus back without ever killing or healing the player.</summary>
    private void RevokeBonus(JbPlayer p)
    {
        var d = p.Data<WardenPlayerData>();
        if (d.BonusSteps == 0) return;
        var c = p.Controller;
        var pawn = p.Pawn;
        if (c != null && pawn != null && p.IsAlive && pawn.Health > 0)
        {
            c.SetHp(Math.Max(1, Math.Min(pawn.Health, Math.Max(d.BonusPrevMaxHp, pawn.Health - d.BonusHp))), false);
            c.SetArmor(Math.Max(0, pawn.ArmorValue - d.BonusArmor));
            if (d.BonusPrevMaxHp > 0) c.SetMaxHp(d.BonusPrevMaxHp);
            Movement.ClearSpeed(c, "warden.bonus.speed");
        }

        d.BonusSteps = d.BonusHp = d.BonusArmor = d.BonusPrevMaxHp = 0;
    }

    private static void AddToQueue(JbPlayer p, List<JbPlayer> queue)
    {
        if (!queue.Contains(p)) queue.Add(p);
    }

    private void SetWarden(JbPlayer p)
    {
        var d = p.Data<WardenPlayerData>();
        d.SavedModel = ReadModel(p.Pawn);
        _warden = p;

        var pawn = p.Pawn;
        if (pawn != null && !string.IsNullOrWhiteSpace(Cfg.WardenModel))
        {
            try { Fx.SetModel(pawn, Cfg.WardenModel); }
            catch (Exception ex) { Logger.LogWarning(ex, "Warden model {Model} could not be applied", Cfg.WardenModel); }
        }

        d.WardenCount++;
        _roundWardenCount++;
        d.BonusSteps = d.BonusHp = d.BonusArmor = d.BonusPrevMaxHp = 0; // new life: nothing granted yet
        ResetPointerState();

        JbEvents.RaiseWardenSelected(p);
        GiveGuardClanTag(p);
        ApplyMarker(p);
        Jb.Mute.Refresh(p);
        if (Cfg.ShowPointerTip) Chat.Print(p.Controller, WText.T("warden.pointer.tip"));
        RefreshBonus();
    }

    /// <summary>TryRemoveClientFromWarden: remove + (optionally) the 3 s reselection + model restore while alive.</summary>
    private bool TryRemoveClientFromWarden(JbPlayer? p, bool startTimer = true, bool restoreModel = true)
    {
        if (p == null || !IsWardenP(p)) return false;
        RemoveWardenInternal(p);
        if (startTimer) StartWardenTimer();

        if (restoreModel && p.IsAlive)
        {
            var saved = p.Data<WardenPlayerData>().SavedModel;
            var pawn = p.Pawn;
            if (pawn != null && !string.IsNullOrEmpty(saved) && !string.IsNullOrWhiteSpace(Cfg.WardenModel))
            {
                try { Fx.SetModel(pawn, saved); }
                catch (Exception ex) { Logger.LogDebug(ex, "Warden model restore failed"); }
            }
        }

        return true;
    }

    /// <summary>RemoveWarden: clear, back to the plain guard tag, stop the gestures, fire OnRemoved.</summary>
    private void RemoveWardenInternal(JbPlayer p)
    {
        RevokeBonus(p);
        _warden = null;
        GiveGuardClanTag(p);
        StopPointer();
        ClearMarker(p);
        JbEvents.RaiseWardenRemoved(p);
        Jb.Mute.Refresh(p);
    }

    private static string? ReadModel(CCSPlayerPawn? pawn)
    {
        if (pawn == null) return null;
        try
        {
            return pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- indicators (model is the original one)

    private void ApplyMarker(JbPlayer p)
    {
        var color = WDraw.ParseColor(Cfg.WardenColor);
        var c = p.Controller;
        if (color != null && c != null && p.IsAlive)
        {
            c.SetColor(color.Value);
            p.Data<WardenPlayerData>().ColorApplied = true;
        }

        if (Cfg.ShowWardenHud)
            Hud.SetGlobal(HudKey, WText.T("warden.hud", WText.HtmlName(p.Name)), 10);
    }

    private void ClearMarker(JbPlayer p)
    {
        var d = p.Data<WardenPlayerData>();
        if (d.ColorApplied)
        {
            d.ColorApplied = false;
            if (p.IsAlive) p.Controller?.ResetColor();
        }

        Hud.ClearGlobal(HudKey);
    }

    // ---------------------------------------------------------------- guard clan tag (2.1.13)

    private void GiveGuardClanTag(JbPlayer p)
    {
        if (!Cfg.GuardClanTags) return;
        var c = p.Controller;
        if (c == null) return;
        var d = p.Data<WardenPlayerData>();
        var current = c.Clan ?? "";
        if (!d.HasGuardClanTag)
        {
            d.OriginalClanTag = current;
            d.HasOriginalClanTag = current.Length > 0;
        }
        else if (current != d.AppliedClanTag)
        {
            // The client changed its tag while holding the guard tag: that becomes the tag restored later.
            d.OriginalClanTag = current;
            d.HasOriginalClanTag = current.Length > 0;
        }

        // Guards show no tag (original), or their crew tag when Crews.GuardTags is on; the warden shows WardenClanTag.
        var tag = IsWardenP(p) ? Cfg.WardenClanTag : Jb.Crews.ScoreboardTag(p) ?? "";
        c.SetClanTag(tag);
        d.AppliedClanTag = tag;
        d.HasGuardClanTag = true;
    }

    /// <summary>Re-apply a guard's scoreboard tag (e.g. after they joined or left a crew).</summary>
    internal void RefreshGuardClanTag(JbPlayer p)
    {
        if (p.IsGuard) GiveGuardClanTag(p);
    }

    private void RemoveGuardClanTag(JbPlayer p)
    {
        if (!Cfg.GuardClanTags) return;
        var d = p.Data<WardenPlayerData>();
        if (!d.HasGuardClanTag) return;
        d.HasGuardClanTag = false;
        var c = p.Controller;
        if (c == null) return;
        c.SetClanTag(d.HasOriginalClanTag ? d.OriginalClanTag : "");
    }

    // ================================================================= commands

    [ConsoleCommand("css_w", "Adds you to the warden queue.")]
    [ConsoleCommand("css_warden", "Adds you to the warden queue.")]
    [CommandHelper(0, "", CommandUsage.CLIENT_ONLY)]
    public void OnWardenQueue(CCSPlayerController? player, CommandInfo info)
    {
        var p = Players.Get(player);
        if (p == null || player == null) return;
        var w = Warden;

        if (!p.IsGuard)
        {
            WText.TellBoth(player, w != null ? WText.T("warden.current", w.Name) : WText.T("warden.none"));
            return;
        }

        if (w != null) WText.TellBoth(player, WText.T("warden.current", w.Name));

        if (ReferenceEquals(_lastForceRemoved, p))
        {
            WText.TellBoth(player, WText.T("warden.queue.force_removed"));
            return;
        }

        if (_queuePrimary.Contains(p))
        {
            WText.TellBoth(player, WText.T("warden.queue.already"));
            return;
        }

        _queuePrimary.Add(p);
        WText.TellBoth(player, WText.T("warden.queue.added"));
    }

    [ConsoleCommand("css_uw", "Allows the warden to unwarden themselves.")]
    [CommandHelper(0, "", CommandUsage.CLIENT_ONLY)]
    public void OnUnwarden(CCSPlayerController? player, CommandInfo info)
    {
        var p = Players.Get(player);
        if (p == null || !IsWardenP(p)) return; // silently ignored for everyone else (original)
        Chat.PrintAll(WText.T("warden.unwarden.self", p.Name));
        if (!TryRemoveClientFromWarden(p))
            WText.TellBoth(player, WText.T("warden.unwarden.error"));
    }

    [ConsoleCommand("css_wwho", "Displays the current warden.")]
    [CommandHelper(0, "", CommandUsage.CLIENT_ONLY)]
    public void OnWardenWho(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null) return;
        var w = Warden;
        WText.TellBoth(player, w != null ? WText.T("warden.current", w.Name) : WText.T("warden.none"));
    }

    [ConsoleCommand("css_kiss", "Kisses the player you're looking at (or !kiss <player>).")]
    [ConsoleCommand("css_smooch", "Kisses the player you're looking at (or !smooch <player>).")]
    [CommandHelper(0, "", CommandUsage.CLIENT_ONLY)]
    public void OnWardenKiss(CCSPlayerController? player, CommandInfo info)
    {
        var p = Players.Get(player);
        if (player == null || p == null) return;
        if (!IsWardenP(p) || !player.IsAliveP()) { Chat.Tell(player, "warden.kiss.not_warden"); return; }

        var now = Server.EngineTime;
        if (now < _nextKiss) { Chat.Tell(player, "warden.kiss.wait", (int)Math.Ceiling(_nextKiss - now)); return; }

        CCSPlayerController? target;
        if (info.ArgCount > 1)
        {
            var t = Targets.FindOrReply(player, info, info.GetArg(1), aliveOnly: true, singleOnly: true, respectImmunity: false);
            if (t == null) return;
            target = t.Players[0];
            if (target.DistanceTo(player) > Cfg.KissRange) { Chat.Tell(player, "warden.kiss.too_far", target.PlayerName); return; }
        }
        else
        {
            // CSS 1.0.376 ray trace (GetClientAimTarget returned nothing on current builds, so !kiss silently did nothing).
            target = Aim.LookTarget(player, Cfg.KissRange);
        }

        if (target == null || !target.IsValid || target.Slot == player.Slot) { Chat.Tell(player, "warden.kiss.no_target"); return; }

        _nextKiss = now + Math.Max(0f, Cfg.KissCooldown);
        var color = target.Team == CsTeam.CounterTerrorist ? "{blue}" : "{red}";
        var key = Random.Shared.Next(2) == 0 ? "warden.kiss.smooch" : "warden.kiss.blow";
        Chat.PrintAll(WText.T(key, player.PlayerName, color, target.PlayerName), false);
        PlayNear(player, Cfg.KissSound, Cfg.KissSoundRadius);
    }

    /// <summary>
    /// Remove the warden like !rw (no announcement): they are skipped by this round's reselection and cannot rejoin the
    /// queue. Used by !votewarden. Returns the removed warden, or null.
    /// </summary>
    internal JbPlayer? ForceRemoveWarden()
    {
        var w = Warden;
        if (w == null || !TryRemoveClientFromWarden(w)) return null;
        _lastForceRemoved = w;
        return w;
    }

    [ConsoleCommand("css_rw", "css_rw - Removes the current warden.")]
    public void OnRemoveWardenCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Perms.Has(player, "warden.manage")) // Level 1+ (warden control)
        {
            Chat.Reply(player, info, Lang.T("core.no_access"));
            return;
        }

        var w = Warden;
        if (w == null)
        {
            ReplyBoth(player, info, WText.T("warden.rw.none"));
            return;
        }

        var name = w.Name;
        if (!TryRemoveClientFromWarden(w))
        {
            ReplyBoth(player, info, WText.T("warden.rw.error"));
            return;
        }

        _lastForceRemoved = w;
        Chat.PrintAll(WText.T("warden.rw.done", name));
        Jb.Log.LogEvent($"{player?.PlayerName ?? "Console"} removed {name} from warden.", Players.Get(player), w, LogType.Admin);
    }

    [ConsoleCommand("css_sw", "Sets the current warden.")]
    [ConsoleCommand("css_setw", "Sets the current warden.")]
    [ConsoleCommand("css_setwarden", "Sets the current warden.")]
    public void OnSetWardenCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Perms.Has(player, "warden.manage")) // Level 1+ (warden control)
        {
            Chat.Reply(player, info, Lang.T("core.no_access"));
            return;
        }

        if (info.ArgCount < 2)
        {
            Chat.Reply(player, info, WText.T("warden.set.usage"));
            return;
        }

        var res = Targets.FindOrReply(player, info, info.GetArg(1), singleOnly: true, respectImmunity: false);
        if (res == null) return;
        var target = Players.Get(res.Players[0]);
        if (target == null) return;

        if (!target.IsAlive)
        {
            Chat.Reply(player, info, WText.T("warden.set.dead"));
            return;
        }

        if (!target.IsGuard)
        {
            Chat.Reply(player, info, WText.T("warden.set.notguard"));
            return;
        }

        SetWardenByAdmin(target, player, info);
    }

    private bool SetWardenByAdmin(JbPlayer target, CCSPlayerController? admin, CommandInfo? info)
    {
        var w = Warden;
        if (w != null)
        {
            if (ReferenceEquals(w, target))
            {
                Chat.Reply(admin, info, WText.T("warden.set.already"));
                return false;
            }

            if (!TryRemoveClientFromWarden(w))
            {
                Chat.Reply(admin, info, WText.T("warden.set.error"));
                return false;
            }
        }

        SetWarden(target);
        var adminName = admin is { IsValid: true } ? admin.PlayerName : "Console";
        Chat.PrintAll(WText.T("warden.set.done", target.Name, adminName));
        StopWardenTimer(); // cancels the 3 s timer started by TryRemove
        Jb.Log.LogEvent($"{adminName} set {target.Name} as warden.", Players.Get(admin), target, LogType.Admin);
        return true;
    }

    private static void ReplyBoth(CCSPlayerController? player, CommandInfo info, string text)
    {
        if (player == null || !player.IsValid)
        {
            Server.PrintToConsole(Colors.Strip(text));
            return;
        }

        WText.TellBoth(player, text);
    }

    /// <summary>Play a configured sound: ".vsnd" to humans within radius of the source, otherwise a positional sound event.</summary>
    internal static void PlayNear(CCSPlayerController source, string sound, float radius)
    {
        if (string.IsNullOrWhiteSpace(sound) || !source.IsValid) return;
        var origin = source.Origin();
        if (sound.EndsWith(".vsnd", StringComparison.OrdinalIgnoreCase) || sound.Contains('/'))
        {
            foreach (var c in Players.Humans())
            {
                var o = c.Origin();
                if (origin == null || o == null || VectorUtil.Distance(o, origin) <= radius)
                    Sound.Play(c, sound);
            }

            return;
        }

        Sound.PlayAt(source, sound);
    }
}
