# Implementer brief — SWBS Ultimate Jailbreak (CS2 / CounterStrikeSharp)

You are implementing part of a CS2 port of hlstriker's SMPlugins "Ultimate Jailbreak" (Swoobles/SWBS CS:GO server)
as ONE CounterStrikeSharp plugin. CLAUDE.md (auto-loaded) contains the owner's Master Project Prompt — obey it:
stay true to the original SMPlugins design (same commands, rules, timings, menus, messages), rebuild everything on
real CS2/CSS mechanisms, never fabricate APIs/hit results/rendering, no text/ASCII stand-ins for in-world effects
(use real beams/particles/sounds/HUD), document every limitation honestly.

## Where things are
* Repository (your git worktree): the plugin is `src/SwbsJailbreak`. READ FIRST: `docs/DEVELOPER.md`, then skim
  `src/SwbsJailbreak/Core/*.cs` (Api.cs contracts + JbEvents, JbModule, JbPlayer, Players, Chat/Lang, Hud, Menu,
  PlayerExtensions, Effects (Fx/Sound), Damage, Weapons, Movement, Round, Perms, Storage, Targets, Config).
* Original SourcePawn (read-only): `/tmp/claude-0/ext/6038a3bf-SMPlugins-master/SMPlugins-master`
  (UltimateJailbreak: `Mods/UltimateJailbreak`, includes in `Mods/UltimateJailbreak/Includes`).
* CSS API decompiled — the plugin builds for BOTH, so every API you use must exist in both:
  - CSS 1.0.376 (net10.0 build): `/tmp/claude-0/css-api/decomp`
  - CSS 1.0.368 (net8.0 build):  `/tmp/claude-0/css368/decomp`
  The only known difference so far: `Trace`/`TraceOptions`/`Contents` exist only in 1.0.376 — wrap such code in
  `#if NET10_0_OR_GREATER` (or call `VectorUtil.AimPoint`, which already returns null on net8) and fall back to a
  REAL position (player origin, last bullet_impact) on net8.
* eGo Jailbreak decompile (working CS2 jailbreak; learn techniques only, never copy code/branding): `/tmp/claude-0/ego-decomp`
* Specs already written (if your task names one, it is your primary design input; verify against source when unsure):
  `/tmp/claude-0/specs/01-core-warden.md`, `/tmp/claude-0/specs/02-core-cells-logger-mute-jihad.md`

## Rules
1. Put your code under `src/SwbsJailbreak/Modules/<Area>/` (namespace `SwbsJailbreak.Modules.<Area>`) and your
   phrases in `src/SwbsJailbreak/lang/en/<area>.json`. Do NOT edit anything under `Core/` or other modules'
   folders. If you genuinely need a core change, implement a local helper in your module and list the request in
   your final report (the orchestrator will integrate it).
2. Modules are discovered automatically (subclass `JbModule`). Register cross-module services by assigning
   `Jb.<Service> = this;` in OnLoad, raise `JbEvents.Raise*` where the original raised forwards, and consume other
   subsystems ONLY through `Jb.*` / `JbEvents` (they may be null objects if that module is disabled).
3. Commands: `css_<original sm_ name without sm_>` so `!name` keeps working (e.g. sm_warden -> css_warden).
   Keep every original alias. Use `[RequiresPermissions]` or `Perms.Has(player, "<capability>")`.
4. All player-facing text in your lang file (Chat.Tell / Chat.TellAll / Lang.T). Centre text only via `Hud`.
   Menus only via `JbMenu`. Timers that must die with the round: `Round.Timer`. Clean up entities, timers,
   pickup/drop rules, Movement keys, Cvars overrides and HUD layers on round end / map end / unload.
5. Config: one section class per module via `Section<T>("<Area>")` with the original cvar defaults.
6. Never cache controllers/pawns across frames; use JbPlayer/slot and re-check validity in every callback/timer.
7. Build with `dotnet build src/SwbsJailbreak/SwbsJailbreak.csproj -c Release` — BOTH target frameworks must
   compile with 0 errors (fix warnings that indicate real bugs). Iterate until clean.
8. Write `docs/modules/<Area>.md`: for every original feature you covered — original file, behaviour summary,
   CS2 implementation, commands/permissions/config keys, status (Implemented-unverified / Partial / Unsupported +
   reason), and a numbered in-game test procedure. Nothing is "Verified" (no live server here).
9. Be economical with tokens: read the original files you need once, targeted; do not produce separate spec
   documents beyond docs/modules/<Area>.md.
10. When done: `git add -A && git commit -m "<Area>: <summary>"` in your worktree (end the message with the two
    trailer lines `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` and
    `Claude-Session: https://claude.ai/code/session_01XrjeEHcKxRnBgbrXcCZZP9`). Do not push.

## Final answer (keep it short)
* Branch name (`git rev-parse --abbrev-ref HEAD`) and commit hash.
* Bullet list of features implemented with status.
* Core change requests (exact proposed code), if any.
* Known risks / things needing in-game verification.
