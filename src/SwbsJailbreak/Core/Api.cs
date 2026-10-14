using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Core;

// =====================================================================================================
// Cross-module contracts. Each mirrors the natives of one original UltJB SourceMod plugin. The owning
// module assigns itself to the matching Jb.* slot in OnLoad; until then a harmless null object answers.
// =====================================================================================================

/// <summary>Service locator for jailbreak subsystems.</summary>
public static class Jb
{
    public static IWardenService Warden { get; set; } = NullServices.Instance;
    public static ICellService Cells { get; set; } = NullServices.Instance;
    public static ILastRequestService LastRequests { get; set; } = NullServices.Instance;
    public static IDayService Days { get; set; } = NullServices.Instance;
    public static IEffectService Effects { get; set; } = NullServices.Instance;
    public static IWeaponSelectionService WeaponSelection { get; set; } = NullServices.Instance;
    public static ISettingsService Settings { get; set; } = NullServices.Instance;
    public static ILastGuardService LastGuard { get; set; } = NullServices.Instance;
    public static ILogService Log { get; set; } = NullServices.Instance;
    public static IJihadService Jihad { get; set; } = NullServices.Instance;
    public static IPlayerModelService Models { get; set; } = NullServices.Instance;
    public static ITeamService Teams { get; set; } = NullServices.Instance;
    public static IPointsService Points { get; set; } = NullServices.Instance;
    public static IMuteService Mute { get; set; } = NullServices.Instance;
    public static IRebelService Rebels { get; set; } = NullServices.Instance;
    public static IZoneService Zones { get; set; } = NullZoneService.Instance;
    public static ICrewService Crews { get; set; } = NullCrewService.Instance;

    internal static void Reset()
    {
        var n = NullServices.Instance;
        Warden = n; Cells = n; LastRequests = n; Days = n; Effects = n; WeaponSelection = n; Settings = n;
        LastGuard = n; Log = n; Jihad = n; Models = n; Teams = n; Points = n; Mute = n; Rebels = n;
        Zones = NullZoneService.Instance;
        Crews = NullCrewService.Instance;
    }
}

/// <summary>Inter-module events (the original plugins' global forwards).</summary>
public static class JbEvents
{
    // ---- warden (ultjb_warden.inc) ----
    public static event Action<JbPlayer>? WardenSelected;
    public static event Action<JbPlayer>? WardenRemoved;
    public static event Action<JbPlayer>? WardenDeath;

    // ---- cells (ultjb_cell_doors.inc) ----
    public static event Action? CellsRoundStartReady;
    public static event Action? CellsOpened;

    // ---- last guard ----
    /// <summary>Return false from any handler to cancel activation (UltJB_LastGuard_OnActivated_Pre).</summary>
    public static event Func<JbPlayer, bool>? LastGuardActivatingPre;
    public static event Action<JbPlayer>? LastGuardActivated;

    // ---- last request ----
    public static event Action<JbPlayer>? LastRequestInitialized;
    public static event Action<JbPlayer, LrFlags>? LastRequestStarted;
    public static event Action<JbPlayer, JbPlayer?>? LastRequestEnded;

    // ---- days ----
    public static event Action<JbPlayer?, DayType, bool>? DayStarted;
    public static event Action<JbPlayer?>? WardayStarted;
    public static event Action? WardayFreezeEnded;
    public static event Action? DayEnded;
    public static event Action<JbPlayer>? DaySpawnPost;

    // ---- misc ----
    public static event Action<JbPlayer>? PlayerModelsApplied;
    public static event Action<JbPlayer>? SettingsSpawnPost;
    public static event Action<JbPlayer>? RebelMarked;
    public static event Action<JbPlayer>? FreedayGranted;

    public static void RaiseWardenSelected(JbPlayer p) => Invoke(WardenSelected, p);
    public static void RaiseWardenRemoved(JbPlayer p) => Invoke(WardenRemoved, p);
    public static void RaiseWardenDeath(JbPlayer p) => Invoke(WardenDeath, p);
    public static void RaiseCellsRoundStartReady() => Invoke(CellsRoundStartReady);
    public static void RaiseCellsOpened() => Invoke(CellsOpened);

    public static bool RaiseLastGuardActivatingPre(JbPlayer p)
    {
        if (LastGuardActivatingPre == null) return true;
        foreach (var d in LastGuardActivatingPre.GetInvocationList())
        {
            try
            {
                if (d is Func<JbPlayer, bool> f && !f(p)) return false;
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        return true;
    }

    public static void RaiseLastGuardActivated(JbPlayer p) => Invoke(LastGuardActivated, p);
    public static void RaiseLastRequestInitialized(JbPlayer p) => Invoke(LastRequestInitialized, p);

    public static void RaiseLastRequestStarted(JbPlayer p, LrFlags flags)
    {
        if (LastRequestStarted == null) return;
        foreach (var d in LastRequestStarted.GetInvocationList())
        {
            try { ((Action<JbPlayer, LrFlags>)d)(p, flags); }
            catch (Exception ex) { LogError(ex); }
        }
    }

    public static void RaiseLastRequestEnded(JbPlayer prisoner, JbPlayer? opponent)
    {
        if (LastRequestEnded == null) return;
        foreach (var d in LastRequestEnded.GetInvocationList())
        {
            try { ((Action<JbPlayer, JbPlayer?>)d)(prisoner, opponent); }
            catch (Exception ex) { LogError(ex); }
        }
    }

    public static void RaiseDayStarted(JbPlayer? starter, DayType type, bool freeForAll)
    {
        if (DayStarted == null) return;
        foreach (var d in DayStarted.GetInvocationList())
        {
            try { ((Action<JbPlayer?, DayType, bool>)d)(starter, type, freeForAll); }
            catch (Exception ex) { LogError(ex); }
        }
    }

    public static void RaiseWardayStarted(JbPlayer? starter)
    {
        if (WardayStarted == null) return;
        foreach (var d in WardayStarted.GetInvocationList())
        {
            try { ((Action<JbPlayer?>)d)(starter); }
            catch (Exception ex) { LogError(ex); }
        }
    }

    public static void RaiseWardayFreezeEnded() => Invoke(WardayFreezeEnded);
    public static void RaiseDayEnded() => Invoke(DayEnded);
    public static void RaiseDaySpawnPost(JbPlayer p) => Invoke(DaySpawnPost, p);
    public static void RaisePlayerModelsApplied(JbPlayer p) => Invoke(PlayerModelsApplied, p);
    public static void RaiseSettingsSpawnPost(JbPlayer p) => Invoke(SettingsSpawnPost, p);
    public static void RaiseRebelMarked(JbPlayer p) => Invoke(RebelMarked, p);
    public static void RaiseFreedayGranted(JbPlayer p) => Invoke(FreedayGranted, p);

    private static void Invoke(Action? a)
    {
        if (a == null) return;
        foreach (var d in a.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch (Exception ex) { LogError(ex); }
        }
    }

    private static void Invoke(Action<JbPlayer>? a, JbPlayer p)
    {
        if (a == null) return;
        foreach (var d in a.GetInvocationList())
        {
            try { ((Action<JbPlayer>)d)(p); }
            catch (Exception ex) { LogError(ex); }
        }
    }

    private static void LogError(Exception ex) =>
        Microsoft.Extensions.Logging.LoggerExtensions.LogError(JailbreakPlugin.Instance!.Logger, ex, "JbEvents handler failed");

    internal static void ClearAll()
    {
        WardenSelected = WardenRemoved = WardenDeath = null;
        CellsRoundStartReady = CellsOpened = null;
        LastGuardActivatingPre = null;
        LastGuardActivated = null;
        LastRequestInitialized = null;
        LastRequestStarted = null;
        LastRequestEnded = null;
        DayStarted = null;
        WardayStarted = null;
        WardayFreezeEnded = DayEnded = null;
        DaySpawnPost = PlayerModelsApplied = SettingsSpawnPost = RebelMarked = FreedayGranted = null;
    }
}

// ----------------------------------------------------------------------------------------------- warden

public interface IWardenService
{
    JbPlayer? Warden { get; }
    bool HasWarden { get; }
    bool IsWarden(CCSPlayerController? player);

    /// <summary>How many times the player has been warden this map (UltJB_Warden_GetClientWardenCount).</summary>
    int GetWardenCount(JbPlayer player);

    /// <summary>Make the player warden (admin force when admin != null). Returns false if not allowed.</summary>
    bool TrySetWarden(JbPlayer player, CCSPlayerController? admin = null);

    void RemoveWarden(bool announce = true);

    /// <summary>Warden menu ring marker (UltJB_WardenMenu_CreateRing).</summary>
    void CreateRing(JbPlayer owner, int ringIndex, Vector position, float radius);
}

// ----------------------------------------------------------------------------------------------- cells

public interface ICellService
{
    bool DoExist { get; }
    bool HaveOpened { get; }
    bool ForceOpen(CCSPlayerController? by = null);
    bool ForceClose(CCSPlayerController? by = null);
    bool AddEntityAsDoor(CEntityInstance entity);
    bool RemoveEntityFromBeingDoor(CEntityInstance entity);
}

// ----------------------------------------------------------------------------------------------- last request

[Flags]
public enum LrFlags
{
    None = 0,
    LastPrisonerOnlyCanUse = 1 << 0,
    DontEndOnPrisonerDeath = 1 << 1,
    DontEndOnGuardDeath = 1 << 2,
    DontEndOnGuardLeave = 1 << 3,
    DontEndOnRoundEnd = 1 << 4,
    DontEndOnGuardSelectFail = 1 << 5,
    AllowWeaponPickups = 1 << 6,
    DontAllowDamagingOpponent = 1 << 7,
    AllowWeaponDrops = 1 << 8,
    DontTeleportToOpponent = 1 << 9,
    Freeday = 1 << 10,
    NoRadar = 1 << 11,
    NoBeacon = 1 << 12,
    TempInvincible = 1 << 13,
    ShowAllGuardsOnRadar = 1 << 14,
    RandomTeleportLocation = 1 << 15,
    Rebel = 1 << 16,

    /// <summary>ADDITION: prisoners in this LR still see the other prisoners (group LRs such as Knife Tournament).</summary>
    SeeTeammates = 1 << 17
}

[Flags]
public enum SlayTimerFlags
{
    None = 0,
    Guard = 1 << 0,
    Prisoner = 1 << 1,
    Both = Guard | Prisoner
}

public sealed class LastRequestDefinition
{
    public required string Name { get; init; }
    public string Category { get; set; } = "Other";
    public string Description { get; set; } = "";
    public LrFlags Flags { get; set; }

    /// <summary>Called when the prisoner selects this LR (Callback_StartLastRequest).</summary>
    public required Action<JbPlayer> OnStart { get; init; }

    /// <summary>Called when the LR ends for the prisoner; opponent may be null (Callback_EndLastRequest).</summary>
    public Action<JbPlayer, JbPlayer?>? OnEnd { get; init; }

    /// <summary>The prisoner's opponent left the game (Callback_OnOpponentLeftGame).</summary>
    public Action<JbPlayer>? OnOpponentLeft { get; init; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// ADDITION: live check for the prisoner opening the menu; returns the phrase key shown after the greyed-out name
    /// (e.g. "lr.menu.need_two_t") or null when the LR can be picked. Random LRs skip unavailable ones.
    /// </summary>
    public Func<JbPlayer, string?>? Unavailable { get; init; }

    public int Id { get; internal set; } = -1;

    /// <summary>Set by the LR core.</summary>
    public void AssignId(int id) => Id = id;
}

public interface ILastRequestService
{
    /// <summary>Register an LR (UltJB_LR_RegisterLastRequest + SetLastRequestData). Returns its id.</summary>
    int Register(LastRequestDefinition definition);

    IReadOnlyList<LastRequestDefinition> All { get; }

    /// <summary>True if the player is a prisoner in an LR or a guard who is an LR opponent.</summary>
    bool IsInLastRequest(JbPlayer player);

    bool HasStartedLastRequest(JbPlayer prisoner);

    /// <summary>The LR definition the prisoner is playing, or null.</summary>
    LastRequestDefinition? GetActive(JbPlayer prisoner);

    JbPlayer? GetOpponent(JbPlayer player);
    bool SetOpponent(JbPlayer prisoner, JbPlayer opponent);
    LrFlags GetFlags(JbPlayer prisoner);
    void EndLastRequest(JbPlayer prisoner);
    void EndAll();

    /// <summary>Prisoners can currently type !lr (UltJB_LR_CanLastRequest).</summary>
    bool CanLastRequest { get; }

    int NumInitialized { get; }
    int NumStartedIgnore(LrFlags ignoreMask = LrFlags.None);
    int NumStartedContains(LrFlags containsMask);
    int RoundNumber { get; }

    bool DisplayOpponentSelection(JbPlayer prisoner, Action<JbPlayer, JbPlayer> onSuccess, Action<JbPlayer>? onFailed = null);
    void SaveWeapons(JbPlayer player);
    void RestoreWeapons(JbPlayer player);
    void StripWeapons(JbPlayer player, bool saveWeapons = false);
    void SetHealth(JbPlayer player, int health);

    /// <summary>finished(prisoner, opponent, slayed): slayed = null none, prisoner/opponent, or both via SlayTimerResult.</summary>
    bool StartSlayTimer(JbPlayer prisoner, int secondsBeforeSlay = 45, SlayTimerFlags flags = SlayTimerFlags.Both,
        Action<JbPlayer, JbPlayer?, SlayTimerResult>? finished = null);

    bool StopSlayTimer(JbPlayer prisoner);

    /// <summary>Open the LR menu for a prisoner (as if they typed !lr).</summary>
    void OpenMenu(JbPlayer prisoner);
}

public enum SlayTimerResult
{
    None,
    Prisoner,
    Guard,
    Both
}

// ----------------------------------------------------------------------------------------------- days

public enum DayType
{
    None = -1,
    Freeday = 0,
    Warday = 1
}

[Flags]
public enum DayFlags
{
    None = 0,
    AllowWeaponPickups = 1 << 0,
    AllowWeaponDrops = 1 << 1,
    StripGuardsWeapons = 1 << 2,
    GiveGuardsInfiniteAmmo = 1 << 3,
    StripPrisonersWeapons = 1 << 4,
    KillWorldWeapons = 1 << 5,
    GivePrisonersInfiniteAmmo = 1 << 6,
    DisablePrisonersRadar = 1 << 7,
    DisableGuardsRadar = 1 << 8,
    ForceFreeForAll = 1 << 9,
    DisableWeaponBuying = 1 << 10,
    AllowWeaponPickupsFromDay = 1 << 11
}

[Flags]
public enum FreezeTeams
{
    None = 0,
    Guards = 1 << 0,
    Prisoners = 1 << 1
}

public sealed class DayDefinition
{
    public required string Name { get; init; }
    public required DayType Type { get; init; }
    public DayFlags Flags { get; set; }

    /// <summary>starter may be null when started by the server / vote.</summary>
    public required Action<JbPlayer?> OnStart { get; init; }

    public Action<JbPlayer?>? OnEnd { get; init; }

    /// <summary>Warday freeze time ended (Callback_WardayFreezeEnd).</summary>
    public Action? OnFreezeEnd { get; init; }

    public bool Enabled { get; set; } = true;
    public int FreezeTime { get; set; }
    public FreezeTeams FreezeTeams { get; set; }
    public bool AllowFreeForAll { get; set; }
    public string Description { get; set; } = "";

    /// <summary>ADDITION: live availability check (e.g. Crew War needs two crews online); null = always available.</summary>
    public Func<bool>? CanStart { get; init; }
    public int Id { get; internal set; } = -1;

    public void AssignId(int id) => Id = id;
}

public interface IDayService
{
    int Register(DayDefinition definition);
    IReadOnlyList<DayDefinition> All { get; }
    bool IsInProgress { get; }
    DayDefinition? Current { get; }
    DayType CurrentType { get; }
    bool IsFreeForAll { get; }
    int FreezeTimeRemaining { get; }
    void ForceEndFreezeTime();

    /// <summary>Start a day now. starter = warden/admin who chose it (null for server).</summary>
    bool StartDay(DayDefinition day, JbPlayer? starter, bool freeForAll = false);

    void EndDay();

    /// <summary>Mark an entity as created by the day (weapons etc.) so it can be cleaned / allowed for pickup.</summary>
    void SetEntityAsSpawnedByDay(CEntityInstance entity, bool spawnedByDay = true);

    bool IsEntitySpawnedByDay(CEntityInstance entity);

    /// <summary>Open the day selection menu (warden / admin).</summary>
    void OpenMenu(CCSPlayerController player, DayType? type = null, bool adminForce = false);
}

// ----------------------------------------------------------------------------------------------- effects

public sealed class EffectDefinition
{
    public required string Name { get; init; }
    public required Action<JbPlayer, float> OnStart { get; init; }
    public required Action<JbPlayer> OnStop { get; init; }
    public float DefaultData { get; set; }
    public int Id { get; internal set; } = -1;

    public void AssignId(int id) => Id = id;
}

public interface IEffectService
{
    int Register(EffectDefinition definition);
    IReadOnlyList<EffectDefinition> All { get; }
    int GetEffectId(string name);
    float GetDefaultData(int effectId);
    bool StartEffect(JbPlayer player, int effectId, float data = 0f);
    bool StopEffect(JbPlayer player, int effectId);
    void StopAllEffects(JbPlayer player);
    bool HasEffect(JbPlayer player, int effectId);

    bool DisplaySelectionMenu(JbPlayer player, Action<JbPlayer, int> onSelected, Action<JbPlayer>? onFailed = null, int selectTimeOverride = 0);
    bool CancelSelection(JbPlayer player);
}

// ----------------------------------------------------------------------------------------------- weapon selection

public enum WeaponCategory
{
    Knife = 0,
    Pistols,
    Heavy,
    Smgs,
    Rifles,
    Grenades
}

/// <summary>Which weapons a selection menu may offer. Empty = everything.</summary>
public sealed class WeaponFilter
{
    public HashSet<string> Disabled { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<WeaponCategory> DisabledCategories { get; } = new();

    public bool Allows(string weapon, WeaponCategory category) =>
        !DisabledCategories.Contains(category) && !Disabled.Contains(weapon);

    public static WeaponFilter All() => new();
}

public interface IWeaponSelectionService
{
    bool DisplaySelectionMenu(JbPlayer player, Action<JbPlayer, string> onSelected, Action<JbPlayer>? onFailed, WeaponFilter filter);
    bool CancelSelection(JbPlayer player);
    string? GetRandomWeapon(WeaponFilter filter);
    WeaponCategory? GetCategory(string weapon);

    /// <summary>Give a weapon and remember it was given by the jailbreak (UltJB_Weapons_GivePlayerWeapon).</summary>
    CBasePlayerWeapon? GiveWeapon(JbPlayer player, string weapon);

    bool IsWeaponFromLastRequest(CBasePlayerWeapon weapon);
    void MarkWeaponFromLastRequest(CBasePlayerWeapon weapon);
    bool IsGettingItem(JbPlayer player);
}

// ----------------------------------------------------------------------------------------------- settings

public enum RespawnTeam
{
    None = 0,
    Both = 1,
    Prisoners = 2,
    Guards = 3
}

public interface ISettingsService
{
    void StartAutoRespawning(bool resetRespawnLimits = false, int allowedRespawnsPerPlayer = 0, int secondsToRespawnFor = 0,
        RespawnTeam team = RespawnTeam.Both, bool disableRespawnOnDeath = false, bool disableRespawnOnTeamChange = true);

    void StopAutoRespawning();
    void SetAutoRespawnDelay(float delay, RespawnTeam team = RespawnTeam.Both);
    bool IsAutoRespawning { get; }

    /// <summary>Stop the round from ending on team elimination (mp_ignore_round_win_conditions).</summary>
    void BlockTerminateRound(bool block);

    bool IsBlockingTerminateRound { get; }
    void SetNextRoundEndReason(bool useReason, RoundEndReason reason = RoundEndReason.RoundDraw);
    void StripWeaponFromOwner(CBasePlayerWeapon weapon);

    /// <summary>Per-player rotating help index (UltJB_Settings_GetClientHelpNumber).</summary>
    int GetHelpNumber(JbPlayer player);
}

// ----------------------------------------------------------------------------------------------- last guard

public interface ILastGuardService
{
    JbPlayer? LastGuard { get; }
    bool IsActive { get; }
    int NumNeeded { get; }
    void ResetDamageCounters();
}

// ----------------------------------------------------------------------------------------------- logger

public enum LogType
{
    Any = 0,
    None = 1,
    Attack,
    Item,
    Use,
    Break,
    LastRequest,
    Warden,
    Day,
    Admin
}

public interface ILogService
{
    void LogEvent(string message, JbPlayer? primary, JbPlayer? secondary, LogType type, bool global = true);
}

// ----------------------------------------------------------------------------------------------- jihad

public interface IJihadService
{
    bool IsJihad(JbPlayer player);
    void SetJihad(JbPlayer player);
    void ClearJihad(JbPlayer player);
    void SetAllowBombDropping(bool allow);
}

// ----------------------------------------------------------------------------------------------- models

public interface IPlayerModelService
{
    bool ApplyGuardModel(JbPlayer player);
    bool ApplyPrisonerModel(JbPlayer player);
}

// ----------------------------------------------------------------------------------------------- teams / ct ban

public interface ITeamService
{
    bool IsCtBanned(ulong steamId64);

    /// <summary>Can this player join guards right now? reason = phrase text to show when false.</summary>
    bool CanJoinGuards(JbPlayer player, out string reason);

    /// <summary>Move a player to a team respecting jailbreak rules (admin bypass when force).</summary>
    void MoveToTeam(JbPlayer player, CsTeam team, bool force = false);
}

// ----------------------------------------------------------------------------------------------- points

public interface IPointsService
{
    int GetPoints(JbPlayer player);
    void AddPoints(JbPlayer player, int amount, string? reason = null);

    /// <summary>Add (or with a negative amount remove) points by SteamID64, also for players who left. Never below 0.</summary>
    void AddPoints(ulong steamId64, int amount);
}

// ----------------------------------------------------------------------------------------------- mute

public interface IMuteService
{
    bool IsMuted(JbPlayer player);

    /// <summary>Re-evaluate voice flags for one player (call after team/life/warden changes).</summary>
    void Refresh(JbPlayer player);

    void RefreshAll();
}

// ----------------------------------------------------------------------------------------------- rebels (enhancement)

public interface IRebelService
{
    bool IsRebel(JbPlayer player);
    void MarkRebel(JbPlayer player, string? reason = null);
    void ClearRebel(JbPlayer player);
}

// ----------------------------------------------------------------------------------------------- null object

/// <summary>Answers every contract harmlessly when the owning module is disabled.</summary>
public sealed class NullServices : IWardenService, ICellService, ILastRequestService, IDayService, IEffectService,
    IWeaponSelectionService, ISettingsService, ILastGuardService, ILogService, IJihadService, IPlayerModelService,
    ITeamService, IPointsService, IMuteService, IRebelService
{
    public static readonly NullServices Instance = new();

    // warden
    public JbPlayer? Warden => null;
    public bool HasWarden => false;
    public bool IsWarden(CCSPlayerController? player) => false;
    public int GetWardenCount(JbPlayer player) => 0;
    public bool TrySetWarden(JbPlayer player, CCSPlayerController? admin = null) => false;
    public void RemoveWarden(bool announce = true) { }
    public void CreateRing(JbPlayer owner, int ringIndex, Vector position, float radius) { }

    // cells
    public bool DoExist => false;
    public bool HaveOpened => false;
    public bool ForceOpen(CCSPlayerController? by = null) => false;
    public bool ForceClose(CCSPlayerController? by = null) => false;
    public bool AddEntityAsDoor(CEntityInstance entity) => false;
    public bool RemoveEntityFromBeingDoor(CEntityInstance entity) => false;

    // last request
    int ILastRequestService.Register(LastRequestDefinition definition) => -1;
    IReadOnlyList<LastRequestDefinition> ILastRequestService.All => Array.Empty<LastRequestDefinition>();
    public bool IsInLastRequest(JbPlayer player) => false;
    public bool HasStartedLastRequest(JbPlayer prisoner) => false;
    public LastRequestDefinition? GetActive(JbPlayer prisoner) => null;
    public JbPlayer? GetOpponent(JbPlayer player) => null;
    public bool SetOpponent(JbPlayer prisoner, JbPlayer opponent) => false;
    public LrFlags GetFlags(JbPlayer prisoner) => LrFlags.None;
    public void EndLastRequest(JbPlayer prisoner) { }
    public void EndAll() { }
    public bool CanLastRequest => false;
    public int NumInitialized => 0;
    public int NumStartedIgnore(LrFlags ignoreMask = LrFlags.None) => 0;
    public int NumStartedContains(LrFlags containsMask) => 0;
    public int RoundNumber => 0;
    public bool DisplayOpponentSelection(JbPlayer prisoner, Action<JbPlayer, JbPlayer> onSuccess, Action<JbPlayer>? onFailed = null) => false;
    public void SaveWeapons(JbPlayer player) { }
    public void RestoreWeapons(JbPlayer player) { }
    public void StripWeapons(JbPlayer player, bool saveWeapons = false) { }
    public void SetHealth(JbPlayer player, int health) => player.Controller?.SetHp(health);
    public bool StartSlayTimer(JbPlayer prisoner, int secondsBeforeSlay = 45, SlayTimerFlags flags = SlayTimerFlags.Both,
        Action<JbPlayer, JbPlayer?, SlayTimerResult>? finished = null) => false;
    public bool StopSlayTimer(JbPlayer prisoner) => false;
    void ILastRequestService.OpenMenu(JbPlayer prisoner) { }

    // days
    int IDayService.Register(DayDefinition definition) => -1;
    IReadOnlyList<DayDefinition> IDayService.All => Array.Empty<DayDefinition>();
    public bool IsInProgress => false;
    public DayDefinition? Current => null;
    public DayType CurrentType => DayType.None;
    public bool IsFreeForAll => false;
    public int FreezeTimeRemaining => 0;
    public void ForceEndFreezeTime() { }
    public bool StartDay(DayDefinition day, JbPlayer? starter, bool freeForAll = false) => false;
    public void EndDay() { }
    public void SetEntityAsSpawnedByDay(CEntityInstance entity, bool spawnedByDay = true) { }
    public bool IsEntitySpawnedByDay(CEntityInstance entity) => false;
    void IDayService.OpenMenu(CCSPlayerController player, DayType? type, bool adminForce) { }

    // effects
    int IEffectService.Register(EffectDefinition definition) => -1;
    IReadOnlyList<EffectDefinition> IEffectService.All => Array.Empty<EffectDefinition>();
    public int GetEffectId(string name) => -1;
    public float GetDefaultData(int effectId) => 0f;
    public bool StartEffect(JbPlayer player, int effectId, float data = 0f) => false;
    public bool StopEffect(JbPlayer player, int effectId) => false;
    public void StopAllEffects(JbPlayer player) { }
    public bool HasEffect(JbPlayer player, int effectId) => false;
    // Like the real modules when nothing can be offered: report failure through onFailed, so callers never stall.
    public bool DisplaySelectionMenu(JbPlayer player, Action<JbPlayer, int> onSelected, Action<JbPlayer>? onFailed = null, int selectTimeOverride = 0)
    {
        onFailed?.Invoke(player);
        return false;
    }
    bool IEffectService.CancelSelection(JbPlayer player) => false;

    // weapon selection
    public bool DisplaySelectionMenu(JbPlayer player, Action<JbPlayer, string> onSelected, Action<JbPlayer>? onFailed, WeaponFilter filter)
    {
        onFailed?.Invoke(player);
        return false;
    }
    bool IWeaponSelectionService.CancelSelection(JbPlayer player) => false;
    public string? GetRandomWeapon(WeaponFilter filter) => null;
    public WeaponCategory? GetCategory(string weapon) => null;
    public CBasePlayerWeapon? GiveWeapon(JbPlayer player, string weapon)
    {
        player.Controller?.Give(weapon);
        return null;
    }
    public bool IsWeaponFromLastRequest(CBasePlayerWeapon weapon) => false;
    public void MarkWeaponFromLastRequest(CBasePlayerWeapon weapon) { }
    public bool IsGettingItem(JbPlayer player) => false;

    // settings
    public void StartAutoRespawning(bool resetRespawnLimits = false, int allowedRespawnsPerPlayer = 0, int secondsToRespawnFor = 0,
        RespawnTeam team = RespawnTeam.Both, bool disableRespawnOnDeath = false, bool disableRespawnOnTeamChange = true) { }
    public void StopAutoRespawning() { }
    public void SetAutoRespawnDelay(float delay, RespawnTeam team = RespawnTeam.Both) { }
    public bool IsAutoRespawning => false;
    public void BlockTerminateRound(bool block) { }
    public bool IsBlockingTerminateRound => false;
    public void SetNextRoundEndReason(bool useReason, RoundEndReason reason = RoundEndReason.RoundDraw) { }
    public void StripWeaponFromOwner(CBasePlayerWeapon weapon) { }
    public int GetHelpNumber(JbPlayer player) => 0;

    // last guard
    public JbPlayer? LastGuard => null;
    public bool IsActive => false;
    public int NumNeeded => 0;
    public void ResetDamageCounters() { }

    // log
    public void LogEvent(string message, JbPlayer? primary, JbPlayer? secondary, LogType type, bool global = true) { }

    // jihad
    public bool IsJihad(JbPlayer player) => false;
    public void SetJihad(JbPlayer player) { }
    public void ClearJihad(JbPlayer player) { }
    public void SetAllowBombDropping(bool allow) { }

    // models
    public bool ApplyGuardModel(JbPlayer player) => false;
    public bool ApplyPrisonerModel(JbPlayer player) => false;

    // teams
    public bool IsCtBanned(ulong steamId64) => false;
    public bool CanJoinGuards(JbPlayer player, out string reason)
    {
        reason = "";
        return true;
    }
    public void MoveToTeam(JbPlayer player, CsTeam team, bool force = false) => player.Controller?.ChangeTeam(team);

    // points
    public int GetPoints(JbPlayer player) => 0;
    public void AddPoints(JbPlayer player, int amount, string? reason = null) { }
    public void AddPoints(ulong steamId64, int amount) { }

    // mute
    public bool IsMuted(JbPlayer player) => false;
    public void Refresh(JbPlayer player) { }
    public void RefreshAll() { }

    // rebels
    public bool IsRebel(JbPlayer player) => player.IsRebel;
    public void MarkRebel(JbPlayer player, string? reason = null) => player.IsRebel = true;
    public void ClearRebel(JbPlayer player) => player.IsRebel = false;
}
