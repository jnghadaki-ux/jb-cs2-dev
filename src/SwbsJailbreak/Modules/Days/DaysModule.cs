using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.LastRequest;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Days;

/// <summary>Section "Days" (ultjb_day_* / ultjb_warday_* cvars).</summary>
public sealed class DaysConfig
{
    public bool Enabled { get; set; } = true;
    public int SelectTime { get; set; } = 15;            // ultjb_day_select_time
    public int WardayFreezeTime { get; set; } = 30;      // ultjb_warday_freeze_time
    public bool ForceAllowOverride { get; set; } = false; // ultjb_day_force_allow_override

    /// <summary>Rebel points needed before a day may be free-for-all (original: 90 PathPoints "rebels").</summary>
    public int FreeForAllMinRebelPoints { get; set; } = 90;

    /// <summary>Played when free-for-all starts (original survival/rocketalarmclose.wav).</summary>
    public string FreeForAllSound { get; set; } = "UIPanorama.round_report_round_lost";
}

/// <summary>Disabled days per map (configs/day_configs/&lt;map&gt;.txt in the original).</summary>
public sealed class DayMapConfig
{
    public List<string> Disabled { get; set; } = new(); // "<type>-<name>"
}

/// <summary>
/// Port of UltJB_API_Days.sp (v1.35): day registry, warden day menu (!day / !warday / !freeday) with the original
/// selection rules (15 s after the first warden, warden twice this map, a round between days), team play or
/// free-for-all, warday freeze with countdown, weapon rules per day flags, freeday slaying, per-map enabled days
/// (!de / !daysedit) and the forwards. Days themselves live in Days/*.cs.
/// </summary>
public sealed partial class DaysModule : JbModule, IDayService
{
    private DaysConfig Cfg => Section<DaysConfig>("Days");

    public override string Name => "Days";
    public override int Order => 11;
    public override bool Enabled => Cfg.Enabled;

    private const int RoundDayEnabled = -1;

    private readonly List<DayDefinition> _days = new();
    private DayDefinition? _current;
    private bool _ffa;
    private int _wardenCountForRound;
    private float _wardenSelectedTime;
    private readonly int[] _roundsAfterDay = { RoundDayEnabled, RoundDayEnabled };
    private int _freezeTime;
    private int _freezeCountdown;
    private Timer? _freezeTimer;
    private readonly HashSet<uint> _spawnedByDay = new();
    private DayMapConfig _mapConfig = new();
    private DayMapConfig _globalConfig = new();
    private string _map = "";

    // ================================================================== lifecycle

    public override void OnLoad(bool hotReload)
    {
        Jb.Days = this;
        JbEvents.WardenSelected += OnWardenSelected;
        JbEvents.SettingsSpawnPost += OnSpawnPost;
        WeaponService.SetPickupRule(Name, PickupRule);
        WeaponService.SetDropRule(Name, (_, _) => CanDropWeapon());
        Plugin.AddCommandListener("buy", OnBuy, HookMode.Pre);
        Plugin.AddCommandListener("autobuy", OnBuy, HookMode.Pre);
        Plugin.AddCommandListener("rebuy", OnBuy, HookMode.Pre);
        Plugin.RegisterEventHandler<EventWeaponReload>(OnWeaponReload);
        // UltJB_API_Days ended the day on cs_pre_restart / cs_match_end_restart (mp_restartgame skips round_end).
        Plugin.RegisterEventHandler<EventCsPreRestart>((_, _) => { EndDay(); return HookResult.Continue; });
        Plugin.RegisterEventHandler<EventCsMatchEndRestart>((_, _) => { EndDay(); return HookResult.Continue; });
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
        if (hotReload) _map = Server.MapName; // Cold start: no map yet, and Server.* reads the engine globals natively (crashes the server); OnMapStart covers it.
    }

    public override void OnAllModulesLoaded(bool hotReload)
    {
        _days.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        LoadDayConfig();
    }

    public override void OnUnload(bool hotReload)
    {
        JbEvents.WardenSelected -= OnWardenSelected;
        JbEvents.SettingsSpawnPost -= OnSpawnPost;
        WeaponService.RemovePickupRule(Name);
        WeaponService.RemoveDropRule(Name);
        EndDay();
    }

    public override void OnMapStart(string mapName)
    {
        _map = mapName;
        _wardenCountForRound = 0;
        _roundsAfterDay[0] = _roundsAfterDay[1] = RoundDayEnabled;
        foreach (var d in _days) d.Enabled = true;
        LoadDayConfig();
    }

    public override void OnMapEnd() => EndDay();

    public override void OnRoundStart()
    {
        EndDay(); // a day forced during the round-end delay, or a restart that skipped round_end, never carries over
        _wardenCountForRound = 0;
        _spawnedByDay.Clear();
        for (var i = 0; i < _roundsAfterDay.Length; i++)
        {
            if (_roundsAfterDay[i] == RoundDayEnabled) continue;
            _roundsAfterDay[i]++;
            if (_roundsAfterDay[i] >= 3) _roundsAfterDay[i] = RoundDayEnabled;
        }
    }

    public override void OnRoundEnd(CsTeam winner, int reason) => EndDay();

    private void OnWardenSelected(JbPlayer warden)
    {
        _wardenCountForRound++;
        if (_wardenCountForRound == 1) _wardenSelectedTime = Server.CurrentTime;
    }

    // ================================================================== registry / queries

    public IReadOnlyList<DayDefinition> All => _days;

    public int Register(DayDefinition definition)
    {
        if (_days.Any(d => d.Type == definition.Type && string.Equals(d.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Logger.LogError("Day [{Name}] is already registered.", definition.Name);
            return -1;
        }

        definition.AssignId(_days.Count == 0 ? 1 : _days.Max(d => d.Id) + 1);
        if (definition.FreezeTime == 0) definition.FreezeTime = Cfg.WardayFreezeTime;
        // Original default FREEZE_TEAM_PRISONERS; a day may change FreezeTeams/FreezeTime after registering.
        if (definition.FreezeTeams == FreezeTeams.None) definition.FreezeTeams = FreezeTeams.Prisoners;
        if ((definition.Flags & DayFlags.ForceFreeForAll) != 0) definition.AllowFreeForAll = true;
        _days.Add(definition);
        _days.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return definition.Id;
    }

    public bool IsInProgress => _current != null;
    public DayDefinition? Current => _current;
    public DayType CurrentType => _current?.Type ?? DayType.None;
    public bool IsFreeForAll => _ffa;
    public int FreezeTimeRemaining => _freezeTime - _freezeCountdown;

    public void SetEntityAsSpawnedByDay(CEntityInstance entity, bool spawnedByDay = true)
    {
        if (entity == null || !entity.IsValid) return;
        if (spawnedByDay) _spawnedByDay.Add(entity.EntityHandle.Raw);
        else _spawnedByDay.Remove(entity.EntityHandle.Raw);
    }

    public bool IsEntitySpawnedByDay(CEntityInstance entity) => entity is { IsValid: true } && _spawnedByDay.Contains(entity.EntityHandle.Raw);

    private int RebelPointCount => (Jb.LastRequests as LastRequestModule)?.RebelPointCount ?? 0;

    private bool HasEnoughRebelPointsForFfa => RebelPointCount >= Cfg.FreeForAllMinRebelPoints;

    private bool CanDayBeFreeForAll(DayDefinition day) =>
        HasEnoughRebelPointsForFfa && ((day.Flags & DayFlags.ForceFreeForAll) != 0 || day.AllowFreeForAll);

    // ================================================================== commands + menus

    [ConsoleCommand("css_day", "sm_day - open the days menu (warden)")]
    public void CmdDay(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null && CanUseDayMenu(caller)) DisplayDayTypeMenu(caller);
    }

    [ConsoleCommand("css_warday", "sm_warday - open the warday menu (warden)")]
    public void CmdWarday(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !CanUseDayMenu(caller) || !CanSelectWarday(caller)) return;
        DisplayDayMenu(caller, DayType.Warday);
    }

    [ConsoleCommand("css_freeday", "sm_freeday - open the freeday menu (warden)")]
    public void CmdFreeday(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !CanUseDayMenu(caller) || !CanSelectFreeday(caller)) return;
        DisplayDayMenu(caller, DayType.Freeday);
    }

    public void OpenMenu(CCSPlayerController player, DayType? type = null, bool adminForce = false)
    {
        if (!adminForce && !CanUseDayMenu(player)) return;
        if (adminForce && IsInProgress)
        {
            TellBoth(player, "days.already");
            return;
        }

        if (type is { } t) DisplayDayMenu(player, t, adminForce);
        else DisplayDayTypeMenu(player, adminForce);
    }

    private static void TellBoth(CCSPlayerController c, string key, params object?[] args)
    {
        var text = Lang.T(key, args);
        Chat.Print(c, text);
        Chat.PrintToConsole(c, Colors.Strip(text));
    }

    private bool IsWarden(CCSPlayerController c) => Jb.Warden.IsWarden(c);

    private bool CanUseDayMenu(CCSPlayerController c)
    {
        if (!IsWarden(c)) { TellBoth(c, "days.must_be_warden"); return false; }
        if (IsInProgress) { TellBoth(c, "days.already"); return false; }
        if (SelectTimeExpired()) { TellBoth(c, "days.expired"); return false; }
        return true;
    }

    private bool SelectTimeExpired() =>
        !Jb.Warden.HasWarden || Server.CurrentTime > _wardenSelectedTime + Cfg.SelectTime;

    private bool CanSelectFreeday(CCSPlayerController c) => CanSelect(c, DayType.Freeday);
    private bool CanSelectWarday(CCSPlayerController c) => CanSelect(c, DayType.Warday);

    private bool CanSelect(CCSPlayerController c, DayType type)
    {
        if (Cfg.ForceAllowOverride) return true;
        var p = c.Jb();
        if (p == null || Jb.Warden.GetWardenCount(p) < 2) return false;
        var own = (int)type;
        var other = type == DayType.Freeday ? 1 : 0;
        return _roundsAfterDay[own] == RoundDayEnabled && _roundsAfterDay[other] != 1;
    }

    private bool StillAllowed(CCSPlayerController c, bool admin)
    {
        if (admin) return Perms.Has(c, "days");
        if (!IsWarden(c)) return false;
        if (SelectTimeExpired()) { TellBoth(c, "days.expired"); return false; }
        return true;
    }

    private void DisplayDayTypeMenu(CCSPlayerController c, bool admin = false)
    {
        if (!admin && !IsWarden(c)) return;
        var m = new JbMenu(Lang.T("days.menu.title"));
        var free = admin || CanSelectFreeday(c);
        var war = admin || CanSelectWarday(c);
        m.Add(Lang.T(free ? "days.type.freeday" : "days.type.freeday_wait"), x =>
        {
            if (StillAllowed(x, admin)) DisplayDayMenu(x, DayType.Freeday, admin);
        }, free);
        m.Add(Lang.T(war ? "days.type.warday" : "days.type.warday_wait"), x =>
        {
            if (StillAllowed(x, admin)) DisplayDayMenu(x, DayType.Warday, admin);
        }, war);
        m.Open(c);
    }

    private void DisplayDayMenu(CCSPlayerController c, DayType type, bool admin = false)
    {
        if (!admin && !IsWarden(c)) return;
        if (type == DayType.Warday && !Jb.Cells.DoExist)
        {
            c.PrintToChat(" " + Lang.T("days.no_cells"));
            DisplayDayTypeMenu(c, admin);
            return;
        }

        var days = _days.Where(d => d.Type == type).ToList();
        if (days.Count == 0)
        {
            c.PrintToChat(" " + Lang.T("days.none"));
            DisplayDayTypeMenu(c, admin);
            return;
        }

        var m = new JbMenu(Lang.T(type == DayType.Warday ? "days.menu.wardays" : "days.menu.freedays"));
        foreach (var d in days)
        {
            var day = d;
            var forcedFfa = (d.Flags & DayFlags.ForceFreeForAll) != 0;
            var usable = d.Enabled && (!forcedFfa || HasEnoughRebelPointsForFfa) && (d.CanStart?.Invoke() ?? true);
            m.Add(Hud.Escape(d.Name), x =>
            {
                if (!StillAllowed(x, admin)) return;
                var force = (day.Flags & DayFlags.ForceFreeForAll) != 0;
                if (!force && CanDayBeFreeForAll(day))
                {
                    DisplayFfaMenu(x, day, admin);
                    return;
                }

                StartDay(day, x.Jb(), force);
            }, usable);
        }

        m.Open(c);
    }

    private void DisplayFfaMenu(CCSPlayerController c, DayDefinition day, bool admin)
    {
        var m = new JbMenu(Lang.T("days.menu.mode"));
        m.Add(Lang.T("days.mode.team"), x =>
        {
            if (StillAllowed(x, admin)) StartDay(day, x.Jb(), false);
        });
        m.Add(Lang.T("days.mode.ffa"), x =>
        {
            if (StillAllowed(x, admin)) StartDay(day, x.Jb(), true);
        });
        m.Open(c);
    }

    // ================================================================== start / end

    public bool StartDay(DayDefinition day, JbPlayer? starter, bool freeForAll = false)
    {
        var c = starter?.Controller;
        if (IsInProgress)
        {
            c?.PrintToChat(" " + Lang.T("days.already_plain"));
            return false;
        }

        if (!Round.InProgress)
        {
            c?.PrintToChat(" " + Lang.T("days.not_in_round"));
            return false;
        }

        if (!_days.Contains(day)) return false;
        if (day.CanStart != null && !day.CanStart())
        {
            c?.PrintToChat(" " + Lang.T("days.unavailable", day.Name));
            return false;
        }

        if (!CanDayBeFreeForAll(day))
        {
            if ((day.Flags & DayFlags.ForceFreeForAll) != 0)
            {
                c?.PrintToChat(" " + Lang.T("days.ffa_forced_unavailable"));
                return false;
            }

            if (freeForAll)
            {
                c?.PrintToChat(" " + Lang.T("days.ffa_not_allowed"));
                freeForAll = false;
            }
        }

        if (Jb.Cells.HaveOpened && day.Type == DayType.Warday)
        {
            c?.PrintToChat(" " + Lang.T("days.cells_open"));
            return false;
        }

        var typeName = Lang.T(day.Type == DayType.Warday ? "days.type.warday" : "days.type.freeday");
        Chat.TellAll("days.started", starter?.Name ?? "Console", typeName, day.Name);
        _current = day;
        _roundsAfterDay[(int)day.Type] = 0;
        _ffa = freeForAll;
        if (freeForAll)
        {
            Cvars.SetTemp("mp_teammates_are_enemies", "1");
            Chat.TellAll("days.ffa_warning");
            if (Cfg.FreeForAllSound.Length > 0) Sound.PlayAll(Cfg.FreeForAllSound);
        }

        foreach (var p in Players.Connected())
        {
            var pc = p.Controller!;
            if (!pc.IsAliveP() && pc.Team >= CsTeam.Terrorist) pc.Respawn();
            var pawn = pc.PawnOrNull();
            if (pawn != null)
            {
                Jb.LastRequests.SetHealth(p, Math.Max(1, pawn.MaxHealth));
                pc.SetArmor(0, false);
            }
        }

        try
        {
            day.OnStart(starter);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Day {Name} failed to start", day.Name);
            c?.PrintToChat(" " + Lang.T("days.load_error"));
            EndDay();
            return false;
        }

        // Jihad bombs are put away by the Jihad module on this event (and given back when the day ends).
        JbEvents.RaiseDayStarted(starter, day.Type, freeForAll);
        InitDayType(starter, day);
        Jb.Log.LogEvent(Lang.T("days.log_started", starter?.Name ?? "Console", typeName, day.Name), starter, null, LogType.Any);
        return true;
    }

    private void InitDayType(JbPlayer? starter, DayDefinition day)
    {
        if ((day.Flags & DayFlags.KillWorldWeapons) != 0) KillWorldWeapons();
        if ((day.Flags & DayFlags.StripPrisonersWeapons) != 0) StripTeam(CsTeam.Terrorist);
        if ((day.Flags & DayFlags.StripGuardsWeapons) != 0) StripTeam(CsTeam.CounterTerrorist);
        if (day.Type != DayType.Warday) return;

        var freezeTime = _ffa ? 5 : day.FreezeTime;
        var freezeTeams = _ffa ? FreezeTeams.Guards | FreezeTeams.Prisoners : day.FreezeTeams;
        if (!Jb.Cells.HaveOpened) Jb.Cells.ForceOpen();
        _freezeTime = freezeTime;
        _freezeCountdown = 0;
        if (freezeTeams != FreezeTeams.None && freezeTime > 0)
        {
            FreezePlayers(true, (freezeTeams & FreezeTeams.Prisoners) != 0, (freezeTeams & FreezeTeams.Guards) != 0);
            JbEvents.RaiseWardayStarted(starter);
            StartFreezeTimer();
        }
        else
        {
            JbEvents.RaiseWardayStarted(starter);
            FreezeEnded(day);
        }

        if (_ffa) TeleportToFreeForAllPoints();
    }

    public void EndDay()
    {
        var day = _current;
        if (day == null) return;
        if (_ffa) Cvars.Restore("mp_teammates_are_enemies");
        _freezeTime = 0;
        FreezePlayers(false);
        StopFreezeTimer();
        try
        {
            day.OnEnd?.Invoke(null);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "There was an error ending day [{Name}]", day.Name);
            Chat.PrintAll(Lang.T("days.end_error", day.Name), false);
        }

        _current = null;
        _ffa = false;
        foreach (var p in Players.Connected())
        {
            var pawn = p.Controller?.PawnOrNull();
            if (pawn == null) continue;
            try
            {
                pawn.EntitySpottedState.Spotted = false;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_entitySpottedState");
            }
            catch
            {
                // radar is cosmetic
            }
        }

        JbEvents.RaiseDayEnded();
    }

    // ================================================================== warday freeze

    private void StartFreezeTimer()
    {
        StopFreezeTimer();
        _freezeCountdown = 0;
        ShowUnfreezeCountdown();
        _freezeTimer = Plugin.AddTimer(1f, () =>
        {
            _freezeCountdown++;
            if (_freezeCountdown < _freezeTime)
            {
                ShowUnfreezeCountdown();
                return;
            }

            EndFreezeTimer(true);
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StopFreezeTimer()
    {
        try { _freezeTimer?.Kill(); }
        catch { /* already stopped */ }
        _freezeTimer = null;
    }

    private void ShowUnfreezeCountdown()
    {
        var html = Lang.T("days.unfreeze_in", _freezeTime - _freezeCountdown);
        foreach (var c in Players.Humans()) Hud.Set(c, "days.freeze", html, 60, 2);
    }

    public void ForceEndFreezeTime() => EndFreezeTimer(false);

    private void EndFreezeTimer(bool fromTimer)
    {
        _freezeCountdown = _freezeTime;
        if (_freezeTimer == null) return;
        StopFreezeTimer();
        FreezePlayers(false);
        if (_current == null) return;
        FreezeEnded(_current);
        foreach (var c in Players.Humans()) Hud.Set(c, "days.freeze", Lang.T("days.unfrozen"), 60, 4);
    }

    private void FreezeEnded(DayDefinition day)
    {
        try { day.OnFreezeEnd?.Invoke(); }
        catch (Exception ex) { Logger.LogError(ex, "Day {Name} freeze-end callback failed", day.Name); }
        JbEvents.RaiseWardayFreezeEnded();
    }

    private static void FreezePlayers(bool freeze, bool prisoners = true, bool guards = true)
    {
        foreach (var c in Players.Alive())
        {
            if (!(prisoners && c.Team == CsTeam.Terrorist || guards && c.Team == CsTeam.CounterTerrorist)) continue;
            if (freeze)
            {
                Fx.Fade(c, Color.FromArgb(255, 0, 0, 0), 0f, 0f, Fx.FadeStayOut | Fx.FadePurge);
                c.SetMoveType(MoveType_t.MOVETYPE_NONE);
            }
            else
            {
                Fx.Fade(c, Color.FromArgb(255, 0, 0, 0), 0f, 0f, Fx.FadePurge);
                if (c.PawnOrNull()?.MoveType == MoveType_t.MOVETYPE_NONE) c.SetMoveType(MoveType_t.MOVETYPE_WALK);
            }
        }
    }

    // ================================================================== weapons

    private void KillWorldWeapons()
    {
        foreach (var e in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("game_player_equip")) e.AcceptInput("Kill");
        foreach (var w in Utilities.FindAllEntitiesByDesignerName<CBasePlayerWeapon>("weapon_"))
        {
            if (!w.IsValid || w.OwnerEntity.IsValid) continue;
            w.AcceptInput("Kill");
        }
    }

    private static void StripTeam(CsTeam team)
    {
        foreach (var c in Players.AliveOnTeam(team))
            if (c.Jb() is { } p) Jb.LastRequests.StripWeapons(p);
    }

    private bool PickupRule(CCSPlayerController player, string weapon, AcquireMethod method)
    {
        var day = _current;
        if (day == null) return true;
        if (player.PawnOrNull()?.MoveType == MoveType_t.MOVETYPE_NONE) return false;
        var p = player.Jb();
        if (p != null && Jb.WeaponSelection.IsGettingItem(p)) return true;
        if (method != AcquireMethod.PickUp)
            return (day.Flags & DayFlags.AllowWeaponPickups) != 0 && (day.Flags & DayFlags.DisableWeaponBuying) == 0 && !_ffa;
        if ((day.Flags & DayFlags.AllowWeaponPickups) != 0) return true;
        var entity = FindPickupEntity(weapon);
        if ((day.Flags & DayFlags.AllowWeaponPickupsFromDay) != 0 && entity != null && IsEntitySpawnedByDay(entity)) return true;
        // Original deleted the weapon the player tried to pick up.
        if (entity != null) Server.NextFrame(() => { if (entity.IsValid && !entity.OwnerEntity.IsValid) entity.AcceptInput("Kill"); });
        return false;
    }

    private static CBasePlayerWeapon? FindPickupEntity(string weapon)
    {
        var item = WeaponService.CurrentItem;
        if (item == IntPtr.Zero) return null;
        foreach (var w in Utilities.FindAllEntitiesByDesignerName<CBasePlayerWeapon>(weapon))
            if (w.IsValid && w.AttributeManager.Item.Handle == item) return w;
        return null;
    }

    private HookResult OnBuy(CCSPlayerController? player, CommandInfo info)
    {
        var day = _current;
        if (day == null || player == null || !player.IsValid) return HookResult.Continue;
        if (player.PawnOrNull()?.MoveType == MoveType_t.MOVETYPE_NONE) return HookResult.Handled;
        if ((day.Flags & DayFlags.AllowWeaponPickups) == 0) return HookResult.Handled;
        if ((day.Flags & DayFlags.DisableWeaponBuying) != 0 || _ffa) return HookResult.Handled;
        return HookResult.Continue;
    }

    private bool CanDropWeapon() => _current == null || (_current.Flags & DayFlags.AllowWeaponDrops) != 0;

    /// <summary>Infinite ammo on reload. Faithful to the original check, which only worked when a day had both flags.</summary>
    private HookResult OnWeaponReload(EventWeaponReload ev, GameEventInfo info)
    {
        var day = _current;
        var c = ev.Userid;
        if (day == null || c == null || !c.IsValid) return HookResult.Continue;
        const DayFlags both = DayFlags.GiveGuardsInfiniteAmmo | DayFlags.GivePrisonersInfiniteAmmo;
        if ((day.Flags & both) != both) return HookResult.Continue;
        var give = _ffa ||
                   c.Team == CsTeam.CounterTerrorist && (day.Flags & DayFlags.GiveGuardsInfiniteAmmo) != 0 ||
                   c.Team == CsTeam.Terrorist && (day.Flags & DayFlags.GivePrisonersInfiniteAmmo) != 0;
        if (!give) return HookResult.Continue;
        Server.NextFrame(() =>
        {
            var w = c.IsValid ? c.ActiveWeapon() : null;
            if (w == null || Weapons.IsKnife(w.DesignerName) || Weapons.IsGrenade(w.DesignerName)) return;
            w.ReserveAmmo[0] = Math.Max(w.ReserveAmmo[0], 500);
            Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_pReserveAmmo");
        });
        return HookResult.Continue;
    }

    // ================================================================== spawns, deaths, radar

    private void OnSpawnPost(JbPlayer player)
    {
        var day = _current;
        var c = player.Controller;
        if (day == null || c == null) return;
        if (c.Team == CsTeam.Terrorist)
        {
            if ((day.Flags & DayFlags.StripPrisonersWeapons) != 0) Jb.LastRequests.StripWeapons(player);
            if (_freezeTimer != null) c.SetMoveType(MoveType_t.MOVETYPE_NONE);
        }
        else if (c.Team == CsTeam.CounterTerrorist)
        {
            if ((day.Flags & DayFlags.StripGuardsWeapons) != 0) Jb.LastRequests.StripWeapons(player);
        }

        JbEvents.RaiseDaySpawnPost(player);
    }

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev) => CheckSlayRemainingFreedays();

    /// <summary>When only freeday prisoners (and no other prisoners) are left - or in FFA fewer than 2 others - slay them.</summary>
    private void CheckSlayRemainingFreedays()
    {
        int guards = 0, prisoners = 0;
        var freedays = new List<CCSPlayerController>();
        foreach (var c in Players.Alive())
        {
            if (c.Team == CsTeam.Terrorist)
            {
                prisoners++;
                if (c.Jb() is { } p && (Jb.LastRequests.GetFlags(p) & LrFlags.Freeday) != 0) freedays.Add(c);
            }
            else if (c.Team == CsTeam.CounterTerrorist)
            {
                guards++;
            }
        }

        if (freedays.Count == 0) return;
        var notInFreeday = prisoners - freedays.Count;
        if (!_ffa && notInFreeday > 0) return;
        notInFreeday += guards;
        if (_ffa && notInFreeday >= 2) return;
        Chat.PrintAll(Lang.T("days.slay_freedays"), false);
        foreach (var c in freedays) c.CommitSuicide(false, true);
    }

    private void OnTick()
    {
        var day = _current;
        if (day == null) return;
        var hidePrisonersRadar = (day.Flags & DayFlags.DisablePrisonersRadar) != 0;
        var hideGuardsRadar = (day.Flags & DayFlags.DisableGuardsRadar) != 0;
        if (!_ffa && !hidePrisonersRadar && !hideGuardsRadar) return;
        foreach (var c in Players.Alive())
        {
            var unspot = _ffa || c.Team == CsTeam.CounterTerrorist && hidePrisonersRadar || c.Team == CsTeam.Terrorist && hideGuardsRadar;
            if (!unspot) continue;
            var pawn = c.PawnOrNull();
            if (pawn == null) continue;
            try
            {
                if (!pawn.EntitySpottedState.Spotted) continue;
                pawn.EntitySpottedState.Spotted = false;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_entitySpottedState");
            }
            catch
            {
                // radar is cosmetic
            }
        }
    }

    // ================================================================== free-for-all teleport

    /// <summary>Spread players over the rebel points: the two furthest apart first, then always the next furthest point.</summary>
    private void TeleportToFreeForAllPoints()
    {
        var points = (Jb.LastRequests as LastRequestModule)?.RebelPointList ?? Array.Empty<float[]>();
        var pts = points.Where(p => p.Length >= 3).Select(p => new Vector(p[0], p[1], p[2])).ToList();
        if (pts.Count < 2)
        {
            EndDay();
            return;
        }

        var chosen = new List<Vector>();
        var (bestA, bestB, bestD) = (0, 1, -1f);
        for (var i = 0; i < pts.Count; i++)
        for (var j = i + 1; j < pts.Count; j++)
        {
            var d = VectorUtil.Distance(pts[i], pts[j]);
            if (d > bestD) (bestA, bestB, bestD) = (i, j, d);
        }

        chosen.Add(pts[bestA]);
        chosen.Add(pts[bestB]);
        var players = Players.Alive().OrderBy(_ => Random.Shared.Next()).ToList();
        for (var n = 0; n < players.Count; n++)
        {
            Vector target;
            if (n < 2)
            {
                target = chosen[n];
            }
            else
            {
                target = pts.OrderByDescending(p => chosen.Min(c => VectorUtil.Distance(p, c))).First();
                chosen.Add(target);
            }

            var c = players[n];
            var yaw = (c.PawnOrNull()?.EyeAngles.Y ?? 0f) + Random.Shared.NextSingle() * 360f;
            c.TeleportTo(target, new QAngle(0, yaw, 0), new Vector(0, 0, 0));
        }
    }

    // ================================================================== !daysedit (per-map enabled days)

    [ConsoleCommand("css_de", "sm_de - turn days on/off (this map or every map)")]
    [ConsoleCommand("css_daysedit", "sm_daysedit - turn days on/off (this map or every map)")]
    public void CmdDaysEdit(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        if (!Perms.Has(caller, "days.edit")) { Chat.Tell(caller, "core.no_access"); return; }
        OpenEditMenu(caller);
    }

    /// <summary>The admin on/off menu (also in !admin > Jailbreak).</summary>
    public void OpenEditMenu(CCSPlayerController c, JbMenu? parent = null)
    {
        var m = new JbMenu(Lang.T("days.menu.title")) { Parent = parent, Subtitle = Lang.T("toggle.help") };
        m.Add(Lang.T("days.type.freeday"), x => DisplayEditMenu(x, DayType.Freeday, m));
        m.Add(Lang.T("days.type.warday"), x => DisplayEditMenu(x, DayType.Warday, m));
        m.Open(c);
    }

    private static string DayKey(DayDefinition d) => $"{(int)d.Type}-{d.Name}";

    private Availability StateOf(DayDefinition d) => AvailabilityRules.Of(_mapConfig.Disabled, _globalConfig.Disabled, DayKey(d));

    private void DisplayEditMenu(CCSPlayerController c, DayType type, JbMenu? parent = null)
    {
        var m = new JbMenu(Lang.T(type == DayType.Warday ? "days.edit.wardays" : "days.edit.freedays")) { Parent = parent, Subtitle = Lang.T("toggle.help") };
        foreach (var d in _days.Where(d => d.Type == type))
        {
            var day = d;
            m.Add($"{Lang.T(AvailabilityRules.TagKey(StateOf(d)))} {Hud.Escape(d.Name)}", x =>
            {
                if (!Perms.Has(x, "days.edit")) return;
                var next = AvailabilityRules.Next(StateOf(day));
                AvailabilityRules.Apply(_mapConfig.Disabled, _globalConfig.Disabled, DayKey(day), next);
                day.Enabled = next == Availability.On;
                var state = Colors.Strip(Lang.T(AvailabilityRules.TagKey(next)));
                x.PrintToChat(" " + Lang.T("toggle.changed", day.Name, state));
                Jb.Log.LogEvent($"{x.PlayerName} set day {day.Name} to {state}", x.Jb(), null, LogType.Admin);
                SaveDayConfig(x);
                DisplayEditMenu(x, type, parent);
            });
        }

        m.Open(c);
    }

    private void SaveDayConfig(CCSPlayerController c)
    {
        try
        {
            Storage.SaveMapDoc("days", _map.ToLowerInvariant(), _mapConfig);
            Storage.SaveMapDoc("days", AvailabilityRules.GlobalDoc, _globalConfig);
            c.PrintToChat(" " + Lang.T("days.edit.saved"));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Saving the day config failed");
            c.PrintToChat(" " + Lang.T("days.edit.save_error"));
        }
    }

    private void LoadDayConfig()
    {
        try { _mapConfig = Storage.LoadMapDoc<DayMapConfig>("days", (_map ?? "").ToLowerInvariant()); }
        catch { _mapConfig = new DayMapConfig(); }
        try { _globalConfig = Storage.LoadMapDoc<DayMapConfig>("days", AvailabilityRules.GlobalDoc); }
        catch { _globalConfig = new DayMapConfig(); }
        foreach (var d in _days) d.Enabled = StateOf(d) == Availability.On;
    }
}
