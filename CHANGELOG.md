# Changelog

## 0.10.0 — .NET 8 only (session 3, 2026-10-10) — not yet verified on a live server
### Added
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
