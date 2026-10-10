# Session handoff

_Updated: end of session 3 (2026-10-10), v0.10.0, branch `claude/swbs-jailbreak-cs2` (pushed)._

## State
* **.NET 8 only** now (owner rule): `net8.0` against CounterStrikeSharp.API 1.0.368. All net10 code removed.
  `dotnet build -c Release` → 0 warnings / 0 errors; `dotnet test -c Release` → 21/21 pass (net8.0);
  `./build.sh` → `artifacts/SwbsJailbreak-0.10.0-net8.zip` (CSS API not bundled).
* Aim on CSS 1.0.368 (no ray trace): `Core/Aim` stores each player's last `player_ping` (engine crosshair point +
  pinged entity). Warden ping → pointer laser + pulse ring; warden menu rings/healing at the recent ping; cell editor
  selects the pinged door. Line/ring gestures and paint use `bullet_impact` (as the original).
* All 26 LRs and 22 of 23 days are Implemented, unverified. RPG warday is **Blocked** (needs world collision).
* Nothing has been run on a live CS2 server yet.

## Exact next actions (priority order)
1. **If the owner sent server results** (TEST_PLAN.md §4 / console log): fix those failures first.
2. **Review pass for runtime bugs** — read-only review of LR (`Modules/LastRequest/*`), Days (`Modules/Days/*`),
   Jihad, Settings, Teams, Cells editor, Warden ping code. Acceptance: each finding fixed or documented; build 0/0;
   tests pass; push. (The last attempt died on a usage limit — keep it to ≤2 agents or do it inline.)
3. **RPG warday on CSS 1.0.368** — launcher = real gun with bullet damage blocked; rocket flies to the shot's
   `bullet_impact` point (engine trace); `weapon_accuracy_nospread 1` during the day (restore after). Acceptance: day
   registers, rockets explode at real impact points, build 0/0, FEATURE_INVENTORY row → Implemented, unverified.
4. Small server plugins (FEATURE_GAPS.md "Not ported yet"): UnlimitedReserveAmmo (500 reserve on reload),
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
