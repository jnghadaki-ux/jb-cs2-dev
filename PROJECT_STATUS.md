# Project status — SWBS Ultimate Jailbreak for CS2

_Last updated: session 2 (2026-10-10). Nothing has been tested on a live CS2 server yet: every gameplay feature is
**Implemented, unverified** unless stated otherwise._

## Build

| Item | State |
|---|---|
| `dotnet build -c Release` (net8.0 → CSS 1.0.368, net10.0 → CSS 1.0.376) | **Passes**, 0 warnings, 0 errors |
| Unit tests (`tests/SwbsJailbreak.Tests`, 21 tests: round state machine, storage, targets, config) | **Pass** |
| Packaging (`build.sh` / `build.ps1` → `SwbsJailbreak-<ver>-net10.zip` / `-net8.zip`) | Script exists, output not loaded on a server |
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
| **Last Request API + 26 LRs** | UltJB_API_LastRequest + LastRequests/* | **Not started** (NullServices stub) |
| **Days API + 22 days** | UltJB_API_Days + Days/* | **Not started** (NullServices stub) |
| Other SMPlugins extras (AFK manager, rotating messages, rules/help menus, spectate menu, radio spam block, ...) | Plugins/* | Not started — see FEATURE_GAPS.md (to be written) |

## Blockers
* No CS2 dedicated server available to the agent: Level 2/3 tests must be run by the owner.
* Account usage window limits session length; sub-agents are kept to a minimum on request.

## Next actions (priority order)
1. Last Request API + LRs (largest remaining core system).
2. Days API + days.
3. Review salvaged Warden/Cells/Logger/Mute/WeaponSelection code paths against specs.
4. Remaining SMPlugins extras; FEATURE_INVENTORY / FEATURE_GAPS / BUILD_AND_TEST / TEST_PLAN / CHANGELOG / RELEASE_NOTES.
5. Owner runs the in-game test plan; fix loop.
