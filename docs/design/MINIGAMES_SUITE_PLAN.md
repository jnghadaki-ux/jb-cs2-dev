# Minigames & game-mode suite: porting plan (session 3)

Status: **Plan only.** Nothing built. Source: the owner's SMPlugins-master upload (`Mods/`, `Plugins/`), checked file by
file. These plugins ran on other SWBS servers (minigames, deathrun, course, hide & seek, speedrun). The plan brings the
parts that make sense into SwbsJailbreak as **jailbreak games and days**, built on systems we already have (Zones, Days
API, menus, HUD, CheckTransmit, Crews), not as separate server types.

## What the repo still holds

| SMPlugins source | Size | What it is |
|---|---|---|
| `Mods/HideAndSeek/HideAndSeek.sp` | 424 lines | Hide & Seek mod: hiders get `hns_hide_time` (15 s); seekers' screens are faded black during it |
| `Mods/SpeedRuns/*` (14 files) | ~9,000 lines | Course timer: Core, Checkpoints, Teleport, Stats, Points, Ranks, Experience, Spectate, Scoreboard, MapTiers + zone types Timer_Start / Timer_End / Timer_EndStart + StartEndLines helper |
| `Plugins/BlockMaker/API_BlockMaker.sp` + `Plugins/Blocks/*` (30) | 2,452 + ~5,500 lines | Admin-placed blocks (`sm_bm`, `sm_badd`, `sm_bedit` ...). Types: Arrow, Boost, Bunnyhop, Camouflage, CautionTape, Chicken, Death, Freeze, Glass, GodMode, Healing, Ice, Ladder, Lava, Letters, Light, LowGravity, Magnet, NoFallDamage, Nuke, PlatformDisappearing, PortalDoor, Slap, Slime, Speed, TeamBarrier, Teleport, Train, Trampoline, Weapons |
| `Plugins/EntityPatches/MultigamesGameSelect` | 277 lines | Marks which map `func_button`s select a game on multigame maps; auto-selects after `sv_multigame_auto_select_time` (45 s) |
| `Plugins/MultigamesQueue` | 163 lines | Fair queue: everyone gets a turn to select the multigame |
| `Plugins/UserPointsForMinigamesWinner` | 112 lines | Points for the winner of the minigames stage |
| `Plugins/MinigamesAutoBhop` | 391 lines | Auto-bhop only on named bhop levels (`sm_setbhopname`, `sm_setbhopteam`, `sm_showteleportnames`) |
| `Plugins/DeathrunQueue` | 246 lines | Queue so everyone gets a turn as the trap-button presser |
| `Plugins/CourseAutoRespawn` | 388 lines | Auto respawn on course maps |
| `Plugins/HidePlayers` | 286 lines | `!hide`: hide other players |
| `Plugins/Jetpacks` | 141 lines | Jetpack flying |
| `Plugins/NoScopeRounds` | 158 lines | Chance of a no-scope-only round (we already have a No Scope warday + LR) |
| `Plugins/MovementStyles/*` (27) + API | ~4,000 lines | Bhop/surf styles (auto-strafe, sideways, TAS, FOV, parkour, rocket jump ...) |
| `Plugins/EntityPatches/*` (others) | n/a | CS:GO map entity fixes (FixTriggerPush, FixTeleportAngles, OpenAreaportals ...) |

## The suite: what to port, in dependency order

### Phase 1: Course timer zones ("Courses"), from SpeedRuns (Core / Checkpoints / Stats / Timer zone types)
* **What:** new zone types: **Course Start**, **Course End**, **Course Checkpoint**. They are drawn as start/end lines like
  `ZoneTypeHelper_StartEndLines`. A run starts when a player leaves Start and finishes when they enter End.
  * HUD timer, checkpoint splits, `!restart` (back to Start), best time per player per course per map.
  * Map records shown with `!records`.
* **Why for jailbreak:** warden races and obstacle courses become judged by the plugin. The first finisher or the best time is real, not argued. Results can feed Crew respect (optional, capped).
* **CS2:** Feasible. Uses Zones polling (16 Hz). For record precision, read the exact crossing tick from the blockade-style per-tick sweep.
* **Not ported:** Ranks, Experience, MapTiers, Spectate, Replays (no reliable bot-playback in CSS 1.0.368).
* **Effort:** M. **Acceptance:** an admin builds a course with `!zm`; a timed run shows HUD + splits; best times persist per map; an LR or game can ask "who finished first".

### Phase 2: Warden Games framework (MultigamesGameSelect + MultigamesQueue + UserPointsForMinigamesWinner)
* **What:**
  * A "Games" menu for the warden: pick a game, set who plays, the plugin runs and judges it, the winner(s) get a reward (freeday next round, first LR pick, or Crew respect).
  * **Map game buttons:** admins mark `func_button`s as game selectors with the cell editor's ping selection (same flow as doors). The plugin presses them for the chosen game, auto-selecting after 45 s like the original.
  * **Queue:** MultigamesQueue's fairness for who gets to pick.
* **Built-in judged games** (real engine data only):
  * Race (Phase 1 zones)
  * Last One Standing in a zone (Zones)
  * Simon Says (jump and crouch read from real buttons)
  * Red Light / Green Light (real velocity while "red")
  * Musical Zones (be inside a zone when the music stops)
* **CS2:** Feasible: buttons, velocity, zones, `AcceptInput("Press")` on `func_button` (verify the input name in CS2).
* **Effort:** M–L. **Acceptance:** the warden starts each game from the menu, the plugin names the winner correctly, and map game buttons fire.

### Phase 3: Blocks, from BlockMaker (a CS2-safe subset)
* **What:** admin-placed blocks saved per map, edited like zones (`!bm`). Each block is a **stock CS2 model** (`prop_dynamic`, base-game models only, no downloads) with a zone attached for its effect.
* **Port first:** Trampoline, Boost, Speed, LowGravity, Ice (low friction), Death, Lava (damage over time), Healing, NoFallDamage, Freeze, Slap, Teleport, PlatformDisappearing, Glass (breaks), TeamBarrier (guards or prisoners only), Bunnyhop (platform that drops when stood on).
* **Needs checking first:** a solid `prop_dynamic` with working collision in CS2 (verify the collision group and solid flags in 1.0.368). Without it, blocks are effect volumes with a visual model, like our blockade.
* **Skip:**
  * Camouflage and Chicken (player model changes: GSLT rule)
  * Letters (custom models)
  * Train (804 lines of path movers, revisit later)
  * Nuke and Weapons (balance)
* **Why:** wardens and admins can build obstacle courses and mini-arenas on any jail map. Pairs with Phases 1 and 2.
* **Effort:** L. **Acceptance:** blocks persist per map, effects apply on touch, nothing needs a client download.

### Phase 4: Days from the other mods
* **Hide & Seek day** (from `Mods/HideAndSeek`):
  * Guards seek, prisoners hide for `hide_time` (15 s).
  * The original fades seekers' screens black. CS2 substitute: seekers frozen, plus CheckTransmit hides every hider from seekers during the hide time, then reveals them. Check whether the CSS 1.0.368 `Fade` user message is usable first; if it is, use the original fade.
  * Check overlap with our existing hide days first.
  * **Effort:** S.
* **Deathrun day** (DeathrunQueue): one guard (taken in queue order) runs the map's trap buttons (marked like game buttons); prisoners run a course (Phase 1 zones). **Effort:** M.
* **Jetpack day** (Jetpacks): jetpack on +jump hold (velocity boost), for a fun warday or freeday. **Effort:** S.

### Phase 5: Small helpers
* **HidePlayers** (`!hide`): hide other players during courses and games (CheckTransmit; already have `Visibility`). S.
* **CourseAutoRespawn:** respawn at Course Start when dying during a course game. S.
* **MinigamesAutoBhop:** auto-bhop only inside "bhop" zones. CS2 `sv_autobunnyhopping` is server-wide; per-player needs `ReplicateConVar` (verify in 1.0.368), else enable only during the game. S.

### Not ported (with reason)
* **MovementStyles** (27): bhop/surf server styles (TAS, FOV, strafe limits). They need per-player movement code; not jailbreak.
* **SpeedRuns** Ranks / Experience / MapTiers / Replays: speedrun-server progression; replays have no reliable CSS API.
* **EntityPatches:** CS:GO map-entity fixes; CS2 maps differ. Revisit only if a CS2 jail map shows the same bug.
* **NoScopeRounds:** covered by our No Scope warday and LR.

## Order, models, usage
* Order: **1 → 2 → 4 (Hide & Seek) → 3 → rest.** Courses are the base for games, deathrun and blocks.
* Model fit (owner rule 10):
  * **Opus:** Phases 1–3 design (new systems, per-tick timing, collision checks).
  * **Sonnet:** Phase 4 days, Phase 5 helpers, individual block types once the Blocks framework exists.
* Each phase is one session-sized milestone: build 0/0, unit tests for the pure logic (timing, judging rules, block rules), TEST_PLAN rows, push.
