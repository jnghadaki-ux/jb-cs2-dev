# Session handoff

_Updated: session 3 (2026-10-11), v0.10.0, branch `claude/swbs-jailbreak-cs2` (pushed)._

## State
* **.NET 8 only** now (owner rule): `net8.0` against CounterStrikeSharp.API 1.0.368. All net10 code removed.
  `dotnet build -c Release` → 0 warnings / 0 errors; `dotnet test -c Release` → 139/139 pass (net8.0);
  `./build.sh` → `artifacts/SwbsJailbreak-0.10.0-net8.zip` (CSS API not bundled).
* Aim on CSS 1.0.368 (no ray trace): `Core/Aim` stores each player's last `player_ping` (engine crosshair point +
  pinged entity). Warden ping → pointer laser + pulse ring; warden menu rings/healing at the recent ping; cell editor
  selects the pinged door. Line/ring gestures and paint use `bullet_impact` (as the original).
* All 25 LRs and 22 of 23 days are Implemented, unverified. RPG warday is **Blocked** (needs world collision).
* Nothing has been run on a live CS2 server yet.
* Owner usage report (session 3): 5-hour window 44 %, weekly 60 % — below the stop thresholds.

## Owner decisions (session 3, latest)
* `!open` is admin-only (done). Auto warden when nobody queues (done). Warden bonus only with 1–2 guards (done, stacking by step).
* Extra days: later. **Remake the SMPlugins Zone Manager** (plan: docs/design/ZONE_MANAGER_PLAN.md) — recommended model: Opus.
* Gang system: new "Crews" design proposed (docs/design/CREWS.md).
* **Zone Manager and Crews were built in this session** (owner switched to Opus here instead of separate sessions):
  `Modules/Zones/*` (remake of SMPlugins ZoneManager + ZoneTypes, guide in docs/design/ZONE_MANAGER_PLAN.md) and
  `Modules/Crews/*` + Crew War day (docs/design/CREWS.md). Also fixed: phrases mixing colour tags and arguments printed raw `{0}`.

* **Zones + Crews review done** (2 bounded agents, 22 findings, all fixed — CHANGELOG 0.10.0 "Review of Zones + Crews";
  TEST_PLAN 27r). Build 0/0, tests 139/139.

* CS:GO/CS:S feature research (report only): `docs/research/JB_FEATURE_RESEARCH.md`. Owner usage: weekly 76 %.

* Minigames / game-mode suite plan (SMPlugins HideAndSeek, SpeedRuns, BlockMaker, multigame helpers): `docs/design/MINIGAMES_SUITE_PLAN.md` + full design/implementation steps `docs/design/MINIGAMES_SUITE_DESIGN.md` (design only — owner said do not implement yet).

## Exact next actions (priority order)
1. **If the owner sent server results** (TEST_PLAN.md §4 / console log): fix those failures first.
2. **Review pass for runtime bugs** (Zones/Crews done) — read-only review of LR (`Modules/LastRequest/*`), Days (`Modules/Days/*`),
   Jihad, Settings, Teams, Cells editor, Warden ping code. Acceptance: each finding fixed or documented; build 0/0;
   tests pass; push. (The last attempt died on a usage limit — keep it to ≤2 agents or do it inline.)
3. **RPG warday on CSS 1.0.368** — launcher = real gun with bullet damage blocked; rocket flies to the shot's
   `bullet_impact` point (engine trace); `weapon_accuracy_nospread 1` during the day (restore after). Acceptance: day
   registers, rockets explode at real impact points, build 0/0, FEATURE_INVENTORY row → Implemented, unverified.
4. Small server plugins (FEATURE_GAPS.md "Not ported yet"; CommandList is done): UnlimitedReserveAmmo (500 reserve on reload),
   DisableFallDrownDamage (block fall/drown damage + sounds), FixGravityOnNewRound, MapArmour, Parachute, then AFK,
   rotating messages, rules/help menus, spectate menu, radio/text spam.
5. Before "finished": validate admin + JB commands vs CS2-SimpleAdmin / eGo / destoer JB; research CS:GO/CS:S JB
   plugins for missing feasible features (owner request) — report, do not implement unasked.

## Where things are
* Specs: `docs/specs/`. Conventions: `docs/DEVELOPER.md`. Master prompt v2 + owner rules: `CLAUDE.md`.
* Original SMPlugins sources and the decompiled CSS API are NOT in the repo (`/tmp/claude-0/...` in this container only).
  Re-extract the owner's `SMPlugins-master` upload / decompile the 1.0.368 NuGet package if needed.

## Patterns that worked
* Module `OnLoad`: `if (Jb.Days is not DaysModule) return;` (never in `Enabled` — evaluated before modules load).
* Kill credited to another player: `DamageService.KillWithCredit(victim, attacker, weapon)`.
* Touch detection: poll positions in `OnTick` (no SDKHooks touch in CSS).
* Aim: `Aim.TryGetPing(c, maxAge, out pos, out entity)`; subscribe `Aim.Pinged` in OnLoad, unsubscribe in OnUnload.
