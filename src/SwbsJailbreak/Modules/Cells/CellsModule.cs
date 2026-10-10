using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SwbsJailbreak.Modules.Cells;

public sealed class CellsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>ultjb_cell_door_open_time (min 1): seconds after round_start before the cells open automatically.</summary>
    public float OpenTime { get; set; } = 60f;

    /// <summary>ultjb_cell_doors_autodetect: find doors heuristically when a map has no JSON and no matching original list.</summary>
    public bool AutoDetect { get; set; } = true;

    /// <summary>ultjb_cell_doors_autodetect_threshold (1-10).</summary>
    public int AutoDetectThreshold { get; set; } = 4;

    /// <summary>Try the original CS:GO name lists (exact map, aliases.json, then same-named map family).</summary>
    public bool UseLegacyLists { get; set; } = true;

    /// <summary>ultjb_cell_doors_warden_open: NEW optional !open / !o for the warden (original has no warden command).</summary>
    public bool WardenOpen { get; set; } = false;

    /// <summary>Editor visual aid: resolved cell doors glow green while an admin edits.</summary>
    public bool EditorGlow { get; set; } = true;

    /// <summary>Seconds between real door-state checks while the cells are still closed (0 = outputs only, like the original).</summary>
    public float StatePollSeconds { get; set; } = 1f;

    /// <summary>Tell online root admins once per map when no cell doors could be found.</summary>
    public bool NotifyAdminsWhenMissing { get; set; } = true;
}

/// <summary>
/// Port of UltJB_API_CellDoors.sp (v1.6) + UltJB_CellDoors.sp (v1.2): per-map cell door list, automatic opening,
/// admin force-open, "cells opened" detection (real door outputs + real entity state, never assumed) and the in-game
/// editor. CS2 additions: JSON per-map files, original name lists as fallback, heuristic auto-detection with
/// diagnostics, button-press open method for script-driven doors.
/// </summary>
public sealed partial class CellsModule : JbModule, ICellService
{
    private readonly List<CellDoorEntry> _entries = new();
    private readonly List<CellButtonEntry> _buttons = new();
    private OpenMethod _openMethod = OpenMethod.Doors;
    private float? _autoOpenOverride;
    private string _source = "none";
    private string _sourceDetail = "";
    private string _jsonState = "missing";

    private readonly HashSet<uint> _doorHandles = new();
    private readonly Dictionary<uint, DoorKind> _kindByHandle = new();
    private readonly Dictionary<uint, CellDoorEntry> _entryByHandle = new();
    private readonly HashSet<uint> _buttonHandles = new();
    private readonly HashSet<uint> _initialOpen = new();
    private readonly Dictionary<uint, Vector> _initialOrigin = new();
    private readonly HashSet<uint> _removedDuringRound = new();
    private int _resolvedCount;

    private bool _haveOpened;
    private string _map = "";
    private bool _autoDetectPending;
    private bool _legacyCheckPending;
    private bool _legacyIsStem;
    private bool _announceAuto;
    private bool _noDoorsNotified;
    private string _lastSummary = "";
    private AutoDetectResult? _lastAuto;
    private Timer? _pollTimer;

    private CellsConfig Cfg => Section<CellsConfig>("Cells");

    public override int Order => 5;
    public override bool Enabled => Cfg.Enabled;

    private bool UsesDoors => _openMethod is OpenMethod.Doors or OpenMethod.Both;
    private bool UsesButtons => _openMethod is OpenMethod.Buttons or OpenMethod.Both;

    // ================================================================== lifecycle

    public override void OnLoad(bool hotReload)
    {
        Jb.Cells = this;

        Plugin.HookEntityOutput("func_door", "OnOpen", OnDoorOutput, HookMode.Post);
        Plugin.HookEntityOutput("func_door_rotating", "OnOpen", OnDoorOutput, HookMode.Post);
        Plugin.HookEntityOutput("prop_door_rotating", "OnOpen", OnDoorOutput, HookMode.Post);
        Plugin.HookEntityOutput("func_movelinear", "OnFullyOpen", OnDoorOutput, HookMode.Post);
        Plugin.HookEntityOutput("func_breakable", "OnBreak", OnDoorOutput, HookMode.Post);
        Plugin.HookEntityOutput("func_button", "OnPressed", OnButtonOutput, HookMode.Post);
        Plugin.HookEntityOutput("func_rot_button", "OnPressed", OnButtonOutput, HookMode.Post);

        Plugin.RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        Plugin.RegisterListener<Listeners.OnEntityDeleted>(OnEntityDeleted);
        Plugin.RegisterListener<Listeners.OnTick>(OnEditorTick);

        if (Cfg.WardenOpen)
        {
            Plugin.AddCommand("css_open", "Warden: open the cell doors (optional SWBS CS2 addition)", OnWardenOpenCommand);
            Plugin.AddCommand("css_o", "Warden: open the cell doors (optional SWBS CS2 addition)", OnWardenOpenCommand);
        }

        if (hotReload)
        {
            _map = CellDoorStore.SanitizeMap(Server.MapName);
            LoadConfigForMap();
            if (_autoDetectPending)
            {
                _autoDetectPending = false;
                RunAutoDetect();
            }

            ResolveDoors(roundStart: true, additive: false);
            _legacyCheckPending = false;
            ReportResolution();
        }
    }

    public override void OnUnload(bool hotReload)
    {
        EndAllEditSessions();
        StopPoll();
    }

    public override void OnMapStart(string mapName)
    {
        EndAllEditSessions();
        _map = CellDoorStore.SanitizeMap(mapName);
        _haveOpened = false;
        _pollTimer = null;
        ClearResolved();
        _resolvedCount = 0;
        _noDoorsNotified = false;
        _lastSummary = "";
        LoadConfigForMap();
    }

    public override void OnMapEnd()
    {
        EndAllEditSessions();
        StopPoll();
    }

    public override void OnRoundStart()
    {
        _haveOpened = false;
        _removedDuringRound.Clear();

        if (_autoDetectPending)
        {
            _autoDetectPending = false;
            RunAutoDetect();
        }

        ResolveDoors(roundStart: true, additive: false);

        if (_legacyCheckPending)
        {
            _legacyCheckPending = false;
            if (_doorHandles.Count == 0)
            {
                Logger.LogInformation("[CellDoors] legacy names for {Map} matched no entities; trying auto-detect", _map);
                _entries.Clear();
                _source = "none";
                if (Cfg.AutoDetect)
                {
                    RunAutoDetect();
                    ResolveDoors(roundStart: true, additive: false);
                }
            }
            else
            {
                var matched = _entryByHandle.Values.Select(e => e.Name).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
                var unmatched = _entries.Where(e => e.Name.Length > 0 && !matched.Contains(e.Name)).Select(e => e.Name).ToList();
                Logger.LogInformation("[CellDoors] {Detail}: {Matched} original name(s) still exist on {Map} ({Count} entities){Unmatched}",
                    _sourceDetail, matched.Count, _map, _doorHandles.Count,
                    unmatched.Count > 0 ? $"; not found: {string.Join(", ", unmatched)}" : "");
                if (_legacyIsStem) _entries.RemoveAll(e => e.Name.Length > 0 && !matched.Contains(e.Name));
            }
        }

        ReportResolution();

        var openTime = Math.Max(1f, _autoOpenOverride ?? Cfg.OpenTime);
        Round.Timer(openTime, () => ForceOpen());
        StartPoll();

        JbEvents.RaiseCellsRoundStartReady();

        // CS2 may spawn template / late entities during the first frame: keep the larger set (no second event).
        Server.NextFrame(() => ResolveDoors(roundStart: false, additive: true));
    }

    public override void OnRoundEnd(CsTeam winner, int reason) => StopPoll(); // auto-open timer is a round timer

    private void ClearResolved()
    {
        _doorHandles.Clear();
        _kindByHandle.Clear();
        _entryByHandle.Clear();
        _buttonHandles.Clear();
        _initialOpen.Clear();
        _initialOrigin.Clear();
        _removedDuringRound.Clear();
    }

    // ================================================================== config loading (spec 02 §1.2.5)

    private void LoadConfigForMap()
    {
        _entries.Clear();
        _buttons.Clear();
        _openMethod = OpenMethod.Doors;
        _autoOpenOverride = null;
        _source = "none";
        _sourceDetail = "";
        _autoDetectPending = false;
        _legacyCheckPending = false;
        _legacyIsStem = false;
        _lastAuto = null;

        var path = CellDoorStore.JsonPath(_map);
        var json = CellDoorStore.LoadJson(_map, Logger);
        if (json != null)
        {
            var method = ParseMethod(json.OpenMethod);
            if (json.Doors.Count > 0 || (json.Buttons.Count > 0 && method != OpenMethod.Doors))
            {
                _entries.AddRange(json.Doors.Where(d => d != null));
                _buttons.AddRange(json.Buttons.Where(b => b != null));
                _openMethod = method;
                _autoOpenOverride = json.AutoOpenSeconds is > 0 ? json.AutoOpenSeconds : null;
                _source = string.IsNullOrWhiteSpace(json.Source) ? "manual" : json.Source;
                _sourceDetail = path;
                _jsonState = "loaded";
                return;
            }

            _jsonState = "has no doors";
        }
        else
        {
            _jsonState = File.Exists(path) ? "malformed" : "missing";
        }

        if (Cfg.UseLegacyLists)
        {
            var legacy = CellDoorStore.FindLegacy(_map, Logger);
            if (legacy != null)
            {
                foreach (var n in legacy.Value.Names) _entries.Add(new CellDoorEntry { Name = n });
                _source = "legacy";
                _sourceDetail = legacy.Value.Description;
                _legacyCheckPending = true;
                _legacyIsStem = legacy.Value.IsStem;
                return;
            }
        }

        if (Cfg.AutoDetect) _autoDetectPending = true;
    }

    private static OpenMethod ParseMethod(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "buttons" => OpenMethod.Buttons,
        "both" => OpenMethod.Both,
        _ => OpenMethod.Doors
    };

    private static string MethodName(OpenMethod m) => m.ToString().ToLowerInvariant();

    private void RunAutoDetect()
    {
        try
        {
            var res = CellAutoDetect.Run(Math.Clamp(Cfg.AutoDetectThreshold, 1, 10));
            _lastAuto = res;
            foreach (var line in res.Table) Logger.LogDebug("{Line}", line);
            if (res.Doors.Count == 0)
            {
                _source = "none";
                return;
            }

            _entries.Clear();
            _entries.AddRange(res.Doors);
            _buttons.Clear();
            _buttons.AddRange(res.Buttons);
            _openMethod = OpenMethod.Doors;
            _source = "auto";
            _sourceDetail = $"auto-detect via {res.Method}";
            _announceAuto = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Cells] auto-detection failed on {Map}", _map);
        }
    }

    // ================================================================== resolution (spec 02 §1.2.6)

    private static bool Matches(CellDoorEntry e, string dn, string name, CBaseEntity be)
    {
        if (!string.IsNullOrEmpty(e.Name)) return name == e.Name; // case-sensitive like StrEqual
        // Unnamed entries never match by name (fixes the original "" name bug that added every unnamed door).
        if (!string.IsNullOrEmpty(e.Class) && e.Class != dn) return false;
        if (!string.IsNullOrEmpty(e.HammerId))
        {
            var hid = CellAutoDetect.SafeHammerId(be);
            if (hid.Length > 0) return hid == e.HammerId;
        }

        return e.Origin is { Length: 3 } o && be.AbsOrigin is { } a &&
               VectorUtil.Distance(a, new Vector(o[0], o[1], o[2])) <= 16f;
    }

    private static bool MatchesButton(CellButtonEntry e, string dn, string name, CBaseEntity be)
    {
        if (!string.IsNullOrEmpty(e.Name)) return name == e.Name;
        if (!string.IsNullOrEmpty(e.Class) && e.Class != dn) return false;
        if (!string.IsNullOrEmpty(e.HammerId))
        {
            var hid = CellAutoDetect.SafeHammerId(be);
            if (hid.Length > 0) return hid == e.HammerId;
        }

        return e.Origin is { Length: 3 } o && be.AbsOrigin is { } a &&
               VectorUtil.Distance(a, new Vector(o[0], o[1], o[2])) <= 16f;
    }

    /// <summary>
    /// GetCellDoorEnts(): resolve the entries to entity handles. roundStart clears everything and Enables every
    /// func_brush door (original); otherwise only newly found func_brush doors get Enable. additive keeps the old set.
    /// </summary>
    private void ResolveDoors(bool roundStart, bool additive)
    {
        var known = roundStart ? new HashSet<uint>() : new HashSet<uint>(_doorHandles);
        if (!additive)
        {
            _doorHandles.Clear();
            _kindByHandle.Clear();
            _entryByHandle.Clear();
            _buttonHandles.Clear();
            if (roundStart)
            {
                _initialOpen.Clear();
                _initialOrigin.Clear();
            }
        }

        if (_entries.Count == 0 && _buttons.Count == 0)
        {
            _resolvedCount = additive ? _resolvedCount : 0;
            return;
        }

        foreach (var ent in Utilities.GetAllEntities())
        {
            try
            {
                if (ent == null || !ent.IsValid) continue;
                var dn = ent.DesignerName;
                if (dn == null) continue;
                if (DoorClasses.TryKind(dn, out var kind))
                {
                    if (_entries.Count == 0) continue;
                    var raw = ent.EntityHandle.Raw;
                    if (_doorHandles.Contains(raw)) continue;
                    var name = ent.Entity?.Name ?? "";
                    var be = ent.As<CBaseEntity>();
                    foreach (var e in _entries)
                    {
                        if (!Matches(e, dn, name, be)) continue;
                        AddHandle(raw, kind, e, be, !known.Contains(raw));
                        break;
                    }
                }
                else if (DoorClasses.IsButton(dn) && _buttons.Count > 0)
                {
                    var name = ent.Entity?.Name ?? "";
                    var be = ent.As<CBaseEntity>();
                    if (_buttons.Any(b => MatchesButton(b, dn, name, be))) _buttonHandles.Add(ent.EntityHandle.Raw);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "[Cells] resolve skipped an entity");
            }
        }

        _resolvedCount = additive ? Math.Max(_resolvedCount, _doorHandles.Count) : _doorHandles.Count;
        if (_sessions.Count > 0) RefreshGlows();
    }

    private void AddHandle(uint raw, DoorKind kind, CellDoorEntry entry, CBaseEntity be, bool isNew)
    {
        _doorHandles.Add(raw);
        _kindByHandle[raw] = kind;
        _entryByHandle[raw] = entry;
        if (kind == DoorKind.Brush && isNew)
        {
            try { be.AcceptInput("Enable"); }
            catch (Exception ex) { Logger.LogDebug(ex, "[Cells] Enable failed"); }
        }

        if (!_initialOrigin.ContainsKey(raw) && be.AbsOrigin is { } o) _initialOrigin[raw] = new Vector(o.X, o.Y, o.Z);
        if (IsOpenState(be, kind, raw)) _initialOpen.Add(raw);
    }

    private void OnEntitySpawned(CEntityInstance entity)
    {
        // Mirrors EntityHooker's late hook: a supported door spawned mid-round that matches an entry joins the set.
        if (_entries.Count == 0 || Round.Phase is not (RoundPhase.Active or RoundPhase.FreezeTime)) return;
        try
        {
            if (entity == null || !entity.IsValid) return;
            var dn = entity.DesignerName;
            if (!DoorClasses.TryKind(dn, out var kind)) return;
            var raw = entity.EntityHandle.Raw;
            Server.NextFrame(() =>
            {
                var be = new CHandle<CBaseEntity>(raw).Value;
                if (be == null || !be.IsValid || _doorHandles.Contains(raw)) return;
                var name = be.Entity?.Name ?? "";
                var e = _entries.FirstOrDefault(x => Matches(x, dn!, name, be));
                if (e == null) return;
                AddHandle(raw, kind, e, be, true);
                _resolvedCount = Math.Max(_resolvedCount, _doorHandles.Count);
            });
        }
        catch
        {
            // ignore
        }
    }

    private void OnEntityDeleted(CEntityInstance entity)
    {
        if (_doorHandles.Count == 0 && _buttonHandles.Count == 0) return;
        uint raw;
        try { raw = entity.EntityHandle.Raw; }
        catch { return; }

        _buttonHandles.Remove(raw);
        if (!_doorHandles.Remove(raw)) return;
        if (Round.Phase is not (RoundPhase.Active or RoundPhase.FreezeTime)) return;
        _removedDuringRound.Add(raw);
        // A breakable / wall toggle / track train cell door that is really gone mid-round is open.
        if (!_haveOpened && _kindByHandle.TryGetValue(raw, out var kind) && DoorClasses.IsRemovalKind(kind))
            Server.NextFrame(() => MarkOpened($"{kind} removed"));
    }

    // ================================================================== ICellService

    public bool DoExist => _resolvedCount > 0 || (UsesButtons && _buttonHandles.Count > 0);

    public bool HaveOpened => _haveOpened;

    public IReadOnlyCollection<uint> DoorHandles => _doorHandles;

    /// <summary>UltJB_CellDoors_ForceOpen: per-class open action on every resolved door. Does not itself set "opened".</summary>
    public bool ForceOpen(CCSPlayerController? by = null)
    {
        var buttonsUsable = UsesButtons && _buttonHandles.Count > 0;
        if (_resolvedCount == 0 && !buttonsUsable) return false;

        if (buttonsUsable)
        {
            var activator = Players.AliveGuards().Select(c => c.PawnOrNull()).FirstOrDefault(p => p != null);
            foreach (var raw in _buttonHandles.ToArray())
            {
                var b = new CHandle<CBaseEntity>(raw).Value;
                if (b == null || !b.IsValid) continue;
                try
                {
                    b.AcceptInput("Unlock", activator, activator);
                    b.AcceptInput("Press", activator, activator);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "[Cells] pressing button failed");
                }
            }
        }

        if (UsesDoors)
        {
            foreach (var raw in _doorHandles.ToArray())
            {
                var ent = new CHandle<CBaseEntity>(raw).Value;
                if (ent == null || !ent.IsValid || !_kindByHandle.TryGetValue(raw, out var kind)) continue;
                var mode = _entryByHandle.TryGetValue(raw, out var e) ? e.OpenMode?.Trim().ToLowerInvariant() ?? "auto" : "auto";
                try
                {
                    SendOpen(ent, kind, mode);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "[Cells] open input failed on {Class}", ent.DesignerName);
                }
            }
        }

        // Verify by real state shortly after (doors already open never fire OnOpen; removals/brush disables have no output).
        Round.Timer(1f, PollState);
        Round.Timer(3f, PollState);
        return true;
    }

    private static void SendOpen(CBaseEntity ent, DoorKind kind, string mode)
    {
        var hasUnlock = kind is DoorKind.Door or DoorKind.RotDoor or DoorKind.PropDoor;
        switch (mode)
        {
            case "open":
                if (hasUnlock) ent.AcceptInput("Unlock");
                ent.AcceptInput("Open");
                return;
            case "break":
                ent.AcceptInput("Break");
                return;
            case "kill":
                ent.AcceptInput("KillHierarchy");
                return;
            case "disable":
                ent.AcceptInput("Disable");
                return;
            case "toggle":
                ent.AcceptInput("Toggle");
                return;
        }

        switch (kind)
        {
            case DoorKind.Door:
            case DoorKind.RotDoor:
            case DoorKind.PropDoor:
                ent.AcceptInput("Unlock");
                ent.AcceptInput("Open");
                break;
            case DoorKind.MoveLinear:
                ent.AcceptInput("Open");
                break;
            case DoorKind.Breakable:
                ent.AcceptInput("Break");
                break;
            case DoorKind.WallToggle:
            case DoorKind.TrackTrain:
                // "can't be detected when open so kill it" (original)
                ent.AcceptInput("KillHierarchy");
                break;
            case DoorKind.Brush:
                // never kill a func_brush - it is not restored on new rounds (original)
                ent.AcceptInput("Disable");
                break;
        }
    }

    /// <summary>NEW (not in the original): close what can be closed (moving doors Close, func_brush Enable).</summary>
    public bool ForceClose(CCSPlayerController? by = null)
    {
        var any = false;
        foreach (var raw in _doorHandles.ToArray())
        {
            var ent = new CHandle<CBaseEntity>(raw).Value;
            if (ent == null || !ent.IsValid || !_kindByHandle.TryGetValue(raw, out var kind)) continue;
            try
            {
                switch (kind)
                {
                    case DoorKind.Door:
                    case DoorKind.RotDoor:
                    case DoorKind.PropDoor:
                    case DoorKind.MoveLinear:
                        ent.AcceptInput("Close");
                        any = true;
                        break;
                    case DoorKind.Brush:
                        ent.AcceptInput("Enable");
                        any = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[Cells] close input failed");
            }
        }

        return any;
    }

    /// <summary>UltJB_CellDoors_AddEntityAsDoor: live only, not saved.</summary>
    public bool AddEntityAsDoor(CEntityInstance entity)
    {
        if (entity == null || !entity.IsValid) return false;
        var dn = entity.DesignerName;
        if (!DoorClasses.IsEditorDoorType(dn)) return false;
        var be = entity.As<CBaseEntity>();
        if (FindEntry(be) == null) _entries.Add(EntryFor(be));
        ResolveDoors(roundStart: false, additive: true);
        return true;
    }

    /// <summary>UltJB_CellDoors_RemoveEntityFromBeingDoor.</summary>
    public bool RemoveEntityFromBeingDoor(CEntityInstance entity)
    {
        if (entity == null || !entity.IsValid) return false;
        var e = FindEntry(entity.As<CBaseEntity>());
        if (e == null) return false;
        _entries.Remove(e);
        RestoreGlowsNotIn(null);
        ResolveDoors(roundStart: false, additive: false);
        return true;
    }

    private static CellDoorEntry EntryFor(CBaseEntity be)
    {
        var name = be.Entity?.Name ?? "";
        if (name.Length > 0) return new CellDoorEntry { Name = name };
        var hid = CellAutoDetect.SafeHammerId(be);
        return new CellDoorEntry
        {
            Name = "",
            HammerId = hid.Length > 0 ? hid : null,
            Class = be.DesignerName,
            Origin = be.AbsOrigin is { } o ? CellDoorStore.ToArray(o) : null
        };
    }

    private CellDoorEntry? FindEntry(CBaseEntity be)
    {
        var name = be.Entity?.Name ?? "";
        var dn = be.DesignerName ?? "";
        if (name.Length > 0) return _entries.FirstOrDefault(e => e.Name == name);
        var raw = be.EntityHandle.Raw;
        if (_entryByHandle.TryGetValue(raw, out var known) && _entries.Contains(known)) return known;
        return _entries.FirstOrDefault(e => e.Name.Length == 0 && Matches(e, dn, name, be));
    }

    private CellButtonEntry? FindButton(CBaseEntity be)
    {
        var name = be.Entity?.Name ?? "";
        var dn = be.DesignerName ?? "";
        return _buttons.FirstOrDefault(b => MatchesButton(b, dn, name, be));
    }

    // ================================================================== opened detection (spec 02 §1.2.9)

    private HookResult OnDoorOutput(CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay)
    {
        try
        {
            if (!_haveOpened && caller != null && caller.IsValid && _doorHandles.Contains(caller.EntityHandle.Raw))
                MarkOpened(name);
        }
        catch
        {
            // ignore
        }

        return HookResult.Continue;
    }

    private HookResult OnButtonOutput(CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay)
    {
        try
        {
            // Buttons-only maps (script / prop driven doors): the linked cell button being pressed is the open signal.
            if (!_haveOpened && _doorHandles.Count == 0 && UsesButtons && caller != null && caller.IsValid &&
                _buttonHandles.Contains(caller.EntityHandle.Raw))
                MarkOpened("cell button pressed");
        }
        catch
        {
            // ignore
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnCellDoorMoving(EventDoorMoving ev, GameEventInfo info)
    {
        if (!_haveOpened && _doorHandles.Count > 0) Server.NextFrame(PollState);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnCellDoorOpenEvent(EventDoorOpen ev, GameEventInfo info)
    {
        if (!_haveOpened && _doorHandles.Count > 0) Server.NextFrame(PollState);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnCellBreakableBroken(EventBreakBreakable ev, GameEventInfo info)
    {
        if (_haveOpened || _doorHandles.Count == 0) return HookResult.Continue;
        try
        {
            var ent = Utilities.GetEntityFromIndex<CEntityInstance>((int)ev.Entindex);
            if (ent != null && ent.IsValid && _doorHandles.Contains(ent.EntityHandle.Raw)) MarkOpened("break_breakable");
        }
        catch
        {
            // ignore
        }

        return HookResult.Continue;
    }

    private void StartPoll()
    {
        StopPoll();
        if (Cfg.StatePollSeconds <= 0 || _doorHandles.Count == 0) return;
        _pollTimer = Round.Timer(Math.Max(0.25f, Cfg.StatePollSeconds), PollState, repeat: true);
    }

    private void StopPoll()
    {
        try { _pollTimer?.Kill(); }
        catch { /* already dead */ }
        _pollTimer = null;
    }

    /// <summary>Real entity state check (fix for already-open doors / doors without outputs). Never assumes.</summary>
    private void PollState()
    {
        if (_haveOpened)
        {
            StopPoll();
            return;
        }

        if (Round.Phase is not (RoundPhase.Active or RoundPhase.FreezeTime)) return;
        foreach (var (raw, kind) in _kindByHandle.ToArray())
        {
            var ent = new CHandle<CBaseEntity>(raw).Value;
            if (ent == null || !ent.IsValid)
            {
                if (DoorClasses.IsRemovalKind(kind) && _removedDuringRound.Contains(raw))
                {
                    MarkOpened($"{kind} removed");
                    return;
                }

                continue;
            }

            var open = IsOpenState(ent, kind, raw);
            if (_initialOpen.Contains(raw))
            {
                // Started open (map design): only a close followed by a re-open counts.
                if (!open) _initialOpen.Remove(raw);
                continue;
            }

            if (open)
            {
                MarkOpened($"{ent.DesignerName} state");
                return;
            }
        }
    }

    private bool IsOpenState(CBaseEntity ent, DoorKind kind, uint raw)
    {
        try
        {
            switch (kind)
            {
                case DoorKind.Door:
                case DoorKind.RotDoor:
                {
                    var ts = ent.As<CBaseToggle>().Toggle_state;
                    if (ts is TOGGLE_STATE.TS_AT_TOP or TOGGLE_STATE.TS_GOING_UP) return true;
                    return kind == DoorKind.Door && Moved(ent, raw);
                }
                case DoorKind.MoveLinear:
                case DoorKind.TrackTrain:
                    return Moved(ent, raw);
                case DoorKind.PropDoor:
                {
                    var st = ent.As<CBasePropDoor>().DoorState;
                    return st is DoorState_t.DOOR_STATE_OPENING or DoorState_t.DOOR_STATE_OPEN or DoorState_t.DOOR_STATE_AJAR;
                }
                case DoorKind.Brush:
                    return ent.As<CFuncBrush>().Disabled != 0;
                default:
                    return false; // breakable / wall toggle: only their removal shows they are open
            }
        }
        catch
        {
            return false;
        }
    }

    private bool Moved(CBaseEntity ent, uint raw)
    {
        if (!_initialOrigin.TryGetValue(raw, out var start) || ent.AbsOrigin is not { } now) return false;
        return VectorUtil.Distance(start, now) > 2f;
    }

    private void MarkOpened(string how)
    {
        if (_haveOpened) return;
        _haveOpened = true;
        StopPoll();
        Logger.LogInformation("[Cells] cell doors opened on {Map} ({How})", _map, how);
        // Raised next frame: subscribers may manipulate entities, which is unsafe inside an entity output callback.
        Server.NextFrame(() =>
        {
            JbEvents.RaiseCellsOpened();
            Jb.Log.LogEvent(Lang.T("cells.log_opened"), null, null, LogType.Any);
        });
    }

    // ================================================================== diagnostics

    private void ReportResolution()
    {
        var summary = $"[Cells] {_map}: source={_source}{(_sourceDetail.Length > 0 ? $" ({_sourceDetail})" : "")}, entries={_entries.Count}, " +
                      $"resolved door entities={_doorHandles.Count}, linked buttons={_buttonHandles.Count}, openMethod={MethodName(_openMethod)}";
        if (summary != _lastSummary)
        {
            Logger.LogInformation("{Summary}", summary);
            _lastSummary = summary;
        }

        if (_announceAuto)
        {
            _announceAuto = false;
            if (_doorHandles.Count > 0)
            {
                var msg = T("cells.autodetected", _doorHandles.Count, _lastAuto?.Method ?? "auto-detect");
                Server.PrintToConsole(Colors.Strip(msg));
                foreach (var c in Players.Humans().Where(c => Perms.Has(c, "@css/root"))) Chat.Print(c, msg);
            }
        }

        if (DoExist || _noDoorsNotified) return;
        _noDoorsNotified = true;
        Logger.LogWarning("{Diag}", BuildMissingDiagnostic());
        if (Cfg.NotifyAdminsWhenMissing)
            foreach (var c in Players.Humans().Where(c => Perms.Has(c, "@css/root")))
                Chat.Print(c, T("cells.none_found_admin", _map));
    }

    private string BuildMissingDiagnostic()
    {
        var sb = new StringBuilder();
        sb.Append($"[Cells] No cell doors found on {_map}. Wardays, jihad and closed-cell respawns stay disabled until doors are set.\n");
        sb.Append($"  JSON {CellDoorStore.JsonPath(_map)}: {_jsonState}\n");
        sb.Append(Cfg.UseLegacyLists ? $"  Original name lists: {(_sourceDetail.StartsWith("legacy") ? _sourceDetail + " (no names matched)" : "none for this map / stem")}\n" : "  Original name lists: disabled\n");
        if (_lastAuto != null)
        {
            sb.Append($"  Auto-detect: {_lastAuto.Candidates} door-class entities scored, {_lastAuto.Passed} passed threshold {Cfg.AutoDetectThreshold}, " +
                      $"{_lastAuto.TSpawns} prisoner spawns, {_lastAuto.ButtonsOnMap} buttons\n");
            foreach (var line in _lastAuto.Table.Take(5)) sb.Append("    ").Append(line).Append('\n');
        }
        else
        {
            sb.Append(Cfg.AutoDetect ? "  Auto-detect: not run\n" : "  Auto-detect: disabled\n");
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ent in Utilities.GetAllEntities())
        {
            var dn = ent?.DesignerName;
            if (dn == null || !(DoorClasses.IsEditorDoorType(dn) || DoorClasses.IsButton(dn))) continue;
            counts[dn] = counts.GetValueOrDefault(dn) + 1;
        }

        sb.Append("  Door-type / button entities on this map: ")
            .Append(counts.Count == 0 ? "none" : string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")))
            .Append('\n');
        sb.Append("  Fix: !celldoors_edit (aim at a door or \"Pick door from list\"), or css_celldoors_list + css_celldoors_add <index|name>, then \"Save doors.\" / css_celldoors_save.");
        return sb.ToString();
    }

    private static string T(string key, params object?[] args)
    {
        var text = Lang.T(key);
        for (var i = 0; i < args.Length; i++) text = text.Replace("{" + i + "}", args[i]?.ToString() ?? "");
        return text;
    }
}
