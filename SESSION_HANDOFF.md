# Session handoff

## Where things are
* Branch `claude/swbs-jailbreak-cs2` (pushed). WIP agent branches `wip/warden` and `wip/cells-logger-mute-jihad` are
  already merged into the main branch by copy (keep only as history).
* Specs for implemented systems live in `docs/specs/` (01 core/warden/settings/teams/points/models, 02
  cells/logger/mute/voice/jihad/weapon selection, implementation conventions, raw SMPlugins inventory).
* Original SMPlugins sources are NOT in the repo; re-extract the owner's `SMPlugins-master` upload (Mods/UltimateJailbreak/*)
  when porting LRs and Days. Session-1/2 copies were under `/tmp/claude-0/ext/6038a3bf-SMPlugins-master/`.
* CSS API reference: decompile the NuGet packages (1.0.368 net8, 1.0.376 net10) if `/tmp/claude-0/css-api` is gone.

## Conventions (see docs/DEVELOPER.md)
* One `JbModule` per feature, auto-discovered; config section via `Section<T>("Name")`; phrases in `lang/en/<module>.json`.
* Cross-module calls only through `Jb.*` service contracts (`Core/Api.cs`) and `JbEvents`.
* Round-scoped timers: `Round.Timer(...)`. Per-player state: `player.Data<T>()` (`IRoundScoped` resets each round).
* Ray trace only under `#if NET10_0_OR_GREATER`; the net8 path must use real positions, never invented hit points.
* After every module: build both TFMs, run tests, commit, push.

## Immediate next step
1. Read the owner's test results (TEST_PLAN.md §4) and fix failures first.
3. Remaining small server plugins (FEATURE_GAPS.md "Not ported yet").
4. Review pass over salvaged modules (Warden/Cells/Logger/Mute/WeaponSelection) against docs/specs.

## Patterns that worked
* Module `OnLoad`: `if (Jb.Days is not DaysModule) return;` (never in `Enabled` - evaluated before modules load).
* Kill credited to another player: `DamageService.KillWithCredit(victim, attacker, weapon)`.
* Touch detection: poll positions in `OnTick` (no SDKHooks touch in CSS).
* Ray traces only under `#if NET10_0_OR_GREATER`; features that truly need them are disabled on net8 (RPG day).
