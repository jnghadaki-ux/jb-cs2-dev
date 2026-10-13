using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.LastRequest;

/// <summary>Section "LastRequest" (ultjb_lr_* cvars).</summary>
public sealed class LastRequestConfig
{
    public bool Enabled { get; set; } = true;
    public float PrisonersCanUsePercent { get; set; } = 8f;   // ultjb_lr_prisoners_can_use_percent
    public float SelectLastRequestTime { get; set; } = 20f;   // ultjb_lr_select_last_request_time
    public float SelectLastRequestTimeHelp1 { get; set; } = 15f; // ultjb_lr_select_last_request_time_help1
    public float SelectOpponentTime { get; set; } = 15f;      // ultjb_lr_select_opponent_time
    public int GuardsNeededForRebel { get; set; } = 3;        // ultjb_lr_guards_needed_for_rebel
    public int DisableFreedayLrTime { get; set; } = 150;      // ultjb_lr_disable_freeday_lr_time
    public bool IgnorePlayerCount { get; set; } = false;      // ultjb_lr_ignore_player_count

    /// <summary>Played to everyone when an LR is initialised (original: custom last_request_activated.mp3).</summary>
    public string ActivatedSound { get; set; } = "UIPanorama.popup_accept_match_beep";

    /// <summary>LR players only see their opponent (original SetTransmit). Turn off if it causes client problems.</summary>
    public bool HidePlayersDuringLr { get; set; } = true;

    /// <summary>Health-coloured rings around LR players and a beam to the opponent (original TE beams).</summary>
    public bool Beacons { get; set; } = true;
}

internal sealed class LrState
{
    public bool Initialized, Started, DiedSinceStarted, RoundEndedSinceStarted, Invincible, AdminFreeday;
    public bool CanDealDamage = true;
    public LastRequestDefinition? Lr;
    public int OpponentSlot = -1;
    public int OpponentUserId = -1;
    public Timer? SelectTimer, OpponentTimer, SlayTimer, SlayClientTimer, CancelFreedayTimer, InvincibleTimer;
    public Action<JbPlayer, JbPlayer>? OnOpponentSuccess;
    public Action<JbPlayer>? OnOpponentFailed;
    public Action<JbPlayer, JbPlayer?, SlayTimerResult>? SlayFinished;
    public int SlayTick, SlayTime;
    public SlayTimerFlags SlayFlags;
    public JbMenu? LrMenu;
    public JbMenu? OpponentMenu;
    public int SavedHealth, SavedMaxHealth, SavedArmor;
    public bool SavedHelmet;
    public readonly List<(string Name, int Clip, int Reserve0, int Reserve1)> SavedWeapons = new();
    public string SavedActive = "";
    public bool HasSavedWeapons;
    public Vector? PreLrLocation;
    public float NextRing, NextBeam;
    public CsTeam DisconnectTeam;
}

/// <summary>
/// Port of UltJB_API_LastRequest.sp (v1.x): LR slots, initialisation of the last prisoners, category and LR menus,
/// random LR on timeout, rebel-as-a-team, admin-given freedays, opponent selection, slay timers, weapon save/restore,
/// damage isolation (LR players only hurt their opponent), weapon pickup/drop/buy blocking, visibility isolation,
/// beacons, abort/freeday admin commands and the forwards. LRs themselves are registered by LastRequests/*.cs.
/// </summary>
public sealed partial class LastRequestModule : JbModule, ILastRequestService
{
    private LastRequestConfig Cfg => Section<LastRequestConfig>("LastRequest");

    public override string Name => "LastRequest";
    public override int Order => 9; // before LR implementations and days; after effects/weapon selection
    public override bool Enabled => Cfg.Enabled;

    private readonly List<LastRequestDefinition> _lrs = new();
    private bool _roundStarted;
    private int _roundNumber;
    private int _slotCount;
    private bool _shownRebelAsTeamMenu;
    private bool _rebelAsTeamStarted;
    private int _numRebelingTogether;
    private readonly List<int> _rebelMenuSlots = new();

    private static LrState S(JbPlayer p) => p.Data<LrState>();
    private static IEnumerable<JbPlayer> AllSlots() => Enumerable.Range(0, Players.MaxSlots).Select(Players.Get);

    // ================================================================== lifecycle

    public override void OnLoad(bool hotReload)
    {
        Jb.LastRequests = this;
        DamageService.Register(Name, 100, OnDamage);
        WeaponService.SetPickupRule(Name, PickupRule);
        WeaponService.SetDropRule(Name, DropRule);
        Plugin.RegisterListener<Listeners.OnEntityTakeDamagePre>(OnEntityTakeDamage);
        Plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        Plugin.RegisterEventHandler<EventRoundStart>(OnRoundStartPre, HookMode.Pre);
        Plugin.AddCommandListener("buy", OnBuy, HookMode.Pre);
        Plugin.AddCommandListener("autobuy", OnBuy, HookMode.Pre);
        Plugin.AddCommandListener("rebuy", OnBuy, HookMode.Pre);
        if (hotReload) LoadPoints(Server.MapName); // Cold start: no map yet, and Server.* reads the engine globals natively (crashes the server); OnMapStart covers it.
        if (hotReload) _roundStarted = Round.Phase is RoundPhase.Active or RoundPhase.FreezeTime;
    }

    public override void OnAllModulesLoaded(bool hotReload)
    {
        // OnRegisterReady: LRs registered themselves in their OnLoad; sort like SortLastRequestsByName.
        _lrs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        if (hotReload) CountSlots(false);
    }

    public override void OnUnload(bool hotReload)
    {
        DamageService.Unregister(OnDamage);
        WeaponService.RemovePickupRule(Name);
        WeaponService.RemoveDropRule(Name);
        foreach (var p in Players.Connected())
        {
            if (S(p).Started) EndLastRequest(p);
            KillAllTimers(S(p));
        }
    }

    public override void OnMapStart(string mapName)
    {
        _roundStarted = false; // warmup has no round_start: no LR until the first real round
        _roundNumber = 0;
        _shownRebelAsTeamMenu = false;
        _rebelAsTeamStarted = false;
        foreach (var p in AllSlots()) ResetState(p);
        LoadPoints(mapName);
    }

    private static void ResetState(JbPlayer p)
    {
        var s = S(p);
        KillAllTimers(s);
        s.Initialized = s.Started = s.AdminFreeday = s.Invincible = false;
        s.CanDealDamage = true;
        s.Lr = null;
        s.OpponentSlot = s.OpponentUserId = -1;
        s.LrMenu = s.OpponentMenu = null;
        s.HasSavedWeapons = false;
        s.SavedWeapons.Clear();
    }

    private static void KillAllTimers(LrState s)
    {
        Kill(ref s.SelectTimer);
        Kill(ref s.OpponentTimer);
        Kill(ref s.SlayTimer);
        Kill(ref s.SlayClientTimer);
        Kill(ref s.CancelFreedayTimer);
        Kill(ref s.InvincibleTimer);
    }

    private static void Kill(ref Timer? t)
    {
        try { t?.Kill(); }
        catch { /* already stopped */ }
        t = null;
    }

    private Timer AddTimer(float seconds, Action a) => Plugin.AddTimer(seconds, a, TimerFlags.STOP_ON_MAPCHANGE);

    // ================================================================== registry

    public IReadOnlyList<LastRequestDefinition> All => _lrs;

    public int Register(LastRequestDefinition definition)
    {
        if (_lrs.Any(l => string.Equals(l.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Logger.LogError("Last request [{Name}] is already registered.", definition.Name);
            return -1;
        }

        definition.AssignId(_lrs.Count == 0 ? 1 : _lrs.Max(l => l.Id) + 1);
        if (string.IsNullOrWhiteSpace(definition.Category)) definition.Category = "Other";
        _lrs.Add(definition);
        _lrs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return definition.Id;
    }

    private IEnumerable<LastRequestDefinition> Usable => _lrs.Where(l => l.Enabled);

    private List<string> Categories() =>
        Usable.Select(l => l.Category).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

    // ================================================================== queries (natives)

    public int RoundNumber => _roundNumber;

    public LrFlags GetFlags(JbPlayer prisoner) => S(prisoner).Lr?.Flags ?? LrFlags.None;

    public LastRequestDefinition? GetActive(JbPlayer prisoner) => S(prisoner).Started ? S(prisoner).Lr : null;

    public int NumInitialized => Players.Connected().Count(p => S(p).Initialized);

    public int NumStartedContains(LrFlags containsMask) => Players.Connected().Count(p => S(p).Started && (GetFlags(p) & containsMask) != 0);

    public int NumStartedIgnore(LrFlags ignoreMask = LrFlags.None) =>
        Players.Connected().Count(p => S(p).Started && (GetFlags(p) & ignoreMask) == 0);

    public bool HasStartedLastRequest(JbPlayer player)
    {
        var c = player.Controller;
        if (c == null) return false;
        return c.Team switch
        {
            CsTeam.Terrorist => S(player).Started,
            CsTeam.CounterTerrorist => GuardsPrisoner(player) != null,
            _ => false
        };
    }

    public bool IsInLastRequest(JbPlayer player) => HasStartedLastRequest(player) || S(player).Initialized;

    public JbPlayer? GetOpponent(JbPlayer player)
    {
        var c = player.Controller;
        if (c == null) return null;
        return c.Team switch
        {
            CsTeam.Terrorist => PrisonersOpponent(player),
            CsTeam.CounterTerrorist => GuardsPrisoner(player),
            _ => null
        };
    }

    /// <summary>GetPrisonersOpponent: valid only while the same client (user id) is still connected.</summary>
    private static JbPlayer? PrisonersOpponent(JbPlayer prisoner, bool cached = false)
    {
        var s = S(prisoner);
        if (s.OpponentSlot < 0) return null;
        var o = Players.Get(s.OpponentSlot);
        if (cached) return o;
        var c = o.Controller;
        return c != null && c.IsValid && c.UserId == s.OpponentUserId ? o : null;
    }

    /// <summary>GetGuardsLastRequestClient: the prisoner whose opponent is this guard.</summary>
    private static JbPlayer? GuardsPrisoner(JbPlayer guard, bool cached = false)
    {
        foreach (var p in AllSlots())
        {
            if (p.Slot == guard.Slot) continue;
            var o = PrisonersOpponent(p, cached);
            if (o != null && o.Slot == guard.Slot) return p;
        }

        return null;
    }

    public bool SetOpponent(JbPlayer prisoner, JbPlayer opponent)
    {
        if (!S(prisoner).Started) return false;
        SetPrisonersOpponent(prisoner, opponent);
        return true;
    }

    public bool CanLastRequest => CanLastRequestInternal(out _);

    private bool CanLastRequestInternal(out List<JbPlayer> prisonersAlive)
    {
        prisonersAlive = new List<JbPlayer>();
        if (!_roundStarted || _slotCount == 0) return false;
        int inFreeday = 0, guards = 0;
        foreach (var p in Players.Connected())
        {
            var c = p.Controller!;
            if (c.Team == CsTeam.CounterTerrorist) guards++;
            if (c.Team != CsTeam.Terrorist || !c.IsAliveP()) continue;
            if (S(p).Started && (GetFlags(p) & LrFlags.Freeday) != 0) inFreeday++;
            prisonersAlive.Add(p);
        }

        if (prisonersAlive.Count < 1 || guards < 1) return false;
        if (!Cfg.IgnorePlayerCount && prisonersAlive.Count > _slotCount + inFreeday) return false;
        return true;
    }

    // ================================================================== round flow

    private HookResult OnRoundStartPre(EventRoundStart ev, GameEventInfo info)
    {
        _roundNumber++;
        return HookResult.Continue;
    }

    public override void OnRoundStart()
    {
        _roundStarted = false;
        RoundCleanUp(false);
        _roundStarted = true;
        _shownRebelAsTeamMenu = false;
        _rebelAsTeamStarted = false;
        foreach (var p in Players.Connected())
        {
            S(p).CanDealDamage = true;
            S(p).Invincible = false;
        }

        CountSlots(true);
    }

    public override void OnRoundEnd(CsTeam winner, int reason)
    {
        _roundStarted = false;
        RoundCleanUp(true);
    }

    private void RoundCleanUp(bool skipFreedayCheck)
    {
        foreach (var p in Players.Connected())
        {
            var s = S(p);
            var flags = GetFlags(p);
            if (s.Started && (flags & LrFlags.DontEndOnRoundEnd) == 0)
            {
                if ((flags & LrFlags.Freeday) != 0)
                {
                    // False on the first round end, true on the next round start and on the second round end.
                    if (!skipFreedayCheck || s.RoundEndedSinceStarted)
                    {
                        if (s.RoundEndedSinceStarted) EndLastRequest(p, true);
                        s.RoundEndedSinceStarted = true;
                    }
                }
                else
                {
                    EndLastRequest(p);
                }
            }
            else if (!s.Started)
            {
                s.Initialized = false;
                s.AdminFreeday = false;
                Kill(ref s.CancelFreedayTimer);
                Kill(ref s.SelectTimer);
                CloseLrMenu(p);
            }
        }
    }

    /// <summary>GetAvailableLastRequestSlotCount: 8 % of prisoners (min 1), announced to everyone.</summary>
    private void CountSlots(bool announce)
    {
        int prisoners = 0, guards = 0;
        foreach (var c in Players.All())
        {
            if (c.Team == CsTeam.Terrorist) prisoners++;
            else if (c.Team == CsTeam.CounterTerrorist) guards++;
        }

        if (guards == 0) return;
        _slotCount = prisoners < 1 ? 0 : Math.Max(1, (int)MathF.Round(prisoners * (Cfg.PrisonersCanUsePercent / 100f)));
        if (announce) Chat.TellAll("lr.slots", _slotCount);
    }

    [ConsoleCommand("css_lrs", "sm_lrs - how many players can use last request this round")]
    public void CmdSlots(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null) Chat.Tell(caller, "lr.slots", _slotCount);
    }

    // ================================================================== !lr

    [ConsoleCommand("css_lr", "sm_lr - open the last request menu")]
    [ConsoleCommand("css_lastrequest", "sm_lastrequest - open the last request menu")]
    public void CmdLr(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !caller.IsValid) return;
        var p = caller.Jb();
        if (p != null) OpenMenu(p);
    }

    public void OpenMenu(JbPlayer p)
    {
        var s = S(p);
        if (s.AdminFreeday)
        {
            DisplayAdminFreedayMenu(p);
            return;
        }

        if (s.Initialized && !s.Started)
        {
            DisplayCategoryMenu(p);
            return;
        }

        TryInitializeLastRequest(p);
    }

    private void TryInitializeLastRequest(JbPlayer p)
    {
        var c = p.Controller;
        if (c == null) return;
        if (c.Team != CsTeam.Terrorist) { Plain(c, "lr.must_be_prisoner"); return; }
        if (S(p).Initialized) { Plain(c, "lr.already_in"); return; }
        if (Jb.Days.IsInProgress) { Plain(c, "lr.custom_day"); return; }
        if (!IsCandidate(p)) { Plain(c, "lr.not_yet"); return; }
        if (InitializeForCandidates()) return;
        InitializeLastRequest(p);
    }

    private static void Plain(CCSPlayerController c, string key, params object?[] args) =>
        c.PrintToChat(" " + Colors.Strip(Lang.T(key, args)));

    private bool GetCandidates(out List<JbPlayer> candidates)
    {
        candidates = new List<JbPlayer>();
        if (!CanLastRequestInternal(out var alive)) return false;
        foreach (var p in alive)
        {
            var s = S(p);
            if (s.Started && (GetFlags(p) & LrFlags.Freeday) != 0)
            {
                // A freeday prisoner still alive when LR becomes available is slain (original StartTimer_SlayClient).
                Kill(ref s.SlayClientTimer);
                var slot = p.Slot;
                s.SlayClientTimer = AddTimer(0.1f, () =>
                {
                    var pl = Players.Get(slot);
                    S(pl).SlayClientTimer = null;
                    if (pl.Controller is { IsValid: true } ctl && ctl.IsAliveP()) ctl.CommitSuicide(false, true);
                });
                continue;
            }

            if (s.Initialized || p.Slot == _leavingSlot) continue;
            candidates.Add(p);
        }

        return candidates.Count > 0;
    }

    private bool IsCandidate(JbPlayer p) => GetCandidates(out var list) && list.Any(x => x.Slot == p.Slot);

    /// <summary>InitializeLastRequestForCandidates: every remaining prisoner gets LR at once (with the rebel-as-team offer).</summary>
    private bool InitializeForCandidates()
    {
        if (Jb.Days.IsInProgress) return false;
        if (!GetCandidates(out var candidates)) return false;
        var showRebelMenu = false;
        if (!_shownRebelAsTeamMenu)
        {
            _shownRebelAsTeamMenu = true;
            var canRebel = AliveOn(CsTeam.Terrorist) - NumStartedContains(LrFlags.Freeday);
            if (AliveOn(CsTeam.CounterTerrorist) >= GuardsNeededForRebeling(canRebel) && canRebel != 1) showRebelMenu = true;
        }

        _numRebelingTogether = showRebelMenu ? 0 : 1;
        _rebelMenuSlots.Clear();
        foreach (var p in candidates)
        {
            _rebelMenuSlots.Add(p.Slot);
            if (p.Controller?.PawnOrNull()?.AbsOrigin is { } o) S(p).PreLrLocation = new Vector(o.X, o.Y, o.Z);
            InitializeLastRequest(p, showRebelMenu);
        }

        return true;
    }

    private static int AliveOn(CsTeam team) => Players.AliveOnTeam(team).Count();

    private int GuardsNeededForRebeling(int prisonersRebeling)
    {
        var needed = prisonersRebeling * 2;
        return prisonersRebeling == 1 || needed < Cfg.GuardsNeededForRebel ? Cfg.GuardsNeededForRebel : needed;
    }

    private void InitializeLastRequest(JbPlayer p, bool showRebelMenu = false)
    {
        // Pending admin freedays are cancelled once LR starts.
        foreach (var other in Players.Connected())
        {
            var os = S(other);
            if (!os.AdminFreeday || os.Started) continue;
            CloseLrMenu(other);
            Kill(ref os.CancelFreedayTimer);
            os.AdminFreeday = false;
        }

        var s = S(p);
        if (!p.IsBot)
        {
            if (showRebelMenu) DisplayRebelAsTeamMenu(p);
            else DisplayCategoryMenu(p);
        }
        else
        {
            _numRebelingTogether++;
            CheckShouldStartRebelingAsATeam();
        }

        s.Initialized = true;
        if (Cfg.ActivatedSound.Length > 0) Sound.PlayAll(Cfg.ActivatedSound);
        Chat.TellAll("lr.initialized", p.Name);
        var selectTime = SelectTime(p);
        Kill(ref s.SelectTimer);
        var slot = p.Slot;
        s.SelectTimer = AddTimer(selectTime, () => OnSelectTimeout(slot));
        if (p.Controller is { } c) Plain(c, "lr.select_time", (int)MathF.Round(selectTime));
        TeleportToLrPoint(p);
        JbEvents.RaiseLastRequestInitialized(p);
    }

    private float SelectTime(JbPlayer p)
    {
        var t = Cfg.SelectLastRequestTime;
        var help = Jb.Settings.GetHelpNumber(p);
        if (help > 0) t += Cfg.SelectLastRequestTimeHelp1 / help;
        return Math.Max(1f, t);
    }

    private void OnSelectTimeout(int slot)
    {
        _rebelMenuSlots.Clear();
        var p = Players.Get(slot);
        var s = S(p);
        s.SelectTimer = null;
        if (!s.Initialized || s.Started) return;
        CloseLrMenu(p);
        Chat.Tell(p, "lr.random");
        StartRandomLastRequest(p);
    }

    // ================================================================== menus

    private void CloseLrMenu(JbPlayer p)
    {
        var s = S(p);
        var c = p.Controller;
        if (s.LrMenu != null && c != null && ReferenceEquals(MenuService.Current(c), s.LrMenu)) MenuService.Close(c, false);
        s.LrMenu = null;
    }

    private void ShowLrMenu(JbPlayer p, JbMenu m)
    {
        var c = p.Controller;
        if (c == null) return;
        S(p).LrMenu = m;
        m.Open(c);
    }

    private void OnLrMenuCancelled(CCSPlayerController c)
    {
        var p = c.Jb();
        if (p == null) return;
        S(p).LrMenu = null;
        if (c.IsAliveP()) Plain(c, "lr.type_again");
    }

    private void DisplayCategoryMenu(JbPlayer p)
    {
        var cats = Categories();
        if (cats.Count == 0)
        {
            if (p.Controller is { } c) Plain(c, "lr.no_categories");
            return;
        }

        var m = new JbMenu(Lang.T("lr.menu.categories")) { Timeout = -1, OnCancel = OnLrMenuCancelled };
        foreach (var cat in cats)
        {
            var category = cat;
            m.Add(Hud.Escape(category), x =>
            {
                var pl = x.Jb();
                if (pl == null || !S(pl).Initialized || S(pl).Started) return;
                DisplayLrMenu(pl, category, m);
            });
        }

        ShowLrMenu(p, m);
    }

    private void DisplayLrMenu(JbPlayer p, string category, JbMenu parent)
    {
        var m = new JbMenu(Lang.T("lr.menu.lrs", category)) { Timeout = -1, Parent = parent, OnCancel = OnLrMenuCancelled };
        var prisonersAlive = AliveOn(CsTeam.Terrorist);
        var guardsAlive = AliveOn(CsTeam.CounterTerrorist);
        var guardsNeeded = GuardsNeededForRebeling(_numRebelingTogether);
        var mapEndingSoon = MapTimeLeft() is { } left && left <= Cfg.DisableFreedayLrTime;
        var any = false;
        foreach (var lr in Usable.Where(l => l.Category == category))
        {
            any = true;
            var def = lr;
            string? disabled = null;
            if ((lr.Flags & LrFlags.Freeday) != 0 && mapEndingSoon) disabled = "lr.menu.map_end";
            else if ((lr.Flags & LrFlags.LastPrisonerOnlyCanUse) != 0 && prisonersAlive > 1) disabled = "lr.menu.last_t";
            else if ((lr.Flags & LrFlags.Rebel) != 0 && _numRebelingTogether == 1)
            {
                if (prisonersAlive > 1) disabled = "lr.menu.last_t";
                else if (guardsAlive < guardsNeeded) disabled = "lr.menu.need_ct";
            }

            if (disabled != null)
            {
                m.Add(Hud.Escape(lr.Name) + " " + Lang.T(disabled), null, false);
                continue;
            }

            m.Add(Hud.Escape(lr.Name), x =>
            {
                var pl = x.Jb();
                if (pl == null) return;
                Kill(ref S(pl).SelectTimer);
                if (!S(pl).Initialized || S(pl).Started) return;
                StartLastRequest(pl, def);
            });
        }

        if (!any)
        {
            if (p.Controller is { } c) Plain(c, "lr.no_lrs_in_category");
            DisplayCategoryMenu(p);
            return;
        }

        ShowLrMenu(p, m);
    }

    private static float? MapTimeLeft()
    {
        try
        {
            var limit = ConVar.Find("mp_timelimit")?.GetPrimitiveValue<float>() ?? 0f;
            var rules = Round.GameRules;
            if (limit <= 0f || rules == null) return null;
            return rules.GameStartTime + limit * 60f - Server.CurrentTime;
        }
        catch
        {
            return null;
        }
    }

    // ================================================================== rebel as a team

    private void DisplayRebelAsTeamMenu(JbPlayer p)
    {
        var m = new JbMenu(Lang.T("lr.rebel_team.title")) { ExitButton = false, Timeout = -1, OnCancel = OnLrMenuCancelled };
        m.Add(Lang.T("lr.rebel_team.no"), x =>
        {
            var pl = x.Jb();
            if (pl == null || !S(pl).Initialized || _rebelMenuSlots.Count == 0) return;
            S(pl).LrMenu = null;
            CancelRebelAsATeam();
        });
        m.AddInfo(" ");
        m.AddInfo(" ");
        m.Add(Lang.T("lr.rebel_team.yes"), x =>
        {
            var pl = x.Jb();
            if (pl == null || !S(pl).Initialized || _rebelMenuSlots.Count == 0) return;
            S(pl).LrMenu = null;
            _numRebelingTogether++;
            if (!CheckShouldStartRebelingAsATeam()) Chat.Print(x, Lang.T("lr.rebel_team.waiting"), false);
        });
        ShowLrMenu(p, m);
    }

    private void CheckRemoveFromRebelAsTeam(JbPlayer p)
    {
        if (!_rebelMenuSlots.Remove(p.Slot)) return;
        if (S(p).LrMenu == null) _numRebelingTogether--;
        CheckShouldStartRebelingAsATeam();
    }

    private void CancelRebelAsATeam()
    {
        foreach (var slot in _rebelMenuSlots.ToList())
        {
            var p = Players.Get(slot);
            if (p.Controller is not { IsValid: true }) continue;
            DisplayCategoryMenu(p);
            Chat.Print(p, Lang.T("lr.rebel_team.cancelled"), false);
        }

        _rebelMenuSlots.Clear();
    }

    private bool CheckShouldStartRebelingAsATeam()
    {
        if (_numRebelingTogether < 1 || _numRebelingTogether != _rebelMenuSlots.Count) return false;
        var rebels = Usable.Where(l => (l.Flags & LrFlags.Rebel) != 0 && (l.Flags & LrFlags.LastPrisonerOnlyCanUse) == 0).ToList();
        var started = false;
        if (rebels.Count == 0)
        {
            Chat.PrintAll(Lang.T("lr.rebel_team.none"), false);
        }
        else
        {
            var lr = rebels[Random.Shared.Next(rebels.Count)];
            var point = RandomRebelPoint();
            foreach (var slot in _rebelMenuSlots.ToList())
            {
                var p = Players.Get(slot);
                if (p.Controller is not { IsValid: true }) continue;
                StartLastRequest(p, lr, fromRebelAsTeam: true);
                TeleportToRebelPoint(p, point);
            }

            _rebelAsTeamStarted = true;
            started = true;
        }

        if (!started) CancelRebelAsATeam();
        _rebelMenuSlots.Clear();
        return started;
    }

    // ================================================================== admin freeday / abort

    [ConsoleCommand("css_givefreeday", "sm_givefreeday <#steamid|#userid|name> - give a player a freeday")]
    public void CmdGiveFreeday(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null && !Perms.Has(caller, "lr")) { Chat.Tell(caller, "core.no_access"); return; }
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "css_givefreeday <#steamid|#userid|name>"); return; }
        var r = Targets.FindOrReply(caller, info, info.GetArg(1), respectImmunity: false);
        if (r == null) return;
        var gave = false;
        foreach (var t in r.Players.Where(t => !t.IsBot))
        {
            var target = t.Jb();
            if (target == null || !InitializeAdminGivenFreeday(caller, target)) continue;
            Logger.LogInformation("\"{Admin}\" gave freeday to \"{Target}\"", caller?.PlayerName ?? "Console", t.PlayerName);
            Chat.PrintAll(Lang.T("lr.freeday.given_all", t.PlayerName), false);
            gave = true;
        }

        if (gave && caller != null) Chat.Tell(caller, "lr.freeday.respawn_note");
    }

    private bool InitializeAdminGivenFreeday(CCSPlayerController? admin, JbPlayer target)
    {
        var c = target.Controller!;
        if (c.Team != CsTeam.Terrorist) { Chat.ReplyT(admin, null, "lr.freeday.not_prisoner", c.PlayerName); return false; }
        var s = S(target);
        if (s.AdminFreeday || s.Initialized) { Chat.ReplyT(admin, null, "lr.freeday.already", c.PlayerName); return false; }
        if (NumInitialized - NumStartedContains(LrFlags.Freeday) > 0) { Chat.ReplyT(admin, null, "lr.freeday.after_lr"); return false; }
        if (!DisplayAdminFreedayMenu(target)) return true;
        s.AdminFreeday = true;
        Kill(ref s.CancelFreedayTimer);
        var slot = target.Slot;
        s.CancelFreedayTimer = AddTimer(30f, () =>
        {
            var pl = Players.Get(slot);
            var st = S(pl);
            st.CancelFreedayTimer = null;
            if (!st.AdminFreeday) return;
            st.AdminFreeday = false;
            Chat.Tell(pl, "lr.freeday.timeout");
        });
        return true;
    }

    private bool DisplayAdminFreedayMenu(JbPlayer p)
    {
        if (S(p).Started) { Chat.Tell(p, "lr.already_in_colored"); return false; }
        var freedays = Usable.Where(l => (l.Flags & LrFlags.Freeday) != 0).ToList();
        if (freedays.Count == 0)
        {
            if (p.Controller is { } c) Plain(c, "lr.freeday.none");
            return false;
        }

        var m = new JbMenu(Lang.T("lr.freeday.menu")) { Timeout = -1, OnCancel = x =>
        {
            var pl = x.Jb();
            if (pl == null) return;
            S(pl).LrMenu = null;
            if (S(pl).AdminFreeday) Plain(x, "lr.type_again");
        } };
        foreach (var lr in freedays)
        {
            var def = lr;
            m.Add(Hud.Escape(lr.Name), x =>
            {
                var pl = x.Jb();
                if (pl == null || !S(pl).AdminFreeday) return;
                Kill(ref S(pl).CancelFreedayTimer);
                StartLastRequest(pl, def, fromAdminFreeday: true);
            });
        }

        ShowLrMenu(p, m);
        return true;
    }

    [ConsoleCommand("css_abortlr", "sm_abortlr <#steamid|#userid|name> - abort a player's last request")]
    public void CmdAbortLr(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null && !Perms.Has(caller, "lr")) { Chat.Tell(caller, "core.no_access"); return; }
        if (info.ArgCount < 2) { Chat.ReplyT(caller, info, "core.usage", "css_abortlr <#steamid|#userid|name>"); return; }
        var r = Targets.FindOrReply(caller, info, info.GetArg(1), aliveOnly: true, respectImmunity: false);
        if (r == null) return;
        var targets = r.Players.Where(t => !t.IsBot).ToList();
        foreach (var t in targets)
        {
            var p = t.Jb();
            if (p == null) continue;
            if (targets.Count == 1 && !HasStartedLastRequest(p))
            {
                Chat.ReplyT(caller, info, "lr.abort.not_in", t.PlayerName);
                return;
            }

            EndLastRequestFor(p);
            Logger.LogInformation("\"{Admin}\" removed LR for \"{Target}\"", caller?.PlayerName ?? "Console", t.PlayerName);
            Chat.PrintAll(Lang.T("lr.abort.done", t.PlayerName), false);
        }
    }

    /// <summary>UltJB_LR_EndLastRequest: works for the prisoner or their guard opponent.</summary>
    private void EndLastRequestFor(JbPlayer p)
    {
        var c = p.Controller;
        if (c == null) return;
        if (c.Team == CsTeam.CounterTerrorist)
        {
            var prisoner = GuardsPrisoner(p);
            if (prisoner != null) EndLastRequest(prisoner);
        }
        else if (c.Team == CsTeam.Terrorist)
        {
            EndLastRequest(p);
        }
    }

    public void EndAll()
    {
        // Ending every LR must not re-open LR selection after each one (menus/teleports would come straight back).
        _suppressReinit = true;
        try
        {
            foreach (var p in Players.Connected().Where(p => S(p).Initialized || S(p).Started).ToList()) EndLastRequest(p);
        }
        finally { _suppressReinit = false; }
    }

    /// <summary>Set while ending LRs that must not restart LR selection (abort all, a disconnecting prisoner).</summary>
    private bool _suppressReinit;

    /// <summary>The slot that is disconnecting this frame: never an LR candidate (its pawn still counts as alive).</summary>
    private int _leavingSlot = -1;

    private void AbortAllNonFreeday()
    {
        foreach (var p in Players.Connected().ToList())
        {
            var c = p.Controller!;
            var s = S(p);
            if (!c.IsAliveP() || !s.Initialized || (GetFlags(p) & LrFlags.Freeday) != 0 || c.Team != CsTeam.Terrorist) continue;
            EndLastRequest(p);
            if (s.PreLrLocation != null) c.TeleportTo(s.PreLrLocation, null, new Vector(0, 0, 0));
        }
    }

    private void CheckIfNeedToAbort()
    {
        if (CanLastRequestInternal(out _)) return;
        if (NumInitialized - NumStartedContains(LrFlags.Freeday) == 0) return;
        Chat.TellAll("lr.abort_all");
        AbortAllNonFreeday();
    }

    // ================================================================== start / end

    private void StartRandomLastRequest(JbPlayer p)
    {
        if (_lrs.Count == 0)
        {
            if (p.Controller is { } c) Plain(c, "lr.no_options");
            EndLastRequest(p);
            return;
        }

        var options = Usable.Where(l => (l.Flags & (LrFlags.Freeday | LrFlags.LastPrisonerOnlyCanUse | LrFlags.Rebel)) == 0).ToList();
        if (options.Count == 0)
        {
            if (p.Controller is { } c) Plain(c, "lr.no_usable_options");
            EndLastRequest(p);
            return;
        }

        StartLastRequest(p, options[Random.Shared.Next(options.Count)]);
    }

    private bool StartLastRequest(JbPlayer p, LastRequestDefinition lr, bool fromAdminFreeday = false, bool fromRebelAsTeam = false)
    {
        var s = S(p);
        Kill(ref s.SelectTimer);
        CloseLrMenu(p);
        var c = p.Controller;
        if (c == null) return false;

        s.Initialized = true;
        s.Started = true;
        s.Lr = lr;
        s.DiedSinceStarted = false;
        s.RoundEndedSinceStarted = false;

        foreach (var other in Players.Humans().Where(o => o.Slot != c.Slot))
            Chat.Tell(other, "lr.started_all", p.Name, lr.Category, lr.Name);

        if ((lr.Flags & LrFlags.TempInvincible) != 0) SetTempInvincibility(p, 1f);
        if (!fromRebelAsTeam && (lr.Flags & LrFlags.RandomTeleportLocation) != 0) TeleportToRebelPoint(p, RandomRebelPoint());

        Chat.Tell(p, "lr.started_self", lr.Category, lr.Name);
        FillHealthToDefault(p);
        Jb.Log.LogEvent(Lang.T("lr.log_started", p.Name, lr.Category, lr.Name), p, null, LogType.LastRequest);

        try
        {
            lr.OnStart(p);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Last request {Name} failed to start", lr.Name);
            Plain(c, "lr.load_error", 1);
            EndLastRequest(p);
            return false;
        }

        if (!S(p).Started) return false; // the LR ended itself during start
        JbEvents.RaiseLastRequestStarted(p, lr.Flags);

        if (!fromAdminFreeday)
        {
            if ((lr.Flags & LrFlags.Freeday) != 0)
            {
                if (c.IsAliveP()) c.CommitSuicide(false, true);
                Chat.Tell(p, "lr.freeday.next_round");
            }
        }
        else
        {
            if (!c.IsAliveP()) c.Respawn();
            s.DiedSinceStarted = true;
            s.RoundEndedSinceStarted = true;
            Chat.Tell(p, "lr.freeday.now");
        }

        return true;
    }

    public void EndLastRequest(JbPlayer prisoner) => EndLastRequest(prisoner, false);

    private void EndLastRequest(JbPlayer p, bool endingFromFreeday)
    {
        var s = S(p);
        StopSlayTimerInternal(p);
        CheckRemoveFromRebelAsTeam(p);
        s.Initialized = false;
        s.AdminFreeday = false;
        Kill(ref s.SelectTimer);
        SetTempInvincibility(p, 0f);
        if (!s.Started) return;

        var opponent = PrisonersOpponent(p);
        var opponentCached = PrisonersOpponent(p, true);
        ResetListen(p, opponent);
        if (!endingFromFreeday)
        {
            CleanupWeaponsFromLr(p);
            if (opponentCached != null) CleanupWeaponsFromLr(opponentCached);
            RestoreHealth(p);
            if (opponent != null) RestoreHealth(opponent);
        }

        s.Started = false;
        s.OpponentSlot = s.OpponentUserId = -1;
        StopOpponentSelection(p, true);
        Jb.WeaponSelection.CancelSelection(p);
        Jb.Effects.CancelSelection(p);
        CloseOpponentMenu(p);

        var lr = s.Lr;
        s.Lr = null;
        if (lr?.OnEnd != null)
        {
            try { lr.OnEnd(p, opponent); }
            catch (Exception ex) { Logger.LogError(ex, "Last request {Name} end callback failed", lr.Name); }
        }

        JbEvents.RaiseLastRequestEnded(p, opponent);

        if (!_suppressReinit && !Jb.Days.IsInProgress && _roundStarted && p.Controller is { IsValid: true } c && c.IsAliveP() && AvailableGuards() > 0)
            TryInitializeLastRequest(p);
    }

    private int AvailableGuards() =>
        Players.AliveGuards().Count(g => g.Jb() is { } gp && GuardsPrisoner(gp) == null);

    private void SetTempInvincibility(JbPlayer p, float seconds)
    {
        var s = S(p);
        Kill(ref s.InvincibleTimer);
        if (seconds <= 0f)
        {
            s.Invincible = false;
            return;
        }

        s.Invincible = true;
        var slot = p.Slot;
        s.InvincibleTimer = AddTimer(seconds, () =>
        {
            var st = S(Players.Get(slot));
            st.Invincible = false;
            st.InvincibleTimer = null;
        });
    }

    private static void FillHealthToDefault(JbPlayer p)
    {
        var c = p.Controller;
        var pawn = c?.PawnOrNull();
        if (c == null || pawn == null) return;
        var s = S(p);
        s.SavedHealth = pawn.Health;
        s.SavedMaxHealth = pawn.MaxHealth;
        s.SavedArmor = pawn.ArmorValue;
        s.SavedHelmet = pawn.ItemServices != null && new CCSPlayer_ItemServices(pawn.ItemServices.Handle).HasHelmet;
        c.SetMaxHp(100);
        c.SetHp(100, false);
        c.SetArmor(0, false);
    }

    private static void RestoreHealth(JbPlayer p)
    {
        var c = p.Controller;
        var s = S(p);
        if (c == null || !c.IsAliveP() || s.SavedHealth == 0) return;
        c.SetMaxHp(s.SavedMaxHealth);
        c.SetHp(s.SavedHealth, false);
        c.SetArmor(s.SavedArmor, s.SavedHelmet);
        s.SavedHealth = 0;
        s.SavedMaxHealth = 0;
    }

    public void SetHealth(JbPlayer player, int health)
    {
        var c = player.Controller;
        if (c == null) return;
        if ((c.PawnOrNull()?.MaxHealth ?? 100) < health) c.SetMaxHp(health);
        c.SetHp(health, false);
    }

    private static void ResetListen(JbPlayer a, JbPlayer? b)
    {
        if (b == null) return;
        Jb.Mute.Refresh(a);
        Jb.Mute.Refresh(b);
    }

    // ================================================================== opponent selection

    public bool DisplayOpponentSelection(JbPlayer prisoner, Action<JbPlayer, JbPlayer> onSuccess, Action<JbPlayer>? onFailed = null)
    {
        var s = S(prisoner);
        s.OnOpponentSuccess = onSuccess;
        s.OnOpponentFailed = onFailed;
        if (!ShowOpponentMenu(prisoner)) return false;
        Kill(ref s.OpponentTimer);
        var slot = prisoner.Slot;
        s.OpponentTimer = AddTimer(Math.Max(1f, Cfg.SelectOpponentTime), () =>
        {
            var p = Players.Get(slot);
            S(p).OpponentTimer = null;
            CloseOpponentMenu(p);
            Chat.Tell(p, "lr.opponent.random");
            SelectRandomOpponent(p);
        });
        return true;
    }

    private List<JbPlayer> OpponentChoices(JbPlayer prisoner) =>
        Players.AliveGuards().Select(g => g.Jb()!).Where(g => g != null && (GuardsPrisoner(g) is not { } gp || gp.Slot == prisoner.Slot)).ToList();

    private bool ShowOpponentMenu(JbPlayer prisoner)
    {
        var c = prisoner.Controller;
        var choices = OpponentChoices(prisoner);
        if (c == null || choices.Count == 0)
        {
            if (c != null) Plain(c, "lr.opponent.none");
            OpponentSelectedFailed(prisoner);
            return false;
        }

        var m = new JbMenu(Lang.T("lr.opponent.title")) { ExitButton = false, Timeout = -1 };
        foreach (var g in choices)
        {
            var slot = g.Slot;
            var userId = g.Controller!.UserId;
            m.Add(Hud.Escape(g.Name), x => OnOpponentChosen(x, slot, userId));
        }

        S(prisoner).OpponentMenu = m;
        m.OnCancel = x =>
        {
            var pl = x.Jb();
            if (pl != null) S(pl).OpponentMenu = null;
        };
        m.Open(c);
        return true;
    }

    private void OnOpponentChosen(CCSPlayerController x, int guardSlot, int? guardUserId)
    {
        var p = x.Jb();
        if (p == null) return;
        var s = S(p);
        s.OpponentMenu = null;
        if (!s.Started)
        {
            Kill(ref s.OpponentTimer);
            return;
        }

        var g = Players.Get(guardSlot);
        var gc = g.Controller;
        if (gc == null || !gc.IsValid || gc.UserId != guardUserId)
        {
            Plain(x, "lr.opponent.gone");
            ShowOpponentMenu(p);
            return;
        }

        if (!gc.IsAliveP())
        {
            Plain(x, "lr.opponent.dead", gc.PlayerName);
            ShowOpponentMenu(p);
            return;
        }

        var other = GuardsPrisoner(g);
        if (other != null && other.Slot != p.Slot)
        {
            Plain(x, "lr.opponent.busy", gc.PlayerName);
            ShowOpponentMenu(p);
            return;
        }

        Kill(ref s.OpponentTimer);
        SelectOpponent(p, g);
    }

    private void SelectRandomOpponent(JbPlayer p)
    {
        var choices = OpponentChoices(p);
        if (choices.Count == 0)
        {
            if (p.Controller is { } c) Plain(c, "lr.opponent.none");
            OpponentSelectedFailed(p);
            return;
        }

        SelectOpponent(p, choices[Random.Shared.Next(choices.Count)]);
    }

    private void CloseOpponentMenu(JbPlayer p)
    {
        var s = S(p);
        var c = p.Controller;
        if (s.OpponentMenu != null && c != null && ReferenceEquals(MenuService.Current(c), s.OpponentMenu)) MenuService.Close(c, false);
        s.OpponentMenu = null;
    }

    private void StopOpponentSelection(JbPlayer p, bool callFailedIfNeeded)
    {
        var s = S(p);
        if (s.OpponentTimer == null) return;
        Kill(ref s.OpponentTimer);
        if (callFailedIfNeeded) OpponentSelectedFailed(p);
    }

    private void OpponentSelectedFailed(JbPlayer p)
    {
        var s = S(p);
        var cb = s.OnOpponentFailed;
        s.OnOpponentFailed = null;
        if (cb != null)
        {
            try { cb(p); }
            catch (Exception ex) { Logger.LogError(ex, "Opponent selection failed callback threw"); }
        }

        if (s.Started && (GetFlags(p) & LrFlags.DontEndOnGuardSelectFail) == 0) EndLastRequest(p);
    }

    private void SelectOpponent(JbPlayer p, JbPlayer opponent)
    {
        SetPrisonersOpponent(p, opponent);
        var s = S(p);
        var cb = s.OnOpponentSuccess;
        s.OnOpponentSuccess = null;
        s.OnOpponentFailed = null;
        try { cb?.Invoke(p, opponent); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Opponent selection success callback threw");
            return;
        }

        try
        {
            p.Controller?.SetListenOverride(opponent.Controller!, ListenOverride.Hear);
            opponent.Controller?.SetListenOverride(p.Controller!, ListenOverride.Hear);
        }
        catch
        {
            // voice override is best effort
        }
    }

    private void SetPrisonersOpponent(JbPlayer p, JbPlayer opponent)
    {
        if ((GetFlags(p) & LrFlags.DontTeleportToOpponent) == 0 && p.Controller != null && opponent.Controller != null)
            TeleportNear(p.Controller, opponent.Controller, true);
        var s = S(p);
        s.OpponentSlot = opponent.Slot;
        s.OpponentUserId = opponent.Controller?.UserId ?? -1;
        FillHealthToDefault(opponent);
        Chat.Tell(p, "lr.opponent.yours", opponent.Name);
        Chat.Tell(opponent, "lr.opponent.chosen_you", p.Name);
    }

    // ================================================================== slay timer

    public bool StartSlayTimer(JbPlayer prisoner, int secondsBeforeSlay = 45, SlayTimerFlags flags = SlayTimerFlags.Both,
        Action<JbPlayer, JbPlayer?, SlayTimerResult>? finished = null)
    {
        if (!HasStartedLastRequest(prisoner)) return false;
        var pr = prisoner.Controller?.Team == CsTeam.CounterTerrorist ? GuardsPrisoner(prisoner) : prisoner;
        if (pr == null) return false;
        StopSlayTimerInternal(pr);
        var opponent = PrisonersOpponent(pr);
        if (opponent == null) return false;
        var s = S(pr);
        s.SlayTick = 0;
        s.SlayTime = Math.Max(1, secondsBeforeSlay);
        s.SlayFlags = flags;
        s.SlayFinished = finished;
        ShowSlayCountdown(pr, s.SlayTime, flags);
        ShowSlayCountdown(opponent, s.SlayTime, flags);
        var slot = pr.Slot;
        s.SlayTimer = Plugin.AddTimer(1f, () => SlayTick(slot), TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        return true;
    }

    private void SlayTick(int slot)
    {
        var pr = Players.Get(slot);
        var s = S(pr);
        var opponent = PrisonersOpponent(pr);
        if (opponent == null)
        {
            Hud.Clear(pr.Controller, "lr.slay");
            Kill(ref s.SlayTimer);
            var cb = s.SlayFinished;
            s.SlayFinished = null;
            cb?.Invoke(pr, null, SlayTimerResult.None);
            return;
        }

        if (s.SlayTick >= s.SlayTime)
        {
            var result = SlayTimerResult.None;
            if ((s.SlayFlags & SlayTimerFlags.Prisoner) != 0)
            {
                Hud.Set(pr.Controller, "lr.slay", Lang.T("lr.slay.slayed"), 70, 4);
                result = SlayTimerResult.Prisoner;
            }
            else
            {
                Hud.Clear(pr.Controller, "lr.slay");
            }

            if ((s.SlayFlags & SlayTimerFlags.Guard) != 0)
            {
                Hud.Set(opponent.Controller, "lr.slay", Lang.T("lr.slay.slayed"), 70, 4);
                result = result == SlayTimerResult.None ? SlayTimerResult.Guard : SlayTimerResult.Both;
            }
            else
            {
                Hud.Clear(opponent.Controller, "lr.slay");
            }

            Kill(ref s.SlayTimer);
            var cb = s.SlayFinished;
            s.SlayFinished = null;
            try { cb?.Invoke(pr, opponent, result); }
            catch (Exception ex) { Logger.LogError(ex, "Slay timer callback threw"); }
            if ((s.SlayFlags & SlayTimerFlags.Prisoner) != 0 && pr.Controller is { } pc && pc.IsAliveP()) pc.CommitSuicide(false, true);
            if ((s.SlayFlags & SlayTimerFlags.Guard) != 0 && opponent.Controller is { } oc && oc.IsAliveP()) oc.CommitSuicide(false, true);
            return;
        }

        ShowSlayCountdown(pr, s.SlayTime - s.SlayTick, s.SlayFlags);
        ShowSlayCountdown(opponent, s.SlayTime - s.SlayTick, s.SlayFlags);
        s.SlayTick++;
    }

    private static void ShowSlayCountdown(JbPlayer p, int left, SlayTimerFlags flags)
    {
        var (who, color) = flags switch
        {
            SlayTimerFlags.Both => ("both", "DE2626"),
            SlayTimerFlags.Prisoner => ("prisoner", "ddaf25"),
            SlayTimerFlags.Guard => ("guard", "257edd"),
            _ => ("", "")
        };
        if (who.Length == 0) return;
        Hud.Set(p.Controller, "lr.slay", Lang.T("lr.slay.countdown", color, who, left), 70, 2);
    }

    public bool StopSlayTimer(JbPlayer prisoner)
    {
        var pr = prisoner.Controller?.Team == CsTeam.CounterTerrorist ? GuardsPrisoner(prisoner) : prisoner;
        if (pr == null) return false;
        StopSlayTimerInternal(pr);
        return true;
    }

    private static void StopSlayTimerInternal(JbPlayer pr)
    {
        var s = S(pr);
        s.SlayFinished = null;
        if (s.SlayTimer == null) return;
        Kill(ref s.SlayTimer);
        Hud.Clear(pr.Controller, "lr.slay");
        var o = PrisonersOpponent(pr);
        if (o != null) Hud.Clear(o.Controller, "lr.slay");
    }

    // ================================================================== deaths, teams, spawns, disconnects

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        var c = victim.Controller;
        if (c == null) return;
        var s = S(victim);
        switch (c.Team)
        {
            case CsTeam.Terrorist:
                if (s.Initialized && !s.Started)
                {
                    EndLastRequest(victim);
                }
                else if (s.Started && (GetFlags(victim) & LrFlags.DontEndOnPrisonerDeath) == 0)
                {
                    if ((GetFlags(victim) & LrFlags.Freeday) != 0)
                    {
                        if (s.DiedSinceStarted) EndLastRequest(victim);
                        s.DiedSinceStarted = true;
                    }
                    else
                    {
                        EndLastRequest(victim);
                    }
                }

                InitializeForCandidates();
                break;
            case CsTeam.CounterTerrorist:
                var prisoner = GuardsPrisoner(victim);
                if (prisoner != null && S(prisoner).Started && (GetFlags(prisoner) & LrFlags.DontEndOnGuardDeath) == 0) EndLastRequest(prisoner);
                break;
        }
    }

    public override void OnPlayerTeam(JbPlayer player, CsTeam oldTeam, CsTeam newTeam, bool disconnect)
    {
        if (oldTeam == newTeam || disconnect) return;
        // Team switch ends the LR (original used the team at event time; here the old team decides the role).
        if (oldTeam == CsTeam.CounterTerrorist)
        {
            var prisoner = GuardsPrisoner(player);
            if (prisoner != null) EndLastRequest(prisoner);
        }
        else if (oldTeam == CsTeam.Terrorist)
        {
            EndLastRequest(player);
        }
    }

    public override void OnPlayerSpawn(JbPlayer player)
    {
        var s = S(player);
        s.CanDealDamage = true;
        s.Invincible = false;
        Server.NextFrame(CheckIfNeedToAbort);
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        var c = player.Controller;
        var team = c?.Team ?? CsTeam.None;
        var s = S(player);
        Kill(ref s.SlayClientTimer);
        // The original ran this in OnClientDisconnect_Post (client gone); here the pawn still counts as alive this frame.
        _leavingSlot = player.Slot;
        _suppressReinit = true;
        try { EndLastRequest(player); }
        finally { _suppressReinit = false; }
        Server.NextFrame(() => { if (_leavingSlot == player.Slot) _leavingSlot = -1; });
        KillAllTimers(s);
        s.HasSavedWeapons = false;
        s.SavedWeapons.Clear();
        var slot = player.Slot;
        Server.NextFrame(() =>
        {
            if (team == CsTeam.Terrorist)
            {
                InitializeForCandidates();
            }
            else if (team == CsTeam.CounterTerrorist)
            {
                var prisoner = GuardsPrisonerBySlot(slot);
                if (prisoner != null) PrisonersGuardLeft(prisoner);
            }
        });
    }

    private static JbPlayer? GuardsPrisonerBySlot(int guardSlot) =>
        AllSlots().FirstOrDefault(p => p.Slot != guardSlot && S(p).Started && S(p).OpponentSlot == guardSlot);

    private void PrisonersGuardLeft(JbPlayer prisoner)
    {
        var lr = S(prisoner).Lr;
        if (lr == null)
        {
            EndLastRequest(prisoner);
            return;
        }

        var dontEnd = (lr.Flags & LrFlags.DontEndOnGuardLeave) != 0;
        if (lr.OnOpponentLeft == null)
        {
            if (dontEnd) Logger.LogError("{Name} is set to not end on guard leave, but has no opponent-left callback.", lr.Name);
            EndLastRequest(prisoner);
            return;
        }

        try { lr.OnOpponentLeft(prisoner); }
        catch (Exception ex) { Logger.LogError(ex, "Opponent-left callback of {Name} threw", lr.Name); }
        S(prisoner).OpponentSlot = S(prisoner).OpponentUserId = -1;
        if (!dontEnd) EndLastRequest(prisoner);
    }
}
