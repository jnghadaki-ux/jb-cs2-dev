# Feature inventory — SMPlugins → SWBS Ultimate Jailbreak (CS2)

Status legend (master prompt §5): **Verified** · **Implemented, unverified** · **Partial** · **Blocked** · **Not started** ·
**Unsupported** · **Out of scope** (plugin belongs to another SWBS server type, not to the jailbreak server).

Nothing has been tested on a live CS2 server yet, so no row is **Verified**. "Implemented, unverified" = code exists, builds
for net8.0 (CounterStrikeSharp 1.0.368, the only target), and follows the original source; it still needs the in-game test in TEST_PLAN.md.

Original sources: `SMPlugins-master/Mods/UltimateJailbreak/*` (hlstriker), `SMPlugins-master/Plugins/*`, plus the owner's
SimpleAdmin mod. CS2 implementation: `src/SwbsJailbreak/Modules/<Module>`.

## 1. Ultimate Jailbreak core (Mods/UltimateJailbreak)

| Original | Behaviour | CS2 module | Status | Notes / limits |
|---|---|---|---|---|
| UltJB_Warden.sp | warden queue (!w), selection timer, !uw, !wwho, !rw, !sw/!setwarden, kiss, pointer/line/ring gestures, heavy model, clan tag | Warden/WardenModule | Implemented, unverified | No ray trace on CSS 1.0.368: the pointer and menu rings use the warden's ping (stock CS2 ping key); line/ring gestures use bullet impacts as the original. Custom kiss/death sounds need an addon. |
| UltJB_WardenMenu.sp | !wm rings (4 colours, sizes), healing area, client colours, !cc | Warden/WardenMenuModule | Implemented, unverified | Rings are real beam entities; m_nSkin tint replaced by render colour. Healing particle is a CS2 particle. |
| UltJB_LastGuard.sp | last guard enable check, beacons, damage timer/slap | LastGuard | Implemented, unverified | |
| UltJB_API_CellDoors.sp / UltJB_CellDoors.sp | per-map cell doors, auto-open (60 s), opened detection, !cdo, editor | Cells | Implemented, unverified | Per-map JSON + 154 original CS:GO name lists + auto-detect; editor picks the pinged door (player_ping entity) or the door nearest the crosshair direction; buttons-only maps supported. |
| UltJB_Logger.sp | round event log, !log / !logs | Logger | Implemented, unverified | |
| UltJB_MuteManager.sp | warden mute power, guard audibility | Mute | Implemented, unverified | Voice via CSS listen overrides / VoiceFlags. |
| UltJB_VoiceChat.sp | prisoners muted 60 s at round start, dead muted, notices | Mute | Implemented, unverified | |
| UltJB_Jihad.sp | random jihad bomb, +attack2 arm, head bomb, radius damage, drop menu | Jihad | Partial | Breach charge does not exist in CS2 → C4 item (visible on back/radar). "Akbar" sound needs an addon. |
| UltJB_API_WeaponSelection.sp | weapon menus/filters, give API, LR weapon marking | WeaponSelection | Implemented, unverified | |
| UltJB_Settings.sp | spawn loadout, stable spawn spot, late-join respawn, cell weapon nerf, suicide weapon wipe, round end, natives | Settings | Implemented, unverified | Round-end overlay impossible (r_screenoverlay) → centre banner + tint. Win mp3s need an addon. |
| UltJB_TeamRatio.sp | !guard queue, pre-queue, 2:1 ratio, jointeam rules | Teams | Implemented, unverified | Play-time requirement off by default (no stats DB). |
| Commands/UltJB_Command_CTBan.sp | !ctban / !unctban / !check_ctban | Teams | Implemented, unverified | TimedPunishments DB → JSON storage, real-time expiry. |
| UltJB_PlayerModels.sp | uniform guard model, help-level prisoner models + bonus HP | PlayerExtras/PlayerModelsModule | Partial | Swoobles custom models cannot load → stock CS2 agents (configurable, workshop .vmdl possible). |
| UltJB_Points.sp | points for guard/warden kills | PlayerExtras/PointsModule | Partial | UserPoints store not ported → JSON points + !points. |
| UltJB_API_Effects.sp | effect registry + selection menu | Effects | Implemented, unverified | |
| Effects: Drugs, Drunk, Far Out, Headshots Only, Low Gravity, Speed | per-player effects | Effects/BuiltInEffects | Implemented, unverified | Drunk camera roll impossible in CS2 → FOV swing + fade + shake. |
| Effects: Third Person | third-person camera | — | Unsupported | No server-side third-person control in CS2. |
| UltJB_API_LastRequest.sp | LR slots (8 %), menus, random LR, rebel as a team, admin freedays, opponent selection, slay timers, isolation, beacons, !lr/!lrs/!abortlr/!givefreeday | LastRequest | Implemented, unverified | ZoneManager "lr_tele" and PathPoints "rebels" replaced by !lr_setpoint / !rebelpoint_add per map. |
| LR Freeday: Classic, Auto Bhop, Speed, Low Gravity | freeday next round | LastRequest/SimpleLastRequests | Implemented, unverified | Per-player auto bhop implemented in core Movement. |
| LR End Round: Draw | draw round | LastRequest/SimpleLastRequests | Implemented, unverified | |
| LR Rebel: AWP, Scout, Wild Cowboy, Machine Gunner, Coked Taser, Drug Dealer | rebel loadouts | LastRequest/SimpleLastRequests | Implemented, unverified | |
| LR Rebel: Phat Kid, Ghost | tank / invisible knifer | LastRequest/RebelSpecialLastRequests | Implemented, unverified | Ghost invisibility = render none + weapons hidden via CheckTransmit; shadows may remain. |
| LR Weapon Battle: Classic, No Scope, Shot for Shot, Mag for Mag, Knife Backstab | duels | LastRequest/WeaponBattleLastRequests | Implemented, unverified | |
| LR Contest: Russian Roulette, Most Jumps, Fall Damage (+Old School), Gun Toss (+Old School), Race | contests | LastRequest/ContestLastRequests | Implemented, unverified | Race finish-line model replaced by real beam gate. Kills credited via death-event rewrite. |
| UltJB_API_Days.sp | day menus/rules, team/FFA, warday freeze, weapon rules, !daysedit | Days/DaysModule | Implemented, unverified | FFA needs 90 rebel points per map (configurable). point_template weapon kill simplified. |
| Freedays: Classic, Auto Bhop, Low Gravity, Speed | | Days/SimpleDays | Implemented, unverified | |
| Wardays: Gang, Jihad, Taser, No Scope, Fat Kid, Low Gravity, Scoutz Knivez, Weapon Selection, Classic, Heavy Assault, One In The Chamber | | Days/SimpleDays | Implemented, unverified | Heavy Assault uses item_heavyassaultsuit (no m_bHasHeavyArmor in CS2). |
| Wardays: Hunger Games, Zombie, Throwing Knives, CT Retake, Horror Movie | | Days/LargeDays | Partial | Crate/knife models and fog rendering need in-game verification; custom sounds need an addon. |
| Warday: Freeze Tag | | Days/FreezeTagDay | Implemented, unverified | |
| Warday: RPG | | Days/RpgDay | Blocked | Rockets need a world-collision point; CSS 1.0.368 has no ray trace, so the day is not registered. Plan: engine bullet trace (weapon_fire + bullet_impact). Simulation code kept. |
| Warday: Capture the Flag | side selection, flag carry/drop/return/capture, respawns, HP balance | Days/CaptureTheFlagDay | Partial | Flag/capture point are beam visuals (custom models impossible); map points via !ctf_setflag / !ctf_setcapture. |
| Commands: Freekill, OneUp, Roll, Rules, Speed, Timer, Xray, CTMessage (!g) | | Commands | Implemented, unverified | Rules opens the URL in chat (no MOTD browser in CS2). |

## 2. SMPlugins plugins that ran on the jailbreak server

| Original | CS2 module | Status | Notes |
|---|---|---|---|
| SourceMod basecommands / funcommands / basecomm / basebans (used by SWBS admins) + owner's SimpleAdmin | Admin | Implemented, unverified | 60+ commands incl. !admin menu, bans/comms JSON, activity log. |
| Respawn, Heal, Teamswitch, TimedMuteGag, Noclip | Admin | Implemented, unverified | |
| NoBlock (hlstriker_noblock) | Extras/ServerTweaks | Implemented, unverified | |
| GrenadeNoblock | Extras/ServerTweaks | Implemented, unverified | |
| AdminTags, ClanTagFilter | Extras/ServerTweaks | Implemented, unverified | |
| BlockSpamMessages, DisableKillFeed | Extras/ServerTweaks | Implemented, unverified | |
| BhopCheck | Extras/BhopCheck | Implemented, unverified | CS2 sub-tick input: input counts are a lower bound. |
| MagicTargetFilter (@prisoners etc.) | Core/Targets | Implemented, unverified | |
| LogAdminActivity | Admin (activity log) | Implemented, unverified | |
| CommandList (`sm_commands`) | Extras/CommandListModule | Implemented, unverified | `!commands` menu of the commands the player may use (6/page) or `!commands console` for the full list in the player's console. Permission gates are read from [RequiresPermissions] and, by IL scan, from the capability each command checks in code (83 of 107 commands gated; unit-tested). |
| AFKManager, RotatingMessages, ServerRules, Information, InfoMenus, HelpMenu, MOTD | — | Not started | Planned (FEATURE_GAPS.md). |
| ShowSpectators, SpectateMenu, NoTextSpam, RadioSpamBlock, StopMapMusic, StopWeaponSounds | — | Not started | Planned. |
| Parachute, UnlimitedReserveAmmo, DisableFallDrownDamage, FixGravityOnNewRound, MapArmour | — | Not started | Small; candidates for the next session. |
| ThrowingKnife (standalone), CustomWeapons/RPG | — | Superseded | Their behaviour lives in the Throwing Knives / RPG wardays. |
| UserPoints, Store, StoreItems/*, DonatorItems/*, CoinPoints, TokensUpdate | — | Out of scope (for now) | Shop/donator economy; the points hook (IPointsService) is ready. |
| ZoneManager (Libraries/ZoneManager) | Zones/ZonesModule (+ Editor) | Implemented, unverified | Remade for CS2: per-map JSON (same fields as the SQL rows), named types, polling enter/exit/touch (no SDKHooks), max 256, `!zm`/`!zonemanager` editor with the original menus (position/size by look + click, angles, type, type data, delete, import trigger, import map, noclip) plus feet/ping placement, two-corner sizing, duplicate, label, teleport-to; editor boxes are beams only the editing admin sees; `!showtriggernames`, `css_zones_list`. Expand-to-wall (needed a trace) → expand to a pinged point. |
| ZoneTypes/* (9 plugins) | Zones/ZoneTypes | Implemented, unverified (7) · Unsupported (2) | Kill (timers), Blockade (push-back wall, visible, timers), Respawn, Teleport, Teleport Destination, Weapon Strip, Damage Blocker (+fire) ported; Flashlight (no CS2 flashlight effect) and Hide Toggle (needs HidePlayers) not registered. Additions: Push (jump pad), Notify (area alert), Cell Block (Crews turf). LR uses the `lr_tele` destination, CTF `ctf_flag` / `ctf_capture`, falling back to the point commands. |
| PathPoints, EntityHooker | Partial replacements | Partial | Rebel points (`!rebelpoint_add`) and cell door hammer ids replace the jailbreak uses. |
| Database*, UserStats, UserLogs, ClientTimes, ClientCookies, ClientSettings, Admins, DemoSessions, Replays, WebPageViewer, FileDownloader, ParticleManager, ModelSkinManager, SquelchManager, TimedPunishments | Core services | Replaced | JSON storage, CSS admin system, CSS particles/voice; MySQL is not required. |

## 3. SMPlugins plugins for other SWBS servers (not part of the jailbreak)

Blocks/* + BlockMaker (block-maker servers), MovementStyles/* + timers/replays/MapStats/MapRatings (bhop/surf), DeathrunQueue,
Multigames*/MinigamesAutoBhop, CourseAutoRespawn, AutoBhop/AutoStrafe/BhopCap/PreStrafe/ClientAirAccelerate (movement servers),
Skill-server plugins, Jetpacks, HidePlayers, HideHud, FootstepsMenu, MapVoting, ServerNeed, ShowForumThreads, Unsafe/*,
EntityPatches/* (CS:GO map fixes) — **Out of scope**: they never ran on the jailbreak server; listed so nothing is silently dropped.

## 4. Coverage summary by original `.sp` file (computed 2026-10-10)

Method: all 390 `.sp` files in the owner's SMPlugins-master upload were counted. "Used by Ultimate Jailbreak" is the
dependency closure of `Mods/UltimateJailbreak/*.sp` over `#include` + `RegPluginLibrary`/`LibraryExists` (a script, not
a guess). Which *other* plugins the SWBS jailbreak server ran is **not recorded in the repository**: buckets C/D below
are inferred from what each plugin does — the owner should correct them.

| Bucket | `.sp` files | Result |
|---|---:|---|
| SourceMod 1.6 stock compiler samples (`_SourceMod 1.6 Compiler`) | 92 | n/a — not SWBS code; their admin/base commands are re-created by the Admin module |
| **A. Ultimate Jailbreak itself** (`Mods/UltimateJailbreak`) | 81 | 70 Implemented/unverified · 9 Partial · 1 Blocked (RPG day) · 1 Unsupported (Third Person effect) · 0 Verified |
| **B. Libraries/helpers UJB pulls in** | 38 | 26 replaced by core services (DB/admin/cookies/stats/file/web libs → JSON storage + CSS admin; EntityHooker, ModelSkinManager, ParticleManager, PathPoints, PlayerChat, SquelchManager, ZoneManager → Fx/Chat/Mute/per-map points) · UserPoints Partial · ZoneType_Teleport replaced by `!lr_setpoint` · **Parachute not ported** (the LR API saves/removes/restores a player's parachute around an LR; HidePlayers only enters the closure through the optional store Equipment item, so it is optional, not required) · 8 store/cosmetic items out of scope (DonatorItem ColoredChat/PlayerModels/Titles, Item_Equipment, Item_Paintballs, SkinsForMapWeapons, TokensUpdate, Unsafe_Gloves — skins/gloves also barred by the GSLT rule) |
| **C1.** Other server plugins already ported | 15 | AdminTags, BhopCheck, BlockSpamMessages, ClanTagFilter, CommandList, DisableKillFeed, GrenadeNoblock, Heal, LogAdminActivity, MagicTargetFilter, NoBlock, Noclip, Respawn, Teamswitch, TimedMuteGag — all Implemented, unverified |
| **C2.** Jailbreak-relevant, **not ported yet** | 17 | AFKManager, DisableFallDrownDamage, FixGravityOnNewRound, HelpMenu, InfoMenus, Information, MOTD, MapArmour, NoTextSpam, RadioSpamBlock, RotatingMessages, ServerRules, ShowSpectators, SpectateMenu, StopMapMusic, StopWeaponSounds, UnlimitedReserveAmmo |
| C3. Superseded by a ported day | 2 | ThrowingKnife → Throwing Knives day; CustomWeapons/RPG → RPG day (blocked) |
| C4. Economy / store | 10 | CoinPoints, DonatorItems (2), StoreItems (7) — out of scope |
| **D1.** CS:GO-only engine/map fixes (meaningless on CS2) | 19 | AllowMorePlayers, BlockGameStart, DetectTeleportHacking, DisableDuckDelay, EntityPatches (13), FixCameraBug, FixRoundEndClassnames |
| **D2.** Generic server utilities (not jailbreak, optional) | 12 | ClientFootsteps, DisablePlayerDamage, FootstepsMenu, ForceMapEnd, HideHud, MapRatings, MapStats, MapVoting, MeasureGap, ServerNeed, ShowForumThreads, SteamIdToConsole |
| **D3.** Other server types — unused entirely | 97 | Blocks (30) + BlockMaker, MovementStyles (27), SpeedRuns (14), HideAndSeek, other ZoneTypes (8, speedrun/other servers), AutoBhop, AutoStrafe, BhopCap, PreStrafe, ClientAirAccelerate, CourseAutoRespawn, DeathrunQueue, GrenadesForSkillServers, Jetpacks, MinigamesAutoBhop, MultigamesQueue, NoScopeRounds, PlayerModelsForSkillServers, SkillServerWeapons, Unsafe (2) |
| D4. Stats/DB plumbing for other features | 7 | DatabaseMapStats, DatabaseUserBans, DatabaseUsernameStats, MapCookies, Replays, UserPointsForMinigamesWinner, UserStats |
| **Total** | **390** | |

Original UJB content: 25 last requests (Freeday 4, Draw 1, Rebel 8, Weapon Battle 5, Contest 7), 23 days (Freeday 4, Warday 19),
7 effects, 9 commands, 17 core/API plugins — all accounted for in bucket A.

## 5. Additions that are not SMPlugins ports (owner requests)

| Feature | Module | Status | Notes |
|---|---|---|---|
| Crews (prisoner gang system) | Crews/CrewsModule | Implemented, unverified | Design: docs/design/CREWS.md. Roster + ranks, respect (no buying), levels, turf on Cell Block zones, favors, crew chat, tags, rivals, warden shakedown, admin tools. |
| Crew War warday | Crews/CrewWarDay | Implemented, unverified | Crews vs crews, guards are referees; needs 2 crews with 2+ living members (`DayDefinition.CanStart`). |
| `!commands` | Extras/CommandListModule | Implemented, unverified | SMPlugins CommandList port. |
| `!open` / `!o` (admin) | Cells | Implemented, unverified | Owner decision: admin-only. |
| Auto warden setting, warden bonus with ≤2 guards | Warden | Implemented, unverified | Owner rules. |

