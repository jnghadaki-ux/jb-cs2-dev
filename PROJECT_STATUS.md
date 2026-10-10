# Project status — SWBS Ultimate Jailbreak for CS2

_Last updated: session 3 (2026-10-10), version 0.10.0 (.NET 8 only). Nothing has been tested on a live CS2 server yet: every gameplay feature is
**Implemented, unverified** unless stated otherwise._

## Build

| Item | State |
|---|---|
| `dotnet build -c Release` (net8.0 only → CSS 1.0.368) | **Passes**, 0 warnings, 0 errors (session 3) |
| Unit tests (`tests/SwbsJailbreak.Tests`, 21 tests: round state machine, storage, targets, config) | **Pass** |
| Packaging (`build.sh` / `build.ps1` → `SwbsJailbreak-<ver>-net8.zip`) | **Works** (zip produced, CSS API not bundled); not yet loaded on a server |
| Live server load test | **Not done** (no CS2 server in this environment) |

## Modules (single plugin, `src/SwbsJailbreak/Modules/*`)

| Module | Original SMPlugins source | Status |
|---|---|---|
| Core (round state machine, players, HUD compositor, menus, damage/movement/weapon services, storage, perms, lang) | framework | Implemented, unverified (state machine unit-tested) |
| Admin (SourceMod basecommands/funcommands/basecomm/basebans equivalents, Respawn/Heal/Teamswitch/TimedMuteGag, SimpleAdmin salvage, !admin menu) | Plugins/* + owner's SimpleAdmin | Implemented, unverified |
| Commands (!g, freekill, whokilled, 1up/xup, roll, rules, speed, timer, xray) | Commands/* | Implemented, unverified |
| Effects (Drugs, Drunk, Far Out, HS Only, Low Gravity, Speed; Third Person = Unsupported) | Effects/* + API_Effects | Implemented, unverified |
| Warden + Warden Menu (queue, selection timer, pointer/line/ring markers, rings, healing area, client colours, kiss) | UltJB_Warden / UltJB_WardenMenu | Implemented, unverified (salvaged agent code, compiled + phrases added; needs review) |
| Last Guard | UltJB_LastGuard | Implemented, unverified (salvaged) |
| Cells (per-map JSON, legacy name lists, auto-detect, auto-open, opened detection, editor) | UltJB_API_CellDoors / UltJB_CellDoors | Implemented, unverified (editor written this session) |
| Logger (!log/!logs) | UltJB_Logger | Implemented, unverified (salvaged) |
| Mute + VoiceChat (warden mute power, prisoner round-start mute, dead mute) | UltJB_MuteManager / UltJB_VoiceChat | Implemented, unverified (salvaged) |
| WeaponSelection API | UltJB_API_WeaponSelection | Implemented, unverified (salvaged) |
| Settings (spawn loadout, stable spawns, late-join respawn, cell weapon nerf, suicide wipe, round-end banner, natives) | UltJB_Settings | Implemented, unverified |
| Teams (guard queue, pre-queue, ratio, jointeam, CT bans) | UltJB_TeamRatio / Command_CTBan | Implemented, unverified |
| PlayerModels, Points | UltJB_PlayerModels / UltJB_Points | Implemented, unverified |
| Jihad | UltJB_Jihad | Implemented, unverified |
| BhopCheck, ServerTweaks (noblock, grenade noblock, admin tags, clan tag filter, spam messages, kill feed) | Plugins/* | Implemented, unverified |
| Last Request API (slots, menus, rebel-as-team, admin freedays, opponent selection, slay timers, isolation, beacons, LR/rebel map points) | UltJB_API_LastRequest | Implemented, unverified |
| All 25 LRs (Freeday x4, Draw, Rebel x8, Weapon Battle x5, Contest x7) | LastRequests/* | Implemented, unverified |
| Days API (menus + rules, team/FFA, warday freeze, weapon rules, !daysedit) | UltJB_API_Days | Implemented, unverified |
| All 23 days | Days/* | 22 Implemented, unverified (CTF needs !ctf_setflag/!ctf_setcapture per map); RPG **Blocked** on CSS 1.0.368 |
| Other SMPlugins extras (AFK manager, rotating messages, rules/help menus, spectate menu, radio spam block, ...) | Plugins/* | Not started — see FEATURE_GAPS.md |

## Counts (session 3, from the code and the original sources)

| Item | Count |
|---|---|
| Plugin modules (`JbModule` classes) / core service contracts / source files / lines | 30 / 15 / 67 / ~23,500 |
| Console commands + aliases registered (`[ConsoleCommand]`) | 136 (Admin 71, Commands 14, Warden 12, LastRequest 8, Days 7, Cells 7, Extras 6, Teams 4, Logger 4, Mute 2, PlayerExtras 1) |
| Chat/HUD phrases (17 files) | 581 defined; every literal phrase key used in code resolves |
| LRs / days / effects / admin+fun command groups | 25 / 23 (22 usable) / 6 (+1 unsupported) / see Admin |
| Original UJB sources (81): usable in code | 79 (70 Implemented-unverified + 9 Partial); 1 Blocked, 1 Unsupported |
| Automated tests | 35 passing (round state machine, config, storage, phrases, targets, weapons, perms, command catalogue + permission gates, no duplicate command names); **0 in-game / server tests** |

SMPlugins source coverage (390 `.sp`): see FEATURE_INVENTORY.md §4.

## Blockers
* No CS2 dedicated server available to the agent: Level 2/3 tests must be run by the owner.
* Account usage window limits session length; sub-agents are kept to a minimum on request.

## Next actions (priority order)
1. Owner: run TEST_PLAN.md §2–§3 on a CS2 server with CSS 1.0.368 + the net8 package; report console errors and failures.
2. Review pass for runtime bugs (LR, Days, Jihad, Settings, Teams, Cells editor, Warden ping) — highest code risk.
3. RPG warday on CSS 1.0.368 via the engine bullet trace (FEATURE_GAPS.md plan).
4. Remaining small server plugins (FEATURE_GAPS.md "Not ported yet").
5. Validate admin + JB commands against current CSS plugins (CS2-SimpleAdmin, eGo/destoer JB) and research extra
   CS:GO/CS:S JB features (owner request, before declaring finished).
