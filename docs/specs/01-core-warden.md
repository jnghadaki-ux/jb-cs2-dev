# Spec 01: Ultimate Jailbreak core (Warden, Warden Menu, Last Guard, Settings, Team Ratio, Points, Player Models)

Port target: one CounterStrikeSharp plugin (`SwbsJailbreak`, CSS API 1.0.376, .NET 10, Metamod:Source 2.0).
Credits string used everywhere: **"Gdaki", designed after HlStriker, SWBS development/lead admins**.

Original sources (SourcePawn, hlstriker / RussianLightning):

| Original file | Version | Port module (suggested class) |
|---|---|---|
| `UltJB_Warden.sp` + `Includes/ultjb_warden.inc` | 1.42 | `WardenModule` (service `IWardenService`) |
| `UltJB_WardenMenu.sp` + `Includes/ultjb_wardenmenu.inc` | 1.11 | `WardenMenuModule` (service `IWardenMenuService`) |
| `UltJB_LastGuard.sp` + `Includes/ultjb_last_guard.inc` | 1.9 | `LastGuardModule` (service `ILastGuardService`) |
| `UltJB_Settings.sp` + `Includes/ultjb_settings.inc` | 1.36 | `SettingsModule` (service `ISettingsService`) |
| `UltJB_TeamRatio.sp` | 1.18 | `TeamRatioModule` (service `ITeamRatioService`, internal) |
| `UltJB_Points.sp` (author RussianLightning) | 1.3 | `PointsModule` |
| `UltJB_PlayerModels.sp` + `Includes/ultjb_player_models.inc` | 1.20 | `PlayerModelsModule` (service `IPlayerModelsService`) |

> **Important clarification about "Settings".** `UltJB_Settings.sp` is **not** a player-facing settings menu. It is the "server rules" plugin: team names, forced cvars, spawn loadout/health/armor/noblock, stable spawn positions, the auto-respawn engine, in-cell weapon nerf, round-end sounds/overlays, the round-end-reason override, kill-command blocking and the "help number" (new-player) lookup. There is no menu in it. This spec documents it in full under section 5.

> **There is no "claim" or "pass" command in the original.** Wardens are chosen automatically by a countdown timer from two queues. `!w` only asks for priority for the first warden of a round. Handing over warden works like this: the warden types `!uw`, and a replacement is picked automatically 3 s later. Do **not** add eGo-style `!pass`/first-come claim (that would be a design change).

---

## 0. Conventions used in this spec

### 0.1 Teams and identity
* `TEAM_PRISONERS = 2` = `CsTeam.Terrorist`, `TEAM_GUARDS = 3` = `CsTeam.CounterTerrorist`.
* SourcePawn kept the warden as a *client serial* (`GetClientSerial`), which changes on reconnect so a reused slot never inherits warden status. **Port:** store `JbPlayer` plus a connection serial. JbPlayer is per-slot, so add a monotonically increasing `int Serial` that is assigned in `OnClientPutInServer` (framework change suggested; `ConnectTime` can be used as a stand-in). Every "serial" in this spec means `(slot, Serial)`.
* "Alive" means `JbPlayer.IsAlive` (controller `PawnIsAlive` **and** pawn `LifeState == LIFE_ALIVE` **and** `Health > 0`).
  **CS2 pitfall:** inside `EventPlayerDeath` (both pre and post) the victim's `PawnIsAlive` can still read `true` for that frame. Any "count alive players" check that runs from a death handler **must explicitly exclude the victim**, or must be deferred with `Server.NextFrame`.
* Bots: the original excluded bots (`IsFakeClient`) from warden selection. Keep that. For local testing add the config `Warden.AllowBotWarden` (default **false**). It is debug-only and does not change behaviour when off.

### 0.2 Chat colours (hls_color_chat → CS2)
The original used `CPrintToChat`/`CPrintToChatAll` with hlstriker tags. In CS:GO these tags mapped to the bytes below. To keep the **same colours** in CS2, convert the tags to the framework tags (`Core/Lang.cs` `Colors`) using this table. The names differ but the bytes match:

| hls tag | CS:GO byte | Framework tag to use (same byte in CS2) |
|---|---|---|
| `{default}` | `\x03` | `{lightpurple}` |
| `{white}` | `\x01` | `{default}` / `{white}` |
| `{yellow}` | `\x09` | `{yellow}` |
| `{green}` | `\x04` | `{green}` |
| `{olive}` | `\x06` | **`{lime}`** |
| `{lightgreen}` | `\x05` | **`{olive}`** |
| `{red}` | `\x02` | **`{darkred}`** |
| `{lightred}` | `\x07` | **`{red}`** |
| `{blue}` | `\x0B` | `{blue}` |
| `{gold}` | `\x10` | `{gold}` |
| `{purple}` | `\x0E` | `{purple}` |

The standard UltJB prefix `{green}[{lightred}SM{green}] {olive}` therefore becomes `{green}[{red}SM{green}] {lime}` in framework tags. The prefix text "SM" should come from the core config so it can be changed, but the default stays `SM` to stay faithful. The CS2 chat line must begin with a space or a control byte, otherwise the first colour code is ignored. The framework chat helper should prepend `" "`.

The message tables below quote the **original hls text**. Implementers convert the tags with the table above.

> **Framework bug spotted while writing this spec:** `Lang.T` runs `string.Format(text, args)` **before** colour tags are expanded. Any phrase that contains `{green}`-style tags *and* `{0}` placeholders throws `FormatException`, the exception is caught, and the raw unformatted text is returned. So **no placeholder will ever be substituted in coloured phrases**. Fix: expand colour tags first (`Colors.Apply`), then format, or use a placeholder syntax that does not clash. Also `JbPlayer.IsValid` uses `PlayerConnectedState.PlayerConnected`, which **does not exist** in CSS 1.0.376. The enum member is `PlayerConnectedState.Connected`.

### 0.3 Hint text → CS2 centre HTML
CS:GO `PrintHintText` rendered HTML in the hint box. CS2 port: `controller.PrintToCenterHtml(html, durationSeconds)`, which CSS implements via `EventShowSurvivalRespawnStatus`. Replace every `\n` with `<br>`. `<font color='#RRGGBB'>` works.
* Per-second countdown updates use duration **2** so there is no flicker gap. One-shot results use duration **5**.
* **Pitfall:** `CenterHtmlMenu` redraws the centre HTML **every tick** for a player with an open menu, which overwrites these hints. That is acceptable, but a shared `Hud` helper should skip non-menu hints for players with `MenuManager.GetActiveMenu(player) != null`, or merge them.
* Original colours used throughout: green `#6FC41A`, red `#DE2626`.

### 0.4 Sounds in CS2
CS2 cannot play arbitrary `.wav`/`.mp3` files. Sounds are either:
1. **`.vsnd` file played client-side**: `controller.ExecuteClientCommand("play sounds/....vsnd")`. This is 2D (no position) and has no pitch or volume control. Known-good built-ins (used by eGo / CS2-SimpleAdmin): `sounds/buttons/bell1.vsnd`, `sounds/ui/counter_beep.vsnd`, `sounds/tools/sfm/beep.vsnd`.
2. **Sound event emitted from an entity**: `entity.EmitSound(string soundEventName, RecipientFilter? recipients = null, float volume = 1f, float pitch = 0f)`. This is positional and supports volume and pitch, but it needs a *sound event name*. Custom ones require a workshop addon with a `soundevents_*.vsndevts` file. Precache it in `Listeners.OnServerPrecacheResources` with `manifest.AddResource("soundevents/....vsndevts")`, and the server must mount the addon (e.g. MultiAddonManager).

Every sound below is a **config string**. If it ends in `.vsnd` the port uses method 1; otherwise it is treated as a sound event name and uses method 2; an empty string means silent. For "positional" originals played via method 1, send only to players within the listed audible radius.

### 0.5 Models in CS2
CS:GO `.mdl` files do not exist in CS2. Models are `.vmdl` paths: `pawn.SetModel(path)`, which calls `CBaseModelEntity.SetModel` → `VirtualFunctions.SetModel`. Every model must be precached in `Listeners.OnServerPrecacheResources` (`manifest.AddResource(path)`). Read the current model with `pawn.CBodyComponent!.SceneNode!.GetSkeletonInstance().ModelState.ModelName`. Apply models **one frame after spawn** (framework `OnPlayerSpawn` already runs next-frame). A wrong path shows the ERROR model. Every model path is config, and an empty string means "do not change the model".

### 0.6 Beams/rings in CS2 (shared `Draw` helper, used by sections 2, 3 and 4)
CS2 has no TempEnts API in CSS (no `TE_SetupBeamPoints` / `TE_SetupBeamRingPoint` / `BeamEnts` / `World Decal`). Every visual is built from **beam entities**:
* **Line:** `var b = Utilities.CreateEntityByName<CBeam>("beam")` (or `CEnvBeam` "env_beam" as eGo does). Set `b.RenderMode = RenderMode_t.kRenderTransAlpha; b.Render = Color.FromArgb(a,r,g,b); b.Width = w; b.EndWidth = w2;` then `b.Teleport(start, new QAngle(), new Vector())`, then `b.EndPos.X/Y/Z = end`, then `b.DispatchSpawn()`. To move it: `Teleport(start)`, set `EndPos`, then `Utilities.SetStateChanged(b, "CBeam", "m_vecEndPos")`. To delete: `b.Remove()`. Track every created beam and delete all of them on round end, map end and plugin unload.
* **Ring:** a regular polygon of N line beams at constant Z. Original TE rings are replaced by persistent polygons that are moved or recoloured rather than recreated every 0.3 s.
* **Expanding pulse ring** (TE `BeamRingPoint` start→end radius over `life`): create a polygon at the start radius, then rebuild the segment endpoints every `step` seconds (default 0.05 s) with a linearly interpolated radius, and remove it after `life`. CS2-SimpleAdmin's beacon uses the same technique.
* `RenderMode_t` in this API only has `kRenderNormal`, `kRenderTransAlpha` and `kRenderNone`. Use `kRenderTransAlpha` for anything with alpha.
* **Lost in CS2:** the custom "through walls" beam material `materials/swoobles/ultimate_jailbreak/wall_beam.vmt` (`$ignorez`). CS2 beams use the default laser material and are occluded by walls unless a workshop material is supplied. If the config `Draw.BeamMaterial` is non-empty, set `CEnvBeam.SpriteName` and precache it. Also lost: per-client visibility of TE (`TE_SendToClient`). Beams are networked to everyone. To restrict visibility, use `Listeners.CheckTransmit` to hide the beam entity from other clients. That is optional; the default is to show to all.

### 0.7 Timers and time base
* Repeating timers: `Plugin.AddTimer(sec, cb, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE)`. Keep the returned `Timer` and call `.Kill()` to stop it. Calling Kill from inside its own callback is fine.
* `GetEngineTime()` → `Server.EngineTime` (double, real time). `GetGameTime()` → `Server.CurrentTime`.
* Per-frame logic: `Listeners.OnTick`. Button edge detection: `Listeners.OnPlayerButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)`, which fires in pre-think and is the replacement for `SDKHook_PreThinkPost` button polling.

### 0.8 Admin flags
`ADMFLAG_KICK` → `@css/kick`, `ADMFLAG_BAN` → `@css/ban`, `ADMFLAG_ROOT` → `@css/root`. Use `[RequiresPermissions("@css/...")]` or `AdminManager.PlayerHasPermissions(player, "@css/...")`.

### 0.9 Commands
Original `sm_xxx` commands are registered as `css_xxx` so chat `!xxx` and `/xxx` work through CSS chat triggers. Whether to also register `sm_xxx` console aliases is a framework-wide decision. Messages that the original sent to "chat + console" go to `PrintToChat` + `PrintToConsole`. `ReplyToCommand` maps to `commandInfo.ReplyToCommand(msg)`.

### 0.10 Shared CS2 helper recipes (verified against CSS 1.0.376 decomp)
| Need | CS2 / CSS call |
|---|---|
| Set health (UltJB_LR_SetClientsHealth semantics: raise max if needed) | `if (pawn.MaxHealth < hp) { pawn.MaxHealth = hp; SetStateChanged(pawn,"CBaseEntity","m_iMaxHealth"); } pawn.Health = hp; SetStateChanged(pawn,"CBaseEntity","m_iHealth");` |
| Armor / helmet / defuser | `pawn.ArmorValue = n; SetStateChanged(pawn,"CCSPlayerPawn","m_ArmorValue"); var isv = pawn.ItemServices?.As<CCSPlayer_ItemServices>(); isv.HasHelmet=false; isv.HasDefuser=false; SetStateChanged(pawn,"CBasePlayerPawn","m_pItemServices");` |
| Render colour (SetEntityRenderColor) | `pawn.RenderMode = RenderMode_t.kRenderTransAlpha; pawn.Render = Color.FromArgb(a,r,g,b); SetStateChanged(pawn,"CBaseModelEntity","m_clrRender");` |
| Gravity reset | `pawn.GravityScale = 1f;` (CS2 keeps gravity across respawn; reset explicitly) |
| Speed reset (`m_flLaggedMovementValue` has **no CS2 equivalent**) | `pawn.VelocityModifier = 1f; SetStateChanged(pawn,"CCSPlayerPawn","m_flVelocityModifier");`. Speed multipliers owned by other modules must be cleared via their services. |
| Noblock (COLLISION_GROUP_DEBRIS_TRIGGER) | Do **not** map the number 2 (in CS2 enum 2 = TRIGGER). Use `pawn.Collision.CollisionGroup = (byte)CollisionGroup.COLLISION_GROUP_DISSOLVING; pawn.Collision.CollisionAttribute.CollisionGroup = same; SetStateChanged(pawn,"CCollisionProperty","m_CollisionGroup"); SetStateChanged(pawn,"CCollisionProperty","m_collisionAttribute");`. DISSOLVING is the value used by popular CS2 noblock plugins; make it config (`Settings.NoblockCollisionGroup`, fallback `COLLISION_GROUP_DEBRIS`). |
| Eye position | `pawn.AbsOrigin + (0,0,pawn.ViewOffset.Z)` (ViewOffset is a `CNetworkViewOffsetVector` with `X/Y/Z`, ~64.09 standing). Eye angles: `pawn.EyeAngles`. |
| World trace from eyes | `Trace.TraceShape(eyePos, pawn.EyeAngles, pawn, new TraceOptions{ InteractsAs = Contents.Player, InteractsWith = Contents.Solid | Contents.Window | Contents.PlayerClip, InteractsExclude = Contents.Player | Contents.NoDraw })` gives a `TraceResult` with `DidHit()`, `EndPos`, `Normal`, `HitEntity()`. `TraceShape` is 8192 units long. Point-to-point: `Trace.TraceEndShape(start,end,ignore,opts)`. |
| Player under crosshair | `GameRules.GetClientAimTarget(controller)` returns `CCSPlayerController?`. Get the game rules via `Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!` (cache per map). |
| Screen fade (Fade usermessage) | `var um = UserMessage.FromId(106); um.SetInt("duration", ms*512/1000); um.SetInt("hold_time", ...); um.SetInt("flags", flags); um.SetInt("color", r | g<<8 | b<<16 | a<<24); um.Send(controller);` Flags: FFADE_IN 1, OUT 2, STAYOUT 8, PURGE 0x10 (eGo-proven). |
| Clan tag | `controller.Clan = "Warden"; Utilities.SetStateChanged(controller,"CCSPlayerController","m_szClan");` (eGo-proven) |
| Switch team keeping alive (CS_SwitchTeam) | `controller.SwitchTeam(CsTeam.X)` |
| Join team like `jointeam` (ChangeClientTeam) | `controller.ChangeTeam(CsTeam.X)` |
| Pending team | `controller.PendingTeamNum` (ref byte); fall back to `TeamNum` if 0 |
| Respawn (CS_RespawnPlayer) | `controller.Respawn()` |
| Suicide (ForcePlayerSuicide) | `controller.CommitSuicide(false, true)` |
| Terminate round | `GameRules.TerminateRound(7.0f, RoundEndReason.RoundDraw)` |
| Hook round termination (CS_OnTerminateRound) | `VirtualFunctions.TerminateRoundFuncLinux.Hook(h, HookMode.Pre)` on Linux (params: 0=this, **1=reason, 2=delay**) / `TerminateRoundFuncWindows` on Windows (**1=delay, 2=reason**). Modify with `hook.SetParam<RoundEndReason>(idx, value)`. Choose by `OperatingSystem.IsWindows()`. Unhook in `OnUnload`. |
| Block event broadcast (SetEventBroadcast) | Pre-hook handler: `info.DontBroadcast = true;` |
| Cvars | `ConVar.Find("name")?.SetValue(value)` or `Server.ExecuteCommand("name value")`. There is no change hook for engine cvars, so re-apply on map start, round start and config load. |

---

## 1. Global round flow (how the 7 modules interlock)

```
OnMapStart ─► Settings: reset original teams, OnRoundStart() (auto-respawn for late prisoners, cell-weapon scan)
           ─► WardenMenu: remove all rings, wardenCount=0
           ─► TeamRatio: preQueueFinished=false   (CellDoors fires OnRoundStartReady at map start → pre-guard-queue timer may start)
round_prestart / cs_pre_restart (every round) ─► Settings: reset "original team/spawn" table
                                             ─► WardenMenu: remove healing area, rings, colours (+ fade purge + hint)
round_start ─► Points: enable iff >=10 clients; deadWarden=0
            ─► Settings: clear overlay(n/a), OnRoundStart()
            ─► LastGuard: remove LG; count guards on team → ENABLED/DISABLED chat
            ─► CellDoors (other spec) → OnRoundStartReady → TeamRatio pre-guard-queue check
player spawns (next frame) ─► Settings.PrepareSpawn → raise PlayerPrepared
                              ├─► PlayerModels: help HP + models → raise ModelsApplied
                              ├─► Warden: maybe start 3 s selection timer (mid-round respawn without warden)
                              └─► Days / Jihad (other specs)
round_freeze_end ─► Warden: if ≥1 alive guard → 10 s initial selection timer, "Type !w ..." to guards
... warden selected ─► WardenMenu "Type !wm ..." ; Days select window starts (other spec)
player_death ─► Warden (pre): warden death → WardenDied event, chat, sound, remove + 3 s reselection
             ─► LastGuard: maybe activate LG / +5 HP per kill
             ─► Points: 50 / 100 points
             ─► Settings: time of death, guard-suicide weapon deletion, auto-respawn deny
round_end ─► Warden: cleanup (remove warden, stop timers, stop line)
          ─► LastGuard: remove
          ─► TeamRatio: FixTeamRatio() (moves players for next round)
          ─► Settings (pre): optionally block round_end broadcast; TerminateRound hook plays win sounds
```

---

## 2. Warden (`UltJB_Warden.sp` v1.42)

### 2.1 Original behaviour

#### 2.1.1 Cvars
| Cvar | Default | Min | Meaning |
|---|---|---|---|
| `ultjb_warden_ver` | "1.42" | – | version |
| `ultjb_warden_select_time` | 3 | 1 | seconds of countdown before selecting a **replacement** warden |
| `ultjb_warden_select_time_initial` | 10 | 1 | seconds of countdown before selecting the **first** warden of the round |

#### 2.1.2 Commands
| Command(s) | Access | Behaviour |
|---|---|---|
| `sm_w`, `sm_warden` | everyone | Warden-queue request (see 2.1.6) |
| `sm_uw` | the current warden only (others: silently ignored, no message) | Unwarden yourself |
| `sm_wwho` | everyone | Tell caller who the warden is |
| `sm_kiss`, `sm_smooch` | warden only (others: silently ignored) | Kiss the player under the crosshair |
| `sm_rw` | ADMFLAG_KICK | Remove the current warden |
| `sm_sw`, `sm_setw`, `sm_setwarden <#steamid\|#userid\|name>` | ADMFLAG_ROOT | Force a guard to be warden |

#### 2.1.3 State
* `g_iWardenSerial`: the current warden (0 = none).
* `g_aQueuePrimary`: ordered list of clients who typed `!w`. It is used **only for the first warden of each round** and **persists across rounds** (entries are removed only when selected, when the player switches to prisoner/spectator, or on disconnect).
* `g_aQueueSecondary`: ordered round-robin list of everybody who joined guards. Prisoners are also appended when they switch to prisoners, which keeps their position. It is never cleared except on disconnect. The selected warden is moved to the end.
* `g_iClientWardenCount[client]`: times this client has been warden **since they connected** (reset in `OnClientPutInServer`). It is incremented on every selection. It is exposed via a native and **Days uses it: a warden can only choose a Freeday or Warday when this count is ≥ 2**.
* `g_iRoundWardenCount`: wardens selected this round (reset at `round_freeze_end`). Zero means "use the primary queue".
* `g_iLastForceRemovedWardenSerial`: the warden last removed with `sm_rw`. That player cannot use `!w`, and is skipped by secondary selection unless they are the only candidate. It is cleared after the next selection attempt finishes and at round end.
* `g_bRoundStarted`: set at freeze end if at least one guard is alive, cleared at round end.
* `g_szPlayerOriginalModel[client]`: the model saved at selection and restored on removal while alive.
* Pointer, line and ring state, bullet history and paintball timing (2.1.9 – 2.1.12).

#### 2.1.4 Selection timer (`StartWardenTimer(bool initial)`)
1. Stop any running selection timer.
2. **Abort (no timer)** if any of these is true: LR is available (`UltJB_LR_CanLastRequest()`), a last guard is active, or a Day is in progress.
3. `countdown = 0`, `max = initial ? select_time_initial(10) : select_time(3)`.
4. Show the hint to every in-game player **who has not started an LR** (`!UltJB_LR_HasStartedLastRequest`):
   `<font color='#6FC41A'>Selecting warden in:</font>\n<font color='#DE2626'>{max-countdown}</font> <font color='#6FC41A'>seconds.</font>`
5. Start a 1.0 s repeating timer. Chat to all: `{green}[{lightred}SM{green}] {olive}A warden will be selected in {lightred}%i {olive}seconds.` (with `max`).

Timer tick (once per second):
1. If a last guard is active: stop silently.
2. `countdown++`. If `countdown < max`: show the countdown hint again and continue. The hints read 10, 9, …, 1 and selection happens on the 10th tick.
3. Selection: if `roundWardenCount == 0`, call `GetPrimaryWarden()`. If that returns nobody, call `GetSecondaryWarden()`.
4. `lastForceRemovedSerial = 0`.
5. Nobody found: hint `<font color='#6FC41A'>A warden could</font> <font color='#DE2626'>not</font> <font color='#6FC41A'>be found.</font>` and chat `{green}[{lightred}SM{green}] {olive}A warden could not be found.` Stop. (The original has a TODO "automatically go into a freeday". It is **not implemented**; do not add it.)
6. Found: hint `<font color='#DE2626'>%N</font>\n<font color='#6FC41A'>has been selected as warden.</font>` and chat `{green}[{lightred}SM{green}] {lightred}%N {olive}has been selected as warden.` Then `SetWarden(client)`.

`GetPrimaryWarden()`: walk the primary queue in order and take the first entry that is in game, alive and not a bot. Remove it from the queue and return it. The primary queue does **not** check the team, because entries are already removed when a player leaves guards. Invalid entries are skipped but not removed.

`GetSecondaryWarden()`: walk the secondary queue in order and skip entries that are not in game, dead, bots, or not on the guard team. If the entry is the force-removed warden, remember its index and continue. The first other valid entry wins. If none is found, fall back to the force-removed warden if they were valid; otherwise return nobody. The chosen player is moved to the **end** of the secondary queue.

#### 2.1.5 SetWarden(client)
1. Save the client's current model name.
2. Set the warden serial.
3. Set the model to `models/player/custom_player/legacy/ctm_heavy.mdl` (the heavy-armour CT model, the main visual identifier).
4. (`ItemEquipment_RecalculateClientsEquipment`, a Swoobles store hat/equipment refresh: N/A.)
5. `clientWardenCount[client]++`, `roundWardenCount++`.
6. Fire forward `UltJB_Warden_OnSelected(client)`.
7. `GiveGuardClanTag(client)`: the warden gets fake clan tag **"Warden"** and chat title **"Warden"** (2.1.13).
8. Hook PreThinkPost for the pointer, ring and line gestures.
No LR, Day or LG checks happen here. Admins can force a warden at any time.

#### 2.1.6 `!w` / `!warden`
* Caller not a guard: tell them `{green}[{lightred}SM{green}] {olive}The current warden is: {lightred}%N{olive}.` or `{green}[{lightred}SM{green}] {olive}There is no warden.` (chat + console `[SM] ...`) and stop.
* Caller is a guard:
  * If a warden exists, show the "current warden is" line, **then continue** (they can still queue, and the current warden can queue themselves).
  * If the caller is the force-removed warden: `{olive}You cannot join the queue after being force removed.` Stop.
  * Add them to the primary queue. If already present: `{olive}You are already in the warden queue.` Otherwise: `{olive}You have been added to the warden queue. This queue is only used for the first warden of each round.` (chat + console)

#### 2.1.7 `!uw`
Warden only. Chat to all `{green}[{lightred}SM{green}] {lightred}%N {olive}removed themself from warden.`, then `TryRemoveClientFromWarden(client)`, which starts the 3 s reselection. On failure: `{olive}Error removing yourself from warden.` + console.

#### 2.1.8 Admin commands
* `sm_rw`:
  * No warden: `{olive}There is no warden to remove.` (chat + console).
  * Otherwise `TryRemove`. On failure: `{olive}Error removing the warden.`
  * On success: `lastForceRemovedSerial = warden` and `PrintToChatAll("[SM] %N has been removed from warden.")` (plain, no colours).
  * The `TryRemove` path starts the 3 s reselection, which skips the removed player.
* `sm_setwarden`:
  * No argument: reply `[SM] Usage: sm_setwarden <#steamid|#userid|name>`.
  * Resolve the target with `FindTarget(caller, arg, nobots=false, immunity=false)`; FindTarget replies its own errors.
  * Target dead: `[SM] Target must be alive to set warden.`
  * Target not a guard: `[SM] Target must be a guard to set warden.`
  * If a warden exists and is the same player: `[SM] Target is already warden.`. If a different warden exists, `TryRemove` them (on failure `[SM] Error removing the warden.`). `TryRemove` starts a 3 s timer.
  * Then `SetWarden(target)` and chat `{green}[{lightred}SM{green}] {lightred}%N {olive}set as warden by {blue}%N{olive}.` (target, admin; console shows as "Console").
  * Finally `StopWardenTimer()` cancels the timer that `TryRemove` started.

#### 2.1.9 `!kiss` / `!smooch` (warden only)
* **Global** 5.0 s cooldown (one static timestamp shared by every warden, engine time). The cooldown is consumed **even if nothing is hit**.
* Trace from the eyes along the eye angles (MASK_SHOT, infinite), only players, ignoring self. If the first thing hit is not a player, nothing happens.
* 50/50 random message to all:
  * `{purple}%N {olive}smooches %s%N{olive}!`
  * `{purple}%N {olive}blows %s%N {olive}a big kiss!`
  * where `%s` = `{blue}` if the target is a guard, otherwise `{lightred}`.
* Sound `sound/swoobles/ultimate_jailbreak/kiss.mp3` from the warden (SNDCHAN_ITEM, SNDLEVEL_NORMAL, positional).

#### 2.1.10 Pointer laser (double-tap USE)
This is evaluated in the warden's PreThinkPost (engine time). `POINTER_BUTTON_EXPIRE_TIME = 0.5`.
* Every think: if `now < buttonExpire + 0.2`, move the pointer end point to the crosshair. The trace is MASK_PLAYERSOLID, infinite, ignores players, and the end point is the trace end.
* While USE is held:
  * **Pointer inactive** (`now > buttonExpire`):
    * If USE was already held last frame: do nothing (a fresh press is required).
    * If `now > startExpire`: this is the first tap, so `startExpire = now + 0.5`.
    * Otherwise (second tap within 0.5 s): **activate**. Update the end point, draw the beam, `beamExpire = now + 0.5`, `buttonExpire = now + 0.5`.
  * **Pointer active**:
    * If this is a fresh press, draw the **pulse ring** at the end point.
    * If `now > beamExpire`, redraw the beam (TE life 0.6 s) and set `beamExpire = now + 0.5`.
    * `buttonExpire = now + 0.5`, so the pointer stays alive while USE is held.
* **Net effect:** double-tap E to turn the laser on. It stays on while E is held and dies about 0.5–0.6 s after release. Tapping E again while it is active (within 0.5 s) sends an expanding pulse ring at the pointed spot.
* **Beam:** TE BeamEnts from the end-point entity to the weapon muzzle. The warden's own copy goes to their viewmodel attachment (`m_hViewModel | 0x1000`). Everyone else sees it from the world-model muzzle (`m_hWeaponWorldModel | 0x5000`), or from the hand attachment (`client | 0x2000`) when holding a grenade (hegrenade, smokegrenade, incgrenade, decoy, molotov, tagrenade, flashbang), knife or healthshot. Material: `wall_beam.vmt` (visible through walls). Width 0.4 → end width 1.0, colour **{240,50,15,255}**, life 0.6, speed 20, framerate 1.
* **Pulse ring:** TE BeamRingPoint at the end point, radius 2.0 → 80.0 (the TE takes diameters, so the visible radius grows 1→40), wall_beam material, life 0.3, width 3.0, colour {240,50,15,255}, speed 20.
* End point anchor: an invisible `prop_dynamic_override` (`models/error.mdl`, EF_NODRAW) that is created lazily and teleported.

#### 2.1.11 Bullet history, line (triple-tap WALK) and ring (triple-tap DUCK)
* **bullet_impact** (warden only):
  * At most once per 0.03 s (to avoid penetration duplicates), save the impact: shift `saved[0]→saved[1]` and `pulled[0]→pulled[1]`, then `saved[0] = impact`.
  * `pulled[0] = impact + planeNormal*10`. The normal comes from a separate trace from the eye along the eye angles (MASK_SHOT, ignoring players).
  * This history is global and is **not reset** when the warden changes.
* **Triple-tap counting** (same logic for DUCK and SPEED/walk): on each fresh press, if `now > expire` reset the count to 0. Then set `expire = now + 0.5` and increment the count. The action fires **only when the count == 3**; the 4th and later taps in the same window do nothing. If DUCK and SPEED are freshly pressed on the same frame, only DUCK is processed.
* **Line (3× SPEED/walk):**
  * `line = (pulled[0], pulled[1])`, which are the last two shots, each pulled 10 units off its surface.
  * Draw it immediately, then every 0.3 s: TE BeamPoints, laserbeam material (normal, *not* through walls), life 0.4, width 5→5, colour **{255,0,136,255}** (pink), framerate 1, speed 10.
  * There is only ever one line; a new triple-tap moves it.
  * It persists until **round end or map end**, even after the warden is removed or dies (only `RoundEndCleanup` stops it).
* **Ring (3× DUCK):** `radius = |saved[0]-saved[1]| / 2`, `centre = saved[0] + normalize(saved[1]-saved[0])*radius` (the midpoint). Call `UltJB_WardenMenu_CreateRing(warden, ringIndex=0 /*Green*/, centre, radius)`; see 3.1.6 for the clamping and the chat message.

#### 2.1.12 Warden paintballs
On each warden bullet impact (rate limit 0.02 s between paints):
* Pick one of 3 decals at random: `materials/swoobles/ultimate_jailbreak/paintballs/splat_blue|splat_green|splat_pink`.
* Trace from the eye to the impact point (MASK_PLAYERSOLID). Paint only if the trace hit the **world** (entity 0); props and players are not painted.
* TE "World Decal" at the impact, sent to all. Decals last until the engine cleans them up (round restart).
* Hook `ItemPaintballs_OnShootPaintball`: return Handled for the warden, so the store paintball item does not double-paint. **This must be honoured by the Store/paintball port if one exists.**

#### 2.1.13 Guard clan tag / chat title (needs ClientSettings + PlayerChat libraries)
* **Joining guards:** save the player's current fake clan tag (or "none"), then set the fake clan tag to `""` and clear the chat title. Guards show no clan tag.
* **Warden:** clan tag `"Warden"` and chat title `"Warden"`.
* **Removed from warden:** back to the guard state (`""`, no title).
* **Leaving guards** (to prisoner/spectator): restore the saved original tag (or clear it), and clear the title.
* If the player changes their tag while holding the guard tag (`ClientSettings_OnFakeClanTagChange` not initiated by this plugin), the new value becomes the "original" that will be restored later.
* PlayerChat formats a titled message as ` \x01[\x0E{title}\x01] \x03{name} : \x01{msg}` (white brackets, purple "Warden"). PlayerChat also lets **the warden bypass `!sg` gags** (squelch), so a gagged player still sees warden messages.

#### 2.1.14 Events and edge cases
| Trigger | Behaviour |
|---|---|
| `round_freeze_end` | If ≥1 **alive** guard: `roundWardenCount=0`, `StartWardenTimer(initial=true)` (10 s), `roundStarted=true`. Then to every in-game guard (alive or dead): `{green}[{lightred}SM{green}] {olive}Type {lightred}!w {olive}if you would like to have priority.` If no alive guard, nothing happens, `roundStarted` stays false, and no warden is selected for the whole round. |
| `UltJB_Settings_OnSpawnPost(any client)` | If `roundStarted` and ≥1 alive guard and no warden and no timer running, `StartWardenTimer()` (3 s). This covers a mid-round respawn after a failed selection. |
| `round_end`, `OnMapEnd` | `RoundEndCleanup`: if a warden exists, `RemoveWarden` (fires OnRemoved; **no model restore, no timer**). Then `lastForceRemoved=0`, stop the selection timer, stop the line timer, `roundStarted=false`. |
| `player_death` (**pre**) | If the victim is the warden: fire `UltJB_Warden_OnDeath(victim)` **first**, then chat `{green}[{lightred}SM{green}] {olive}The warden has died!`, then the sound `sound/music/revenge.wav` to all (SNDLEVEL_NONE, i.e. 2D everywhere). Then `TryRemoveClientFromWarden(victim)`, which fires OnRemoved and starts the 3 s reselection. |
| `player_team` → guards | Append to the secondary queue; give the guard clan tag. |
| `player_team` → prisoners | Remove from primary; append to secondary; remove the guard clan tag; `TryRemove` if warden. |
| `player_team` → spec / unassigned / disconnect | Remove from primary; remove the clan tag; `TryRemove` if warden. |
| `OnClientDisconnect` | `TryRemove` if warden (3 s reselection). |
| `OnClientDisconnect_Post` | Remove from both queues. |
| `OnClientPutInServer` | `wardenCount[client] = 0`. |
| `UltJB_LastGuard_OnActivated_Pre` | Stop the selection timer and `TryRemove(warden)`. **Quirk:** at Pre time the LG serial is not set yet, so `TryRemove→StartWardenTimer` *does* start a 3 s timer and prints "A warden will be selected in 3 seconds." The first tick then sees the LG and stops. |
| `UltJB_LR_OnLastRequestInitialized` | Stop the timer and `TryRemove(warden)`. `StartWardenTimer` then aborts because an LR is available. |
| `UltJB_Day_OnStart` | Stop the timer and `TryRemove(warden)`. `StartWardenTimer` aborts because a day is in progress. |

`TryRemoveClientFromWarden(c)`: only if `c` is the warden. Calls `RemoveWarden(c)`, then `StartWardenTimer()` (3 s, subject to the aborts), then, if `c` is alive, restores the saved model.

`RemoveWarden(c)`: `serial = 0`, `GiveGuardClanTag(c)` (back to the plain guard tag), unhook PreThinkPost, fire `UltJB_Warden_OnRemoved(c)`.

#### 2.1.15 Assets (original)
* Models: `ctm_heavy.mdl`, `models/error.mdl`.
* Sounds: `music/revenge.wav`, `swoobles/ultimate_jailbreak/kiss.mp3`.
* Materials: `sprites/laserbeam.vmt`, `swoobles/ultimate_jailbreak/wall_beam.vmt`, 3 paintball vmt/vtf.

#### 2.1.16 Natives / forwards exposed (`ultjb_warden`)
| Native/forward | Signature | Consumers in SMPlugins |
|---|---|---|
| `UltJB_Warden_GetWarden()` | → client or 0 | WardenMenu, Days API (only the warden selects days), LR API, MuteManager (warden voice), Commands Roll/Timer (warden-only), PlayerChat (gag bypass) |
| `UltJB_Warden_GetClientWardenCount(client)` | → int | Days API (`< 2` blocks choosing Freeday/Warday) |
| forward `UltJB_Warden_OnSelected(client)` | after the counters increment, before the clan tag | WardenMenu ("Type !wm"), Days API (starts the day-select window on the first warden of the round), Xray (rebuild glow) |
| forward `UltJB_Warden_OnRemoved(client)` | after serial=0 | WardenMenu (remove healing area), MuteManager, Xray |
| forward `UltJB_Warden_OnDeath(client)` | from the player_death **pre** hook, before removal | Points (warden kill bonus), Command Timer (stops timer), MuteManager |

### 2.2 CS2 port design

**Module:** `WardenModule : JbModule`, Order ≈ 20. **Service:**
```csharp
public interface IWardenService
{
    JbPlayer? Warden { get; }                       // UltJB_Warden_GetWarden
    bool IsWarden(JbPlayer? p);
    int GetWardenCount(JbPlayer p);                 // UltJB_Warden_GetClientWardenCount (per connection)
    bool TryRemoveWarden(JbPlayer p);               // TryRemoveClientFromWarden semantics
    bool SetWardenByAdmin(JbPlayer target, JbPlayer? admin, out string? error);
    event Action<JbPlayer>? WardenSelected;         // UltJB_Warden_OnSelected
    event Action<JbPlayer>? WardenRemoved;          // UltJB_Warden_OnRemoved
    event Action<JbPlayer>? WardenDied;             // UltJB_Warden_OnDeath (raised before WardenRemoved)
}
```
Per-player data (`JbPlayer.Data<WardenPlayerData>()`, **not** `IRoundScoped`): `int WardenCount`, `string? SavedModel`, `string? SavedClanTag`, `bool HasSavedClanTag`, `bool HasGuardClanTag`. Queues: `List<JbPlayer>` for primary and secondary, with entries removed in `OnPlayerDisconnect`.

#### 2.2.1 Events and hooks used
| Original | CS2 / CSS |
|---|---|
| `round_freeze_end` | `[GameEventHandler] EventRoundFreezeEnd` (framework `OnRoundFreezeEnd`) |
| `round_end` / `OnMapEnd` | framework `OnRoundEnd` / `OnMapEnd` |
| `player_death` pre | `RegisterEventHandler<EventPlayerDeath>(h, HookMode.Pre)`. This must run **before** Points' post handler. Raise `WardenDied` here. |
| `player_team` post | framework `OnPlayerTeam(player, old, new, disconnect)` |
| `bullet_impact` | `[GameEventHandler] EventBulletImpact` (`Userid`, `X`, `Y`, `Z`) |
| `OnClientDisconnect` / `_Post` | framework `OnPlayerDisconnect` (do TryRemove, then remove from queues) |
| SDKHook PreThinkPost (warden only) | `Listeners.OnPlayerButtonsChanged` (edges) + `Listeners.OnTick` (continuous, only while the pointer is active) |
| LastGuard Pre / LR initialized / Day start forwards | subscribe to `ILastGuardService.LastGuardActivating`, LR spec event `LastRequestInitialized`, Days spec event `DayStarted` in `OnAllModulesLoaded` |
| `UltJB_Settings_OnSpawnPost` | `ISettingsService.PlayerPrepared` |

#### 2.2.2 Selection timer
Same algorithm as 2.1.4, using `Plugin.AddTimer(1.0f, Tick, REPEAT|STOP_ON_MAPCHANGE)`. The hint goes to `Utilities.GetPlayers().Where(p => !p.IsBot && !lr.HasStarted(p))` via the shared HUD helper (centre HTML, duration 2; the final result message duration 5). The abort checks call `ILastRequestService.CanLastRequest()`, `ILastGuardService.LastGuard != null` and `IDayService.IsInProgress`.
**Port fix (documented deviation):** in the `LastGuardActivating` (Pre) handler, remove the warden **without** starting the reselection timer and still restore the model. This prevents the spurious "A warden will be selected in 3 seconds" message. Functionally identical, because the original timer was cancelled on its first tick anyway.
**Ordering requirement for the Days spec:** `IDayService.IsInProgress` must already be true when `DayStarted` is raised (the original relied on this).

#### 2.2.3 Model swap
* **On select:** `SavedModel = pawn.CBodyComponent.SceneNode.GetSkeletonInstance().ModelState.ModelName`. If `Config.PlayerModels.WardenModel` is not empty, call `pawn.SetModel(WardenModel)`.
* **On TryRemove while alive:** if `SavedModel` is not empty, call `pawn.SetModel(SavedModel)`.
* Then raise the event so Xray/glow modules can rebuild (they listen to `WardenSelected`/`WardenRemoved`).
* Default `WardenModel = "characters/models/ctm_heavy/ctm_heavy.vmdl"` (see 8.2 for the CS2 replacement plan and verification note).

#### 2.2.4 Clan tag (FULL) and chat title (dependency)
* Guard tag logic as in 2.1.13 using `controller.Clan` + `SetStateChanged(controller,"CCSPlayerController","m_szClan")`.
* Save `controller.Clan` as the original tag when the player joins guards.
* CS2 clients can resend their clan tag (when they change `cl_clanid`), which overwrites ours. Re-apply the guard/warden tag on every `PlayerPrepared` (spawn) and when warden status changes.
* The chat title "Warden" and the warden gag-bypass belong to the chat module (PlayerChat port, other spec). Expose `IWardenService.IsWarden` and, if that spec defines `IChatTitleService`, call `SetCustomTitle(p,"Warden")` / `ClearCustomTitle(p)` at the same points as the original. Title format: ` {default}[{purple}Warden{default}] {lightpurple}{name} : {default}{msg}` in framework tags.
* Optionally, guard the whole clan-tag feature behind `Warden.GuardClanTags` (default true). The original only ran it when both Swoobles libraries were loaded, and they always were on SWBS.

#### 2.2.5 Pointer laser (PARTIAL)
State: `double startExpire, buttonExpire, beamExpire; Vector endPos; CBeam? pointerBeam`.
* `OnPlayerButtonsChanged(player, pressed, released)`, warden only:
  * `pressed & Use`: run the original "fresh press" branch (first tap / activate / pulse ring when active).
  * `pressed & Duck`: ring counter. Otherwise, if `pressed & (Speed | Walk)`: line counter. (CS2 sets `PlayerButtons.Speed` (0x10000) for the walk key; accept `Walk` too.)
* `OnTick` (only while `now < buttonExpire + 0.2`):
  * Recompute `endPos` with the world trace (0.10).
  * If `(controller.Buttons & PlayerButtons.Use) != 0` and the pointer is active, set `buttonExpire = now + 0.5`.
  * Beam start = eye position + `forward*16 + right*5 − up*5`. This approximates the weapon muzzle; viewmodel and world-model attachments cannot be referenced from CSS for beams.
  * Create or move `pointerBeam` (width 0.4 → end width 1.0, colour (255,240,50,15)).
  * Remove the beam when `now > buttonExpire + 0.1` (original TE life 0.6 = 0.5 + 0.1).
  * Get `forward`/`right`/`up` with `NativeAPI.AngleVectors(eyeAngles.Handle, f.Handle, r.Handle, u.Handle)`.
* Pulse ring: an expanding polygon (0.6) at `endPos + (0,0,2)`, radius 1 → 40, life 0.3, width 3, 16 segments, colour (255,240,50,15).
* **Lost:** through-wall visibility (unless a custom material is configured) and the separate viewmodel/world beam origin. Everyone sees one beam from near the warden's weapon.
* Deviation note: CS2 E also opens doors and picks up weapons, as in CS:GO. In buy zones, `cl_use_opens_buy_menu` (client cvar, cannot be forced) may open the buy menu. The original had a TODO to warn about this. Optionally print a tip on selection: "Tip: set cl_use_opens_buy_menu 0 to use the pointer in buy zones." Mark it optional (config `Warden.ShowPointerTip`, default false).

#### 2.2.6 Line and gesture ring (FULL)
* Bullet history from `EventBulletImpact` (0.03 s gate, `Server.EngineTime`).
* The normal comes from `Trace.TraceShape(eye, eyeAngles, pawn, worldOpts)`. `pulled = impact + Normal*10`.
* Line: a single persistent `CBeam` (width 5, colour (255,255,0,136)) between `pulled[0]` and `pulled[1]`, created or moved on the triple-tap and deleted at round end and map end (not on warden removal).
* Ring: call `IWardenMenuService.CreateRing(warden, 0, centre, radius)`.

#### 2.2.7 Paintballs (PARTIAL)
* `EventBulletImpact` from the warden, 0.02 s rate limit.
* World check: `Trace.TraceEndShape(eye, impact + dir*2, pawn, worldOpts)`. Paint only if `HitEntity()` is null, invalid, or `Index == 0` / `DesignerName == "worldent"`.
* **Substitute (default `Warden.PaintballMode = "beam"`):** a small "splat" made of 2 crossed `CBeam`s (length 8, width 2, offset 1 unit along the surface normal), in a random colour from blue (0,128,255), green (0,220,0), pink (255,0,170).
* Keep at most `Warden.PaintballMaxSplats = 48` splats (FIFO, oldest deleted first) and delete all at round start/end.
* Alternative `"decal"`: `CEnvDecal` (`env_decal`) with workshop `.vmat` paths from `Warden.PaintballDecalMaterials`. Off unless the server ships those materials.
* Set `Warden.PaintballMode = "off"` to disable.
* **Lost:** real permanent world decals with the Swoobles splat textures (CS2 has no TE World Decal, and custom materials need a workshop addon).
* Store integration: expose `IWardenService.IsWarden` so a Store paintball port can skip the warden.

#### 2.2.8 Kiss (FULL except custom sound)
* `GameRules.GetClientAimTarget(caller)` returns the hit player controller (or null).
* Global cooldown `static double nextKiss` (EngineTime + 5). It is consumed before the trace, as in the original.
* Messages are as in the original. Sound: `Warden.KissSound` (default `""`). If it is a `.vsnd`, play it to players within 1000 units of the warden.

#### 2.2.9 Death sound
`Warden.DeathSound` (default `""`). The original `music/revenge.wav` was a CS:GO stock sting. Use a `.vsnd` from a workshop addon or a built-in path. It plays to **all** players (it was SNDLEVEL_NONE).

#### 2.2.10 Commands (CSS)
| CSS command | Attribute(s) | Notes |
|---|---|---|
| `css_w`, `css_warden` | `[CommandHelper(0,"",CommandUsage.CLIENT_ONLY)]` | |
| `css_uw` | CLIENT_ONLY | silent if not the warden |
| `css_wwho` | CLIENT_ONLY | |
| `css_kiss`, `css_smooch` | CLIENT_ONLY | silent if not the warden |
| `css_rw` | `[RequiresPermissions("@css/kick")]` | console allowed |
| `css_sw`, `css_setw`, `css_setwarden` | `[RequiresPermissions("@css/root")]`, `[CommandHelper(1,"<#steamid|#userid|name>")]` | Resolve with `new Target(arg).GetTarget(caller)`. 0 matches → `[SM] No matching client`. More than 1 → `[SM] More than one client matched the given pattern.` Bots are allowed (nobots=false). No immunity check. |

#### 2.2.11 Phrase keys (suggested; text = original, convert tags per 0.2)
`warden.queue.current` `{olive}The current warden is: {lightred}{0}{olive}.` · `warden.none` `{olive}There is no warden.` · `warden.queue.force_removed` · `warden.queue.already` · `warden.queue.added` · `warden.unwarden.self` `{lightred}{0} {olive}removed themself from warden.` · `warden.unwarden.error` · `warden.rw.none` · `warden.rw.error` · `warden.rw.done` (plain `[SM] {0} has been removed from warden.`) · `warden.set.usage` · `warden.set.dead` · `warden.set.notguard` · `warden.set.already` · `warden.set.error` · `warden.set.done` `{lightred}{0} {olive}set as warden by {blue}{1}{olive}.` · `warden.kiss.smooch` · `warden.kiss.blow` · `warden.timer.chat` `{olive}A warden will be selected in {lightred}{0} {olive}seconds.` · `warden.timer.hint` · `warden.notfound.hint` · `warden.notfound.chat` · `warden.selected.hint` · `warden.selected.chat` · `warden.priority` `{olive}Type {lightred}!w {olive}if you would like to have priority.` · `warden.died` `{olive}The warden has died!`

### 2.3 Feasibility
| Feature | Verdict | Notes |
|---|---|---|
| Queues, timers, selection rules, admin commands, !uw/!wwho | **FULL** | |
| Warden model swap | **FULL / config** | needs a valid CS2 `.vmdl` (8.2) |
| Clan tag "Warden" | **FULL** | re-apply on spawn |
| Chat title + gag bypass | **FULL (via chat module)** | dependency |
| Hints | **FULL** | centre HTML |
| Pointer laser | **PARTIAL** | single beam for all, not through walls, start approximated near the weapon |
| Pulse ring, line, gesture ring | **FULL** (geometry) / PARTIAL (no through-wall material) | |
| Paintballs | **PARTIAL** | beam splats instead of decals |
| Kiss | **FULL** (custom sound needs addon) | |
| Warden death sound | **PARTIAL** | custom/built-in `.vsnd` only |

---

## 3. Warden Menu (`UltJB_WardenMenu.sp` v1.11)

### 3.1 Original behaviour

#### 3.1.1 Commands
* `sm_wm`: open the Warden Menu (warden only).
* `sm_cc <ring|remove>`: client colours shortcut (warden only).

The non-warden message for both is `{green}[{lightred}SM{green}] {red}You must be warden to use this command.` Every menu action re-checks warden status and shows the same message if the player is no longer warden.

#### 3.1.2 Constants
* **Rings**, index order and names: 0 `Green` (0,255,0,255) `#00ff00` · 1 `Red` (255,0,0,255) `#ff0000` · 2 `Yellow` (255,255,0,255) `#ffff00` · 3 `Blue` (**0,255,255**,255; cyan) `#00ffff`.
* `RING_RADIUS = 50`, `RING_SIZE_MIN = 1`, `RING_SIZE_MAX = 5`. A removed/reset ring has size `(5-1)/2 = 2.0` (radius 100). Size steps are ±1, so the reachable radii are 50, 100, 150, 200 and 250.
* `RING_UPDATE_TIME = 0.3` s.
* Healing: `HEALING_RADIUS = 33`, `HEALING_AREA_UPDATE_TIME = 0.5`, `HEALTH_INCREASE_AMOUNT = 5`, health cap 100.
* Fade flags: `FFADE_STAYOUT 0x0008`, `FFADE_PURGE 0x0010`.

#### 3.1.3 On warden selected
Chat to the warden: `{green}[{lightred}SM{green}] {olive}Type {lightred}!wm {olive} to open the warden's menu.` (note the double space). `g_iWardenCount++`; the auto-open of the menu for the first warden is commented out (do not auto-open).
On warden removed: `RemoveHealingArea()`.

#### 3.1.4 Menus
**"Warden Menu"** (exit button only, no back):
1. `Create healing area` / `Remove healing area` (the label toggles with state).
   * Create calls `SetPosition(HEALING)`.
   * Remove deletes the area and chats `{olive}Healing area removed.`
   * Either way the menu reopens at the same page.
2. `Open rings menu` → Rings Menu.
3. `Set client colors` → Client Color Menu.

**"Rings Menu"** (ExitBack → Warden Menu):
* `Remove all rings` → `RemoveAllRings(warden)`. Each ring that was set prints `{olive}%s ring removed.` Then the menu reopens at the same page.
* `Manage ring: Green`, `Manage ring: Red`, `Manage ring: Yellow`, `Manage ring: Blue` → Manage Ring menu.

**"Manage Ring: {Name}"** (ExitBack → Rings Menu). After every action it reopens at the same page.
1. `Set position` → `SetPosition(RINGS, index)`.
2. `Increase size`: enabled only if the ring is set and `size != 5`. `size++`, custom radius cleared, ring redrawn.
3. `Decrease size`: enabled only if the ring is set and `size != 1`. `size--`, custom radius cleared.
4. *(spacer)*
5. `Remove ring`: enabled only if the ring is set. Chat `{olive}%s ring removed.`, unset, size reset to 2.

**"Client Color Menu"** (ExitBack → Warden Menu). It reopens after each action.
1. `Set client ring color` → `SetClientsColor(remove=false, inRingsOnly=true, useRingColor=true)`.
2. `Remove client ring color` → `SetClientsColor(remove=true, inRingsOnly=true)`.
3. `Remove all client colors` → `SetClientsColor(remove=true, inRingsOnly=false)`.

If a menu cannot be displayed: `{red}Could not display menu: warden menu.` (or `ring menu.` / `manage ring.` / `client color menu.`).

#### 3.1.5 SetPosition(type, ringIndex)
1. Trace from the eyes along the eye angles (MASK_PLAYERSOLID, ignoring players, infinite) to get `pos` and the surface normal.
2. Always show a **set-position effect** to the warden only: a TE ring shrinking from radius 35 → 2 (diameter semantics), wall_beam material, life 0.25, width 2, colour cyan (0,255,255,255), speed 20.
3. If `normal.z < 0.85`: `{red}You must place this on a flat surface.` and stop.
4. Healing: `InitHealingArea(pos)` and chat `{olive}Healing area created.`
5. Ring: `SetRingPosition(warden, index, pos)`.

#### 3.1.6 SetRingPosition(client, index, origin, useCustomRadius=false, customRadius=0)
Used by the menu and by the native `UltJB_WardenMenu_CreateRing` (the warden's crouch triple-tap, 2.1.11). The native always passes `useCustomRadius=true`.
* With a custom radius:
  * `custom = r`. If `r > 250`, set `size = 5` and `custom = 0`. If `r < 50`, set `size = 1` and `custom = 0`.
  * Otherwise the custom radius is used and `size` is left unchanged. `size` still drives the Increase/Decrease enable states; Increase/Decrease clear the custom radius and apply `size±1` to the old size.
* `origin` stored, `set = true`, immediate redraw.
* Chat to `client`: `{olive}%s ring's position set.`

#### 3.1.7 Ring drawing
Every 0.3 s (OnGameFrame, engine time), each set ring is drawn as a TE BeamRingPoint at `origin + (0,0,10)` with start/end diameter `D-1 → D`, where `D = 2 * (custom != 0 ? custom : 50*size)`. Material laserbeam (normal, not through walls), framerate 5, life 0.4, width 5.0, amplitude 0, the ring colour, speed 10. The visible radius is `custom` or `50*size`.
**Persistence:** rings are removed only on map start and `cs_pre_restart` (every round restart; see 3.1.10). They are **not** removed when the warden is removed or dies, so they survive until the next round.

#### 3.1.8 Healing area
* `InitHealingArea(pos)`: `origin = pos + (0,0,5)`, `set = true`. Teleport a hidden `info_target` there (FL_EDICT_ALWAYS) and start the looping sound `ambient/atmosphere/underground_hall_loop1.wav` from it (SNDCHAN_BODY, normal level).
* Every 0.5 s while set:
  * Spawn the particle `healing_area` (from `particles/swoobles/jailbreak_v1.pcf`) at the origin, tinted with colour `(255, r, r)` where `r = random 65..160` (pinkish).
  * Then, for each **alive prisoner** whose `|AbsOrigin - origin| <= 33` (3D) and whose health is below 100: health `+5`, capped at 100, via `UltJB_LR_SetClientsHealth`.
  * Log `"Warden healed %N for %d."` via `UltJB_Logger_LogEvent(msg, client, 0, LOGTYPE_ATTACK)`.
  * If at least one prisoner was healed this tick, play the ambient sound `items/medshot4.wav` once at the area origin (volume 0.6, pitch 130).
* `RemoveHealingArea()` stops the loop sound (SND_STOP|SND_STOPLOOPING) and sets `set = false`.
* It is removed on: the menu toggle, warden removed (includes death, `!uw`, LR, day, round end via RoundEndCleanup), and `cs_pre_restart`. Guards are never healed.
* Materials downloaded: `swoobles/particles/ring_wave_10_water(_add)`, `flare_002`, `heal_cross_1`.

#### 3.1.9 Client colours (`SetClientsColor(remove, colour=white, inRingsOnly, useRingColor)`)
For every **alive prisoner**:
* If `inRingsOnly`: `ring = GetClientsRingIndex` (the first set ring, in index order, whose radius contains the player: 3D distance from AbsOrigin to the ring origin, which is ground-level and not +10). Skip if the player is in no ring. If `useRingColor`, the colour is the ring colour.
* `remove`: render colour (255,255,255,255), `m_nSkin = 0`, purge the screen fade (flags PURGE, colour {1,1,1,255}, duration 0, hold 0), hint `Your color has been removed.`
* Set: render colour = ring colour, `m_nSkin = 1` (the white-body skin of the Swoobles prisoner models, so the tint shows), screen fade with the ring colour at **alpha 20** and flags STAYOUT|PURGE (a permanent subtle tint), hint `Your color has been set to\n<font color='{hex}'>{Name}</font>.`
* Quirk: the "remove all" path sends the removed hint to **every** alive prisoner, even ones who had no colour.

`sm_cc`:
* `ring` → set by ring, chat `{olive}Client colors have been set based on ring colors.`
* `remove` → remove all, chat `{olive}Client colors have all been removed.`
* Anything else or no argument → `ReplyToCommand "[SM] Please enter 'ring' or 'remove'."`

Fade purge also happens on: `player_death` (victim) and `UltJB_LR_OnLastRequestInitialized(client)`. Only the screen tint is purged there; the render colour stays.

#### 3.1.10 `cs_pre_restart` (fires at the start of **every** round restart in CS:GO)
Remove the healing area, remove all rings (silent, client 0), `wardenCount = 0`, and remove all client colours. The colour removal shows the "Your color has been removed." hint to any still-alive prisoner from the last round. That is a faithful quirk.
`OnMapStart`: remove all rings and reset the count.

#### 3.1.11 Native exposed (`ultjb_wardenmenu`)
`UltJB_WardenMenu_CreateRing(client, ring_index, const Float:position[3], Float:ring_radius)` → `SetRingPosition(client, idx, pos, true, radius)`. Consumer: Warden (crouch triple-tap).

### 3.2 CS2 port design
**Module:** `WardenMenuModule`, Order ≈ 21. **Service** `IWardenMenuService { void CreateRing(JbPlayer client, int ringIndex, Vector pos, float radius); bool IsHealingAreaSet {get;} }`.

* **Menus:** use the framework menu helper. If none exists, use `CenterHtmlMenu(title, Plugin)`, which shows 5 items per page with `!1..!9`. CSS menus have **no ExitBack and no spacer**:
  * Emulate ExitBack with a first option `« Back`. Hmm, no: put **`Back`** as the last option, so the numbering of the original items is unchanged.
  * Omit the spacer.
  * Disabled items: `AddMenuOption(text, cb, disabled: true)`.
  * "Reopen at the same page" means reopening the same menu. CSS has no start-item API, and with 5 items per page every menu fits on one page except Rings Menu (6 entries incl. Back), which can just reopen at page 1.
  * Set `PostSelectAction = PostSelectAction.Nothing` and reopen manually, so the menu does not flicker closed.
  * Every callback re-checks `IWardenService.IsWarden(player)`.
* **SetPosition:** trace per 0.10. `pos = tr.EndPos`, `normal = tr.Normal`. The set-position effect is a shrinking polygon (radius 17.5 → 1, 0.25 s, width 2, cyan, 16 segments). Show it to all, or only to the warden via a CheckTransmit helper if the framework provides one (PARTIAL).
* **Rings:** 4 persistent polygons, `WardenMenu.RingSegments = 32`, width 5, at `origin.Z + 10`, coloured per ring. Create, move or resize them only when the state changes; no 0.3 s re-creation is needed. A safety redraw every 0.3 s is optional, in case an entity got removed by round cleanup. Remove them at `OnMapStart` and on every round reset (3.1.10).
* **Healing area:**
  * Store `origin`.
  * Every 0.5 s (`AddTimer(0.5, REPEAT)` while set): heal logic exactly as in 3.1.8. Use the shared `SetHealth` recipe and the Logger spec's `LogEvent(msg, client, null, LogType.Attack)`.
  * **Visual (PARTIAL):** a ring polygon of radius 33 at `origin` (16 segments, width 2) whose colour is re-randomised every 0.5 s to `(255, r, r)`, `r ∈ [65,160]`, matching the original particle tint.
  * Optional `WardenMenu.HealingParticle` (default `""`): a `.vpcf` from a workshop addon, spawned with `CParticleSystem` ("info_particle_system"; set `EffectName`, `Teleport`, `DispatchSpawn`, `AcceptInput("Start")`), with the tint via `Tint`.
* **Sounds:** `WardenMenu.HealingLoopSound` (default `""`) and `WardenMenu.HealSound` (default `""`). There is no built-in equivalent, so they are silent by default. Positional originals use radius 1000 when using `.vsnd` (0.4).
* **Client colours:**
  * Render colour per the 0.10 recipe. `m_nSkin` has no CS2 equivalent for stock models, so skip it (PARTIAL: the tint is applied over the normal textures).
  * The screen tint uses the Fade usermessage: `duration=0`, `hold_time=0`, flags `0x0008|0x0010`, colour `(r,g,b,20)`. Purge: flags `0x0010`, colour (1,1,1,255).
  * The hint uses centre HTML.
  * Ring membership uses `pawn.AbsOrigin` against the ring origin, 3D distance.
* **Round reset hook:** the CS2 events `EventCsPreRestart` **and** `EventRoundPrestart`, handled idempotently (once per round). It has not been confirmed that `cs_pre_restart` fires every round in CS2; `round_prestart` does.
* Death and LR-initialised fade purge: framework `OnPlayerDeath` and the LR spec's `LastRequestInitialized`.

### 3.3 Feasibility
| Feature | Verdict | Notes |
|---|---|---|
| Menus and all items | **FULL** | Back emulated as an item, no spacer |
| Rings (4 colours, sizes, gesture ring) | **FULL** | beams, not through walls |
| Healing area logic | **FULL** | |
| Healing area particle and sounds | **PARTIAL** | beam ring with random pink tint; custom particle/sound needs a workshop addon |
| Client colours (render colour, screen tint, hints) | **FULL** (no `m_nSkin` white-body skin → PARTIAL tint quality) | |
| `!cc ring/remove` | **FULL** | |

---

## 4. Last Guard (`UltJB_LastGuard.sp` v1.9)

### 4.1 Original behaviour

#### 4.1.1 Cvars
| Cvar | Default | Min | Meaning |
|---|---|---|---|
| `ultjb_lastguard_guards_needed` | 3 | 1 | guards on the team at round start needed to enable LG for the round |
| `ultjb_lastguard_hp_per_prisoner` | 17 | 0 | bonus HP per alive prisoner at activation |
| `ultjb_lastguard_hp_per_kill` | 5 | 0 | HP gained per prisoner the LG kills |
| `ultjb_lastguard_damage_wait` | 20 | 0 | seconds the LG has to damage someone before the punishment ticks start |
| `ultjb_lastguard_damage_interval` | 3 | 1 | seconds between punishment ticks |
| `ultjb_lastguard_damage_amount` | 10 | 1 | damage per tick |

#### 4.1.2 Enable check (`round_start`)
`RemoveLastGuard()`. Count in-game players **on the guard team** (alive or not).
* 0 guards: return, and `canLastGuard` **keeps its previous value** (harmless quirk).
* At least `guards_needed`: `canLastGuard = true` and chat `{green}[{lightred}SM{green}] {lightred}%i {olive}guard%s alive at round start. {yellow}Last Guard: {green}ENABLED{olive}!`
* Otherwise: `canLastGuard = false` and `... {yellow}Last Guard: {red}DISABLED{olive}.` (`%s` = "" when 1 guard, else "s").

#### 4.1.3 Activation (`player_death` post)
* Victim is a guard: `TryRemoveLastGuard(victim)`, then `CheckForLastGuard()`.
* Victim is a prisoner and the attacker is a guard: if the attacker is the LG, health `+5` (via SetClientsHealth, which raises max).
* The victim's beacon is always unhooked.

`CheckForLastGuard`: requires `canLastGuard` and no Day in progress. Count **alive guards**. If exactly 1 → `SetLastGuard(that guard)`.

`SetLastGuard(c)`:
1. `numInitialized = LR_GetNumInitialized() - LR_GetNumStartedContains(LR_FLAG_FREEDAY)`. If > 0, return: no LG while a non-freeday LR is initialised.
2. Fire `UltJB_LastGuard_OnActivated_Pre(c)`. The Warden module removes the warden here.
3. Stop the LG timer, then set `lastGuard = c`.
4. For every **alive** player except those with a started freeday LR:
   * `nextBeacon = now + random(0, 1.0)` and start that player's beacon.
   * Prisoners also get the hint `<font color='#DE2626'>%N</font>\n<font color='#6FC41A'>is last guard. Kill them!</font>` and are counted.
5. Chat to all `{green}[{lightred}SM{green}] {lightred}%N {olive}is the last guard!`
6. LG health `+= 17 × counted prisoners` (raises max HP).
7. LG model → `ctm_heavy.mdl`.
8. Start the LG timer and show the wait hint.
9. Fire `UltJB_LastGuard_OnActivated_Post(c)`. Xray rebuilds its glow here.

#### 4.1.4 Beacons (each beaconed player, PostThinkPost, game time)
If `now >= next`:
* `p = GetBeaconPercent` = `(clamp(minDist, 550, 2200) - 550) / (2200 - 550)`. `minDist` is the distance to the nearest alive **enemy-team** player (excluding started freeday LRs); if there is none, 2200.
* `delay = 0.7·p + 0.3` (0.3 s when close … 1.0 s when far). `next = now + delay + 0.2`.
* Colour: guard **(0,178,255,200)**, prisoner **(255,77,0,200)**. Any other team: no beacon.
* TE BeamRingPoint at `AbsOrigin + (0,0,10)`, diameter 10 → 220 (radius 5 → 110) over `life = delay`, laserbeam material, width 5.5, amplitude 10 (jagged), speed 0.
* Sound `buttons/blip1.wav` from the player, normal level, volume 0.7, pitch `round(29·(1-p) + 91)` (91 far … 120 close).

#### 4.1.5 Punishment timer (1 s repeating)
* If the LG is gone: `RemoveLastGuard()`.
* `wait++`. If `wait < 20`: hint `<font color='#6FC41A'>You have </font><font color='#DE2626'>{20-wait}</font><font color='#6FC41A'> seconds\nto damage the prisoners.</font>`
* Else `interval++`. Note that `interval` starts at 3, so the **first tick fires immediately when the wait expires**. If `interval < 3`: hint `You have {3-interval} seconds\nbefore you're damaged again.` (same colours).
* Else `interval = 0`, then:
  * If health > 10: `SlapPlayer(LG, 10, sound=true)` and the interval hint (shows 3).
  * Else `ForcePlayerSuicide(LG)`.
* Timeline after activation: "20" at t=0, 19…1, first slap at t=20, then t=23, 26, …

`player_hurt`: if the attacker is the LG and the victim is any player, `ResetDamageCounters()`: `wait = 0`, `interval = 3`, wait hint shown again. The victim check also accepts the LG himself (self-damage resets). That is a faithful quirk.

Native `UltJB_LastGuard_ResetDamageCounters()` does the same; it is used by the RPG warday.

#### 4.1.6 Removal
`RemoveLastGuard()`: stop the timer, clear the LG and unhook every beacon. The LG keeps their HP and model. It is triggered by:
* `round_end`, `round_start`, `cs_match_end_restart`
* the LG dies or disconnects
* the LG changes to a non-guard team
* `UltJB_LR_OnLastRequestInitialized`

#### 4.1.7 Natives / forwards (`ultjb_last_guard`)
| API | Notes / consumers |
|---|---|
| `UltJB_LastGuard_GetLastGuard()` → client or 0 | Warden (abort selection), RPG warday |
| `UltJB_LastGuard_GetNumNeeded()` → cvar | (LR/menus) |
| `UltJB_LastGuard_ResetDamageCounters()` → true | RPG warday |
| forward `UltJB_LastGuard_OnActivated_Pre(client)` | Warden |
| forward `UltJB_LastGuard_OnActivated_Post(client)` | Xray |

### 4.2 CS2 port design
**Module:** `LastGuardModule`, Order ≈ 22.
```csharp
public interface ILastGuardService
{
    JbPlayer? LastGuard { get; }
    int GuardsNeeded { get; }
    bool IsEnabledThisRound { get; }
    void ResetDamageCounters();
    event Action<JbPlayer>? LastGuardActivating;   // _Pre
    event Action<JbPlayer>? LastGuardActivated;    // _Post
}
```
* **Activation check:** framework `OnPlayerDeath(victim, attacker, ev)`. Count alive guards **excluding the victim** (0.1 pitfall). Run the check immediately, so it happens before round-end processing. The LR checks use the LR spec: `NumInitialized`, `NumStartedContains(LrFlags.Freeday)`, `HasStarted`, `GetFlags`.
* **Health:** shared SetHealth.
* **Model:** `Config.PlayerModels.LastGuardModel` (default = WardenModel).
* **Beacons (PARTIAL):** one global `OnTick` loop over the beaconed set (no per-pawn hook needed), using `Server.CurrentTime`.
  * Each pulse is an expanding polygon (0.6): radius 5 → 110 over `delay` seconds, `LastGuard.BeaconSegments = 16`, step 0.05 s, width 5.5, colour as in the original, at `AbsOrigin + 10z`. The pulse **does not follow** the player after it starts; the TE ring didn't either.
  * The TE amplitude 10 (jagged) is optional (`CBeam.Amplitude = 10` may work; untested), so it is PARTIAL.
  * **Entity budget:** about 20 players × 16 beams, with at most one live pulse per player. Pulses overlap at most ~1.2 s apart, because the next pulse comes after delay + 0.2.
  * **Sound:** if `LastGuard.BeaconSoundEvent` is set, call `pawn.EmitSound(evt, recipients=all, volume 0.7, pitch = computedPitch/100f)`. CSS `pitch` is a multiplier offset; verify scale in-game, 0 = default. Otherwise use `LastGuard.BeaconSound` `.vsnd` (default `sounds/tools/sfm/beep.vsnd`), sent with `play` only to players within **1000 units** of the beaconed player (approximate attenuation). The pitch is lost in this fallback.
* **Punishment timer:** `AddTimer(1.0, REPEAT)`.
  * Slap (shared FunCommands slap helper if that spec provides one; otherwise implement): `pawn.Health -= 10` + SetStateChanged; velocity `v += (±(rand(0..179)+50), ±(rand(0..179)+50), rand(0..199)+100)` applied with `pawn.Teleport(null, null, newVel)`; play `LastGuard.SlapSound` (default `""`).
  * Suicide: `controller.CommitSuicide(false, true)`.
* **Damage reset:** `[GameEventHandler] EventPlayerHurt`. `ev.Attacker` is the LG and `ev.Userid` is non-null → reset.
* **Team change, disconnect, round events:** framework hooks plus `EventCsMatchEndRestart`. LR initialised comes from the LR spec event.
* **Phrase keys:** `lg.enabled`, `lg.disabled` (args: count, plural "s"), `lg.is_last_guard`, `lg.kill_hint`, `lg.wait_hint`, `lg.interval_hint`.

### 4.3 Feasibility
| Feature | Verdict | Notes |
|---|---|---|
| Enable rule, activation rules, HP bonuses, punishment timer, slap/suicide, reset on damage | **FULL** | |
| Beacon rings (colour, timing, distance scaling) | **FULL** geometry / PARTIAL look | beams; amplitude jaggedness optional |
| Beacon sound with pitch | **PARTIAL** | pitch only with a soundevent; `.vsnd` fallback has fixed pitch |
| LG model | **FULL / config** | |

---

## 5. Settings: server rules and spawn (`UltJB_Settings.sp` v1.36)

### 5.1 Original behaviour

#### 5.1.1 Constants and cvars
* `PRISONER_SPAWN_DIST_FROM_WEAPON = 300`, `MAX_IN_CELL_CLIP_SIZE = 15`, `HEALTHSHOT_AMOUNT = 25`.
* `DEFAULT_AUTORESPAWN_DELAY = 0.3`, `MODIFYING_WEAPON_EQUIP_TIME = 90` s.
* Team names: `mp_teamname_1 = "Guards"` (CT), `mp_teamname_2 = "Prisoners"` (T).

| Cvar | Default | Meaning |
|---|---|---|
| `ultjb_cells_closed_auto_respawn_seconds` | 60 | how long late-joining prisoners are auto-respawned while the cells are closed |
| `ultjb_settings_help1_seconds` | 54000 (15 h) | play time below which a player is "help 1" |
| `ultjb_settings_help2_seconds` | 108000 (30 h) | play time below which a player is "help 2" |

**Forced engine cvars** (re-forced on change via `HookConVarChange`):
* `mp_teamname_1 Guards`, `mp_teamname_2 Prisoners`
* `healthshot_health 25`
* `sv_disable_immunity_alpha 1`
* `mp_ignore_round_win_conditions` is only *controlled* via the native (0 at load and at map end).

#### 5.1.2 Blocked console commands
`kill`, `killvector`, `explode` (command listener returns Handled). The healthshot drop block (`drop` listener) is **commented out** in the original; do not port it.

#### 5.1.3 Spawn preparation (SDKHook SpawnPost, alive non-observer only; or `MSManager_OnSpawnPost`)
Skipped if ModelSkinManager is force-respawning the player (N/A in the port).
1. Collision group = `COLLISION_GROUP_DEBRIS_TRIGGER` (noblock: players pass through each other).
2. Strip all weapons (`UltJB_LR_StripClientsWeapons(client)`).
3. **Stable spawn spot:** `originalTeam[client]` is reset at every `cs_pre_restart` (every round) and at map start.
   * If NONE, record team, AbsOrigin and AbsAngles.
   * Otherwise, if the current team equals the recorded team, teleport to the recorded spot.
   * Result: **a player who respawns in the same round (late-join auto-respawn, warday respawns) reappears at their first spawn spot of that round**, e.g. their cell.
4. Loadout:
   * Guards: `weapon_fiveseven` (made the active weapon), `weapon_knife`, `weapon_healthshot`, armor 100, **no helmet**.
   * Prisoners: `weapon_knife_t` (active), armor 0, no helmet.
   * All weapons are given through `UltJB_Weapons_GivePlayerWeapon` (the WeaponSelection spec).
5. Health 100, max health 100, no defuser.
6. Fire `UltJB_Settings_OnSpawnPost(client)`.

#### 5.1.4 OnRoundStart (`round_start` post **and** `OnMapStart`)
* `roundStartTime = now`.
* If cell doors exist (`UltJB_CellDoors_DoExist`): `StartAutoRespawning(resetLimits=true, perClient=1, seconds=60 (cvar), team=ART_PRISONERS, disableOnDeath=true, disableOnTeamChange=true)`.
  * Effect: during the first 60 s, while the cells are still closed, a prisoner who is **dead without having died** is respawned once. That means a late joiner, or someone who switched from spectator.
  * A prisoner who dies during that window is denied.
  * Stops early on `UltJB_CellDoors_OnOpened`.
* `RemoveReserveAmmoFromCellWeapons()`: record every `weapon_*` entity (**excluding grenades**: hegrenade, smokegrenade, incgrenade, decoy, molotov, tagrenade, flashbang) within 300 units of any `info_player_terrorist`.
* For every in-game client: gravity 1.0, lagged movement 1.0.
* `round_start` pre: `useNextRoundEndReason = false`.
* `round_start` post: clear the screen overlay (`r_screenoverlay ""`) for all.

#### 5.1.5 In-cell weapon nerf (WeaponEquipPost)
If **all** of these are true:
* within 90 s of round start
* the player is a prisoner
* the cells have not been opened
* the weapon is in the recorded list

then remove it from the list, set reserve ammo (primary and secondary) to 0, and cap the clip at 15. (A SkinsForMapWeapons variant passes the replaced weapon; N/A.)

#### 5.1.6 Guard suicide weapon wipe
* WeaponDropPost records the dropped weapons per client per game tick.
* In `player_death` **pre**, record `timeOfDeath = now`. If the weapons were dropped on this same tick, the attacker == the victim (suicide), and the victim is a guard → **Kill** all those dropped weapons.
* Purpose: guards cannot suicide to give prisoners guns.

#### 5.1.7 Auto-respawn engine (natives)
Timer every 0.1 s while running. For each in-game **dead** client that is not denied, whose team is ≥ T, where the allowed team != NONE and the team filter matches (ART_BOTH / ART_PRISONERS / ART_GUARDS):
* Check the delay since death: prisoners use `delayPrisoners`, guards use `delayGuards`. Clients who never died have time-of-death 0, so they qualify at once.
* `CS_RespawnPlayer`, then `used++`. If `perClient > 0` and `used >= perClient`, deny.
* Stop after `seconds` if `seconds > 0`.

Deny triggers: `player_death` post while running and `disableOnDeath`. Team change `player_team` (old team ≥ T only) while running and `disableOnTeamChange`.

The native `StartAutoRespawning` sets both delays to 0.3 before starting. The internal round-start call does **not**: the delays keep their previous value, initially 0.0, and could inherit a warday's delays (a latent bug).

`SetAutoRespawnDelay(delay, team)`: minimum 0.1, per team, or both.

`OnMapEnd`: stop, and `BlockTerminateRound(false)`.

#### 5.1.8 Round end
* `round_end` pre: `SetEventBroadcast(true)`, so clients never get the event and the stock round-end radio/music is suppressed.
* `CS_OnTerminateRound`: if `useNextRoundEndReason`, override the reason. Then by reason:
  * **Guards win** (CTWin, TargetSaved, VIPEscaped, CTStoppedEscape, TerroristsStopped, BombDefused, HostagesRescued, TerroristsNotEscaped, TerroristsSurrender): play `swoobles/ultimate_jailbreak/guards_win_v5.mp3` to all (SNDLEVEL_NONE, pitch 90) and show the overlay `swoobles/ultimate_jailbreak/overlay_guards_win_v15` (`r_screenoverlay`).
  * **Prisoners win** (TerroristWin, VIPKilled, TerroristsEscaped, HostagesNotRescued, VIPNotEscaped, CTSurrender, TargetBombed): `prisoners_win_v5.mp3` (pitch 90) and `overlay_prisoners_win_v15`.
  * **Draw**: `radio/rounddraw.wav`.
  * Return Changed when the reason was overridden.

#### 5.1.9 Breakable sound filter
Normal-sound hook. A sound on `SNDCHAN_BODY` emitted by a `func_breakable` or `prop_breakable` entity is sent **only to prisoners**, so guards don't hear prisoners breaking vents.

#### 5.1.10 Help number (`UltJB_Settings_GetClientHelpNumber`)
* Return 0 if DatabaseUserStats is not loaded or the player's stats are not loaded.
* `played = DBUserStats_GetServerTimePlayed`. If `help1 > 0 && played < 54000` → 1. Else if `help2 > 0 && played < 108000` → 2. Else 0.
* Consumers: PlayerModels (help model + bonus HP) and the LR API (other spec).

#### 5.1.11 Natives / forward (`ultjb_settings`)
| API | Consumers |
|---|---|
| `UltJB_Settings_StripWeaponFromOwner(weapon)`: drop it, `RemovePlayerItem` if still owned, clear the owner, KillHierarchy the world model and the weapon | Days API, LR API |
| `UltJB_Settings_StartAutoRespawning(resetLimits=false, perClient=0, seconds=0, team=ART_BOTH, disableOnDeath=false, disableOnTeamChange=true)` | CTF warday |
| `UltJB_Settings_StopAutoRespawning()` | CTF warday |
| `UltJB_Settings_SetAutoRespawnDelay(Float:delay, team=ART_BOTH)`, which must be called after Start | CTF warday |
| `UltJB_Settings_BlockTerminateRound(bool)` → `mp_ignore_round_win_conditions 1/0` | CTF warday, Jihad |
| `UltJB_Settings_IsBlockingTerminateRound()` | Jihad |
| `UltJB_Settings_SetNextRoundEndReason(bool use, reason=Draw)` | CTF warday, Jihad |
| `UltJB_Settings_GetClientHelpNumber(client)` | PlayerModels, LR API |
| forward `UltJB_Settings_OnSpawnPost(client)` | Warden, PlayerModels, Days API, Jihad |

ART constants: `ART_NONE 0`, `ART_BOTH 1`, `ART_PRISONERS 2`, `ART_GUARDS 3`.

### 5.2 CS2 port design
**Module:** `SettingsModule`, Order ≈ 5 (it is a core service, so it loads before gameplay modules).
```csharp
public enum ArtTeam { None = 0, Both = 1, Prisoners = 2, Guards = 3 }
public interface ISettingsService
{
    void StripWeaponFromOwner(CBasePlayerWeapon weapon);
    void StartAutoRespawning(bool resetLimits = false, int perClient = 0, int seconds = 0,
                             ArtTeam team = ArtTeam.Both, bool disableOnDeath = false, bool disableOnTeamChange = true);
    void StopAutoRespawning();
    void SetAutoRespawnDelay(float delay, ArtTeam team = ArtTeam.Both);
    void BlockTerminateRound(bool block);
    bool IsBlockingTerminateRound { get; }
    void SetNextRoundEndReason(bool use, RoundEndReason reason = RoundEndReason.RoundDraw);
    int GetClientHelpNumber(JbPlayer p);           // 0,1,2
    event Action<JbPlayer>? PlayerPrepared;        // UltJB_Settings_OnSpawnPost
}
```

**CS2 implementation details:**
* **Cvars:** apply on `OnMapStart`, after config load, and in `OnRoundStart` (cheap; there is no change hook):
  * `mp_teamname_1 Guards`, `mp_teamname_2 Prisoners`
  * `healthshot_health 25`
  * `sv_disable_immunity_alpha 1` if `ConVar.Find` finds it
  * `mp_ignore_round_win_conditions 0` at map start/end
  * Team names come from config `Settings.TeamNameGuards` / `Settings.TeamNamePrisoners` (defaults as above).
* **Command block:** `AddCommandListener("kill"|"killvector"|"explode", (p,i)=>HookResult.Handled)`. Optionally also `explodevector` (config list `Settings.BlockedCommands`, default the 3 originals).
* **Spawn preparation:** framework `OnPlayerSpawn` (next frame, alive T/CT only), executed in this order:
  1. Noblock per 0.10 (config collision group).
  2. Strip: `controller.RemoveWeapons()`. Or call the LR spec's strip, which also handles saved weapons; the original used the LR strip with `save=false`.
  3. Stable spawn spot: table per slot `(CsTeam team, Vector pos, QAngle ang)`, reset in the round-reset hook (`EventRoundPrestart`/`EventCsPreRestart`, idempotent) and at map start. **Also reset when a new player connects to the slot** (trivial deviation: the original leaked across reconnects). Teleport with `pawn.Teleport(pos, ang, Vector.Zero)`.
  4. Loadout through the WeaponSelection spec's `GivePlayerWeapon(player, "weapon_fiveseven")` etc. Fallback: `controller.GiveNamedItem("weapon_knife")` / `"weapon_knife_t"` / `"weapon_fiveseven"` / `"weapon_healthshot"`. Give the knife first and the active weapon last, then best-effort `controller.ExecuteClientCommand("slot2")` for guards / `"slot3"` for prisoners. Selecting the active weapon is best effort because CS2 has no reliable server-side "set active weapon".
  5. Armor, helmet, defuser and HP/max HP per the 0.10 recipes.
  6. Reset gravity (`GravityScale=1`) and `VelocityModifier=1`. The original did this at round start; in CS2 do it at spawn **and** round start, because GravityScale persists across respawns.
  7. Raise `PlayerPrepared(p)` synchronously.
* **Round start:** framework `OnRoundStart` and `OnMapStart`:
  * Auto-respawn for late prisoners, but **use the default delay 0.3 for both teams** (documented fix of the stale-delay bug).
  * Cell-weapon scan: `Utilities.FindAllEntitiesByDesignerName<CCSWeaponBase>("weapon_")` (Contains match), excluding the grenade designer names listed above. Spawn points come from `Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_player_terrorist")`, using `AbsOrigin`. Store `EntityHandle`/index and validate on use.
  * Gravity reset for all pawns.
  * `useNextRoundEndReason = false` (from `EventRoundStart` Pre).
* **In-cell weapon nerf:** `[GameEventHandler] EventItemPickup` (post). For the picker's `WeaponServices.MyWeapons`, any weapon whose index is in the list gets: `weapon.Clip1 = Math.Min(Clip1, 15)`, `weapon.ReserveAmmo[0] = 0`, and `SetStateChanged(weapon,"CBasePlayerWeapon","m_iClip1")` / `"m_pReserveAmmo"`. Check `CCSWeaponBase.VData` if needed for secondary reserve, which CS2 does not separate; set both array entries to 0. Conditions as in 5.1.5, using `ICellDoorService.HaveOpened`.
* **Guard suicide wipe:** CS2 has no WeaponDropPost hook. In `EventPlayerDeath` (post), if the victim is a guard and `ev.Attacker` is the victim, then on `Server.NextFrame` scan `weapon_` entities and `Remove()` (or `AcceptInput("Kill")`) those where:
  * `OwnerEntity` is invalid
  * `PrevOwner.Value` is the victim's pawn
  * `DroppedAtTime >= Server.CurrentTime - 0.5`

  Record `timeOfDeath[slot] = Server.EngineTime` in the **Pre** handler. FULL by a different mechanism.
* **Auto-respawn:** `AddTimer(0.1, REPEAT)`. Dead check: `!p.IsAlive`. Respawn: `controller.Respawn()`. Note it requires `controller.PlayerPawn.Value != null`; CS2 creates the pawn once a team is joined, so for never-spawned joiners `Respawn` works after team selection. Deny on death via `OnPlayerDeath`, and on team change via `OnPlayerTeam(old >= T)`. Stop on `ICellDoorService.Opened` and map end.
* **BlockTerminateRound:** `ConVar.Find("mp_ignore_round_win_conditions")?.SetValue(block)`. Use a bool or int depending on the convar type; `Server.ExecuteCommand($"mp_ignore_round_win_conditions {(block?1:0)}")` is safest. `IsBlockingTerminateRound` reads `GetPrimitiveValue<bool>()`.
* **Round-end reason override:** TerminateRound pre-hook (0.10). If `useNext`, call `hook.SetParam<RoundEndReason>(reasonIdx, nextReason)`. Note that CS2's `RoundEndReason` values are **SourceMod `CSRoundEndReason` + 1** (e.g. CTsWin=8, TerroristsWin=9, RoundDraw=10). The LR/Day ports must pass the CSS enum, not raw SM numbers.
* **Win sounds and overlay:**
  * Overlay: **NOT POSSIBLE**. `r_screenoverlay` is a client cheat cvar that the server cannot set in CS2, and `.vtf`/`.vmt` overlays don't exist. **Substitute:** centre HTML to all for 5 s: `<font class='fontSize-xl' color='#3FA9F5'>GUARDS WIN!</font>` or `<font class='fontSize-xl' color='#FF4D00'>PRISONERS WIN!</font>` (texts configurable), plus an optional short screen tint (Fade, colour (0,120,255,60) or (255,77,0,60), duration 0.5 s, hold 1 s, flags FADE_IN|PURGE; config `Settings.RoundEndTint`, default true). Trigger it from `EventRoundEnd` **post**, using `ev.Winner` (3 = guards, 2 = prisoners, otherwise draw). This works even when the broadcast is blocked, because the server handler still runs.
  * Sounds: `Settings.GuardsWinSound` / `PrisonersWinSound` / `DrawSound` (defaults `""`), played to all.
  * **Broadcast block:** `Settings.BlockRoundEndBroadcast` (default **false**). Only enable it together with custom win sounds. CS2 drives the round-end HUD panel and the stock win radio from this event, so blocking it without replacements leaves silence. In a Pre `EventRoundEnd` handler: `info.DontBroadcast = true`. Alternative (quieter): set `ev.Nomusic = 1` in Pre to kill only the music.
* **Breakable sound filter (PARTIAL):**
  * `Plugin.HookUserMessage(id, handler, HookMode.Pre)` with `id = UserMessage.FindIdByName("CMsgSosStartSoundEvent")`, fallback 208.
  * In the handler read `um.ReadInt("source_entity_index")` (verify the field name once with `um.DebugString`). Resolve the entity with `Utilities.GetEntityFromIndex<CBaseEntity>(idx)`.
  * If its `DesignerName` is `func_breakable` or `prop_breakable`, rebuild `um.Recipients` without guards.
  * CS2 sound events have no "channel", so this filters **all** sounds from breakables (impact and break) for guards, which is slightly broader than SNDCHAN_BODY only. Config `Settings.HideBreakableSoundsFromGuards` (default true).
* **StripWeaponFromOwner:** `var owner = weapon.OwnerEntity.Value?.As<CCSPlayerPawn>()`. If valid: `owner.RemovePlayerItem(weapon)` (CBasePlayerPawn.RemovePlayerItem). Then `weapon.OwnerEntity.Raw = uint.MaxValue` is not needed; just `weapon.AcceptInput("Kill")` (or `weapon.Remove()` on `Server.NextFrame`, which avoids removing a weapon mid-drop). There are no separate world-model entities in CS2.
* **Help number:** `GetClientHelpNumber` calls `IPlaytimeService` (9.1). It returns 0 unless `Settings.HelpSystemEnabled` (default **false**; see 9.1 for why) and the playtime is loaded.

### 5.3 Feasibility
| Feature | Verdict | Notes |
|---|---|---|
| Team names, healthshot 25, immunity alpha, win-condition block | **FULL** | re-applied, no change hook |
| kill/explode block | **FULL** | |
| Spawn loadout, HP, armor, noblock, stable spawn spot | **FULL** | active-weapon selection best effort |
| Gravity reset | **FULL**; lagged movement → VelocityModifier (**PARTIAL**, no CS2 laggedmovement) | |
| Auto-respawn engine + natives | **FULL** | |
| In-cell weapon nerf | **FULL** (via item_pickup) | |
| Guard suicide weapon wipe | **FULL** (different mechanism) | |
| Round-end reason override | **FULL** (detour hook) | Linux/Windows param order |
| Win overlay | **NOT POSSIBLE** → centre HTML text + tint | |
| Win sounds | **PARTIAL** | custom `.vsnd`/soundevent via addon |
| Breakable sound filter | **PARTIAL** | usermessage recipient filtering |
| Help number | **FULL** with the new playtime service (opt-in) | |

---

## 6. Team Ratio / guard queue (`UltJB_TeamRatio.sp` v1.18)

### 6.1 Original behaviour

#### 6.1.1 Cvars and constants
* `ultjb_prisoners_per_guard` = **2** (float, min 1)
* `ultjb_pre_guard_queue_time` = **30** (min 1)
* Restricted sound: `buttons/button11.wav` (played client-side with `play`).
* Guard play-time requirement: `DBUserStats server time + ClientTimes session time ≥ 18000` s (5 h). Only checked when both libraries are loaded; otherwise always allowed.
* Forced cvar: `mp_force_pick_time 999999` (set at OnConfigsExecuted and re-forced on change), so idle players are never auto-assigned a team.

#### 6.1.2 Commands
`sm_guard` (everyone): join the guard queue.
* Already CT: `{olive}You are already a guard.`
* Not T: sound + `{olive}You must be a prisoner to join the queue.`
* CT banned (TimedPunishments CTBAN seconds-left ≥ 0): sound + `{olive}Cannot join guards because you are CT banned.`
* Hours not met: sound + `{olive}Cannot join guards because you must play at least 5 hours.`
* Otherwise append (if not present):
  * Pre-queue finished: `{olive}You are #%i in the guard queue.`
  * Otherwise: `{olive}You are in the guard queue.`

All messages go to chat (with prefix) and console.

#### 6.1.3 `jointeam` command listener
* No argument: allow.
* `team == 0` (auto-select): sound + `[SM] You cannot use auto select to join a team.` Block.
* Current team ≤ spectator and target T: `ChangeClientTeam(T)` directly and block. This lets players join T even when the engine thinks T is full.
* Target ≠ CT: allow.
* CT checks, in order:
  1. Pre-queue not finished → `[SM] Cannot join guards yet.`
  2. CT banned → `[SM] Cannot join guards because you are CT banned.`
  3. Hours → `[SM] Cannot join guards because you must play at least 5 hours.`
  4. `!CanClientJoinGuards` → `[SM] Guards team full. Type !guard to join the queue.` (not queued) or `[SM] Guards team full. You are #%i in the queue.`

  Each rejection plays the sound and blocks. These messages are plain `PrintToChat` + console with no colours.

#### 6.1.4 Pre-guard queue (`UltJB_CellDoors_OnRoundStartReady`, fired at map start and every round start)
1. Stop the timer. If there is any player on the CT team: return.
2. **Force-open the cell doors** (`UltJB_CellDoors_ForceOpen`).
3. `countdown = 0`, show the hint to **all**: `<font color='#6FC41A'>Selecting guards in:</font>\n<font color='#DE2626'>{30-countdown}</font> <font color='#6FC41A'>seconds.</font>`
4. `preQueueFinished = false`. Start a 1 s timer and chat `{olive}The guard queue will be randomized in {lightred}%i {olive}seconds.`

Mid-map, if the CT team is empty at a round start, the whole procedure runs again, and joining CT is blocked until it finishes.

Timer: `countdown++`. If below 30, update the hint. At 30: `preQueueFinished = true` and the hint `<font color='#6FC41A'>Guards have been selected!</font>`.
* **Queue empty:**
  * Chat `{olive}No players found in the guard queue.`
  * Pick a random player whose *pending* team is T. Try with CT-ban + hours checks first, then without the CT-ban check, then without either.
  * Nobody: `TerminateRound(7.0, Draw)` and stop.
  * Else `SetClientPendingTeam(p, CT)`, chat `{olive}Choosing a random new guard: {lightred}%N{olive}.`, `FixTeamRatio()`, `TerminateRound(7.0, Draw)`.
* **Queue not empty:**
  * Randomly shuffle the queue (repeatedly draw a random remaining element into a new order).
  * Take the first entry, remove it and switch it to CT.
  * Chat `{olive}The guard queue has been randomized, from now on it will be first come first serve.` then `{olive}Choosing a guard from queue: {lightred}%N{olive}.`
  * `FixTeamRatio()`, `TerminateRound(7.0, Draw)`.

#### 6.1.5 Ratio maths (all counts by **pending team**, `m_iPendingTeamNum`)
* `ShouldMovePrisonerToGuard()`: `P' = P-1`, `G' = G+1`. False if `P' < 1`. True iff `P'/G' >= ratio(2)`.
* `ShouldMoveGuardToPrisoner()`: False if `G <= 1`. `maxG = floor(P/ratio)`. True iff `G > maxG`.
* `CanClientJoinGuards(c)`:
  * `G+1`, and `P-1` if c's pending team is T.
  * If `G <= 1` → true (the first guard is always allowed).
  * `r = P/G`. If `r < ratio` → false.
  * `needed = max(1, ceil(r - ratio))`. If `needed > queueSize` → true. Otherwise true only if `c` is among the first `needed` queue entries (queue priority).
* Worked examples (ratio 2):
  * P=3, G=0 → move 1 → G=1, P=2.
  * P=10, G=2 → G=4, P=8.
  * G=5, P=8 → move 1 guard → G=4, P=9.

#### 6.1.6 `FixTeamRatio()` (on `round_end`, and after the pre-queue selection)
1. While `ShouldMovePrisonerToGuard()`:
   * If the queue has entries, take `queue[0]` (removed), and chat `{olive}Choosing a guard from queue: {lightred}%N{olive}.`. Queue entries are **not** re-validated for CT ban or hours here.
   * Otherwise take a random T (same fallback chain) and chat `{olive}Choosing a random new guard: {lightred}%N{olive}.`
   * Nobody: chat `{olive}Could not find a valid player to switch to guards.` and break.
   * `SetClientPendingTeam(CT)`.
2. If anyone was moved: return.
3. While `ShouldMoveGuardToPrisoner()`: take a random pending-CT player (no checks), `SetClientPendingTeam(T)`, and **insert them at the front of the guard queue** so demoted guards get first chance to return.

`SetClientPendingTeam(c, team)`:
* If c is currently a prisoner, `UltJB_LR_EndLastRequest(c)`.
* Set the block-message flag, then `CS_SwitchTeam(c, team)` (keeps the player alive and keeps weapons), then set `m_iPendingTeamNum = team`, then clear the flag.
* `player_team` pre: while the flag is set, `SetEventBroadcast(true)` (hides the "X joined team" feed line).

Queue maintenance: remove the player on disconnect (Post), and on `player_team` post whenever the new team ≠ T. Map start: `preQueueFinished = false`. Map end: stop the timer.

No natives or forwards are exposed.

### 6.2 CS2 port design
**Module:** `TeamRatioModule`, Order ≈ 15. Internal service (optional `ITeamRatioService { int GetQueuePosition(JbPlayer p); bool PreQueueFinished {get;} }` for HUD/other modules).

* **jointeam:** `Plugin.AddCommandListener("jointeam", OnJoinTeam, HookMode.Pre)`. `info.GetArg(1)` holds the team. Return `HookResult.Handled` (or `Stop`) to block. Also block `"jointeam 0"`.
  * Direct T join: `controller.ChangeTeam(CsTeam.Terrorist)` then return Handled.
  * Restricted sound: `TeamRatio.RestrictedSound` (default `sounds/ui/counter_beep.vsnd`, known good; replace with an addon `.vsnd` if desired) via `ExecuteClientCommand("play ...")`.
* **Pending team:** read `controller.PendingTeamNum`; if 0, use `controller.TeamNum`. Write:
  ```csharp
  blockTeamMsg = true;
  controller.SwitchTeam(team);
  controller.PendingTeamNum = (byte)team;
  Utilities.SetStateChanged(controller, "CCSPlayerController", "m_iPendingTeamNum");
  blockTeamMsg = false;
  ```
  The `EventPlayerTeam` **Pre** handler sets `info.DontBroadcast = true` while `blockTeamMsg` is set. Game events fire synchronously inside `SwitchTeam`, so the flag works.
* **LR end before switching:** call the LR spec's `ILastRequestService.EndLastRequest(player)`.
* **Pre-queue trigger:** subscribe to the CellDoors spec's `RoundStartReady` event, which must also fire at map start. If the CellDoors spec doesn't raise it at map start, call the same logic from `OnMapStart` with `Server.NextFrame`.
  * CT count: `Utilities.GetPlayers().Count(p => p.Team == CsTeam.CounterTerrorist)` (current team, including bots and dead players).
  * Force open: `ICellDoorService.ForceOpen()`.
  * The hint goes to all non-bot players, duration 2 (final message 5).
  * TerminateRound per 0.10.
* **Required server cvars** (document in the install guide; the module also forces the first two at map start):
  * `mp_force_pick_time 999999` (if the cvar exists in CS2)
  * `mp_autoteambalance 0`, `mp_limitteams 0` (otherwise CS2's own balancer fights the ratio)
  * `mp_do_warmup_period 0` (warmup rounds interfere with the 30 s pre-queue and with TerminateRound)
* **CT ban:** `ICtBanService.IsCtBanned(JbPlayer)` from the admin/punishments spec (TimedPunishments port). If that service is absent, nobody is banned.
* **Hours:** `IPlaytimeService` (9.1) via `TeamRatio.RequiredGuardPlaytimeSeconds` (default **0 = disabled**; original 18000). A fresh CS2 server has no history, and the original only enforced this when the stats libraries were loaded.
* **Random pick:** use `Random.Shared`. The shuffle is a straight port (or Fisher–Yates; equivalent distribution).
* **Messages:** jointeam rejections are plain text (no prefix colours) as in the original. `!guard` messages use the coloured prefix.

### 6.3 Feasibility
**FULL** for everything: queue, ratio maths, jointeam enforcement, pre-guard queue, forced first-round cell opening, random picks, demotion to queue front, silent team switches. The hours requirement and CT ban depend on the playtime and CT-ban services (opt-in / other spec). The sound uses a built-in `.vsnd`.

---

## 7. Points (`UltJB_Points.sp` v1.3, author RussianLightning)

### 7.1 Original behaviour
* `round_start` pre: `enabled = GetClientCount(inGameOnly=true) >= 10` (counts bots), `deadWarden = 0`.
* `UltJB_Warden_OnDeath(client)`: `deadWarden = client`. This runs from the warden's **pre** death hook, before this plugin's post hook.
* `player_death` post: do nothing unless points are enabled, no Day is in progress, the victim and attacker are valid players, and attacker ≠ victim. Base `points = 50`. If the **victim is a guard**:
  * Victim == deadWarden: `UserPoints_GivePoints(attacker, 100)` and chat to the attacker (plain, no prefix) `You have been given 100 points for killing the warden.`. Then `deadWarden = 0`.
  * Otherwise give 50: `You have been given 50 points for killing a guard.`

  There is no check that the attacker is a prisoner, so a guard team-killing a guard also earns points (faithful).
* Backend: the `user_points` plugin (`Plugins/UserPoints`: DB-backed points with per-minute income, store currency). `UserPoints_GivePoints(client, amount)`.

### 7.2 CS2 port design
**Module:** `PointsModule`, Order ≈ 40.
* Count players with `Utilities.GetPlayers().Count(p => !p.IsHLTV)` (bots included, as in the original).
* Warden-death tracking comes from `IWardenService.WardenDied`, which is guaranteed to be raised from the warden module's **Pre** death handler. This module uses the Post `OnPlayerDeath`, so the order is preserved.
* Day check: `IDayService.IsInProgress`.
* Backend: `IPointsService.GivePoints(JbPlayer p, int amount)` from the UserPoints/Store port (other spec). If no points service is registered, the module logs once and does nothing.
* Config: `Points.MinPlayers = 10`, `Points.GuardKill = 50`, `Points.WardenKillMultiplier = 2`.

### 7.3 Feasibility
**FULL**, provided a points backend exists (dependency on the UserPoints/Store spec).

---

## 8. Player Models (`UltJB_PlayerModels.sp` v1.20)

### 8.1 Original behaviour
* **Guard model:** `models/player/custom_player/legacy/ctm_swat.mdl` (stock CS:GO SWAT; all guards look identical).
* **Prisoner models** (custom Swoobles, downloaded with their materials). The index order matters:
  * 0 `swoobles/prisoners/male_black_big_new/male_black_big_new.mdl`: **help 1** model (play time < 15 h)
  * 1 `swoobles/prisoners/female_asian_new/female_asian_new.mdl`: **help 2** model (< 30 h)
  * 2 `swoobles/prisoners/male_white_hair_new/male_white_hair_new.mdl`: regular. Regular players get `GetRandomInt(2, size-1)`, so with 3 models they **always** get index 2.
* Cvars: `ultjb_playermodels_help1_bonushealth` **30**, `ultjb_playermodels_help2_bonushealth` **15**.
* On `UltJB_Settings_OnSpawnPost`:
  * **Prisoner:**
    * `help = Settings.GetClientHelpNumber - 1`.
    * If `help != -1`, health `+= bonus` (130 / 115 via SetClientsHealth, which raises max HP).
    * If the player has a donator model activated (`DItemPlayerModels_HasUsableModelActivated`), **return** (no jailbreak model, and also no `m_nSkin` reset, no equipment refresh and **no OnApplied forward**).
    * Otherwise apply the prisoner model (the help model, or the regular random one).
  * **Guard:** same donator-model early return; otherwise the guard model.
  * Then `m_nSkin = 0`, equipment refresh (N/A), fire `UltJB_PlayerModels_OnApplied(client)`.
* Natives: `UltJB_PlayerModels_ApplyGuardModel(client)` → true and `UltJB_PlayerModels_ApplyPrisonerModel(client)` → true (uses the help index). Consumer: HorrorMovie warday (re-applies the prisoner model).
* Forward `UltJB_PlayerModels_OnApplied(client)`. Consumer: Xray (rebuild glow).
* Related identifiers elsewhere: **Warden and Last Guard use `ctm_heavy.mdl`**. WardenMenu colours use `m_nSkin 1` (the white prisoner body texture).

### 8.2 CS2 replacement plan
CS2 cannot load the Swoobles `.mdl` models, and only CS2 `.vmdl` models can be used. All model paths are config, precached in `OnServerPrecacheResources`.

| Role | Original | CS2 default (config key) | Rationale |
|---|---|---|---|
| Guard (all guards identical) | `ctm_swat.mdl` | `PlayerModels.GuardModel = "characters/models/ctm_sas/ctm_sas.vmdl"` | the stock CS2 default CT agent, the safest existing path; it keeps "all guards identical", overriding equipped agent skins. Alternative: `characters/models/ctm_swat/ctm_swat_variante.vmdl` (SWAT, closest to the original look) |
| Prisoner help 1 (<15 h) | male_black_big | `PlayerModels.PrisonerModels[0] = "characters/models/tm_jumpsuit/tm_jumpsuit_varianta.vmdl"` | the CS2 jumpsuit (prisoner) models; the 3 variants keep the 3-slot design |
| Prisoner help 2 (<30 h) | female_asian | `PrisonerModels[1] = ".../tm_jumpsuit_variantb.vmdl"` | |
| Prisoner regular | male_white_hair | `PrisonerModels[2] = ".../tm_jumpsuit_variantc.vmdl"` (list `[2..]` = random pool) | |
| Warden | `ctm_heavy.mdl` | `PlayerModels.WardenModel = "characters/models/ctm_heavy/ctm_heavy.vmdl"` | heavy-armour CT, as in the original |
| Last Guard | `ctm_heavy.mdl` | `PlayerModels.LastGuardModel = "characters/models/ctm_heavy/ctm_heavy.vmdl"` | |

* **Verification note for testers:** these are standard CS2 game paths, but CS2 updates sometimes move agents. If any player shows the red ERROR model, change that config entry to a valid path or to `""` (keep the stock model).
* Custom models are supported by putting workshop addon `.vmdl` paths in the config. The server must mount the addon (MultiAddonManager) and clients download it.
* `m_nSkin` has no equivalent for stock agents, so the WardenMenu render-colour tint is used on the normal textures (3.3).

### 8.3 CS2 port design
**Module:** `PlayerModelsModule`, Order ≈ 12.
```csharp
public interface IPlayerModelsService
{
    bool ApplyGuardModel(JbPlayer p);
    bool ApplyPrisonerModel(JbPlayer p);           // uses help index
    event Action<JbPlayer>? ModelsApplied;          // UltJB_PlayerModels_OnApplied
}
```
* Subscribe to `ISettingsService.PlayerPrepared`. Do the help-HP step for prisoners, then the model, then raise `ModelsApplied`.
  * **Deviation:** always raise `ModelsApplied`, even when a cosmetics module overrides the model. The original skipped it for donator models, which left Xray glows stale.
  * Cosmetics hook: if the Store/donator spec exposes `ICosmeticsService.HasActiveModel(p)`, skip applying (but still raise the event).
* `SetModel` is applied immediately in the PlayerPrepared callback, which already runs one frame after spawn. A further `Server.NextFrame` is optional if testing shows the model being overwritten by the agent loadout.
* Help index: `ISettingsService.GetClientHelpNumber(p) - 1`.
* Precache: `RegisterListener<Listeners.OnServerPrecacheResources>(m => { foreach (var path in allConfiguredModels) if (!string.IsNullOrEmpty(path)) m.AddResource(path); })`.
* Config: `PlayerModels.Enabled` (default true), `Help1BonusHealth = 30`, `Help2BonusHealth = 15`.

### 8.4 Feasibility
| Feature | Verdict |
|---|---|
| Uniform guard model, prisoner model with help slots, warden/LG heavy model | **PARTIAL** (stock CS2 agent/jumpsuit models instead of the Swoobles custom models; custom models possible via a workshop addon) |
| Help bonus HP | **FULL** (requires the opt-in playtime/help system) |
| Natives and OnApplied | **FULL** |

---

## 9. Supporting services required by these modules (define here or reuse from other specs)

### 9.1 `IPlaytimeService` (replaces DatabaseUserStats + ClientTimes for these modules)
```csharp
public interface IPlaytimeService
{
    bool IsLoaded(JbPlayer p);                  // DBUserStats_HasServerStatsLoaded
    int StoredSeconds(JbPlayer p);              // DBUserStats_GetServerTimePlayed (saved history)
    int SessionSeconds(JbPlayer p);             // ClientTimes_GetTimePlayed (this connection)
}
```
* Persist per SteamID64 (JSON file `data/playtime.json`, or the framework DB). Load at `OnClientAuthorized`; this is where `IsLoaded` becomes true. Add +60 s every minute for players on T/CT. If the AFK spec provides `IAfkService.IsAway(p)`, exclude away time. Save at disconnect and map end.
* Config: `Playtime.Enabled = true` (always collect), `Settings.HelpSystemEnabled = false`, `Settings.Help1Seconds = 54000`, `Settings.Help2Seconds = 108000`, `TeamRatio.RequiredGuardPlaytimeSeconds = 0`.
* **Why the help system and hours requirement default to off:** on SWBS they were driven by optional libraries holding years of history. On a fresh CS2 server every player has < 15 h, so with help enabled every prisoner would spawn with 130 HP, and with hours enabled nobody could join CT manually. With the defaults off this matches the original's behaviour when those libraries were not loaded. Admins turn them on once history exists.

### 9.2 `ICtBanService`
`bool IsCtBanned(JbPlayer p)`, from the TimedPunishments/admin port (other spec). It is absent-safe: if the service is missing, nobody is banned.

### 9.3 `IPointsService`
`bool GivePoints(JbPlayer p, int amount)`, from the UserPoints/Store port (other spec).

### 9.4 Chat title / gag bypass
The PlayerChat port (other spec) must render the warden title and let the warden bypass `!sg` gags (2.1.13). It needs `IWardenService.IsWarden`.

### 9.5 Expected interfaces from other specs, as used here (names indicative; adapt to those specs)
* **LR:** `CanLastRequest()`, `HasStarted(p)`, `GetFlags(p)`, `NumInitialized`, `NumStartedContains(flags)`, `EndLastRequest(p)`, `StripWeapons(p, save)`, `event LastRequestInitialized(JbPlayer)`. `LrFlags.Freeday = 1<<10`.
* **Days:** `IsInProgress`, `event DayStarted(JbPlayer warden, DayType type, bool ffa)`. It must set `IsInProgress` before raising the event.
* **CellDoors:** `DoExist`, `HaveOpened`, `ForceOpen()`, `event RoundStartReady` (map start and every round start), `event Opened`.
* **WeaponSelection:** `GivePlayerWeapon(p, string weaponClass)`.
* **Logger:** `LogEvent(string msg, JbPlayer? a, JbPlayer? b, LogType type)`.

---

## 10. Forward/native → C# wiring table

| Original | Port API | Raised/implemented by | Must be consumed by | Ordering notes |
|---|---|---|---|---|
| `UltJB_Warden_GetWarden` | `IWardenService.Warden` / `IsWarden` | WardenModule | WardenMenu, Days, LR, MuteManager, Roll, Timer, PlayerChat, Store paintballs | |
| `UltJB_Warden_GetClientWardenCount` | `IWardenService.GetWardenCount` | Warden | Days (≥2 to choose Freeday/Warday) | per connection |
| fwd `UltJB_Warden_OnSelected` | `WardenSelected` | Warden (after counters++, before clan tag) | WardenMenu, Days (day-select window), Xray | |
| fwd `UltJB_Warden_OnRemoved` | `WardenRemoved` | Warden | WardenMenu (remove healing), MuteManager, Xray | after the warden is cleared |
| fwd `UltJB_Warden_OnDeath` | `WardenDied` | Warden, from the **Pre** death handler | Points, Timer command, MuteManager | before `WardenRemoved` and before any Post death handler |
| `UltJB_WardenMenu_CreateRing` | `IWardenMenuService.CreateRing` | WardenMenu | Warden (crouch gesture) | |
| `UltJB_LastGuard_GetLastGuard` | `ILastGuardService.LastGuard` | LastGuard | Warden, RPG warday | |
| `UltJB_LastGuard_GetNumNeeded` | `GuardsNeeded` | LastGuard | LR (if used) | |
| `UltJB_LastGuard_ResetDamageCounters` | `ResetDamageCounters()` | LastGuard | RPG warday | |
| fwd `UltJB_LastGuard_OnActivated_Pre` | `LastGuardActivating` | LastGuard (before LG is set) | Warden | |
| fwd `UltJB_LastGuard_OnActivated_Post` | `LastGuardActivated` | LastGuard | Xray | |
| `UltJB_Settings_*` natives | `ISettingsService` methods | Settings | Days, LR, CTF, Jihad, PlayerModels | |
| fwd `UltJB_Settings_OnSpawnPost` | `PlayerPrepared` | Settings (synchronously at the end of spawn prep) | PlayerModels → Warden → Days → Jihad | PlayerModels must run before anything that reads HP or models; subscribe in module Order |
| `UltJB_PlayerModels_Apply*Model` | `IPlayerModelsService` | PlayerModels | HorrorMovie warday | |
| fwd `UltJB_PlayerModels_OnApplied` | `ModelsApplied` | PlayerModels | Xray | |
| (consumed) `UltJB_LR_OnLastRequestInitialized` | LR spec event | LR | Warden (remove), WardenMenu (fade purge), LastGuard (remove) | |
| (consumed) `UltJB_Day_OnStart` | Days spec event | Days | Warden (remove) | |
| (consumed) `UltJB_CellDoors_OnRoundStartReady` / `OnOpened` | CellDoors events | CellDoors | TeamRatio / Settings | |
| (consumed) `ItemPaintballs_OnShootPaintball` | Store spec check `IsWarden` | Store | – | Store must skip the warden |

---

## 11. Config summary (suggested `JailbreakConfig` sections; defaults = original values unless noted)

```jsonc
"Warden": {
  "SelectTime": 3, "SelectTimeInitial": 10,            // ultjb_warden_select_time(_initial)
  "KissCooldown": 5.0,
  "PointerExpire": 0.5, "PointerColor": [240,50,15,255], "PointerWidth": 0.4, "PointerEndWidth": 1.0,
  "PulseRingRadius": 40, "PulseRingLife": 0.3,
  "LineColor": [255,0,136,255], "LineWidth": 5.0,
  "GestureTapWindow": 0.5,
  "PaintballMode": "beam",          // beam | decal | off
  "PaintballDelay": 0.02, "PaintballMaxSplats": 48, "PaintballDecalMaterials": [],
  "GuardClanTags": true, "WardenClanTag": "Warden",
  "KissSound": "", "DeathSound": "",
  "AllowBotWarden": false,          // debug only (new)
  "ShowPointerTip": false           // optional (new)
},
"WardenMenu": {
  "RingRadius": 50, "RingSizeMin": 1, "RingSizeMax": 5, "RingSizeDefault": 2, "RingSegments": 32, "RingWidth": 5.0,
  "HealingRadius": 33, "HealingInterval": 0.5, "HealingAmount": 5, "HealingMaxHealth": 100,
  "HealingParticle": "", "HealingLoopSound": "", "HealSound": ""
},
"LastGuard": {
  "GuardsNeeded": 3, "HpPerPrisoner": 17, "HpPerKill": 5,
  "DamageWait": 20, "DamageInterval": 3, "DamageAmount": 10,
  "BeaconSegments": 16, "BeaconSound": "sounds/tools/sfm/beep.vsnd", "BeaconSoundEvent": "", "BeaconAudibleRadius": 1000,
  "SlapSound": ""
},
"Settings": {
  "TeamNameGuards": "Guards", "TeamNamePrisoners": "Prisoners",
  "HealthshotHealth": 25, "BlockedCommands": ["kill","killvector","explode"],
  "NoblockCollisionGroup": "COLLISION_GROUP_DISSOLVING",
  "CellsClosedAutoRespawnSeconds": 60, "InCellWeaponRadius": 300, "InCellMaxClip": 15, "InCellWindowSeconds": 90,
  "HelpSystemEnabled": false, "Help1Seconds": 54000, "Help2Seconds": 108000,
  "BlockRoundEndBroadcast": false, "RoundEndTint": true,
  "GuardsWinText": "GUARDS WIN!", "PrisonersWinText": "PRISONERS WIN!",
  "GuardsWinSound": "", "PrisonersWinSound": "", "DrawSound": "",
  "HideBreakableSoundsFromGuards": true
},
"TeamRatio": {
  "PrisonersPerGuard": 2.0, "PreGuardQueueTime": 30,
  "RequiredGuardPlaytimeSeconds": 0,   // original 18000 when stats DB loaded
  "RestrictedSound": "sounds/ui/counter_beep.vsnd"
},
"Points": { "MinPlayers": 10, "GuardKill": 50, "WardenKillMultiplier": 2 },
"PlayerModels": {
  "Enabled": true,
  "GuardModel": "characters/models/ctm_sas/ctm_sas.vmdl",
  "PrisonerModels": ["characters/models/tm_jumpsuit/tm_jumpsuit_varianta.vmdl",
                     "characters/models/tm_jumpsuit/tm_jumpsuit_variantb.vmdl",
                     "characters/models/tm_jumpsuit/tm_jumpsuit_variantc.vmdl"],
  "WardenModel": "characters/models/ctm_heavy/ctm_heavy.vmdl",
  "LastGuardModel": "characters/models/ctm_heavy/ctm_heavy.vmdl",
  "Help1BonusHealth": 30, "Help2BonusHealth": 15
},
"Playtime": { "Enabled": true },
"Draw": { "BeamMaterial": "" }
```

---

## 12. Feasibility matrix (all features in this spec)

| # | Feature | Verdict | Loss / substitute |
|---|---|---|---|
| 1 | Auto warden selection (primary/secondary queues, 10 s/3 s timers, force-removed skip) | FULL | – |
| 2 | `!w` `!uw` `!wwho` `!rw` `!sw/!setw/!setwarden` | FULL | – |
| 3 | Warden heavy model | FULL (config) | stock CS2 heavy model path |
| 4 | Warden clan tag | FULL | re-apply on spawn |
| 5 | Warden chat title / gag bypass | FULL via chat module | dependency |
| 6 | Countdown/selection hints | FULL | centre HTML; menus may overwrite |
| 7 | Pointer laser (double-tap E) + pulse rings | PARTIAL | not through walls; one beam start point for all viewers |
| 8 | Bullet line (triple-tap walk) | FULL | not through walls (same as original line material) |
| 9 | Gesture ring (triple-tap crouch) | FULL | – |
| 10 | Warden paintballs | PARTIAL | beam splats (or addon decals) |
| 11 | `!kiss`/`!smooch` | FULL | custom sound needs addon |
| 12 | Warden death sound | PARTIAL | configurable `.vsnd` |
| 13 | Warden menu, rings menu, manage ring, client colour menu | FULL | Back as an item; no spacer |
| 14 | Four coloured rings, size 1–5 | FULL | beam polygons |
| 15 | Healing area heal logic | FULL | – |
| 16 | Healing area particle/sounds | PARTIAL | pink beam ring; addon particle/sound optional |
| 17 | Client colours (+ screen tint, hints), `!cc` | FULL / PARTIAL tint (no white-body skin) | – |
| 18 | Last guard enable/activate/HP/punish/slap/suicide/reset | FULL | – |
| 19 | Last guard beacons | PARTIAL | beam pulse rings; pitch only with soundevent |
| 20 | Team names, forced cvars, kill block | FULL | – |
| 21 | Spawn loadout/HP/armor/noblock/stable spawn spot | FULL | active weapon best effort |
| 22 | Gravity / speed reset | FULL / PARTIAL | no `m_flLaggedMovementValue` in CS2 |
| 23 | Late-join auto-respawn + auto-respawn API | FULL | – |
| 24 | In-cell weapon ammo nerf | FULL | – |
| 25 | Guard suicide weapon wipe | FULL | different detection mechanism |
| 26 | Round-end reason override, block round termination | FULL | detour hook |
| 27 | Win overlays | NOT POSSIBLE | client `r_screenoverlay` unsettable, no `.vtf` overlays → centre HTML text + tint |
| 28 | Win sounds | PARTIAL | addon/built-in `.vsnd` |
| 29 | Breakable sounds hidden from guards | PARTIAL | usermessage recipient filter (all breakable sounds) |
| 30 | Help number (new players) | FULL (opt-in) | new playtime service |
| 31 | Guard queue, ratio 2:1, jointeam enforcement, pre-guard queue, forced first-round cells | FULL | – |
| 32 | CT-ban and 5 h guard requirement | FULL (dependency / opt-in) | – |
| 33 | Points for killing guards/warden | FULL (needs points backend) | – |
| 34 | Player models (guard/prisoner/help) | PARTIAL | stock CS2 agents/jumpsuits or addon models |

---

## 13. Original quirks and port decisions (implementers: follow this column)

| Quirk in original | Port decision |
|---|---|
| Primary queue persists across rounds; the current warden can `!w` themselves | **Keep** |
| Warden count is per connection; Days needs ≥2 | **Keep** |
| Kiss cooldown is global and consumed even on a miss | **Keep** |
| The line keeps drawing after the warden dies, until round end | **Keep** |
| Rings persist after the warden dies (until the next round reset) | **Keep** |
| Bullet history isn't reset on warden change | **Keep** |
| `"Type !wm {olive} to open..."` double space | **Keep** (or trim; cosmetic) |
| LG Pre forward: TryRemove starts a 3 s timer and prints a spurious message | **Fix**: remove the warden without starting the timer (functionally identical) |
| `round_start` with 0 guards leaves `canLastGuard` stale | **Keep** (harmless) |
| Self-damage by the LG resets the punishment counters | **Keep** |
| The "remove all colours" hint goes to every alive prisoner (also at every round reset) | **Keep** |
| The internal auto-respawn start doesn't reset delays (stale warday delays) | **Fix**: use 0.3 |
| The original-spawn table leaks across reconnects in the same slot | **Fix**: reset on connect |
| OnApplied not fired for donator models | **Fix**: always fire |
| Guards earn points for team kills | **Keep** |
| Queue entries not revalidated for CT ban/hours in FixTeamRatio | **Keep** |
| No warden at all for a round if no guard was alive at freeze end | **Keep** |
| "A warden could not be found" → TODO freeday not implemented | **Keep** (do not add) |

---

## 14. In-game test plan for these modules

Setup:
* `mp_do_warmup_period 0; mp_autoteambalance 0; mp_limitteams 0; bot_quota 0; mp_freezetime 3`.
* For bot-assisted tests, set `Warden.AllowBotWarden=true` temporarily.
* Admin needs `@css/root`.

1. **Pre-guard queue:**
   1. Fresh map, join T. Expect the hint "Selecting guards in: 30", the chat "guard queue will be randomized in 30 seconds", and the cells forced open.
   2. Try to join CT: "Cannot join guards yet." with a beep.
   3. Type `!guard`: "You are in the guard queue."
   4. At 0: "Guards have been selected!", you are moved to CT silently (no join-team feed line), and the round ends as a draw 7 s later.
2. **Ratio:**
   1. With 3 Ts and 1 CT, a T selecting CT gets "Guards team full. Type !guard to join the queue."
   2. Add Ts until the ratio allows it; the player at the front of the queue is moved at round end ("Choosing a guard from queue").
   3. Make CTs exceed P/2; at round end a random CT is moved to T and becomes queue #1.
3. **Warden selection:**
   1. Round starts, freeze ends. Guards see "Type !w if you would like to have priority."; the hint counts down 10…1.
   2. A guard who typed `!w` is selected first. They see the selection hint, wear the heavy model, show the clan tag "Warden", and get "Type !wm to open the warden's menu."
   3. `!wwho` from a prisoner shows the name.
4. **Reselection:**
   1. The warden types `!uw`. Chat shows "removed themself"; 3 s countdown; the next guard is selected round-robin; the old warden's model is restored.
   2. Admin `!rw`: the removed player cannot `!w` ("cannot join the queue after being force removed") and is skipped unless they are the only guard.
   3. `!setwarden <name>` on a prisoner: "Target must be a guard". On a guard: set immediately, with no countdown.
5. **Warden death:** kill the warden. Expect "The warden has died!", the death sound (if configured), and a 3 s reselection. Points: with ≥10 players, the killer gets "You have been given 100 points for killing the warden."
6. **Pointer:** as warden, double-tap E and hold. An orange-red laser follows the crosshair. Tap E again quickly: a pulse ring at the pointed spot. Release: it disappears within ~0.6 s.
7. **Line and gesture ring:**
   1. Shoot two wall points, then triple-tap Shift: a pink line between the two impacts, staying until round end.
   2. Shoot two floor points ~200 units apart, then triple-tap Ctrl: a green ring with radius equal to half the distance, and "Green ring's position set."
8. **Paintballs:** shoot walls as warden: coloured splat markers appear (max 48, cleared next round). Shooting props/players paints nothing.
9. **Warden menu:**
   1. `!wm` → Create healing area on a slope: "You must place this on a flat surface." On a floor: "Healing area created" and a pink-ish ring of radius 33. A damaged prisoner standing on it gains +5 HP every 0.5 s up to 100.
   2. Rings: set Red, increase to size 5 (Increase becomes disabled at 5), decrease to 1, remove.
   3. Client colours with prisoners inside rings: they turn the ring colour, see the screen tint and hint. "Remove client ring color" clears only those inside rings. `!cc remove` clears all.
   4. A non-warden using `!wm`: "You must be warden to use this command."
10. **Last guard:**
    1. Start with ≥3 CTs: round start chat "3 guards alive at round start. Last Guard: ENABLED!"
    2. Kill CTs until one remains (no LR running). Expect "X is the last guard!", the prisoner hint "is last guard. Kill them!", LG HP +17 per alive prisoner, heavy model, beacon rings on everyone (blue for the guard, orange for prisoners, faster and closer-pitched when near), and the warden removed.
    3. LG hint counts 20…1. Without damage, the LG is slapped for 10 every 3 s and killed when HP ≤ 10. Damaging a prisoner resets the counter to 20. Killing a prisoner gives +5 HP.
    4. With 2 CTs at round start: "Last Guard: DISABLED."
11. **Settings:**
    1. Spawn as CT: Five-SeveN + knife + healthshot, 100 armor, no helmet, 100 HP. As T: T knife, 0 armor.
    2. Players pass through each other (noblock).
    3. `kill` in console does nothing.
    4. Join late as T within 60 s while the cells are closed: auto-respawned once at a T spawn.
    5. Pick up a map gun in the cells within 90 s: clip ≤ 15, reserve 0.
    6. As a CT, suicide with an HE grenade: the dropped guns vanish.
    7. Round end shows "GUARDS WIN!" or "PRISONERS WIN!" centre text.
    8. Scoreboard team names read Guards and Prisoners.
12. **Player models:** guards all wear the configured guard model; prisoners wear the jumpsuit (variant c). Enable `HelpSystemEnabled` with a new account: prisoner spawns with 130 HP and variant a.
