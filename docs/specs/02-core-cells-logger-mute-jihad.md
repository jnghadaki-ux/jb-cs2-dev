# Spec 02 — Cell Doors, Logger, Mute Manager (warden mute), Voice Chat rules, Jihad, Weapon Selection

Port target: one CounterStrikeSharp plugin (`SwbsJailbreak`, CSS API **1.0.376**, .NET 10, Metamod:Source 2.0).
Credits: mod by **Gdaki**, "designed after HlStriker, Swbs development/lead admins".

Original sources covered (all under `SMPlugins-master/Mods/UltimateJailbreak/`):

| Original file | Version | Port module (C#) |
|---|---|---|
| `UltJB_API_CellDoors.sp`, `UltJB_CellDoors.sp`, `Includes/ultjb_cell_doors.inc`, `files/addons/sourcemod/configs/cell_doors/*.txt` (154 files) | 1.6 / 1.2 | `CellDoorsModule` (+ `ICellDoorService`) |
| `UltJB_Logger.sp`, `Includes/ultjb_logger.inc` | 1.8 | `LoggerModule` (+ `IJbLogger`) |
| `UltJB_MuteManager.sp` | 1.21 | `WardenMuteModule` (uses `VoiceService`) |
| `UltJB_VoiceChat.sp` | 1.18 | `VoiceChatModule` (uses `VoiceService`) |
| `UltJB_Jihad.sp`, `Includes/ultjb_jihad.inc` (+ `Days/UltJB_Warday_Jihad.sp` interplay) | 1.12 | `JihadModule` (+ `IJihadService`) |
| `UltJB_API_WeaponSelection.sp`, `Includes/ultjb_weapon_selection.inc` | 1.10 | `WeaponSelectionModule` (+ `IWeaponService`) |

Every module derives from `SwbsJailbreak.Core.JbModule` (see `src/SwbsJailbreak/Core/JbModule.cs`) and keeps per-player
state in `JbPlayer.Data<T>()` bags (implement `IRoundScoped` only where the state really is round-scoped — see Jihad,
which deliberately is **not**).

---

## 0. Conventions used throughout this spec

### 0.1 Verified CSS 1.0.376 APIs used here (grep-confirmed in `/tmp/claude-0/css-api/decomp`)

| Need | API (exact) | Source file |
|---|---|---|
| Hook map entity outputs (global, by classname) | `BasePlugin.HookEntityOutput(string classname, string outputName, EntityIO.EntityOutputHandler handler, HookMode mode = HookMode.Pre)`; handler signature `HookResult (CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay)`; attribute form `[EntityOutputHook("func_button","OnPressed")]` | `Core/BasePlugin.cs:505`, `Modules.Entities/EntityIO.cs:7` |
| Read an output's wiring (targets of a button) | `CEntityIOOutput.Connections` → `EntityIOConnection_t` linked list (`.Next`), each has `TargetDesc` (target name string), `TargetInput`, `ValueOverride`, `Delay`, `TimesToFire` | `Core/CEntityIOOutput.cs`, `Core/EntityIOConnectionDesc_t.cs` |
| Fire an input immediately | `CEntityInstance.AcceptInput(string inputName, CEntityInstance? activator = null, CEntityInstance? caller = null, string value = "", int outputId = 0)` | `Core/CEntityInstance.cs:120` |
| Queue an input (with delay) | `CEntityInstance.AddEntityIOEvent(string inputName, CEntityInstance? activator, CEntityInstance? caller, string value, float delay)` | same |
| Targetname / classname / hammer id | `ent.Entity?.Name` (m_iName), `ent.DesignerName`, `CBaseEntity.UniqueHammerID` (string, m_sUniqueHammerID), `CBaseEntity.Target` (m_target) | `Core/CEntityIdentity.cs`, `Core/CBaseEntity.cs:107,179` |
| Door classes | `CBaseDoor` (func_door; `Locked`, `Toggle_state` via `CBaseToggle`, outputs `OnOpen`, `OnFullyOpen`), `CRotDoor` (func_door_rotating), `CBasePropDoor`/`CPropDoorRotating` (prop_door_rotating; `DoorState`, `Locked`, `OnOpen`), `CFuncMoveLinear` (func_movelinear; `OnFullyOpen`, `Toggle_state`), `CBreakable` (func_breakable; `OnBreak`), `CFuncWallToggle`, `CFuncTrackTrain`, `CFuncBrush` (`Disabled`), `CBaseButton`/`CRotButton`/`CMomentaryRotButton` (outputs `OnPressed`, `OnIn`, `OnOut`, `OnDamaged`, `Locked`) | `Core/*.cs` |
| Door state enums | `TOGGLE_STATE { TS_AT_TOP=0 (open), TS_AT_BOTTOM=1 (closed), TS_GOING_UP=2, TS_GOING_DOWN=3 }`, `DoorState_t { DOOR_STATE_CLOSED, OPENING, OPEN, CLOSING, AJAR }` | |
| Find entities | `Utilities.FindAllEntitiesByDesignerName<T>(string)` — **uses `string.Contains`** (see pitfall 0.3), `Utilities.GetAllEntities()`, `Utilities.GetEntityFromIndex<T>(int)` | `API/Utilities.cs:123` |
| Entity lifetime | `Listeners.OnEntitySpawned`, `Listeners.OnEntityCreated`, `Listeners.OnEntityDeleted`, `Listeners.OnEntityParentChanged(CEntityInstance entity, CEntityInstance newParent)` | `Core/Listeners.cs` |
| Handles | `CEntityInstance.EntityHandle` (`CEntityHandle : CHandle<CEntityInstance>`), `.Raw` (uint), `.Index`, `.SerialNum`, `.Value`, `.IsValid` | `Modules.Utils/CHandle.cs` |
| Ray trace | `Trace.TraceShape(Vector start, QAngle angles, CBaseEntity? ignoreEntity, TraceOptions? options)` (8192u), `Trace.TraceEndShape(...)`, `TraceResult.DidHit()`, `.HitEntity()`, `.EndPos`, `.Fraction`; `TraceOptions { InteractsAs, InteractsWith, InteractsExclude }` of `Contents` flags | `Modules.Utils/Trace.cs`, `TraceResult.cs`, `TraceOptions.cs`, `Contents.cs` |
| World AABB | `Trace.GetEntityWorldSpaceAABB(CBaseEntity, out Vector mins, out Vector maxs)` | `Trace.cs` |
| Eye position | `pawn.AbsOrigin` + `pawn.ViewOffset.Z` (`CNetworkViewOffsetVector`), angles `CCSPlayerPawn.EyeAngles` | `Core/CBaseModelEntity.cs:138`, `Core/CCSPlayerPawn.cs:371` |
| Voice | `Listeners.OnClientVoice(int playerSlot)`; `CCSPlayerController.VoiceFlags` (get/set, `[Flags] VoiceFlags { Normal=0, Muted=1, All=2, ListenAll=4, Team=8, ListenTeam=0x10 }`); `receiver.SetListenOverride(CCSPlayerController sender, ListenOverride o)` / `GetListenOverride`, `ListenOverride { Default, Mute, Hear }` | `Core/Listeners.cs:152`, `Core/CCSPlayerController.cs:68,622`, `API/VoiceFlags.cs`, `API/ListenOverride.cs` |
| Damage listeners | `Listeners.OnEntityTakeDamagePre(CBaseEntity entity, CTakeDamageInfo info)` → `HookResult`; `OnEntityTakeDamagePost`; `OnPlayerTakeDamagePre/Post`; `CTakeDamageInfo.Attacker`, `.Inflictor`, `.Ability` (weapon), `.Damage` | `Core/Listeners.cs:46-76`, `Core/CTakeDamageInfo.cs` |
| Buttons pressed | `Listeners.OnPlayerButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)`; polling `controller.Buttons` (`PlayerButtons.Attack2 = 0x800`) | `Core/Listeners.cs:230`, `Core/CCSPlayerController.cs:23` |
| Ticks / time | `Listeners.OnTick` (64 Hz), `Server.CurrentTime`, `Server.TickCount`, `Server.NextFrame`, `Server.RunOnTick`, `BasePlugin.AddTimer(float, Action, TimerFlags?)` | `API/Server.cs` |
| Give / remove weapons | `controller.GiveNamedItem(string)` / `GiveNamedItem<T>(string)` (returns entity), `controller.RemoveWeapons()`, `controller.DropActiveWeapon()`, `pawn.RemovePlayerItem(CBasePlayerWeapon)`, `entity.Remove()` | `Core/CCSPlayerController.cs:379-480`, `Core/CBasePlayerPawn.cs:101` |
| Weapon fields | `CBasePlayerWeapon.NextPrimaryAttackTick`, `.NextSecondaryAttackTick` (ints, ticks), `.Clip1`, `.ReserveAmmo`; `CCSPlayer_WeaponServices.NextAttack` (float); `CPlayer_WeaponServices.ActiveWeapon`, `.MyWeapons`; `CCSWeaponBase.PrevOwner` (CHandle<CCSPlayerPawn>); `CEconEntity.AttributeManager.Item.ItemDefinitionIndex` (ushort) | `Core/CBasePlayerWeapon.cs`, `Core/CCSPlayer_WeaponServices.cs`, `Core/CCSWeaponBase.cs:144`, `Core/CEconItemView.cs:9` |
| Pickup permission hook | `VirtualFunctions.CCSPlayer_WeaponServices_CanUseFunc.Hook(Func<DynamicHook,HookResult>, HookMode)` (gamedata `CCSPlayer_WeaponServices_CanUse` present) | `Modules.Memory/VirtualFunctions.cs:67` |
| Kill a player | `controller.CommitSuicide(bool explode, bool force)` / `pawn.CommitSuicide` | `Core/CCSPlayerController.cs:486` |
| Health write + network | `pawn.Health -= n; Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth")` | `API/Utilities.cs:223` |
| Render colour | `CBaseModelEntity.Render` (Color), `.RenderMode`; network with `Utilities.SetStateChanged(ent, "CBaseModelEntity", "m_clrRender")` | `Core/CBaseModelEntity.cs:74-83` |
| Glow | `CBaseModelEntity.Glow` (`CGlowProperty`: `GlowColorOverride`, `GlowType`, `GlowRange`, `GlowRangeMin`, `GlowTeam`, `Glowing`) | `Core/CGlowProperty.cs` |
| Beams (laser lines) | `Utilities.CreateEntityByName<CBeam>("beam")`, `CBeam.Width`, `.EndWidth`, `.EndPos`, `Render`, `DispatchSpawn()`, `Teleport()` | `Core/CBeam.cs` |
| Particles | `Utilities.CreateEntityByName<CParticleSystem>("info_particle_system")`, `.EffectName`, `.StartActive`, `DispatchSpawn()` | `Core/CParticleSystem.cs` |
| Props | `Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic")`, `SetModel(string)`, `AcceptInput("SetParent", pawn, null, "!activator")` | `Core/CBaseModelEntity.cs:144` |
| Sound | `CBaseEntity.EmitSound(string soundEventName, RecipientFilter? recipients = null, float volume = 1f, float pitch = 0f)` → returns `uint` guid; client-side `controller.ExecuteClientCommand("play sounds/....vsnd")` (technique used by eGo) | `Core/CBaseEntity.cs:382` |
| Resource precache | `Listeners.OnServerPrecacheResources(ResourceManifest manifest)` → `manifest.AddResource(path)` | `Core/Listeners.cs:214` |
| Game events | `EventRoundStart`, `EventRoundEnd`, `EventRoundFreezeEnd`, `EventPlayerDeath` (settable `Attacker`, `Weapon` in a Pre hook), `EventPlayerHurt` (`Attacker`, `Userid`, `DmgHealth`, `DmgArmor`, `Weapon`), `EventPlayerTeam` (`Team`, `Oldteam`, `Disconnect`), `EventPlayerSpawn`, `EventCsIntermission`, `EventCsWinPanelMatch`, `EventItemPickup` (`Userid`, `Item`, `Defindex`), `EventItemEquip`, `EventBombDropped`, `EventDoorOpen`/`EventDoorMoving`/`EventDoorClose` (`Entindex`, `Userid`), `EventBreakBreakable` (`Entindex`, `Userid`) | `Core/Event*.cs` |
| Round end | `CCSGameRules.TerminateRound(float delay, RoundEndReason reason)` (via `cs_gamerules` proxy `.GameRules`); `RoundEndReason.CTsWin = 8`, `TerroristsWin = 9` | `Core/CCSGameRules.cs:651` |
| Console / chat / centre text | `controller.PrintToConsole`, `PrintToChat`, `PrintToCenter` (hint), `PrintToCenterHtml(string html, int duration)`, `Server.PrintToChatAll`, `Server.PrintToConsole` | `Core/CCSPlayerController.cs:411-450` |
| ConVars | `ConVar.Find(name)?.SetValue<T>(v)`, `.GetPrimitiveValue<T>()`; plugin cvars via `FakeConVar<T>` | `Modules.Cvars/ConVar.cs`, `FakeConVar.cs` |
| Admin | `AdminManager.PlayerHasPermissions(player, "@css/root")`, `AdminManager.CommandIsOverriden(cmd)`, `AdminManager.GetPermissionOverrides(cmd)`, `controller.IsHLTV` | `Modules.Admin/AdminManager.cs` |
| Menus (stock) | `CenterHtmlMenu`, `ChatMenu`, `MenuManager.OpenCenterHtmlMenu(plugin, player, menu)`, `MenuManager.GetActiveMenu(player)`, `MenuManager.CloseActiveMenu(player)` — **no cancel/exit/timeout callbacks** (see 0.4) | `Modules.Menu/*.cs` |
| User messages (optional sound stop) | `UserMessage.FromPartialName(string)`, `.SetInt(field, v)`, `.Recipients`, `.Send()` | `Modules.UserMessages/UserMessage.cs` |

### 0.2 SourceMod → CSS permission mapping

| SM flag | letter | CSS permission |
|---|---|---|
| `ADMFLAG_ROOT` | z | `@css/root` |
| `ADMFLAG_BAN` | d | `@css/ban` |
| `ADMFLAG_UNBAN` | e | `@css/unban` |
| `ADMFLAG_CHAT` | j | `@css/chat` |

`CheckCommandAccess(client, "override_name", FLAG)` must be ported as a helper `Perms.Check(player, "override_name", "@css/flag")`:
1. If `AdminManager.CommandIsOverriden(override_name)` → `AdminManager.GetPermissionOverrides(override_name)` permissions are required (any of), else
2. `AdminManager.PlayerHasPermissions(player, "@css/flag")`.
(Use the core's helper if it already exists; override names used here: `sm_ultjb_ignore_wardenmute`, `sm_say`, `sm_logwarn`.)

### 0.3 Global pitfalls for this spec (apply everywhere)

1. **`FindAllEntitiesByDesignerName` is a substring match.** `FindAllEntitiesByDesignerName<CEntityInstance>("func_door")` also returns every `func_door_rotating`; `"func_wall"` also returns `func_wall_toggle`; `"func_breakable"` also returns `func_breakable_surf`. Always filter `ent.DesignerName == exact` (or walk `Utilities.GetAllEntities()` once and switch on `DesignerName`).
2. **Never cache controllers/pawns/entities across frames.** Store `EntityHandle.Raw` (uint) and re-resolve with `new CHandle<CBaseEntity>(raw).Value`; treat `null`/`!IsValid` as gone. Map entities are re-created on every round restart (CS2 `CleanUpMap`), so handles from last round are stale.
3. **`HookSingleEntityOutput` is unusable for N doors**: CSS keys it by handler delegate (`EntitySingleOutputHooks[handler]`) and internally hooks the whole classname anyway. Register **one global** `HookEntityOutput` per (classname, output) in `OnLoad` and filter `caller` against the module's current handle set.
4. Entity names may be `null` (unnamed). Normalise `ent.Entity?.Name ?? ""`.
5. Chat messages go through `Lang.T(key, args)` + `Colors.Apply` (core). The original prefix is `{green}[{lightred}SM{green}]` for coloured messages and plain `[SM]` for uncoloured ones; keep that visual choice per message as listed below.
6. Commands: register `css_<name>` (chat `!<name>` / `/<name>`) **and** an `sm_<name>` alias that calls the same handler (console muscle memory of SWBS admins).
7. Never send unknown inputs to entities (CS2 prints a console warning per call). Send `Unlock` only to classes that have it (`func_door`, `func_door_rotating`, `prop_door_rotating`).

### 0.4 Menu requirements (from core)

The originals depend on SourceMod menu semantics that CSS stock menus lack. The core's menu wrapper (call it `JbMenu`) must provide, and this spec assumes:
- `Title` (multi-line), items `(display, info, enabled)` + **spacer** items, `ExitButton` on/off, `ExitBackButton` on/off.
- `Display(JbPlayer p, int timeSeconds = 0) : bool` (false when nothing to show / player invalid).
- Callbacks: `OnSelect(p, info)`, `OnCancel(p, reason)` with reason ∈ `{Exit, ExitBack, Interrupted, Timeout, Disconnected}`, `OnEnd()`.
- `Cancel(p)` (programmatic close → `OnCancel(Interrupted)`).
- Displaying a new menu to a player who has one open must fire `OnCancel(Interrupted)` on the old one (SM behaviour).
If the core lacks this, implement it in a thin wrapper around `CenterHtmlMenu` that tracks the instance per player via `MenuManager.GetActiveMenu`.

---

## 1. Cell Doors (`UltJB_API_CellDoors.sp` + `UltJB_CellDoors.sp` + `ultjb_cell_doors.inc`)

### 1.1 Original behaviour

#### 1.1.1 Per-map config files (`configs/cell_doors/<map>.txt`)

- **Path**: `addons/sourcemod/configs/cell_doors/<lowercase current map name>.txt` (`GetCurrentMap` → `StringToLower`).
- **Format**: plain text, **one entity targetname (`m_iName`) per line**. Each line is `TrimString`'d; empty lines skipped; duplicates ignored (first occurrence kept). No comments, no other fields. Names may contain spaces (e.g. `Jail Door 1`, `cely dvere`). Last line may lack a trailing newline. Matching is **case-sensitive** (`StrEqual`).
- Doors are identified **only by targetname**, never by hammer id or position. Every entity of a supported class that carries that name becomes a cell door (one name may match many entities, e.g. `cells` on 30 sliding doors).
- **Shipped data**: 154 files (32 `ba_*`, 122 `jb_*`). 112 files contain a single name; max is 31 names (`ba_jail_uprise_csgo_v2-5`: `Cell_2_Move1..31`). 231 distinct names. Most common names: `celldoors` (19 maps), `celldoor` (14), `cell_door` (10), `cell_doors` (8), `cells` (7), `Celldoor` (6). Non-English stems seen: `zelle*`/`zellentuer*` (German), `cela` (Polish/Portuguese), `cely`, `dvere` (Czech), `hucre_door` (Turkish), `jail_portes` (French). Names without any cell stem also appear (`h1..h9`, `v1..v4`, `J1..J16`, `aa1..aa10`, `door2_1..6`, `slider1`, `doors_east`, `main`, `privatedisco`, `map_armoury_door`) — i.e. targetname heuristics alone cannot find every map's doors.
- Written only by the in-game editor "Save doors." item (overwrites the file with the current name list in insertion order, `CreateDirectory(..., 775)` if missing).

#### 1.1.2 Supported entity classes, how each is opened and how "opened" is detected

| Class | Added when name matches | `ForceOpen` action | Output hooked for "opened" (single-shot, re-hooked every round) |
|---|---|---|---|
| `func_door` | yes | `Unlock` then `Open` | `OnOpen` |
| `func_door_rotating` | yes | `Unlock` then `Open` | `OnOpen` |
| `prop_door_rotating` | yes | `Unlock` then `Open` | `OnOpen` |
| `func_movelinear` | yes | `Unlock` then `Open` | `OnFullyOpen` |
| `func_breakable` | yes | `Break` | `OnBreak` |
| `func_tracktrain` | yes | `KillHierarchy` (comment: "can't be detected when open so kill it") | none — **cannot be detected** |
| `func_wall_toggle` | yes | `KillHierarchy` | none — cannot be detected |
| `func_brush` | yes; additionally **`Enable` is sent when it is added** (every round start) | `Disable` (comment: never kill a func_brush — not restored on new rounds) | none — cannot be detected |

(`ForceOpen` classifies by `classname[5..9]` of a 10-char buffer: `"door"`/`"move"` → Unlock+Open (this includes `prop_door_rotating` → "prop_door"), `"brea"` → Break, `"wall"` → KillHierarchy, `"trac"` → KillHierarchy, `"brus"` → Disable.)

#### 1.1.3 State machine

- `g_aDoorNames` (string list, loaded from file at map start, edited live by admins).
- `g_aDoorEntRefs` (entity refs) rebuilt by `GetCellDoorEnts()` = clear + `AddCellDoor(name)` for every stored name.
- `g_bHaveCellDoorsOpened` = false on `OnMapStart` and on every `round_start` (post).
- First time any hooked door output fires in the round → `g_bHaveCellDoorsOpened = true` and global forward **`UltJB_CellDoors_OnOpened()`** fires (exactly once per round; later outputs ignored).
- `OnMapStart`: reset flag → load names from file → resolve entities → forward **`UltJB_CellDoors_OnRoundStartReady()`**.
- `round_start` (post): reset flag → resolve entities → forward `OnRoundStartReady`.
- **Important**: `ForceOpen` does *not* set the opened flag itself; only the real output does. Consequences: (a) doors that were already open never fire `OnOpen` → flag stays false; (b) maps whose only cell doors are `func_tracktrain`/`func_wall_toggle`/`func_brush` never report "opened" (Jihad can never be activated there, wardays stay allowed).
- Bug: adding an entity whose targetname is empty (`AddEntityAsDoor` from the Entity Hooker on an unnamed entity, or the editor) adds `""` as a name → **every unnamed entity of all 8 classes becomes a cell door**.

#### 1.1.4 Natives / forwards (library `ultjb_cell_doors`)

| Native | Behaviour |
|---|---|
| `bool UltJB_CellDoors_ForceOpen()` | false if no resolved door refs this round; else performs the per-class action on every valid ref and returns true. |
| `bool UltJB_CellDoors_HaveOpened()` | returns `g_bHaveCellDoorsOpened`. |
| `bool UltJB_CellDoors_DoExist()` | true if ≥1 door ref was resolved this round. |
| `UltJB_CellDoors_AddEntityAsDoor(entity)` | reads the entity's `m_iName`, `AddCellDoor(name)` (adds name to list + resolves all same-named entities now). Not saved to file. |
| `UltJB_CellDoors_RemoveEntityFromBeingDoor(entity)` | `RemoveCellDoor(name)` — removes name and its refs, unhooks outputs. |
| forward `UltJB_CellDoors_OnRoundStartReady()` | after resolution on map start and every round start. |
| forward `UltJB_CellDoors_OnOpened()` | first door open of the round. |

Consumers (must be wired to the port's events/service):
- **TeamRatio** (`OnRoundStartReady`): if there are 0 guards → `ForceOpen()` immediately (first round of a map), then starts the guard-queue randomisation countdown.
- **Settings**: `DoExist()` at round start → starts prisoner auto-respawn while cells are closed (cvar `cells_closed_auto_respawn_seconds`); `OnOpened` → stops auto-respawn; `HaveOpened()` gates the "in-cell weapon clip limit" logic.
- **Days API**: warday menu refuses if `!DoExist()` ("[SM] Cannot select warday because the cell doors are not set."); starting a warday refuses if `HaveOpened()` ("[SM] Cells are open, you cannot do a warday."); `InitWarday` calls `ForceOpen()` if not opened.
- **Jihad**: random jihad only if `DoExist()`; bomb activation requires `HaveOpened()`.
- **EntityHooker** (DB-backed generic hammer-id tagger, type id 9 "Cell Doors", classes = the 8 above, shows properties `m_iName`, `m_target`, `m_iParent`): when an entity is hooked/unhooked it calls `AddEntityAsDoor` / `RemoveEntityFromBeingDoor` (so even the hammer-id path ends up registering by *name*).

#### 1.1.5 Commands, cvars, messages (`UltJB_CellDoors.sp` + API)

| Command | Access | Behaviour |
|---|---|---|
| `sm_celldoors_open`, `sm_cdo` | ADMFLAG_ROOT | `ForceOpen()`; if false → reply `[SM] There are no cell doors to open.`; else `PrintToChatAll("Admin %N forced the cell doors open.")` (plain white, `%N` of console = "Console") + `LogAction("\"%L\" forced open cell doors")`. |
| `sm_celldoors_edit` | ADMFLAG_ROOT | in-game editor (players only; console does nothing). |

| Cvar | Default | Min | Meaning |
|---|---|---|---|
| `ultjb_cell_door_open_time` | `60` | 1.0 | seconds after `round_start` before the doors automatically `ForceOpen()`. The timer is replaced every round_start; it is not cancelled on round end. |

Wardens have **no** cell command in the original; they open cells by pressing the map's button (the plugin only *detects* it via the door outputs). Only root admins can force-open or edit.

#### 1.1.6 The in-game editor (`sm_celldoors_edit`)

- On open: `g_iLookingAtEntRef = INVALID` and the menu is displayed with no timeout; a `PostThinkPost` SDKHook runs every frame for that admin.
- Every frame: trace an infinite ray from eye position along eye angles with `MASK_SOLID` (passes through player clips) using a filter that ignores players; if it misses a door type, retry with `MASK_PLAYERSOLID` (stops at player clips, can hit `func_brush`). The hit entity is accepted if its class is a "door type" (`classname[5..]` ∈ `door, brea, wall, trac, brus, move` — i.e. func_door*, prop_door*, func_breakable(_surf), func_wall(_toggle), func_tracktrain, func_brush, func_movelinear); otherwise the **parent chain** (`m_hParent`) is walked and the first door-type ancestor is used.
- The menu is re-displayed only when the looked-at entity changes (including changing to "nothing").
- Menu layout:
  ```
  Edit Cell Doors
  ------------------
  <classname>
  <targetname>
  ------------------
  1. Add this door.        (or "Remove this door." if its name is already a cell door name)
  2. (spacer)
  3. Save doors.
  ```
  When not looking at a door: title `Edit Cell Doors\n------------------`, item 1 disabled "Look at a cell door.", spacer, "Save doors.".
- After every selection the menu is shown again. Exit (any cancel reason) removes the think hook.
- Add: `AddCellDoor(name)` live → chat `[SM] Door "<name>" is now a cell door.` + `LogAction("\"%L\" added cell door \"%s\"")`. If the ref went invalid: `[SM] There was a problem adding this door, try again.`
- Remove: `[SM] Door "<name>" is no longer a cell door.` + LogAction `removed cell door`. Error: `[SM] There was a problem removing this door, try again.`
- Save: writes the file. Errors: `[SM] Error creating cell_doors directory.`, `[SM] Error creating save file.`; success `[SM] The cell doors have been saved.` + LogAction `saved cell doors`.
- If `DisplayMenu` fails: unhook and `[SM] Something went wrong!`.

### 1.2 CS2 port design — `CellDoorsModule` (Order 5)

#### 1.2.1 Why the CS:GO data cannot be used as-is

CS2 versions of jailbreak maps are re-made/ported by different authors: hammer ids never match (CS2 uses string `m_sUniqueHammerID`), many targetnames are kept but not all, some maps are renamed (`_csgo`, `_v2`, workshop suffixes), and some new CS2 maps open cells through `logic_relay` chains, `point_script`/`cs_script` (CS2 scripting) or animated `prop_dynamic`s instead of `func_door`. Therefore the port uses a **layered resolution**: per-map JSON (authoritative) → legacy txt names (if the map name matches) → automatic detection → admin editor to confirm and save.

#### 1.2.2 Files

- `addons/counterstrikesharp/configs/plugins/SwbsJailbreak/cell_doors/<map>.json` — authoritative per-map config written by the editor (survives plugin updates). `<map>` = `Server.MapName.ToLowerInvariant()` with any path part removed (take text after the last `/` or `\`) and invalid filename characters replaced by `_`.
- `.../cell_doors/legacy/<map>.txt` — the 154 original files copied **verbatim** (ship them in the release). Read with the original rules (trim, skip blank, dedupe, case-sensitive names).
- `.../cell_doors/aliases.json` — optional `{ "<cs2 map name>": "<legacy txt base name>" }` so a renamed CS2 port can reuse an old name list, e.g. `{ "jb_clouds_d": "jb_clouds_final4" }`.

JSON schema (version 1):
```jsonc
{
  "version": 1,
  "map": "jb_clouds_d",
  "source": "manual",            // manual | auto | legacy (informational)
  "openMethod": "doors",         // doors (default, original behaviour) | buttons | both
  "autoOpenSeconds": null,       // optional per-map override of ultjb_cell_door_open_time
  "doors": [
    // Entry with a name = original semantics: every door-class entity with this exact targetname.
    { "name": "zelle_door1" },
    // Unnamed entity: matched by hammer id, else by class + origin (within 16 units).
    { "name": "", "hammerId": "1234567", "class": "func_door", "origin": [512.0, -128.0, 64.0] },
    // Optional per-entry open override: auto | open | break | kill | disable | toggle
    { "name": "cell_wall_toggle", "openMode": "auto" }
  ],
  "buttons": [                   // optional: map buttons that open the cells (used by openMethod buttons/both and by auto-detect)
    { "name": "cell_button", "hammerId": "7654321", "class": "func_button", "origin": [0.0, 0.0, 0.0] }
  ],
  "savedBy": "76561198000000000",
  "savedAt": "2026-10-10T12:00:00Z"
}
```
Read with `System.Text.Json` (`JsonCommentHandling.Skip`, `AllowTrailingCommas = true`, case-insensitive property names). Unknown fields ignored. Malformed file → log error, treat as missing (do not delete).

#### 1.2.3 Data model (module state)

```csharp
enum DoorKind { Door, RotDoor, PropDoor, MoveLinear, Breakable, TrackTrain, WallToggle, Brush }
sealed record DoorEntry(string Name, string? HammerId, string? Class, Vector3? Origin, string OpenMode);
sealed record ButtonEntry(string Name, string? HammerId, string? Class, Vector3? Origin);

List<DoorEntry> _entries;          // the "names list" (g_aDoorNames equivalent), live-editable
List<ButtonEntry> _buttons;
OpenMethod _openMethod; float? _autoOpenOverride; ConfigSource _source;
HashSet<uint> _doorHandles;        // EntityHandle.Raw of resolved door entities this round (g_aDoorEntRefs)
Dictionary<uint, DoorKind> _kindByHandle;
bool _haveOpened;                  // g_bHaveCellDoorsOpened
Timer? _autoOpenTimer;
```
Supported classes map (exact `DesignerName`): `func_door`→Door, `func_door_rotating`→RotDoor, `prop_door_rotating`→PropDoor, `func_movelinear`→MoveLinear, `func_breakable`→Breakable, `func_tracktrain`→TrackTrain, `func_wall_toggle`→WallToggle, `func_brush`→Brush. (Editor "door type" set additionally accepts `func_wall` and `func_breakable_surf` for *display*, exactly like the original; adding them stores the name, but they only resolve if a supported class carries the same name.)

#### 1.2.4 Map start / round start flow

1. `OnMapStart(mapName)`: `_haveOpened = false`; kill `_autoOpenTimer`; **load config** (1.2.5). Do not resolve yet (entities may not exist); resolution happens at the first `round_start`. (The original also fired `OnRoundStartReady` at map start; in CS2 a `round_start` always follows map load, so fire it there only — TeamRatio's "first round, no guards" logic still runs on that first `round_start`.)
2. `OnRoundStart()` (core calls it from `EventRoundStart` post; Order 5 guarantees this runs before Jihad/Settings/Days/TeamRatio):
   - `_haveOpened = false`;
   - `ResolveDoors()` synchronously (1.2.6) — includes sending `Enable` to `func_brush` doors;
   - start the auto-open timer: `AddTimer(max(1, _autoOpenOverride ?? cvar ultjb_cell_door_open_time), () => { _autoOpenTimer = null; ForceOpen(); })` (store with `TimerFlags.STOP_ON_MAPCHANGE`);
   - raise `JbEvents.CellDoorsRoundStartReady` (C# event `ICellDoorService.RoundStartReady`).
   - `Server.NextFrame(ResolveDoors)` once more — CS2 can spawn template/late entities during the first frame; if the resolved set grows, just keep the larger set (no second event).
3. `OnRoundEnd(...)` / `OnMapEnd()`: kill `_autoOpenTimer` (benign deviation: prevents the timer from firing during round-end on the freshly cleaned-up map).
4. `Listeners.OnEntitySpawned`: if a supported-class entity spawns mid-round and matches an entry (name/hammer id), add its handle (mirrors EntityHooker's late hook).
5. `Listeners.OnEntityDeleted`: remove its handle from `_doorHandles`.

#### 1.2.5 Config loading priority

1. `cell_doors/<map>.json` exists and has ≥1 door → use it (`source` from file).
2. Else legacy name list: `legacy/<map>.txt`, or `legacy/<aliases[map]>.txt` → entries = names (openMethod doors). After the first `ResolveDoors()`, if **zero** entities resolved, fall through to 3 (the names did not survive the CS2 port) and log `[CellDoors] legacy names for <map> matched no entities; trying auto-detect`.
3. Else, if cvar `ultjb_cell_doors_autodetect` ≥ 1 → `AutoDetect()` (1.2.7) at the first round start of the map; result entries become the live list (`source = auto`, **not saved** automatically). Announce to online root admins (and console): `[SM] Cell doors were auto-detected for this map (<N> doors via <method>). Use !celldoors_edit to check them and "Save doors." to make them permanent.`
4. Else no doors: `DoExist() == false` (wardays disabled, no jihad, no auto-respawn — exactly the original "no config" behaviour).

#### 1.2.6 `ResolveDoors()`

```
_doorHandles.Clear(); _kindByHandle.Clear();
foreach ent in Utilities.GetAllEntities():
    kind = KindOf(ent.DesignerName)   // exact match table, else skip
    if kind == null: continue
    name = ent.Entity?.Name ?? ""
    base = ent.As<CBaseEntity>()
    foreach entry in _entries:
        if entry.Name != "" ? name == entry.Name                       // case-sensitive, like StrEqual
           : (entry.HammerId != null && base.UniqueHammerID == entry.HammerId)
             || (entry.Class == ent.DesignerName && entry.Origin != null && Distance(base.AbsOrigin, entry.Origin) <= 16):
            add handle, kind; if kind == Brush: base.AcceptInput("Enable"); break
```
Never match `entry.Name == ""` against unnamed entities by name (fixes the original "empty name adds every unnamed door" bug).

#### 1.2.7 Automatic detection (CS2-only feature, used only when no JSON/legacy match)

Collect candidate door entities = all supported-class entities. Compute for each a score; accept candidates with `score >= ultjb_cell_doors_autodetect_threshold` (default **4**). Signals:

| Signal | Score | How |
|---|---|---|
| **Button link**: the entity is targeted (directly or through ≤3 hops of `logic_relay` `OnTrigger`, `logic_branch` `OnTrue/OnFalse`, `math_counter` `OnHitMax`, `logic_case` `OnCase[0..31]`, `trigger_multiple`/`trigger_once` `OnTrigger/OnStartTouch`) by a `func_button`/`func_rot_button` whose own targetname **or** `m_target` matches the cell regex, with an input in `{Open, Toggle, Break, Disable, Kill, KillHierarchy, StartForward, Unlock}` | +3 | For each button read `CBaseButton.OnPressed`, `OnIn`, `OnDamaged` (`CEntityIOOutput.Connections`, iterate `.Next`; skip `MarkedForRemoval`). `TargetDesc` is a targetname (may contain `*` wildcard suffix — support trailing `*` prefix match; ignore `!activator`/`!self`/`!caller`). Resolve names to entities by `Entity.Name`. |
| Button link from a button whose name does *not* match the regex but which is the **closest button to the CT spawn centroid** and links to ≥2 supported doors | +2 | Mirrors eGo's fallback (closest button to CT spawn). |
| Targetname matches the **cell regex** `(?i)(cell|zelle|jail|prison|cela|celda|cely|dvere|hucre|cdoor|jaildoor)` and does not match `(?i)(vent|armou?ry|gun|weapon|secret|lr_|warden)` | +2 | stems taken from the 231 legacy names (1.1.1) |
| Spatial: entity world AABB centre (`Trace.GetEntityWorldSpaceAABB`) within **384 u** horizontally and **192 u** vertically of any `info_player_terrorist` origin | +2 | T spawns are inside cells on virtually every JB map |
| Shares its targetname with ≥2 other supported doors (grouped cell doors) | +1 | |
| Class is `func_door`, `func_door_rotating`, `func_movelinear`, `prop_door_rotating` | +1 | moving doors are the common case |
| Entity is farther than 2048 u from every T spawn | −3 | |

Rules: when any candidate passes, also add **all supported-class entities that share a passing candidate's non-empty targetname** (original name semantics), and store the result as **name entries** (unnamed ones as hammerId/class/origin entries). If the best button found by the "button link" signal exists, also store it in `_buttons` (so the editor can show it). Log the decision table at debug level (`[CellDoors] autodetect: <class> "<name>" hid=<id> score=<s> reasons=<...>`).

eGo technique note (learned, not copied): eGo never tracks doors at all — it finds a `func_button` whose name/target contains "cell"/"door" (falling back through looser filters, then the button nearest the CT spawn) and fires `Unlock` + `Press` on it. That is our `openMethod: "buttons"` path; our default remains the original door-based approach because it also gives *open detection*.

#### 1.2.8 `ForceOpen()` (native port)

```
if _doorHandles.Count == 0 && (_openMethod == doors || _buttons.Count == 0): return false
if _openMethod is buttons or both: foreach resolved button → AcceptInput("Unlock"); AcceptInput("Press", activator: first alive CT pawn ?? null)   // eGo technique
if _openMethod is doors or both:
   foreach handle in _doorHandles (valid only):
     mode = entry.OpenMode == auto ? DefaultFor(kind) : entry.OpenMode
     Door/RotDoor/PropDoor: AcceptInput("Unlock"); AcceptInput("Open")
     MoveLinear:            AcceptInput("Open")
     Breakable:             AcceptInput("Break")
     WallToggle, TrackTrain: AcceptInput("KillHierarchy")
     Brush:                 AcceptInput("Disable")
     overrides: open→"Open", break→"Break", kill→"KillHierarchy", disable→"Disable", toggle→"Toggle"
undetectableOnly = all resolved kinds ∈ {TrackTrain, WallToggle, Brush}
if undetectableOnly: MarkOpened()            // deliberate fix of 1.1.3(b)
else: Server.RunOnTick(Server.TickCount + 64, VerifyOpenedByState)   // 1 s later, fix of 1.1.3(a)
return true
```
Do **not** use `both` with doors whose button `Toggle`s them (the queued Toggle would close what `Open` just opened) — the editor writes `doors` by default; document this in the admin help text.

`VerifyOpenedByState()`: if `!_haveOpened` and any resolved door is open-ish (`CBaseToggle.Toggle_state` ∈ {TS_AT_TOP, TS_GOING_UP} for Door/RotDoor/MoveLinear; `CBasePropDoor.DoorState` ∈ {OPENING, OPEN, AJAR}; Breakable handle no longer valid) → `MarkOpened()`.

#### 1.2.9 Opened detection

Register once in `OnLoad` (global hooks, `HookMode.Post`):
`("func_door","OnOpen")`, `("func_door_rotating","OnOpen")`, `("prop_door_rotating","OnOpen")`, `("func_movelinear","OnFullyOpen")`, `("func_breakable","OnBreak")`.
Handler: `if (!_haveOpened && caller != null && _doorHandles.Contains(caller.EntityHandle.Raw)) MarkOpened(); return HookResult.Continue;`
Supplementary (if a map's door doesn't fire outputs): `EventDoorMoving`/`EventDoorOpen` (post) with `Entindex` → resolve entity, same check; `EventBreakBreakable` likewise.
`MarkOpened()`: `_haveOpened = true;` raise `ICellDoorService.Opened` / `JbEvents.CellDoorsOpened` once; log to Logger: `Cell doors opened.` (type ANY, global — new line, useful in `!logs`).

#### 1.2.10 Commands (port)

| Command (+ alias) | Permission | Behaviour |
|---|---|---|
| `css_celldoors_open` / `css_cdo` (+ `sm_celldoors_open`, `sm_cdo`) | `@css/root` | exactly as original (1.1.5); log via `Logger.LogInformation("\"{admin}\" forced open cell doors")` (LogAction equivalent). |
| `css_celldoors_edit` (+ `sm_celldoors_edit`) | `@css/root` | editor (1.2.11). |
| `css_open` / `css_o` *(NEW, optional)* | warden, or `@css/root` | only if cvar `ultjb_cell_doors_warden_open` = 1 (default **0** = original behaviour). Warden may use it once per round while `!_haveOpened`; calls `ForceOpen()`; chat to all `{green}[{lightred}SM{green}] {olive}Warden {lightred}%s {olive}opened the cell doors.`; replies `Cells are already open.` / `There are no cell doors to open.`. Intended for CS2 ports whose button is broken. |

#### 1.2.11 In-game editor (CS2)

- `css_celldoors_edit` (player only). Per-admin edit session `{ uint lookingAt; uint[] beamHandles; Timer tracker }`.
- Tracker: every **6 ticks** (~0.1 s, `OnTick` counter) for each editing admin:
  1. `eye = pawn.AbsOrigin + (0,0,pawn.ViewOffset.Z)`, `ang = pawn.EyeAngles`.
  2. Pass 1: `Trace.TraceShape(eye, ang, pawn, new TraceOptions { InteractsWith = Solid | Window | PassBullets | Debris | PhysicsProp | CsgoMoveable | WorldGeometry | StaticLevel, InteractsExclude = Player | Hitbox | Trigger | PlayerClip })`.
  3. If the hit entity (or a parent: `ent.CBodyComponent?.SceneNode?.PParent?.Owner`, walk up to 8 levels) is a door type → use it. Else Pass 2 with `PlayerClip` included (finds clip `func_brush`).
  4. If still nothing: pick the nearest door-type entity whose world AABB is within **64 u** of `trace.EndPos` (helps with invisible/nodraw doors).
  5. If the result differs from `lookingAt` → update and **re-display the menu** (only on change — re-opening a centre menu every tick flickers).
- Highlight (NEW, visual aid): while the session is open, every resolved cell door glows **green** (`Glow.GlowColorOverride = Color.Lime`, `GlowType = 3`, `GlowRange = 4096`, `GlowTeam = -1`, `Glowing = true`; restore on close) and the looked-at entity gets a **yellow 12-beam box** (12 `beam` entities reused per session, width 1.5, from `GetEntityWorldSpaceAABB`; teleport/update `EndPos` on change; remove on close/disconnect/map end). Optional: hide beams from non-editors in `Listeners.CheckTransmit`. If glow on brush entities proves not to render, keep only beams.
- Menu (keep original first page, extras after):
  ```
  Edit Cell Doors
  ------------------
  <classname>
  <targetname or "(unnamed) hid:<UniqueHammerID>">
  ------------------
  1. Add this door. | Remove this door. | [disabled] Look at a cell door.
  2. (spacer)
  3. Save doors.
  4. Link this button   (NEW: only when looking at a func_button/func_rot_button; toggles membership in "buttons")
  5. Auto-detect now    (NEW: replaces the live list with AutoDetect() result, chat summary)
  6. Test: open cells   (NEW: ForceOpen() for verification; same as !cdo but no global message)
  7. Next door / 8. Prev door (NEW: cycle through all door-type entities sorted by distance; teleports the admin to the AABB centre with noclip like the SMPlugins EntityHooker tool)
  9. Remove all doors   (NEW, asks confirm)
  ```
  (Center-HTML menus page at ~5 items; items 1–3 must remain on page 1.)
- "Add this door": if the entity has a non-empty name → add `{name}` (all same-named doors join, original semantics); else add `{hammerId, class, origin}`. Re-run `ResolveDoors()` immediately (live). Chat `[SM] Door "<name>" is now a cell door.` (unnamed: `"(unnamed) hid:<id>"`). Log `"<admin>" added cell door "<name>"`.
- "Remove this door": remove the matching entry (by name, else by hammer id/origin); for `func_brush` do nothing extra; re-resolve. Chat `[SM] Door "<name>" is no longer a cell door.`
- "Save doors.": write JSON atomically (write `<map>.json.tmp` then `File.Move(..., overwrite: true)`), set `source = manual`, `savedBy` = admin SteamID64, `savedAt` = UTC ISO-8601. Chat `[SM] The cell doors have been saved.`; failures → `[SM] Error creating cell_doors directory.` / `[SM] Error creating save file.` (log exception).
- Exit/any cancel/disconnect/death? — death does not close it (original didn't); exit, interruption, disconnect and map end close the session (stop tracker, remove beams, restore glows).

#### 1.2.12 Service interface (C#) and events

```csharp
public interface ICellDoorService
{
    bool ForceOpen();                         // UltJB_CellDoors_ForceOpen
    bool HaveOpened { get; }                  // UltJB_CellDoors_HaveOpened
    bool DoExist { get; }                     // UltJB_CellDoors_DoExist  (=_doorHandles.Count > 0 || openMethod==buttons && buttons resolved)
    void AddEntityAsDoor(CBaseEntity ent);    // live only, not saved
    void RemoveEntityFromBeingDoor(CBaseEntity ent);
    IReadOnlyCollection<uint> DoorHandles { get; }
    event Action RoundStartReady;             // UltJB_CellDoors_OnRoundStartReady
    event Action Opened;                      // UltJB_CellDoors_OnOpened (once per round)
}
```

#### 1.2.13 Cvars

| Cvar | Default | Range | Notes |
|---|---|---|---|
| `ultjb_cell_door_open_time` | 60 | ≥1 | original |
| `ultjb_cell_doors_autodetect` | 1 | 0–1 | NEW: 0 = never auto-detect (original behaviour), 1 = auto-detect when no JSON/legacy match |
| `ultjb_cell_doors_autodetect_threshold` | 4 | 1–10 | NEW |
| `ultjb_cell_doors_warden_open` | 0 | 0–1 | NEW optional warden `!open` |

#### 1.2.14 Feasibility

| Feature | Feasibility | Notes |
|---|---|---|
| Per-map door list, open by inputs, auto-open timer, admin force-open | **FULL** | |
| "Cells opened" detection via `OnOpen`/`OnFullyOpen`/`OnBreak` | **FULL** | plus state verification fallback (improvement) |
| CS:GO txt configs | **PARTIAL** | loaded as legacy name lists; work only where the CS2 port kept targetnames (no hammer-id reuse possible). Auto-detect + editor covers the rest. |
| EntityHooker DB (hammer ids) | **PARTIAL** | replaced by JSON `hammerId` (CS2 `UniqueHammerID` string). No MySQL. |
| In-game editor (look-at, add/remove/save) | **FULL** | with highlight extras |
| Doors driven only by CS2 scripts (`cs_script`/Pulse) or animated `prop_dynamic` | **PARTIAL** | cannot be enumerated via IO; use `openMethod: "buttons"` (press the map's button) or `openMode: "kill"`. Open detection then relies on the state check / button press. |

#### 1.2.15 Test plan (cell doors)

1. Load a CS2 JB map with no JSON (e.g. a port of `jb_clouds`). Expect console `[CellDoors] autodetect ...` lines and root admins receive the auto-detect chat line. `!cdo` → all cell doors open, chat `Admin <name> forced the cell doors open.`
2. Wait 60 s in a new round without pressing anything → doors open automatically.
3. Press the map's cell button as CT; check `css_jb_debug cells` (core debug cmd, or `!logs`) shows "Cell doors opened." once.
4. `!celldoors_edit`: look at a cell door → title shows class/name, glow + yellow box; "Remove this door." → door no longer opens with `!cdo`; "Add this door." → opens again; "Save doors." → file `cell_doors/<map>.json` created; `changelevel` same map → doors resolved from JSON (no autodetect line).
5. Map with only a `func_brush` cell door: `!cdo` → brush disabled, Jihad can be activated (opened flag set).
6. Day interplay: start a warday after cells opened → refused with `[SM] Cells are open, you cannot do a warday.`

---

## 2. Logger (`UltJB_Logger.sp` + `ultjb_logger.inc`)

### 2.1 Original behaviour

#### 2.1.1 Storage

- One string array per client index (`g_hPlayerLogs[0..MaxClients]`, 512-char lines); index **0 = global log**. A parallel "type" array exists but, due to a bug (`IntToString` into a 1-char buffer), every stored type is `0` (`LOGTYPE_ANY`); type filtering is therefore never effective (and `sm_log` always asks for ANY anyway).
- Log types enum: `LOGTYPE_ANY=0, LOGTYPE_NONE=1, LOGTYPE_ATTACK=2, LOGTYPE_ITEM=3, LOGTYPE_USE=4, LOGTYPE_BREAK=5, LOGTYPE_LASTREQUEST=6`.
- Line format: `[MM:SS] <message>` where time = `GetTime() - roundStartTime` (wall-clock seconds since round_start), `MM = (t/60)%60`, `SS = t%60`, zero-padded.
- `LogEvent(msg, primary, secondary, type, global=true)`: push to `primary`'s log if >0; to `secondary`'s if >0 and ≠ primary; to global if `global`. If `primary==0 && secondary==0 && !global` → every client with access `sm_logwarn` (ADMFLAG_UNBAN) gets in console `[SM] WARNING: Not sure where to log following message:` + the line.
- **Round start**: the global log is printed to every SourceTV client's console, then **all logs are cleared**, `roundStartTime = GetTime()`, and every in-game client gets in console:
  ```

  ---------------------
  + NEW ROUND STARTED +
  ---------------------

  ```
- Logs are not cleared on disconnect (slot reuse within a round inherits the old lines).
- "Admin" for console broadcasts = `CheckCommandAccess(client, "sm_say", ADMFLAG_CHAT)` evaluated at `OnClientPostAdminCheck` (cached), plus SourceTV.
- Forces `sv_damage_print_enable 0` and re-forces it on every change (hides CS:GO's built-in damage report so players can't see who shot them).

#### 2.1.2 Every event logged

| # | Trigger (original hook) | Message (exact) | Type | Logs written | Live console broadcast |
|---|---|---|---|---|---|
| 1 | `SDKHook_WeaponDropPost` (weapon > 0) | `%N dropped %s.` (`%s` = weapon classname, e.g. `weapon_ak47`) | ITEM | player only (not global) | — |
| 2 | `SDKHook_WeaponEquipPost` (every equip incl. spawn loadout & plugin-given) | `%N picked up %s.` | ITEM | player only | — |
| 3 | `func_button` / `func_rot_button` output `OnIn` (activator = client, or `m_hOwnerEntity` of activator if that is a client) | `   +++   Button pressed by: <name> - (<button targetname>)` | USE | player + global | if presser is **CT**: to admins + SourceTV consoles (same line) |
| 4 | `SDKHook_OnTakeDamage` on `func_button`, `func_rot_button`, `momentary_rot_button` (hooked at map start + each round start) | `Button damaged by: <name> - (<targetname>)` | USE | player + global | CT attacker → admins+STV console `   +++   Button damaged by: ...` (no rate limit) |
| 5 | `SDKHook_OnTakeDamage` on every `func_breakable` ("vent") | `Vent damaged by: %N using <weapon classname or "unknown weapon"> - (<targetname>)` — owner = attacker if attacker==inflictor client, else inflictor's owner; for non-client attackers, attacker's owner. Weapon = `weapon` param, else inflictor (if not a client), else attacker (if not a client). Rate limit: one message per owner per **0.04 s** engine time. Remembers last-hit vent name. | BREAK | player only (not global) | CT: hint text to the CT `You damaged a vent. This is against the rules unless it is LR or Last Guard.` + admins/STV console `   +++   <msg>` |
| 6 | `func_breakable` `OnBreak` (single-shot hook per entity, re-hooked every round) with client activator | `%N broke a vent (<last-hit vent name>).` | BREAK | player + global | — |
| 7 | `SDKHook_SpawnPost`, alive non-observer, not a ModelSkinManager forced respawn | `%N was respawned.` | ANY | player + global | — |
| 8 | `player_death` (post) | `   ---   "<A>" killed "<V>"  --  <W>.` where: suicide (attacker==victim): A = `(CT|T) <victim name>`, V = `themself`, W = `From suicide`; non-player attacker: A = `(team) <victim>`, V = `themself`, W = `From world damage`; else A = `(team) <attacker>`, V = `(team) <victim>`, W = `With weapon <event weapon>`; `(CT)` if team == guards else `(T)` | ATTACK | attacker + victim + global | printed to **every** client's console immediately |
| 9 | `player_hurt` (post) self | `%N damaged themself for %i Health and %i Armor.` | ATTACK | player + global | — |
| 10 | `player_hurt` world (attacker userid 0) | `%N took %d (+%d) world damage.` | ATTACK | victim + global | — |
| 11 | `player_hurt` player→player | `   ---   %N damaged %N for %i Health and %i Armor.` | ATTACK | attacker + victim + global | if attacker is **not a prisoner**: printed to every client's console |
| 12 | `trigger_teleport` `OnEndTouch` with client activator | `%N used teleport (<trigger name>) to <m_target>` | USE | player only | — |
| 13 | While touching ≥1 `trigger_hurt` (`OnStartTouch` counts up, `OnEndTouch` counts down; health sampled on enter and every PostThinkPost) health increased | `%N healed for %d.` | ATTACK | player only | — |
| 14 | External: `UltJB_Logger_LogEvent(message, primary, secondary, type, global=true)` | caller text. Callers: LR start `%N started LR: <category> - <LR name>.` (LASTREQUEST, player+global); warden heal-area `Warden healed %N for %d.` (ATTACK); day start `%N has started <Warday|Freeday> - <day name>.` (ANY); warday weapon selection `%N selected weapon <classname>.` (ANY) | caller | per args | — |

#### 2.1.3 Viewing logs: `sm_log` / `sm_logs` (ADMFLAG_BAN)

- Usage text: `sm_log <player> <num> - List the actions relating to <player> in the past <num> seconds.` (the "num" is actually a **line count**, not seconds).
- Parsing: no args → global log, all lines. Arg1 → `FindTarget(client, arg1, nobots=false, immunity=false)`; if no target, `lines = StringToInt(arg1)` and if ≤0 → stop silently; if target → that player's log. Arg2 (if present) → `lines`. (Note: `FindTarget` failing prints its usual "No matching client" reply before falling back to the number.)
- Restriction: if the caller is an **alive CT** and (global log requested or the target is alive) → `[SM] You cannot check logs of living players while you are a living CT.`
- Reply `[SM] Printing logs to console.` or `[SM] Printing logs for %N to console.`, then a blank console line, then the lines (last `lines` lines, or all if 0) — printed **40 lines per frame** (continuation via 0-second timer) to avoid console overflow.
- Console only; no chat/menu output.

### 2.2 CS2 port design — `LoggerModule` (Order 6) + `IJbLogger`

```csharp
public enum LogType { Any = 0, None = 1, Attack = 2, Item = 3, Use = 4, Break = 5, LastRequest = 6 }
public interface IJbLogger
{
    void LogEvent(string message, JbPlayer? primary, JbPlayer? secondary, LogType type, bool global = true);
}
```
Storage: `List<LogLine>[] _logs = new List<LogLine>[65]` (index = slot+1; index 0 = global) with `record LogLine(string Text, LogType Type)`; store the type correctly (bug not reproduced; no UI change). Cap each list at **5000** lines (drop oldest) to bound memory. `_roundStart = Server.CurrentTime` (use game time; identical to wall clock except during pauses). Clear a slot's list in `OnPlayerConnected` (fix for slot reuse).

Event mapping:

| # | CS2 implementation |
|---|---|
| 1 drop | `Listeners.OnEntityParentChanged(entity, newParent)`: entity `DesignerName` starts with `weapon_`, `newParent` invalid → owner = `entity.As<CCSWeaponBase>().PrevOwner.Value?.OriginalController.Value` → `"{name} dropped {classname}."`. Weapon classname resolved by **item definition index** (see 6.2.4 `WeaponNames.Resolve`), because CS2 reports alternate weapons under the base designer name (USP-S → `weapon_hkp2000`, M4A1-S → `weapon_m4a1`, R8 → `weapon_deagle`, MP5-SD → `weapon_mp7`, CZ75 → `weapon_p250`). Dedupe: ignore a second drop of the same entity handle within the same tick. |
| 2 pickup | `EventItemPickup` (post): `Userid`, resolve the weapon via the player's `WeaponServices.MyWeapons` entry whose def index == `Defindex` (fallback `"weapon_" + Item`) → `"{name} picked up {classname}."`. (Fires for plugin-given items too, like WeaponEquipPost.) |
| 3 button | global output hooks `("func_button","OnPressed")` and `("func_rot_button","OnPressed")` — CS2 equivalent of S1 `OnIn` that reliably fires for non-moving buttons (also hook `OnIn` only if `OnPressed` is found not to fire on a test map). Activator → player: if activator is a `player` pawn → `pawn.As<CCSPlayerPawn>().OriginalController.Value`; else `activator.As<CBaseEntity>().OwnerEntity.Value` → if a pawn, its controller. Name = `caller.Entity?.Name`. |
| 4 button damage | `Listeners.OnEntityTakeDamagePre`: `entity.DesignerName` ∈ {`func_button`, `func_rot_button`, `momentary_rot_button`} → attacker = `info.Attacker.Value` (pawn → controller), else `OwnerEntity` chain → message; return `HookResult.Continue`. |
| 5 vent damage | `OnEntityTakeDamagePre` for `func_breakable`: owner as above (pawn of `info.Attacker`; if attacker isn't a pawn use `info.Inflictor.Value?.OwnerEntity`); weapon = `info.Ability.Value` (CS2 stores the weapon there) → resolved classname, else inflictor `DesignerName` if not a pawn, else attacker's, else `unknown weapon`. Rate-limit 0.04 s per owner (`Server.EngineTime`). CT → `controller.PrintToCenter("You damaged a vent. This is against the rules unless it is LR or Last Guard.")` + admin console broadcast. |
| 6 vent break | global hook `("func_breakable","OnBreak")` (each entity breaks once, so global == single-shot); activator → player; message with last-hit vent name. |
| 7 respawn | `OnPlayerSpawn(JbPlayer)` (core delivers next frame, alive T/CT only); skip if `PlayerModels.IsBeingForceRespawned(slot)` exists and is true. |
| 8 death | `OnPlayerDeath(victim, attacker, ev)` (post). Attacker null/invalid → world branch. Team tag from team at the event (`(CT)`/`(T)`). Weapon = `ev.Weapon` (CS2 strings are like `ak47`, `knife`, `inferno`, `hegrenade`, `world`). Print to all consoles. If Jihad rewrote the attacker in its Pre hook, this logs the bomber (desired). |
| 9–11 hurt | `EventPlayerHurt` (post) with `Userid`, `Attacker`, `DmgHealth`, `DmgArmor`; same three branches; broadcast if attacker team ≠ T. |
| 12 teleport | global hook `("trigger_teleport","OnEndTouch")`; dest = `caller.As<CBaseEntity>().Target`; activator pawn → controller. If a CS2 map's teleport never fires `OnEndTouch` (player is moved out without an end-touch), fall back to `OnStartTouch` (configurable `ultjb_logger_teleport_output`, default `OnEndTouch`). |
| 13 heal | hooks `("trigger_hurt","OnStartTouch")`/`("trigger_hurt","OnEndTouch")` maintain `touchCount[slot]` and `lastHealth[slot]`; in `OnTick` for slots with count > 0 compare `pawn.Health`; increase → `"{name} healed for {delta}."` (player only). Reset counts on death/round start. |
| 14 API | `IJbLogger.LogEvent(...)` used by LR/Days/WardenMenu/WeaponSelection days (exact caller texts above) and by **Jihad** (new lines, 5.2.6) and **CellDoors** (`Cell doors opened.`). |
| round start | in `OnRoundStart`: dump global log to `IsHLTV` controllers' console, clear all, stamp time, print the NEW ROUND banner to every connected human's console. (CS2 GOTV console output isn't visible to anyone — keep for parity, harmless.) |
| damage print | on load and every `OnMapStart`: `ConVar.Find("sv_damage_print_enable")` → if found set `false` and re-force on change (poll each round start; CSS 1.0.376 has no convar-change hook for game cvars). If the cvar doesn't exist in CS2, log once at debug level and skip. |

Command `css_log` / `css_logs` (+ `sm_log`, `sm_logs`), permission `@css/ban`:
- Target resolution with the core's FindTarget-equivalent (partial name, `#userid`, `#steamid`; bots allowed, immunity ignored) — on failure fall back to integer parsing exactly like the original.
- Alive-CT restriction message, replies (`ReplyToCommand`: chat if invoked from chat, console otherwise), blank line, then lines streamed **40 per frame** via `Server.NextFrame` recursion (abort if the caller disconnects).
- Server console invocation (`player == null`) → print to server console (original ignored console; harmless improvement).

Admin cache: `IsLogAdmin(p) = Perms.Check(p, "sm_say", "@css/chat") || p.IsHLTV`, evaluated live (CSS admin data can change at runtime).

Feasibility: all 14 events **FULL** except: #2/#1 use parent-change/item events instead of SDKHooks (**FULL**, small semantic differences: initial spawn equips are logged as pickups only if `item_pickup` fires for them); #12 depends on map trigger outputs (**FULL**/verify); `sv_damage_print_enable` may not exist in CS2 (**PARTIAL**: CS2 has no equivalent server cvar; the client-side damage report can't be hidden by the server).

Test plan: add bots (`bot_add_t`, `bot_add_ct`), shoot a bot as CT → all consoles show `   ---   X damaged Y for N Health and M Armor.`; kill it → `"(CT) X" killed "(T) bot"  --  With weapon ak47.`; break a vent → `!logs` shows `X broke a vent (...)`; as alive CT run `!logs` → refusal message; as dead CT `!logs <bot> 5` → last 5 lines of that bot.

---

## 3. Mute Manager — warden "mute power" (`UltJB_MuteManager.sp`)

### 3.1 Original behaviour

Constants: `VOICE_HOLD_TIME 0.15` s, `VOICE_MAX_SPEAK_TIME 15.0` s, `VOICE_MIN_SPEAK_TIME 9.0` s, `VOICE_MIN_PLAYERS 10`.

State: `g_fVoiceHoldTime`, `g_fTotalSpeakTime` (seconds of "power used"), `g_fStoppedSpeakingTime`, `g_fLastCheckTime[client]`, `g_bIsWardenSpeaking`, `g_bArePlayersMuted`, `g_bWardenMuteActive` (power enabled, default true), HUD timer.

Voice detection: DHooks virtual hook `OnVoiceTransmit` (gamedata `voicehook.csgo`) on every human client → called for every voice packet.

Algorithm (`OnClientVoice(client)`):
1. Ignore unless `client` is the current warden, alive, and `g_bWardenMuteActive`.
2. `g_fVoiceHoldTime = now + 0.15`.
3. If already speaking → `AddToSpeakTime`: `total += (now - lastCheck)` if players are currently muted, else `total += (now - lastCheck) * 0.2`; `lastCheck = now`.
4. Else → `SubtractFromSpeakTime` (recharge: `total -= now - stoppedSpeakingTime; stoppedSpeakingTime = now; clamp ≥ 0`) then `WardenStartSpeaking`: `lastCheck = now`; mute every **alive** player except the warden and except anyone with access `sm_ultjb_ignore_wardenmute` (default ADMFLAG_BAN) by OR-ing `VOICE_MUTED` into their listening flags (both teams!); `speaking = true; playersMuted = true`; start the HUD timer (0.1 s repeat) if not running.

`OnGameFrame` (every frame) while speaking:
- If `total >= MaxSpeakTime()` (power exhausted) and players are muted → `playersMuted = false` and unmute everyone (the warden keeps talking, others may talk over).
- If `now >= g_fVoiceHoldTime` → `WardenStopSpeaking`: `stoppedSpeakingTime = now; speaking = false;` unmute all.
  (`playersMuted` is not reset here; it is set again on the next start.)

`MaxSpeakTime()`: `n = max(10, #T + #CT)`; `maxPl = MaxClients` (if ≤10 → 11); `p = (n-10)/(maxPl-10)`; `return (15-9)*p + 9` → 9 s with ≤10 players, scaling linearly to 15 s at a full server.

Unmute rule (`UnmuteClient`): skip if BaseComm says the client is muted (admin mute, and — on SWBS — also the VoiceChat plugin's dead/T mutes, which go through BaseComm); else clear `VOICE_MUTED`.

HUD (`Timer_MuteHUD`, 0.1 s): stops if there is no warden. If not speaking → recharge (`SubtractFromSpeakTime`). `percent = 100 - round(total / MaxSpeakTime() * 100)`. Hint text to the warden:
```html
<font size='24' color='#6FC41A'>Mute power:</font> <font size='24' color='#XXXXXX'>N</font>
<font size='16' color='#999999'>When you speak you will mute
everyone while power is above 0.</font>
```
`#XXXXXX` = `DE2626` (red) if `N < 1`, else `26D2DE` (cyan). Timer stops when `N == 100` (fully recharged).

Command `sm_wmute` (everyone may type it): non-warden → `{green}[{lightred}SM{green}] {red}You must be warden to use this command.`; warden toggles `g_bWardenMuteActive` → `{green}[{lightred}SM{green}] {yellow}Warden Mute Powers:{lightred} INACTIVE` / `...{lightgreen} ACTIVE`.

Resets: map start → power active, `stoppedSpeakingTime = now`, HUD timer cleared. `UltJB_Warden_OnRemoved` → `total = 0`, stop speaking (unmute all), power active. `UltJB_Warden_OnDeath` → power active. `player_death` → unmute the victim (unless BaseComm-muted) and re-apply guard listening (3.1.1).

#### 3.1.1 Guard audibility (also in this file; only active when SquelchManager is loaded — it always was on SWBS)

- When a **guard spawns alive** (SpawnPost, not a forced model respawn): for every other human player `p`: `SetListenOverride(p, guard, Listen_Yes)` — everyone hears alive guards (overrides team/dead restrictions *and* personal squelches).
- When a guard **dies**: `SquelchManager_ReapplyListeningState(p, guard)` for every `p` (back to Default, or `No` if `p` personally squelched the guard).
- `player_team` (team actually changed): for every other human `p`: if the mover **joins CT** → `SetListenOverride(p, mover, Yes)`, else reapply `p→mover`; and if `p` is a CT → `SetListenOverride(mover, p, Yes)` (CTs are always heard by the mover), else reapply `mover→p`. Net effect: CTs always hear each other; everyone hears alive CTs.
- Bots are never listeners.

### 3.2 CS2 port design

#### 3.2.1 Shared `VoiceService` (core-level, used by §3 and §4)

Owns the effective server-side mute of every player as a set of reasons, so no module can accidentally lift another's mute:
```csharp
[Flags] public enum MuteReason { None = 0, Admin = 1, Timed = 2, VoiceChat = 4 /* dead / T start / map end / connecting */, WardenMute = 8 }
public sealed class VoiceService
{
    public void Set(JbPlayer p, MuteReason r, bool on);   // updates mask, then Apply(p)
    public bool Has(JbPlayer p, MuteReason r);
    public bool IsMuted(JbPlayer p);                       // mask != 0
    public void Apply(JbPlayer p)                          // controller.VoiceFlags = mask != 0 ? flags | Muted : flags & ~Muted (preserve other bits)
    public event Action<JbPlayer> StartedSpeaking;         // derived from OnClientVoice with 0.3 s end delay (SquelchManager VOICE_END_DELAY)
    public event Action<JbPlayer> Speaking;                // every voice packet
    public event Action<JbPlayer> StoppedSpeaking;
}
```
- `Listeners.OnClientVoice(slot)` → `Speaking`; if `now > lastVoice[slot] + 0.3` (i.e. was not speaking) → `StartedSpeaking`; `lastVoice[slot] = now`. `OnTick`: for speaking slots with `now > lastVoice + 0.3` → `StoppedSpeaking`.
- Admin/Timed reasons are set by the core admin "comm" subsystem (sm_mute / sm_silence / timed punishments — specified elsewhere); `VoiceChat` and `WardenMute` reasons by §4/§3.
- Re-`Apply` on `OnPlayerConnected` and after each spawn (CS2 may reset voice flags on reconnect).
- `OnClientVoice` fires for muted senders too (the server still receives their packets — eGo relies on this to show "you are muted" reminders).

#### 3.2.2 `WardenMuteModule` (Order 30)

- Subscribe to `VoiceService.Speaking`: same algorithm as 3.1 using `Server.CurrentTime` for `now`; "is warden" via `IWardenService` (warden spec). Mute = `VoiceService.Set(p, WardenMute, true)` for every alive player except warden and `Perms.Check(p, "sm_ultjb_ignore_wardenmute", "@css/ban")`. Unmute-all = clear `WardenMute` on every connected player (the reason model implements "don't unmute BaseComm-muted players" automatically).
- `OnTick` replaces `OnGameFrame` (64 Hz is fine for a 0.15 s hold).
- `MaxSpeakTime()` uses `Server.MaxPlayers` for `MaxClients`; team counts via `Utilities.GetPlayers()` (T + CT, any alive state, bots included as in `GetTeamClientCount`).
- HUD: `AddTimer(0.1f, ..., TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE)`; send via the core centre-HUD service (priority "warden mute") → `controller.PrintToCenterHtml(html, 1)` with `\n` replaced by `<br>`; same colours/sizes/texts. If the warden has a centre-HTML menu open, skip the HUD frame (CS2 shows one centre-HTML panel at a time; overwriting would hide the menu). Stop rules identical.
- `css_wmute` (+ `sm_wmute`), any player; messages identical (colour tags via Lang).
- Warden events: `IWardenService.Removed` → total = 0, stop speaking, active = true; `IWardenService.Died` → active = true. `OnMapStart` → active = true, `stoppedSpeakingTime = now`, HUD timer null.
- `OnPlayerDeath(victim)` → `VoiceService.Set(victim, WardenMute, false)` + guard listening update.

#### 3.2.3 Guard audibility (port of 3.1.1, in `WardenMuteModule`)

- `OnPlayerSpawn(guard)` (alive CT) → for every other human `p`: `p.Controller.SetListenOverride(guard.Controller, ListenOverride.Hear)`.
- `OnPlayerDeath(guard)` (CT) → for every other human `p`: `Squelch.Reapply(p, guard)` where `Squelch` = the personal-mute service if the port has one (`ListenOverride.Mute` if `p` personally muted the guard), else `ListenOverride.Default`.
- `OnPlayerTeam(mover, old, new)` with `old != new` (skip `disconnect`): exact original rules (3.1.1) using `Hear`/reapply.
- Cvar `ultjb_voice_guards_always_audible` (default **1**) gates this (original only ran with SquelchManager loaded).
- Server cfg recommendation (document in README): `sv_alltalk 1`, `sv_deadtalk 1`, `sv_full_alltalk 1` (if present in the CS2 build) — the plugin's mute reasons then fully control who may talk, as on SWBS.

Feasibility: warden mute power, HUD, `!wmute`, guard audibility — **FULL** (voice packet hook = `Listeners.OnClientVoice`; HTML hint = `PrintToCenterHtml`). Only difference: centre-HTML is shared with menus (arbitration needed).

Test plan: become warden with ≥2 bots/humans alive; hold voice key → other alive humans get "muted" reminders (§4) and HUD shows "Mute power" decreasing from 100; keep talking ~9 s (≤10 players) → power reaches 0 (red), others unmuted though you keep talking; release → power recharges 1 s/s, HUD disappears at 100. `!wmute` toggles INACTIVE/ACTIVE. An admin with `@css/ban` is never muted by the warden.

---

## 4. Voice Chat rules (`UltJB_VoiceChat.sp`)

### 4.1 Original behaviour

Cvar `ultjb_voice_mute_prisoner_time` default **60**, min 0 — seconds prisoners are muted at round start.
State: `g_bHasRoundStarted`, `g_bHasMapEnded`, `g_hMuteTimer`, per-client `g_bIsClientMutedBySourceMod` (an admin used sm_mute), `g_bSkipNextOnClientMute`.
Muting is done through **BaseComm** (`BaseComm_SetClientMute`), and admin mutes are distinguished by watching the `BaseComm_OnClientMute` forward (the plugin's own calls set "skip next").

- `MutePlayer(c)`: if already BaseComm-muted → nothing; else mark skip-next and `BaseComm_SetClientMute(c, true)`.
- `UnmutePlayer(c)`: if muted by an admin → nothing; if a timed mute is active (`TimedPunishment_GetSecondsLeft(c, TP_TYPE_MUTE) >= 0`) → nothing; else `BaseComm_SetClientMute(c, false)`.
- `round_start` (post): if there are **no CTs** → return (round treated as "not started", nobody is muted, deaths don't mute). Else `g_bHasRoundStarted = true`; mute **all Ts** (alive or dead); cancel the timer; if cvar > 0 start the unmute timer; chat to all: `{green}[{lightred}SM{green}] {olive}The prisoners have been muted for {lightred}%i {olive}seconds.` (printed even when the value is 0).
- Unmute timer: unmute **all alive** players; chat `{green}[{lightred}SM{green}] {olive}The prisoners may now speak quietly.`
- `UltJB_Day_OnStart` (any day): cancel timer, unmute all alive players (no message).
- `player_death`: if round started → mute the victim.
- `round_end`: cancel timer, `g_bHasRoundStarted = false`, **unmute everyone** (dead players and spectators can talk during round end).
- `cs_intermission` (map end scoreboard): `g_bHasMapEnded = true`, cancel timer, mute everyone **except admins** (`CheckCommandAccess(c, "sm_say", ADMFLAG_CHAT)`).
- `OnMapStart`: reset both flags; download+precache `sound/buttons/button11.wav`.
- `OnClientPutInServer`: clear admin-mute flag; hook SpawnPost (humans); **mute** the joining player.
- `CheckToMutePlayer(c)` — on SpawnPost and on every `player_team`:
  1. map ended → mute; 2. no CTs → unmute; 3. round not started → unmute; 4. observer or dead → mute; 5. mute timer running and `c` is T → mute; 6. else unmute.
- Muted-speaker notice (`SquelchManager_OnClientStartSpeaking`, i.e. on voice *start*, 0.3 s end delay): only if the client's listening flags contain `VOICE_MUTED`; per-client rate limit **4 s** (engine time); message:
  - timer running: `{green}[{lightred}SM{green}] {red}Your microphone is muted right now. T's are muted for the first 60 seconds of the round.` (the "60" is hard-coded text)
  - otherwise: `{green}[{lightred}SM{green}] {red}Your microphone is muted right now. Possible Reasons: Talking over Warden, Admin Muted, Timed Muted, Dead`
  - plus `buttons/button11.wav` played to that client only.

Admin immunity, precisely: the **only** exemptions are (a) warden-mute skips `sm_ultjb_ignore_wardenmute` (ADMFLAG_BAN) holders (§3) and (b) the map-end mute skips ADMFLAG_CHAT admins. **Dead admins and T admins are muted like everyone else.**

Quirks to keep: spectators are unmuted at `round_end` and are not re-muted at the next `round_start` (only Ts are), so they stay audible until their next team change/spawn; a player who survives a round keeps talking through round end.

### 4.2 CS2 port design — `VoiceChatModule` (Order 31)

Uses `VoiceService` reason `VoiceChat` for `MutePlayer`/`UnmutePlayer` (Admin/Timed reasons are separate bits, so "don't unmute admin/timed-muted players" holds by construction; `g_bSkipNextOnClientMute` bookkeeping is unnecessary).

| Original | CS2 |
|---|---|
| round_start | `OnRoundStart()` — CT count via `Utilities.GetPlayers().Count(p => p.Team == CsTeam.CounterTerrorist)` |
| unmute timer | `AddTimer(cvar, ..., TimerFlags.STOP_ON_MAPCHANGE)`; keep handle; `Kill()` to cancel |
| Day start | subscribe to `IDayService.Started` (days spec) |
| player_death | `OnPlayerDeath(victim, …)` (post) |
| round_end | `OnRoundEnd(...)` |
| cs_intermission | `[GameEventHandler] EventCsIntermission` **and** `EventCsWinPanelMatch` (CS2 always fires the latter at match end) → same handler, idempotent |
| OnClientPutInServer mute | `OnPlayerConnected(p)` → `Set(p, VoiceChat, true)` |
| SpawnPost / player_team → CheckToMutePlayer | `OnPlayerSpawn(p)` and `OnPlayerTeam(p, …)` (skip when `disconnect`) |
| muted notice | `VoiceService.StartedSpeaking` → if `IsMuted(p)` → rate-limit 4 s (`Server.EngineTime`) → chat (same texts; use `{0}` for the cvar value only if you want — keep "60" literal for fidelity) + `p.Controller.ExecuteClientCommand("play sounds/buttons/button11.vsnd")` (configurable `ultjb_voice_muted_sound`, default `sounds/buttons/button11.vsnd`, fallback `sounds/ui/counter_beep.vsnd` if the first doesn't exist on the CS2 build — both are base-game paths of the kind eGo plays with `play`). |
| sm_say admin check | `Perms.Check(p, "sm_say", "@css/chat")` |
| TimedPunishment mute | the core comm subsystem sets `MuteReason.Timed` (and clears it on expiry) |

Optional (NEW, default off to stay faithful): cvar `ultjb_voice_admin_immunity` (0/1, default **0**) — when 1, players with `@css/chat` are never given the `VoiceChat` reason (eGo behaviour).

Feasibility: **FULL** (all rules), sound: **FULL** with a base-game `.vsnd` (custom `.wav` downloads are impossible in CS2).

Test plan: with ≥1 CT: new round → chat "The prisoners have been muted for 60 seconds."; as T try voice → muted notice + click sound (max once per 4 s); after 60 s → "The prisoners may now speak quietly." and voice works; die → muted (generic notice); round end → everyone can talk; with 0 CTs, nobody is muted; `changelevel` scoreboard → only `@css/chat` admins can talk.

---

## 5. Jihad bomb (`UltJB_Jihad.sp` + `ultjb_jihad.inc`)

### 5.1 Original behaviour

#### 5.1.1 Cvars

| Cvar | Default | Range | Meaning |
|---|---|---|---|
| `ultjb_jihad_guards_needed` | 3 | ≥1 | alive guards required for a random jihad |
| `ultjb_jihad_bomb_timer` | 4.2 | ≥0 | fuse seconds after the bomb is placed on the head |
| `ultjb_jihad_percent_chance_to_give` | 40 | 0–100 | chance per round that one prisoner gets jihad |
| `ultjb_jihad_explode_radius` | 750.0 | ≥1 | explosion radius (units) |
| `ultjb_jihad_max_damage` | 235.0 | ≥0 | damage at distance 0 |
| `ultjb_jihad_damage_percent_to_teammates` | 0.2 | 0–1 | multiplier for the bomber's teammates (non-FFA) |

#### 5.1.2 Assets

- World model of the bomb weapon while deployed: replaced by `models/weapons/w_knife.mdl` (disguise: others see a knife).
- Head bomb prop: `models/weapons/w_c4_planted.mdl`, scale 1.5.
- Sounds: activate `survival/breach_activate_01.wav` (from weapon), "akbar" `swoobles/ultimate_jailbreak/akbar.mp3` (custom download, channel VOICE, volume 0.27, from player), explosion `weapons/c4/c4_explode1.wav` (ambient at origin, level 140).
- Particle: `explosion_coop_mission_c4` from `particles/explosions_fx.pcf`.

#### 5.1.3 Lifecycle

- **round_start**: `allowBombDropping = true`. If `UltJB_CellDoors_DoExist()` and `GetRandomInt(1,100) <= chance` → `SetRandomClientAsJihad()`: among alive players count guards; candidates = alive Ts that are not already jihad and don't have the LR **freeday** flag (`LR_FLAG_FREEDAY`); require `guards >= guards_needed` and ≥1 candidate; pick uniformly → `SetJihad`. Jihad state is **not** cleared at round start: a jihad who survived the previous round without activating keeps it (bomb weapon re-given on spawn), so a round can have more than one.
- **SetJihad(c)**: (no-op if already) `isJihad = true; activated = false;` hook PreThinkPost + WeaponSwitchPost; create bomb weapon. **No chat message** is shown to the player.
- **Create bomb weapon**: `UltJB_Weapons_GivePlayerWeapon(c, CSWeapon_BREACHCHARGE)` (so it is flagged as an LR weapon if the player is in an LR), `m_iClip1 = 1`, then run the weapon-switch logic once.
- **Spawn** (`UltJB_Settings_OnSpawnPost`): if jihad: if already activated or not a T → `ClearJihad`; else remove any old bomb weapon and give a fresh one.
- **Weapon switch** to the bomb (`HasJihadBombWeaponDeployed` = jihad && bomb weapon valid && it's the active weapon): set its world-model entity's model to the T knife; player `m_flNextAttack = FLT_MAX`; weapon `m_flNextPrimaryAttack = FLT_MAX` (can't throw/place the breach charge); `m_flNextSecondaryAttack = now + 0.5` (arming delay).
- **PreThinkPost** (every frame): alive && bomb deployed && `IN_ATTACK2` held → `TryActivateBomb`.
- **TryActivateBomb**: bomb weapon missing → `ClearJihad`; `m_flNextSecondaryAttack > now` → return; cells not opened → `m_flNextSecondaryAttack = now + 1.5` and `PrintToChat("You cannot use your bomb until the cell doors open.")` (plain); already activated → return; else player `m_flNextAttack = 0`, play activate sound from the weapon, `activated = true`, start **0.3 s** timer → remove bomb weapon → `CreateBomb`.
- **CreateBomb**: create `prop_dynamic_override` (`w_c4_planted.mdl`), spawn, `m_nSolidType = 0`, scale 1.5; position = player abs origin + (0,0,**90**); angles = (pitch **270**, yaw = player yaw + **180**, roll 0); parent to the player (`SetParent !activator`). Player render colour **(255, 0, 199, 255)** (pink) and `m_nSkin = 1`. Start fuse timer (cvar 4.2 s). Play "akbar" from the player (VOICE, vol 0.27).
- **Fuse end** → `DetonateBomb(c)`.
- **player_death (Pre)** of a jihad: stop the akbar sound on the player, `ClearJihad`, `DetonateBomb` — which only explodes if the head prop exists (i.e. the bomb was activated); a jihad who dies before activating simply loses it.
- **DetonateBomb(c)**: if no head prop → return. Kill prop. `origin = player abs origin + (0,0,32)`. Particle + ambient explosion at `origin`. Create a `weapon_c4` entity as inflictor → `KillPlayersInRadius` → destroy the c4 entity.
- **KillPlayersInRadius(bomber, origin, c4)**:
  - `ffa = Day in progress && Day is FFA`.
  - If not FFA: save + set `mp_teammates_are_enemies 1` (so T-on-T damage applies), save + enable Settings "block terminate round".
  - **Kill the bomber first**: armor = 0, `SDKHooks_TakeDamage(bomber, inflictor c4, attacker bomber, health + 1, weapon c4)`. (Preserves/restores `m_iTeamNum` in case the death switched team.)
  - For every other alive player not having the LR freeday flag: count alive per team; `dist = |origin − victim abs origin|`; skip if `dist > radius`; `damage = max_damage * (1 − dist/radius)`; if not FFA and victim team == bomber's original team → `damage *= teammate_percent`; armor temporarily set to **0** (armor never absorbs), `SDKHooks_TakeDamage(victim, c4, bomber, damage, c4)`, restore armor; if victim died → decrement its team's alive count. **No line-of-sight check** (walls don't block).
  - If not FFA: restore `mp_teammates_are_enemies`; if no alive (non-freeday) Ts or no alive CTs → `UltJB_Settings_SetNextRoundEndReason(true, reason)` + `CS_TerminateRound(4.0, reason)` with reason = CT win if guards alive else T win; restore "block terminate round".
- **ClearJihad(c)**: `isJihad = false`, remove the bomb weapon (drop then remove item, kill world model, kill entity), unhook, stop timer. Called on: drop-confirm, death, disconnect, spawn-as-non-T/activated, Days start, API.
- **Dropping**: client command `drop` (listener) and `CS_OnCSWeaponDrop` while the bomb is deployed → blocked and, if `allowBombDropping` and not activated, show menu:
  ```
  Drop your jihad bomb?
  It will be destroyed and won't explode.
   
  +attack2 activates the bomb.
  Usually right click.
   
  1. No, do not drop it.
  2. (spacer)
  3. (spacer)
  4. Yes, drop it without exploding.
  ```
  No exit button. "Yes" (only if dropping still allowed) → `ClearJihad`.

#### 5.1.4 Natives (library `ultjb_jihad`)

`bool UltJB_Jihad_IsJihad(client)`, `UltJB_Jihad_SetJihad(client)`, `UltJB_Jihad_ClearJihad(client)`, `UltJB_Jihad_SetAllowBombDropping(bool)`.

Callers: **Days API** — on any day start: remember `startedAsJihad[p] = IsJihad(p)` then `ClearJihad(p)` for everyone; on day end: `SetJihad(p)` for those remembered. **Warday "Jihad"** (`DAY_FLAG_STRIP_PRISONERS_WEAPONS | STRIP_GUARDS_WEAPONS | KILL_WORLD_WEAPONS | DISABLE_WEAPON_BUYING | FORCE_FREE_FOR_ALL`): day start → `SetAllowBombDropping(false)`; freeze end → `SetJihad` on **every alive player (both teams)** + music `jihad_day.mp3` (channel ITEM, SNDLEVEL_NONE, vol 0.2); day end → allow dropping, `ClearJihad` everyone.

#### 5.1.5 Original edge cases / bugs (decide explicitly)

1. Timer-path detonation: the bomber is killed inside `KillPlayersInRadius`; its `player_death` Pre handler sees `IsJihad == true` (never cleared on the timer path) and calls `DetonateBomb` again while the killed prop is still resolvable within the frame → a nested second explosion, i.e. nearby players can take damage twice. **Port: detonate exactly once** (guard flag) — treat as a bug.
2. A disconnecting activated jihad leaves the head prop orphaned. **Port: remove it.**
3. Freeday players are invisible to the alive counts used for the forced round end.

### 5.2 CS2 port design — `JihadModule` (Order 40) + `IJihadService`

```csharp
public interface IJihadService
{
    bool IsJihad(JbPlayer p);
    void SetJihad(JbPlayer p);
    void ClearJihad(JbPlayer p);
    void SetAllowBombDropping(bool allow);
}
sealed class JihadState   // JbPlayer.Data<JihadState>() — NOT IRoundScoped (carry-over semantics)
{
    public bool IsJihad, Activated, Detonated;
    public uint BombWeapon;          // EntityHandle.Raw of the bomb item
    public uint HeadProp;            // EntityHandle.Raw of the head prop
    public Timer? BombTimer;         // 0.3 s activation or fuse timer
    public float ArmAt;              // replaces m_flNextSecondaryAttack gating
    public uint LastActiveWeapon;    // weapon-switch detection
    public uint AkbarGuid;           // sound guid (optional stop)
}
```

#### 5.2.1 The bomb item (CS2 substitute for the breach charge)

`weapon_breachcharge` (Danger Zone) does not exist in CS2 → **NOT POSSIBLE** as-is. Substitute **`weapon_c4`** (configurable cvar `ultjb_jihad_item`, default `weapon_c4`), given through `IWeaponService.GivePlayerItem` (so `IsGettingItem` bypasses pickup blockers and LR marking applies via `GivePlayerWeapon(p, WeaponId.C4)`).
- CS2 C4: primary attack = plant (only valid inside a bomb site, which JB maps don't have). Block it anyway: while deployed, every tick set `weapon.NextPrimaryAttackTick = int.MaxValue` + `Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick")` (only write when the value differs).
- Activation stays **+attack2 (right click)**, true to the original (eGo uses +use; we don't).
- Giving C4 to a CT (Warday Jihad gives everyone): if `GiveNamedItem("weapon_c4")` returns null or the item is immediately dropped for a CT, retry with the pawn's `TeamNum` temporarily set to 2 (same technique as 6.2.3) — verify in game.
- Server cfg: `mp_give_player_c4 0` (no stock bomb), `mp_death_drop_c4 0` if present.
- Lost vs original (**PARTIAL**): the knife world-model disguise (CS2 weapons have no separate world-model entity; replacing the C4 model would also break its view model) and concealment — CS2 shows the C4 on the carrier's back, a C4 icon on the T radar/scoreboard. Optional cvar `ultjb_jihad_hide_backpack` is **not** feasible (client-side rendering).

#### 5.2.2 Flow (CS2)

- `OnRoundStart()`: `allowDropping = true`; reset `Detonated` for everyone; if `ICellDoorService.DoExist` and `Random.Shared.Next(1, 101) <= chance` → `AddTimer(1.0f, PickRandomJihad)` (spawn equipment in CS2 is handed out around round start; giving the item 1 s later avoids it being stripped — eGo uses the same 1 s delay). `PickRandomJihad` re-evaluates the original candidate rules at fire time (alive guards ≥ cvar; alive Ts, not jihad, not freeday via `JbPlayer.IsFreeday` / LR flags).
- `SetJihad(p)`: original semantics + give item. Chat to the player (NEW, minimal UX addition; cvar `ultjb_jihad_notify` default 1): `{green}[{lightred}SM{green}] {olive}You have a {lightred}jihad bomb{olive}. Hold it and press {lightred}+attack2 {olive}(right click) to activate it once the cells are open.`
- Spawn restore: `OnPlayerSpawn(p)` → `Server.NextFrame` ×2 (after Settings' spawn loadout; if the Settings spec exposes a `SpawnPost` event, subscribe to that instead) → original restore rules (activated or not T → clear; else replace item).
- `OnTick` (only iterate players with `IsJihad`):
  - Resolve active weapon `pawn.WeaponServices.ActiveWeapon.Value`; if its handle == `BombWeapon` and it differs from `LastActiveWeapon` → "deployed": `ArmAt = now + 0.5`, block primary (above). Update `LastActiveWeapon`.
  - If deployed, alive, and `(controller.Buttons & PlayerButtons.Attack2) != 0` → `TryActivate` (held-button semantics like PreThinkPost).
- `TryActivate`: item invalid → `ClearJihad`; `now < ArmAt` → return; `!ICellDoorService.HaveOpened` → `ArmAt = now + 1.5`, `PrintToChat("You cannot use your bomb until the cell doors open.")`; activated → return; else play activate sound from the pawn (`EmitSound(cvar ultjb_jihad_sound_activate)`, default soundevent `c4.plant`), `Activated = true`, `BombTimer = AddTimer(0.3f, ...)` → remove item → `CreateBomb`.
- `CreateBomb`:
  - `var prop = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic")`; `prop.SetModel(cvar ultjb_jihad_head_model)` (default `weapons/models/c4/weapon_c4.vmdl`, precached in `OnServerPrecacheResources`); `prop.Collision.SolidType = SolidType_t.SOLID_NONE` before spawn; `DispatchSpawn()`; scale 1.5 via `prop.AcceptInput("SetScale", null, null, "1.5")` (fallback: set `CBodyComponent.SceneNode.Scale = 1.5f` before `DispatchSpawn`).
  - `prop.Teleport(pawnOrigin + (0,0,90), new QAngle(270, pawnYaw + 180, 0), Vector.Zero)`; `prop.AcceptInput("SetParent", pawn, null, "!activator")` (eGo's parenting technique).
  - `pawn.Render = Color.FromArgb(255, 255, 0, 199)`; `Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender")`. (`m_nSkin = 1` has no CS2 meaning — drop.)
  - `BombTimer = AddTimer(fuse, () => Detonate(p))`.
  - Akbar: `AkbarGuid = pawn.EmitSound(cvar ultjb_jihad_sound_akbar, null, 0.27f)` **only if the cvar is non-empty** (default empty). A custom sound requires a server workshop addon with a soundevent (e.g. `UltJB.Akbar` in `soundevents/ultjb.vsndevts`, loaded with MultiAddonManager and added in `OnServerPrecacheResources`). CS2 cannot download raw `.mp3` files → **PARTIAL**.
- `EventPlayerDeath` **Pre** (`[GameEventHandler(HookMode.Pre)]`):
  1. If the victim is in `_pendingKillCredit` (set during detonation): `ev.Attacker = bomber controller; ev.Weapon = "c4";` (killfeed shows bomber + C4 icon, Logger logs the bomber). Return.
  2. If victim is jihad: stop akbar (optional: `UserMessage.FromPartialName("SosStopSoundEvent")`, `SetInt("soundevent_guid", (int)AkbarGuid)`, recipients all, `Send()` — verify field name in game), `ClearJihad`, and if `Activated && !Detonated` → `Detonate(victim, deathOrigin)` on `Server.NextFrame` (cannot run CommitSuicide on others inside the death event safely).
- `Detonate(bomber, origin?)` (exactly once: `if (Detonated) return; Detonated = true;`):
  - Remove head prop. `origin = (origin ?? pawn.AbsOrigin) + (0,0,32)`.
  - Particle: `info_particle_system`, `EffectName = cvar ultjb_jihad_particle` (default `particles/explosions_fx/explosion_c4_500.vpcf`, precached), `StartActive = true`, `Teleport(origin)`, `DispatchSpawn()`, `AddEntityIOEvent("Kill", delay: 5f)`.
  - Sound: `particle.EmitSound(cvar ultjb_jihad_sound_explode)` (default soundevent `c4.explode`).
  - Damage (port of `KillPlayersInRadius`, distance-only, armor ignored, no LOS):
    ```
    ffa = IDayService.IsInProgress && IDayService.IsFreeForAll
    bomberTeam = team at detonation start
    aliveT = aliveCT = 0
    foreach p in players where alive && p != bomber && !IsFreeday(p):
        count alive per team
        d = distance(origin, p.Pawn.AbsOrigin); if d > radius continue
        dmg = maxDamage * (1 - d/radius); if (!ffa && p.Team == bomberTeam) dmg *= teammatePct
        idmg = (int)MathF.Round(dmg)   // SDKHooks damage is float, CS:GO health int truncation ~ same
        if idmg <= 0 continue
        if pawn.Health <= idmg: _pendingKillCredit[p.Slot] = bomber.Slot; p.Controller.CommitSuicide(explode: true, force: true); count--
        else: pawn.Health -= idmg; SetStateChanged(pawn, "CBaseEntity", "m_iHealth"); Logger.LogEvent($"{bomber} jihad bomb damaged {p} for {idmg}.", bomber, p, LogType.Attack)
    bomber: _pendingKillCredit[bomber.Slot] = bomber.Slot; if alive → CommitSuicide(true, true)
    clear _pendingKillCredit next frame
    if (!ffa && (aliveT == 0 || aliveCT == 0) && !roundAlreadyEnded):
        reason = aliveCT > 0 ? RoundEndReason.CTsWin : RoundEndReason.TerroristsWin
        ISettingsService.SetNextRoundEndReason(reason); GameRules.TerminateRound(4.0f, reason)
    ```
    No `mp_teammates_are_enemies` toggling is needed (damage is applied directly, bypassing friendly-fire rules). Bomber's own death counts toward the T alive count by exclusion (it's dead), exactly as in the original. "roundAlreadyEnded" = core round-state flag set by `EventRoundEnd` (the engine may already have ended the round when the last player died via CommitSuicide).
    Why not real engine damage: CSS 1.0.376 exposes `VirtualFunctions.CBaseEntity_TakeDamageOld` but `CTakeDamageInfo` can only wrap an existing native pointer (no constructor) — fabricating one is unsafe. `env_explosion` would apply LOS, armor and friendly-fire rules, all of which differ from the original. Direct health + `CommitSuicide` with death-event rewrite is the technique eGo's C4 uses successfully.
  - Lost vs original (**PARTIAL**): non-lethal jihad damage does not fire `player_hurt` (no hit marker/damage report; logged by the module instead); CommitSuicide gives the victim the engine's suicide score adjustment — compensate: after each credited kill `victim.Score += 1` (undo suicide penalty) and `bomber.Score += 1` (`CCSPlayerController.Score`, schema `m_iScore`, then `SetStateChanged(controller, "CCSPlayerController", "m_iScore")`) — verify the engine's penalty value in game.
- Dropping: `AddCommandListener("drop", OnDrop, HookMode.Pre)` → if the bomb is the active weapon: show the drop menu (if allowed and not activated) and return `HookResult.Handled` (blocks the drop). Same menu text/layout (two spacer items so "Yes" is item 4); no exit button. Additionally `EventBombDropped` (post): if `Entindex` is a jihad bomb that left its owner (death etc.) → remove the entity (nobody may pick up a jihad bomb).
- `ClearJihad`: flags off; kill timers; remove item entity if valid (owned or on the ground) via `entity.Remove()` (drop first with `pawn.RemovePlayerItem` if still owned); remove head prop (fix 5.1.5-2); restore render colour to white if it was set by us.
- `OnPlayerDisconnect` → `ClearJihad`.
- Days interplay: `IDayService.Started` → remember + clear all; `IDayService.Ended` → re-`SetJihad` remembered players (Days module calls the service, as in the original).
- Warday Jihad (days spec) calls `SetAllowBombDropping(false)`, `SetJihad` on all alive at freeze end, music via soundevent cvar `ultjb_warday_jihad_music` (default empty → none; custom addon needed) — **PARTIAL** music.

#### 5.2.3 Cvars (port)

Original six (same names/defaults/ranges) plus: `ultjb_jihad_item` (`weapon_c4`), `ultjb_jihad_notify` (1), `ultjb_jihad_head_model` (`weapons/models/c4/weapon_c4.vmdl`), `ultjb_jihad_particle` (`particles/explosions_fx/explosion_c4_500.vpcf`), `ultjb_jihad_sound_activate` (`c4.plant`), `ultjb_jihad_sound_explode` (`c4.explode`), `ultjb_jihad_sound_akbar` (empty). Sound-event names must be verified on the current CS2 build; all are cvars so admins can swap them without a rebuild.

#### 5.2.4 Feasibility

| Feature | Feasibility | Notes |
|---|---|---|
| Random jihad (40 %, ≥3 guards, needs cell doors) | FULL | |
| Bomb item, +attack2 activation, 0.5 s arm delay, cells-open gate, 1.5 s retry | FULL / PARTIAL | breach charge → C4 substitute |
| Knife disguise of the item | NOT POSSIBLE | no world-model entity in CS2; C4 backpack/radar icon visible |
| 0.3 s → head bomb prop, pink player, 4.2 s fuse | FULL | model path to verify |
| Akbar voice line / day music | PARTIAL | needs custom workshop soundevent addon |
| Explosion particle + sound | FULL | CS2 particle/soundevent substitutes |
| Damage model (235 × (1 − d/750), 20 % teammates, no LOS, armor ignored, freeday immune, FFA full) | FULL | |
| Kill credit to bomber in killfeed/logs | FULL | death-event rewrite (eGo technique) |
| player_hurt / hit markers for non-lethal jihad damage | PARTIAL | logged by module |
| Forced round end 4.0 s with correct winner | FULL | |
| Drop menu, no-drop during warday jihad | FULL | |
| Carry-over of an unused jihad to the next round | FULL | |

#### 5.2.5 Test plan

`ultjb_jihad_percent_chance_to_give 100`, `ultjb_jihad_guards_needed 1`, add 1 CT bot + 1 T (you). New round → after 1 s you receive C4 + chat tip. Right click before cells open → "You cannot use your bomb until the cell doors open." (repeats at most every 1.5 s while held). `!cdo` (or wait 60 s) → right click → plant sound, 0.3 s later C4 above your head, you turn pink, 4.2 s later explosion particle + sound; bots within 750 u take 235×(1−d/750) (teammate Ts 20 %); killfeed shows you with C4 icon; round ends 4 s later if a team is wiped. Press G with C4 in hand → drop menu; option 4 destroys the bomb. Die before activating → bomb removed, no explosion. Die during the fuse → immediate explosion at your corpse position.

---

## 6. Weapon Selection API (`UltJB_API_WeaponSelection.sp` + `ultjb_weapon_selection.inc`)

### 6.1 Original behaviour

#### 6.1.1 Categories and flags

Categories: `WPN_CAT_KNIFE=0, PISTOLS=1, HEAVY=2, SMGS=3, RIFLES=4, GRENADES=5` (`NUM_WPN_CATS=6`). A selection request carries `int flags[6]`; a **set bit disables** that weapon. A category whose value equals its "all disabled" mask is hidden. (GRENADES has no bits and is never shown.)

| Category | bit0 | bit1 | bit2 | bit3 | bit4 | bit5 | bit6 | bit7 | bit8 | bit9 | bit10 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| KNIFE | Knife | Taser | | | | | | | | | |
| PISTOLS | Glock | P2000 | USP-S | Dual Berettas | P250 | CZ75-Auto | Five-Seven | Tec-9 | Desert Eagle | R8 Revolver | |
| HEAVY | Nova | XM1014 | Sawed-Off | MAG-7 | M249 | Negev | | | | | |
| SMGS | MAC-10 | MP7 | MP9 | UMP-45 | P90 | PP-Bizon | MP5-SD | | | | |
| RIFLES | Galil AR | FAMAS | AK-47 | M4A4 | M4A1-S | SSG 08 | SG 553 | AUG | AWP | G3SG1 | SCAR-20 |

(The display names above are the exact menu item texts, listed in menu order.)

#### 6.1.2 `UltJB_Weapons_DisplaySelectionMenu(client, success_cb, failed_cb, flags[6]) → bool`

- client 0 or invalid callbacks → false. Stores flags and per-client private forwards (replacing any previous request for that client).
- Shows the **category menu**: title `Select your weapon`, **no exit button**, items (only non-fully-disabled categories): `Knife/Taser`, `Pistols`, `Heavy`, `SMGs`, `Rifles`. If no category → chat `[SM] There are no weapon categories to select.`, fires **failed**, returns false.
- Starts/restarts a per-client timer of `ultjb_select_weapon_time` seconds (default **15**, min 1).
- Category chosen → **weapon menu**: title `Select your weapon`, no exit, **ExitBack** enabled; items = allowed weapons of that category in the table order. The "Knife" item's value is randomly `CSWeapon_KNIFE` or `CSWeapon_KNIFE_T` (re-rolled each time the menu is built). If the category turns out empty → chat `[SM] There are no weapons to select.` → re-show the category menu; if that fails too → stop timer + failed.
- Back (ExitBack) → category menu again (failing → stop timer + failed). Other cancel reasons (e.g. interrupted by another menu) → nothing; the timer keeps running.
- Weapon chosen → stop timer → **success(client, weaponId, flags)**.
- Timer expiry → cancel the open menu → chat `[SM] Selecting a random weapon.` → `GetRandomWeaponFromFlags(flags)`; `CSWeapon_NONE` → chat `[SM] There was an error selecting a random weapon.` + failed; else success.
- No time-limited menu display (menus stay until selection/timer).

#### 6.1.3 Other natives

| Native | Behaviour |
|---|---|
| `bool UltJB_Weapons_CancelWeaponSelection(client)` | stop timer, cancel the open selection menu (no callback). false for client 0. |
| `UltJB_Weapons_GetEntNameFromWeaponID(id, buf, len)` | classname; special cases: CZ75A→`weapon_cz75a`, M4A1_SILENCER→`weapon_m4a1_silencer`, USP_SILENCER→`weapon_usp_silencer`, KNIFE_T→`weapon_knife_t`, KNIFE_GG→`weapon_knifegg`, HEALTHSHOT→`weapon_healthshot`, TAGRENADE→`weapon_tagrenade`, REVOLVER→`weapon_revolver`, MP5NAVY→`weapon_mp5sd`; otherwise `"weapon_" + CS_WeaponIDToAlias(id)`. |
| `UltJB_Weapons_GetItemDefIndexFromWeaponID(id)` | only HKP2000 → 32; else 0 (unused, commented out because setting def indexes risked GSLT bans). |
| `UltJB_Weapons_GetWeaponsDefaultTeam(classname)` | team table below; unknown → ANY (−1). |
| `UltJB_Weapons_GivePlayerWeapon(client, id) → ent/-1` | classname from id; if the weapon's default team ≠ ANY, temporarily set the client's `m_iTeamNum` to it (so CS:GO's loadout system hands out the right item/skin: T/CT/NONE), `GivePlayerItemCustom`, restore team; if the client has started an LR → mark the weapon as an **LR weapon** (stored in `m_iPendingTeamNum = 1`). |
| `UltJB_Weapons_GivePlayerItem(client, classname)` | `GivePlayerItemCustom` (no team trick, no LR mark). |
| `GivePlayerItemCustom` | `g_bIsGettingItem[client] = true` → `GivePlayerItem` → if the new item has no owner, `EquipPlayerWeapon` → flag false. |
| `bool UltJB_Weapons_IsGettingItem(client)` | true only during the give call — used by LR/Days pickup blockers to let plugin-given weapons through. |
| `bool UltJB_Weapons_IsWeaponFromLR(weapon)` | reads the LR mark. LR API blocks other players from picking up someone's LR weapons and kills a player's LR weapons when their LR ends. |
| `UltJB_Weapons_GetRandomWeaponFromFlags(flags[6])` | builds the allowed list in table order (Knife item = random KNIFE/KNIFE_T, Taser), returns a uniform pick, or NONE. |

Default team table (`WEAPON_TEAM_ANY = -1`, `NONE = 0`, `T = 2`, `CT = 3`): knife CT; knife_t T; knifegg, taser ANY; glock T; **hkp2000 NONE**; usp_silencer CT; elite, p250, cz75a ANY; fiveseven CT; tec9 T; deagle, revolver ANY; nova, xm1014 ANY; sawedoff T; mag7 CT; m249, negev ANY; mac10 T; mp7 ANY; mp9 CT; ump45, p90, bizon, mp5sd ANY; galilar T; famas CT; ak47 T; m4a1 CT; m4a1_silencer CT; ssg08 ANY; sg556 T; aug CT; awp ANY; g3sg1 T; scar20 CT; hegrenade, flashbang, smokegrenade, decoy ANY; incgrenade CT; molotov T; tagrenade ANY; healthshot ANY.

Callers (port must support): LR `WeaponBattle_Classic` (flags all 0 — everything incl. knife & taser), `WeaponBattle_MagForMag` (knife disabled, taser allowed), `WeaponBattle_ShotForShot` (knife disabled), `WeaponBattle_NoScope` (only SSG08, SG556, AUG, AWP, G3SG1, SCAR20), Warday `WeaponSelection` (knife+taser disabled; default NEGEV if nothing chosen; logs `%N selected weapon <classname>.`), Warday `HungerGames` (random from flags with AWP/G3SG1/SCAR20 and knife disabled); `GivePlayerWeapon` is used by ~35 LRs/days/Settings/Jihad.

### 6.2 CS2 port design — `WeaponSelectionModule` (Order 8) + `IWeaponService`

#### 6.2.1 Types

```csharp
public enum WeaponId { None, Knife, KnifeT, KnifeGG, Taser,
    Glock, HKP2000, USP_Silencer, Elite, P250, CZ75A, FiveSeven, Tec9, Deagle, Revolver,
    Nova, XM1014, SawedOff, MAG7, M249, Negev,
    MAC10, MP7, MP9, UMP45, P90, Bizon, MP5SD,
    GalilAR, Famas, AK47, M4A1, M4A1_Silencer, SSG08, SG556, AUG, AWP, G3SG1, SCAR20,
    HEGrenade, Flashbang, SmokeGrenade, Decoy, IncGrenade, Molotov, TAGrenade, Healthshot, C4, BreachCharge }

public enum WeaponCategory { Knife = 0, Pistols, Heavy, Smgs, Rifles, Grenades }
public struct WeaponSelectionFlags { public int[] Cat = new int[6]; /* bit set = disabled, bit layout of 6.1.1 */ }
// plus [Flags] helper enums KnifeFlags/PistolFlags/HeavyFlags/SmgFlags/RifleFlags with the same bit values,
// and constants AllKnives=0x3, AllPistols=0x3FF, AllHeavy=0x3F, AllSmgs=0x7F, AllRifles=0x7FF.

public delegate void WeaponSelectedSuccess(JbPlayer player, WeaponId weapon, WeaponSelectionFlags flags);
public delegate void WeaponSelectedFailed(JbPlayer player, WeaponSelectionFlags flags);

public interface IWeaponService
{
    bool DisplaySelectionMenu(JbPlayer p, WeaponSelectedSuccess ok, WeaponSelectedFailed failed, WeaponSelectionFlags flags);
    bool CancelWeaponSelection(JbPlayer p);
    string GetEntNameFromWeaponId(WeaponId id);
    ushort GetItemDefIndexFromWeaponId(WeaponId id);
    int GetWeaponsDefaultTeam(string classname);           // -1 any, 0 none, 2 T, 3 CT
    CBasePlayerWeapon? GivePlayerWeapon(JbPlayer p, WeaponId id);
    CBasePlayerWeapon? GivePlayerItem(JbPlayer p, string classname);
    bool IsGettingItem(JbPlayer p);
    bool IsWeaponFromLR(CBasePlayerWeapon w);
    WeaponId GetRandomWeaponFromFlags(WeaponSelectionFlags flags);
    string ResolveClassname(CBasePlayerWeapon w);          // 6.2.4
}
```

#### 6.2.2 Weapon table (single source of truth)

| WeaponId | classname | CS2 item def index | default team | menu text | category/bit |
|---|---|---|---|---|---|
| Knife | weapon_knife | 42 | CT | Knife | Knife/0 |
| KnifeT | weapon_knife_t | 59 | T | Knife | Knife/0 |
| KnifeGG | weapon_knifegg → **give `weapon_knife`** (golden knife not giveable in CS2; verify) | 41 | ANY | — | — |
| Taser | weapon_taser | 31 | ANY | Taser | Knife/1 |
| Glock | weapon_glock | 4 | T | Glock | Pistols/0 |
| HKP2000 | weapon_hkp2000 | 32 | NONE(0) | P2000 | Pistols/1 |
| USP_Silencer | weapon_usp_silencer | 61 | CT | USP-S | Pistols/2 |
| Elite | weapon_elite | 2 | ANY | Dual Berettas | Pistols/3 |
| P250 | weapon_p250 | 36 | ANY | P250 | Pistols/4 |
| CZ75A | weapon_cz75a | 63 | ANY | CZ75-Auto | Pistols/5 |
| FiveSeven | weapon_fiveseven | 3 | CT | Five-Seven | Pistols/6 |
| Tec9 | weapon_tec9 | 30 | T | Tec-9 | Pistols/7 |
| Deagle | weapon_deagle | 1 | ANY | Desert Eagle | Pistols/8 |
| Revolver | weapon_revolver | 64 | ANY | R8 Revolver | Pistols/9 |
| Nova | weapon_nova | 35 | ANY | Nova | Heavy/0 |
| XM1014 | weapon_xm1014 | 25 | ANY | XM1014 | Heavy/1 |
| SawedOff | weapon_sawedoff | 29 | T | Sawed-Off | Heavy/2 |
| MAG7 | weapon_mag7 | 27 | CT | MAG-7 | Heavy/3 |
| M249 | weapon_m249 | 14 | ANY | M249 | Heavy/4 |
| Negev | weapon_negev | 28 | ANY | Negev | Heavy/5 |
| MAC10 | weapon_mac10 | 17 | T | MAC-10 | Smgs/0 |
| MP7 | weapon_mp7 | 33 | ANY | MP7 | Smgs/1 |
| MP9 | weapon_mp9 | 34 | CT | MP9 | Smgs/2 |
| UMP45 | weapon_ump45 | 24 | ANY | UMP-45 | Smgs/3 |
| P90 | weapon_p90 | 19 | ANY | P90 | Smgs/4 |
| Bizon | weapon_bizon | 26 | ANY | PP-Bizon | Smgs/5 |
| MP5SD | weapon_mp5sd | 23 | ANY | MP5-SD | Smgs/6 |
| GalilAR | weapon_galilar | 13 | T | Galil AR | Rifles/0 |
| Famas | weapon_famas | 10 | CT | FAMAS | Rifles/1 |
| AK47 | weapon_ak47 | 7 | T | AK-47 | Rifles/2 |
| M4A1 | weapon_m4a1 | 16 | CT | M4A4 | Rifles/3 |
| M4A1_Silencer | weapon_m4a1_silencer | 60 | CT | M4A1-S | Rifles/4 |
| SSG08 | weapon_ssg08 | 40 | ANY | SSG 08 | Rifles/5 |
| SG556 | weapon_sg556 | 39 | T | SG 553 | Rifles/6 |
| AUG | weapon_aug | 8 | CT | AUG | Rifles/7 |
| AWP | weapon_awp | 9 | ANY | AWP | Rifles/8 |
| G3SG1 | weapon_g3sg1 | 11 | T | G3SG1 | Rifles/9 |
| SCAR20 | weapon_scar20 | 38 | CT | SCAR-20 | Rifles/10 |
| HEGrenade | weapon_hegrenade | 44 | ANY | | |
| Flashbang | weapon_flashbang | 43 | ANY | | |
| SmokeGrenade | weapon_smokegrenade | 45 | ANY | | |
| Decoy | weapon_decoy | 47 | ANY | | |
| IncGrenade | weapon_incgrenade | 48 | CT | | |
| Molotov | weapon_molotov | 46 | T | | |
| TAGrenade | weapon_tagrenade | 68 | ANY | (not functional in CS2 — verify; callers should avoid) | |
| Healthshot | weapon_healthshot | 57 | ANY | | |
| C4 | weapon_c4 | 49 | T | | |
| BreachCharge | — (does not exist in CS2; `GivePlayerWeapon` returns null and logs a warning) | 70 | | | |

#### 6.2.3 Giving weapons in CS2

`GivePlayerItem(p, classname)`: `_gettingItem[slot] = true; try { w = p.Controller.GiveNamedItem<CBasePlayerWeapon>(classname); } finally { _gettingItem[slot] = false; }`. CS2's `GiveNamedItem` equips automatically (no `EquipPlayerWeapon` needed). Pickup blockers (Days/LR modules' `CCSPlayer_WeaponServices_CanUse` hook / item events) must consult `IsGettingItem` — the CanUse hook runs synchronously inside `GiveNamedItem`, so the flag works exactly like the original.

`GivePlayerWeapon(p, id)`:
1. classname from the table; `BreachCharge` → null.
2. `w = GivePlayerItem(p, classname)`.
3. **Loadout substitution check (CS2 replacement for the m_iTeamNum trick)**: if `w != null` and `w.AttributeManager.Item.ItemDefinitionIndex != expectedDefIndex` (e.g. asked for P2000, got the player's equipped USP-S; asked for M4A4, got M4A1-S; MP7/MP5-SD; Deagle/R8; P250/CZ75), remove it (`pawn.RemovePlayerItem(w); w.Remove();`) and retry once with `pawn.TeamNum` temporarily set to the table's default team (0 for NONE, 2/3, unchanged for ANY) — set, give, restore **in the same call** (no network update in between). If still mismatched, keep the weapon and `Logger.LogWarning`. (Verify in game whether CS2 substitutes at all; if it never does, step 3 is a no-op.)
4. If `ILastRequestService.HasStartedLastRequest(p)` → `_lrWeapons.Add(w.EntityHandle.Raw)`.

`IsWeaponFromLR(w)` = `_lrWeapons.Contains(w.EntityHandle.Raw)`; remove entries in `Listeners.OnEntityDeleted`; clear on map start. (Replaces abusing `m_iPendingTeamNum`.)

Never write `m_iItemDefinitionIndex` (same GSLT-ban caution as the original).

#### 6.2.4 Classname resolution helper

`ResolveClassname(w)`: look up `w.AttributeManager.Item.ItemDefinitionIndex` in the table (authoritative) → classname; fallback `w.DesignerName`. Needed because CS2 reports some alternate weapons under the base designer name (USP-S/`weapon_hkp2000`, M4A1-S/`weapon_m4a1`, R8/`weapon_deagle`, MP5-SD/`weapon_mp7`, CZ75/`weapon_p250`). Used by Logger, Days, LR, Jihad.

#### 6.2.5 Menu flow (port)

Per-player `SelectionState { WeaponSelectionFlags Flags; WeaponSelectedSuccess Ok; WeaponSelectedFailed Failed; Timer? Timer; JbMenu? Open; }`.
- `DisplaySelectionMenu`: invalid player or null callbacks → false. Replace state, build category `JbMenu` (title `Select your weapon`, `ExitButton = false`), display; no items → chat `[SM] There are no weapon categories to select.` + `Failed` + return false. (Re)start timer `AddTimer(cvar ultjb_select_weapon_time, OnTimeout, STOP_ON_MAPCHANGE)` → return true.
- Category select → weapon `JbMenu` (title `Select your weapon`, `ExitButton = false`, `ExitBackButton = true`), items per 6.2.2 order with `info = (int)WeaponId`; Knife item randomly `Knife`/`KnifeT` per build. Empty → `[SM] There are no weapons to select.` → category menu (fail → stop timer + `Failed`).
- `OnCancel(ExitBack)` → category menu (fail → stop timer + `Failed`); other reasons → nothing.
- Select → stop timer → `Ok(p, id, flags)`.
- Timeout → cancel open menu (no callback loop) → `[SM] Selecting a random weapon.` → random; `None` → `[SM] There was an error selecting a random weapon.` + `Failed`; else `Ok`.
- `CancelWeaponSelection` → stop timer + cancel menu, true (false for invalid player).
- Disconnect → drop state silently (original timer would find no client and return).
- Callbacks are invoked inside try/catch; exceptions logged as `Error calling weapon selection success/failed.`

Cvar: `ultjb_select_weapon_time` = 15 (min 1).

Feasibility: menus, random selection, timer, flags, LR marking, IsGettingItem — **FULL**; CS:GO team-loadout trick — **PARTIAL** (def-index verify/retry; CS2 behaviour must be confirmed); breach charge — **NOT POSSIBLE**; TA grenade / golden knife — verify (likely not giveable).

Test plan: `css_jb_test_weaponselect` (debug command in the core debug module, or start LR "Weapon Battle – Classic" vs a bot): menu "Select your weapon" shows 5 categories; Pistols → 10 pistols in order; Back returns; pick AK-47 → both players receive `weapon_ak47` (check def index 7 via `!logs` pickup line); start again and wait 15 s → "[SM] Selecting a random weapon." and a random allowed weapon is given; NoScope LR shows only Rifles with 6 snipers.

---

## 7. Cross-module contract summary

| Service / event | Provided by | Consumed by |
|---|---|---|
| `ICellDoorService` (`ForceOpen`, `HaveOpened`, `DoExist`, `AddEntityAsDoor`, `RemoveEntityFromBeingDoor`, `RoundStartReady`, `Opened`) | CellDoorsModule (Order 5) | TeamRatio, Settings, Days, Jihad, Warden (optional !open) |
| `IJbLogger.LogEvent(msg, primary, secondary, LogType, global)` | LoggerModule (Order 6) | LR, Days, WardenMenu (heal), Warday WeaponSelection, Jihad, CellDoors |
| `VoiceService` (mute reasons, speaking events) | core | WardenMuteModule (30), VoiceChatModule (31), admin comm commands |
| `IJihadService` (`IsJihad`, `SetJihad`, `ClearJihad`, `SetAllowBombDropping`) | JihadModule (40) | Days API, Warday Jihad |
| `IWeaponService` | WeaponSelectionModule (8) | LRs, Days/Wardays, Settings, Jihad, Logger |
| Needs from others: `IWardenService` (current warden, `Removed`, `Died`), `IDayService` (`IsInProgress`, `IsFreeForAll`, `Started`, `Ended`), `ILastRequestService` (`HasStartedLastRequest`, freeday flag), `ISettingsService` (`SetNextRoundEndReason`, optional `SpawnPost`), core round state (`RoundEnded`), core menu (`JbMenu`), core centre-HUD arbitration, core `Perms.Check`, core FindTarget. | | |

## 8. Feasibility summary

| Feature | Feasibility |
|---|---|
| Cell doors: config, open, auto-open 60 s, admin `!cdo`, editor | FULL |
| Cell doors: open detection | FULL (+state fallback) |
| Cell doors: CS:GO txt reuse | PARTIAL (names only) |
| Cell doors: auto-detection (new) | FULL (heuristic) |
| Cell doors: script/prop_dynamic-driven doors | PARTIAL (buttons/kill modes) |
| Logger: all 14 event sources + `!logs` | FULL |
| Logger: hide engine damage print (`sv_damage_print_enable`) | PARTIAL (cvar may not exist) |
| Warden mute power + HUD + `!wmute` | FULL |
| Guard audibility overrides | FULL |
| Voice rules (60 s T mute, dead mute, map-end mute, notices) | FULL |
| Muted-notice sound | FULL (base-game vsnd substitute) |
| Jihad core gameplay & damage model | FULL |
| Jihad item (breach charge) | PARTIAL (C4 substitute) / disguise NOT POSSIBLE |
| Jihad akbar/day music | PARTIAL (custom addon required) |
| Weapon selection menus/API | FULL |
| Team-loadout give trick | PARTIAL (verify/retry) |
