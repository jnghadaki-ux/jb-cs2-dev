# Changelog

## 0.10.0 — .NET 8 only (session 3, 2026-10-10) — not yet verified on a live server
### Added
- **Warden Games** (`!games`, `!gamebuttons`; Games module): Win Zone, Last One Standing, Simon Says, Red Light / Green Light,
  Musical Zones and map games, auto-judged from real input; picker queue; per-round reward; 23 new unit tests. Based on
  SMPlugins MultigamesGameSelect / MultigamesQueue / UserPointsForMinigamesWinner (hlstriker).
- **Hide and Seek** day, **Jetpack** day, random **no-scope rounds** (`GameDays.NoScopeChance`, off by default), `!hide`,
  **Auto Bhop** zone type and `!measure` / `!gap` (SMPlugins HideAndSeek, Jetpacks, NoScopeRounds, HidePlayers,
  MinigamesAutoBhop by hlstriker; MeasureGap by Hymns For Disco); 7 new unit tests.
- **Zone Manager** remake of SMPlugins ZoneManager + ZoneTypes (`!zm`, 12 types, per-map JSON, LR/CTF use named destinations).
- **Crews** prisoner gang system + **Crew War** warday (docs/design/CREWS.md); Days API `CanStart`.
- Core `ChatInput` (typed chat prompts) and `Visibility` (per-player entity visibility).
### Fixed
- Review of Warden Games / game days (12 findings, all fixed): Hide and Seek loadout was stripped by the day start (now
  given the frame after); a mass failure no longer makes everyone a winner (shared win only for the last two); stale menus
  cannot control a game after the warden changes; quick Simon taps count; Red Light opens with a 3 s stop window; Last One
  Standing is played by the prisoners in the arena; `!declare winner` is warden-only and needs a still-playing player;
  `!hide` also hides weapons and builds its list once per tick; no-scope rounds restore scopes; menus refresh only when on screen.
- Review of Last Requests / Days (9 findings, all fixed): a day forced during the round-end delay or kept through
  `mp_restartgame` no longer carries into the next round (ends on round start and on cs_pre_restart / cs_match_end_restart;
  days only start during a round); an admin-ended CTF no longer decides the next round's winner; Throwing Knives respects
  LR freedays; ending an LR on disconnect or "abort all" no longer restarts LR selection; Weapon Battles / Contests no
  longer stall when Effects or WeaponSelection is disabled; Phat Kid works for several rebels at once; LR / Draw / Ghost /
  Phat Kid state no longer leaks into the next map's warmup; Freeze Tag slays outside the damage hook; Fat Kid speed is cleared.
- `!commands` treated subcommand names (`case "kick":`) as permission gates (hid `!crew`); `!games` is listed for wardens.
### Added (polish)
- Warden menu items "Warden Games" and "Crew shakedown" (`WardenMenu.ShowGamesItem` / `ShowShakedownItem`).
- Review of Zones + Crews (22 findings, all fixed; none could crash the server):
  - Zones: records with a missing/duplicate id or above `MaxZones` are renumbered instead of dropped (the next save erased
    them); unreadable map files are kept as `.corrupt-*` before a fresh start; a bad `BlockadeColor` value no longer
    disables every blockade; the damage blocker checks the victim's position when the damage happens (fast falls, flag
    edits, no stale protection across maps); per-map state resets at map load (respawn spawn cache, timers, notify
    cooldowns, autosave); ids are never reused while editors hold them; opening a zone menu drops a pending chat prompt;
    "move to feet / ping" no longer sinks the zone; unloaded type keys survive duplicate and map import; import-map name
    validated as a file name.
  - Crews: stale menus/prompts can no longer re-save a disbanded crew, leave a player in two crews or make a departed member
    Boss; Crew War factions are fixed when the day starts and a disconnect/team change runs the winner check; crew chat,
    Lookout, crew pings and Roll Call stay among prisoners (no guard↔prisoner back channel); turf needs `MinPlayersForRespect`
    humans; MOTD braces sanitized; SteamID 0 rejected; heir skips CT-banned members; old Boss respects the Lieutenant cap;
    ping/invite timers use engine time; ping/Lookout visibility uses a slot cache (no per-tick SteamID strings).
- Phrases mixing colour tags and arguments (133) were shown with a raw `{0}`: `Lang.T` now escapes colour tags before formatting.
- `!open` / `!o` (admin only, capability `cells.open` = @css/generic, cooldown `Cells.AdminOpenCooldown`); `!cdo` stays root-only.
- Auto warden setting (`Warden.AutoWarden`, `AutoWardenMode`): a guard is picked automatically when nobody queued, with its own chat line.
- Warden bonus only with few guards (`Warden.Bonus*`): +25 HP / +25 armor per step, stacking as guards die when ≤ 2 are alive, taken back when the warden ends; 12 new unit tests.
- Design docs: docs/design/ZONE_MANAGER_PLAN.md, docs/design/CREWS.md.
- `!commands` (CommandList, `sm_commands`): menu of the commands the player may use, `!commands console` prints the full list to the console. Gates come from [RequiresPermissions] and an IL scan of each command (incl. same-class helpers); 14 new unit tests.
### Changed
- Target `net8.0` only, against CounterStrikeSharp.API 1.0.368 (owner directive); the net10 build, its package and all
  `NET10_0_OR_GREATER` trace code were removed. `build.sh`/`build.ps1` produce one `SwbsJailbreak-<ver>-net8.zip`.
- Aimed features use real engine aim data through the new `Core/Aim` (player_ping): warden ping → pointer laser + pulse ring
  (`Warden.PingPointerTime`), menu rings/healing area at the recent ping (`Warden.PingMaxAge`), cell editor selects the
  pinged door.
- Credits: "Gdaki (enhanced with Claude Code) - inspired by HlStriker, SWBS development/lead admins".
### Known
- RPG warday is Blocked on CSS 1.0.368 (no world collision source); not registered, logged at load.

## 0.9.0 — first complete test build (session 2, 2026-10-10) — not yet verified on a live server
### Added
- Last Request API and all 25 original LRs (Freeday ×4, Draw, Rebel ×8, Weapon Battle ×5, Contest ×7).
- Days API and all 23 original days.
- Settings (spawn loadout, stable spawns, late-join respawn, cell weapon nerf, guard suicide wipe, round-end banner).
- Teams (guard queue, pre-queue, 2:1 ratio, jointeam rules) and persistent CT bans.
- PlayerModels, Points, Jihad.
- Salvaged and completed Warden, Warden Menu, Last Guard, Cells (+ in-game editor), Logger, Mute/VoiceChat, Weapon Selection.
- ServerTweaks (noblock, grenade noblock, admin tags, clan tag filter, spam message block, kill feed toggle).
- Core: per-player auto bhop, kill-with-credit helper, pickup item identification for weapon rules.
- Docs: FEATURE_INVENTORY, FEATURE_GAPS, BUILD_AND_TEST, TEST_PLAN, PROJECT_STATUS, SESSION_HANDOFF, specs cache.
### Changed
- Cell door commands default to `@css/root` (original ADMFLAG_ROOT); `!daysedit` to `@css/unban` (original ADMFLAG_UNBAN).
- Version 1.0.0 → 0.9.0 (test build).

## 0.1 – 0.5 (session 1)
- Project skeleton (net8.0 + net10.0), core framework, Admin, Commands, Effects, BhopCheck, unit tests.
