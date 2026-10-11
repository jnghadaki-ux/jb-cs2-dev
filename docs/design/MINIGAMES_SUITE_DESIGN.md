# Minigames & game-mode suite: full design and implementation plan

Status: **Block A1–A4 (Warden Games) implemented, unverified** (session 3, owner: "finish all warden games"). Everything else is design only.
As built: rewards are None / Points / Crew respect (Freeday and First LR pick are not wired: the plugin has no next-round freeday or LR queue to hand them to);
Course Race waits for Courses (block B); map games auto-press is not used, the 45 s auto-select applies to the prisoner picker.
Scope summary and source inventory: `MINIGAMES_SUITE_PLAN.md`. Sources: hlstriker's SMPlugins and its contributors
(Hymns For Disco, FrostJacked, Scoutkllr). BlockMaker is excluded. Original command and cvar names below were read from
the sources.

---

## 1. Goals and constraints
* **Goals:**
  * Bring SMPlugins' course timer, multigame flow, Hide & Seek, Deathrun and helpers into SwbsJailbreak as **jailbreak games and days**.
  * Faithful to the originals: names, numbers, behaviour.
  * Judged by the plugin from real engine data.
* **Non-goals:**
  * Speedrun-server progression: ranks, XP, map tiers, replays.
  * Movement styles.
  * BlockMaker.
* **Engine constraints** (CSS 1.0.368, `net8.0`):
  * No ray trace. Aim comes from `player_ping` and `bullet_impact` only.
  * No touch hooks: positions are polled (Zones).
  * No custom models or downloads; nothing that touches skins or knives (GSLT).
* **Jailbreak rules always win:**
  * A game never overrides a running LR or day.
  * Rebels stay rebels.
  * Guards keep guard rules unless the game says otherwise.

## 2. Architecture

```
Core/
  Courses.cs        pure logic: CourseRun state machine, splits, record compare, time format  (unit-tested)
  WardenGames.cs    contracts: IWardenGame, GameContext, GameResult, GameReward, judging helpers (unit-tested)
  Api.cs            + Jb.Courses (ICourseService, NullCourseService), Jb.Games (IGameService, NullGameService)
Modules/Games/
  CoursesModule.cs            Order 3 (after Zones=2): course zone types, run tracking, checkpoints, records, HUD
  CoursesModule.Commands.cs   css_stop/cancel, css_cp/save, css_load/gocp, css_nextcp/prevcp, css_undo, css_cpmenu,
                              css_wr/pr/top/records, css_restart
  WardenGamesModule.cs        Order 15: games menu, lifecycle, rewards, picker queue, map game buttons
  Games/CourseRaceGame.cs, LastStandingGame.cs, SimonSaysGame.cs, RedLightGame.cs, MusicalZonesGame.cs, MapButtonGame.cs
  HideAndSeekDay.cs, DeathrunDay.cs, JetpackDay.cs     registered through the Days API (like CrewWarDay)
  GameHelpersModule.cs        HidePlayers (!hide), MeasureGap (!measure/!gap), AutoBhop zone type, scoreboard tag
lang/en/games.json            all phrases
```

Wiring follows the existing patterns (DEVELOPER.md, SESSION_HANDOFF "Patterns that worked"):
* **Modules:** `JbModule` subclasses with config sections.
* **Cross-module checks:** go in `OnLoad`, e.g. `if (Jb.Days is not DaysModule) return;`.
* **Services:** published on `Jb.*`, each with a null object so other modules never check for presence.
* **Zone types:** registered with `Jb.Zones.RegisterType(...)`, so the existing `!zm` editor (menus, typed fields, beams, per-map JSON) builds courses and game areas with no new editor.
* **Days:** `DayDefinition`, with `CanStart` for player-count gates.
* **Reused services:** damage rules through `DamageService.Register(name, priority, handler)`; visibility through `Visibility` / `Glow`; HUD through `Hud.Set(player, key, html, priority, seconds)`.

## 3. Courses (SpeedRuns Core + Checkpoints + Timer zone types + Stats)

### 3.1 Zone types (ZoneManager editor, per map)
| Key | Original | Fields | Behaviour |
|---|---|---|---|
| `course_start` | ZoneType_Timer_Start | string 1 = course name, int 1 = stage number (default 1) | Leaving it starts (or restarts) the stage timer. Standing inside resets the run |
| `course_end` | ZoneType_Timer_End | string 1 = course name | Entering it completes the run |
| `course_endstart` | ZoneType_Timer_EndStart | string 1 = course name, int 1 = stage number | Ends stage N and starts stage N+1 (chained stages, splits) |

* Lines are drawn like `ZoneTypeHelper_StartEndLines`: a beam line on the zone's floor edge, green for start, red for end. They are visible to everyone only while a course game runs, otherwise to editors only (Visibility).

### 3.2 Run state machine (`Core/Courses.cs`, no CSS types; unit-tested)
`Idle → InStart → Running(stage, stageStartTick) → Finished | Failed | Cancelled`

| Event | Effect |
|---|---|
| Leave `course_start` (stage 1) | Starts the total run and stage 1 (`SpeedRuns_OnStageStarted`) |
| Enter `course_endstart` (stage N) | Records the split, starts stage N+1 (`OnStageCompleted`) |
| Enter `course_end` | Finishes the run: total time and splits (`OnStageCompleted`, `OnNewRecord` when best) |
| Death, team change, noclip, teleport by anything except a checkpoint load, LR start, day start, `!stop` / `!cancel` | Fails or cancels the run (`OnStageFailed` / `OnRunStopped`) |
| Speed cap (`SpeedRuns_TryCapSpeed`) | Not ported: jailbreak uses stock movement |

* **Timing:** crossings are tested **every tick only for players in a run**, with the swept hull test already used by the Blockade zone (`ZoneBox.HullSweepHits`). Time = ticks × tick interval. Times are stored as milliseconds (int); the tick rate is read from the server.
* **Anti-abuse:** a run is invalid if the player teleported more than 64 u in one tick (same rule as the blockade), unless a checkpoint load caused it. Checkpoint loads mark the run as "CP" and are kept out of records unless `Courses.RecordsAllowCheckpoints` is on.

### 3.3 Checkpoints (SpeedRuns_Checkpoints)
* **Commands:** `css_save` / `css_cp` / `css_checkpoint` saves, `css_load` / `css_gocp` / `css_gocheck` loads, plus `css_nextcp`, `css_prevcp`, `css_undo` and `css_cpmenu` (all original names).
* **What is saved:** origin, angles and velocity. Up to `Courses.MaxCheckpoints` (10) per player.
* **Where it works:** only while a course game or course day runs and the player is in that course (`SpeedRunsCheckpoints_AreUsableDuringSpeedRun`). In normal jailbreak play the commands answer "only during course games", so there is no escape tool.
* **Not ported:** the original `sm_stuck` / `sm_unstuck`. Unstuck is a separate decision (FEATURE_GAPS), and `sm_menu` is too generic to claim; keep `css_cpmenu` only.

### 3.4 Records and stats (SpeedRuns_Stats, Hymns For Disco)
* **Storage:** `maps/course_records/<map>.json`, saved through `Storage.SaveMapDoc`:
  ```json
  { "courses": { "<course>": { "best": [ { "steam": "...", "name": "...", "ms": 0, "splits": [0], "date": 0, "runs": 0 } ] } } }
  ```
  Keep the top `Courses.RecordsPerCourse` (50, the original's `speedruns_stats_records_page_limit`) plus each player's best.
* **Commands:** `css_wr` (course record), `css_pr` (personal best), `css_top` / `css_records` / `css_record` (menu), `css_avg` / `css_average`, `css_stats`. Map-rank and page commands are folded into the menu.
* **HUD:** stage, time and the split delta against the player's best (green or red). Refreshed every 6 ticks (~10 Hz) for runners. `css_hud` toggles it, and `css_sound` / `css_sounds` toggle finish sounds (stock CS2 sounds only).

### 3.5 Service for other features
```csharp
public interface ICourseService {
  bool Available { get; }
  IReadOnlyList<string> Courses { get; }                 // course names on this map
  bool IsRunning(CCSPlayerController c);
  int  CurrentStage(CCSPlayerController c);
  void Cancel(CCSPlayerController c, string reason);
  void SetActive(string? course, Func<CCSPlayerController,bool>? who); // game/day scope: who may run, checkpoints allowed
  event Action<CCSPlayerController, string, int /*ms*/, bool /*record*/>? Finished;
  event Action<CCSPlayerController, string, string /*reason*/>? Failed;
}
```

## 4. Warden Games (MultigamesGameSelect + MultigamesQueue + UserPointsForMinigamesWinner)

### 4.1 Contracts (`Core/WardenGames.cs`)
```csharp
public interface IWardenGame {
  string Key { get; } string Name { get; } string Description { get; }
  GameNeeds Needs { get; }                       // e.g. a course, a named zone, min players
  bool CanStart(GameContext ctx, out string why);
  void Start(GameContext ctx);                   // announce rules, set state
  void Tick(GameContext ctx);                    // every tick while running (cheap)
  void OnPlayerOut(GameContext ctx, CCSPlayerController c, string reason);
  void End(GameContext ctx, GameEndReason why);  // cleanup: visuals, movement keys, cvars
}
public sealed class GameContext {                // owned by the module
  public IReadOnlyList<CCSPlayerController> Players; public CCSPlayerController Warden;
  public Zone? Area; public string? Course; public GameReward Reward; public double StartedAt;
  public void Eliminate(CCSPlayerController c, string reasonKey); public void Win(params CCSPlayerController[] winners);
}
public enum GameReward { None, Freeday, FirstLrPick, Points, CrewRespect }
```

### 4.2 Lifecycle (WardenGamesModule)
1. **Menu:** `css_games` (warden only) opens Games → pick a game → participants (all living prisoners / prisoners in the game area / chosen) → reward → Start.
2. **Gates:**
   * no day in progress;
   * no LR;
   * cells state irrelevant;
   * one game at a time;
   * `Games.MinPrisoners` (2);
   * the game's own `CanStart` (e.g. "no course on this map").
3. **While running:**
   * HUD rules banner;
   * participants glow in a neutral colour for guards (Glow `VisibleTo` guards), so guards know who is playing;
   * leaving the game area (when the game has one) eliminates.
4. **Ends** on: a winner, the warden ending it (menu), warden death or loss (auto-end, no reward), round end, a day start, or an LR start.
5. **Reward** (once per round, as UserPointsForMinigamesWinner's `g_bRoundHasWinner`):
   * Freeday: next round's personal freeday through the existing freeday API.
   * First LR pick: the winner is first in the LR queue if one exists, else a chat line only.
   * Points: the points module.
   * Crew respect: `Jb.Crews` grant, under the per-round cap and the minimum player count, reason "game win".
6. **Logging:** every start, end and winner goes to the server log (anti-abuse review).

### 4.3 Built-in games and exact judging
| Game | Origin | Rules | Engine data | Tie / edge rules |
|---|---|---|---|---|
| **Course Race** | SpeedRuns + mgwin | Participants teleported into `course_start` (or walk). First valid `Finished` event wins | Courses service | Same tick → lower total ms, then lower slot. No finisher by time limit → nobody wins |
| **Win Zone** (map minigames) | UserPointsForMinigamesWinner | First living participant to enter a Named zone whose name is `mgwin` wins (exact original data string) | Zones Entered event | One winner per round |
| **Last One Standing** | multigames | Inside the chosen zone; leaving it or dying eliminates; the last one wins | Zones IsInside each poll | Simultaneous last two out → both win (shared) |
| **Simon Says** | jailbreak classic, auto-judged | The warden issues orders from a menu (Jump, Crouch, Stand still, Look up/down, Simon / not-Simon). The plugin judges: obeying a "not Simon" order, or missing a Simon order within `Games.SimonReactSeconds` (2.5 s), eliminates | Real `Buttons` (IN_JUMP / IN_DUCK), velocity, eye pitch | Lag grace 0.3 s after the order's broadcast tick |
| **Red Light / Green Light** | jailbreak classic | Green = move toward the finish zone; Red (warden key or random 2–6 s) = horizontal speed above `Games.RedLightSpeed` (15 u/s) after 0.4 s grace eliminates; first into the finish zone wins | Pawn `AbsVelocity`, Zones | Grace covers stopping momentum |
| **Musical Zones** | jailbreak classic | Several small zones (Named `music_N`); music plays (stock sound); on stop, every player not inside a zone is out, and one zone is removed each round | Zones, sound events | More players than zones → the first to enter claims the spot (poll order by entry tick) |
| **Map Buttons** | MultigamesGameSelect | The warden picks a map game from the marked `func_button` list; the plugin presses it (`AcceptInput("Press")`, verify the input name); auto-press after `Games.AutoSelectSeconds` (45, `sv_multigame_auto_select_time`) when the picker idles | Entities | Buttons stored per map: `maps/game_buttons/<map>.json` `{ "buttons": [ { "name": "...", "hammerId": "...", "origin": [x,y,z], "label": "Soccer" } ] }`; marked with ping selection (cell-editor flow) |

### 4.4 Picker queue (MultigamesQueue)
* When the warden hands the choice to prisoners (`Games.LetPrisonersPick`), the first prisoner in a FIFO queue picks.
* **Queue rules:**
  * joining T adds you at the back;
  * picking moves you to the back;
  * leaving T removes you (original behaviour).
* Stored per session only.

## 5. Days

### 5.1 Hide & Seek (Mods/HideAndSeek, hlstriker)
* **Teams:** prisoners hide (original TEAM_HIDERS = T); guards seek (TEAM_SEEKERS = CT).
* **Loadout:** hiders get `weapon_knife_t` that **cannot attack** (original sets attack times to max) plus one flashbang; seekers get a knife.
* **Hide time:** `HideAndSeek.HideSeconds` (15, `hns_hide_time`). During it, seekers are frozen and blinded.
  * Use the `Fade` user message if CSS 1.0.368 can send it (verify; original FFADE_STAYOUT, then fade in over 3 s).
  * Fallback: seekers are frozen and CheckTransmit hides every hider from them, so they cannot see where anyone hides. HUD countdown either way.
* **Can't-attack hiders:** block hiders' outgoing damage in DamageService (priority above the day default) instead of attack-time props. Flashbangs still work.
* **Win:** seekers kill every hider before the round time → guards win; else prisoners win. Rebels are irrelevant during the day.
* **Flags:** StripPrisonersWeapons, StripGuardsWeapons, KillWorldWeapons, DisableWeaponBuying. Compare with our existing hide-type days first; if one already matches, add the original's settings to it instead of a duplicate.

### 5.2 Deathrun (DeathrunQueue + Courses)
* **Requires:** a course on the map and marked trap buttons (same storage as Map Buttons, tagged `trap`).
* **Setup:** one guard is the activator, chosen from a **FIFO activator queue** (DeathrunQueue: everyone gets a turn; joining CT adds you, activating moves you to the back). The other guards are referees (no damage dealt or taken).
* **Run:** prisoners run the course with `Courses.SetActive`; finishers survive and the first finisher wins (reward as games). The activator presses traps in the world as normal (map buttons work by `+use`); `Deathrun.TrapCooldown` per button stops spam (button lock through its own input, verify).
* **Win:** any prisoner finishes → prisoners win; all prisoners dead → guards win.

### 5.3 Jetpack day (Jetpacks, hlstriker)
* **Control:** hold jump in the air → upward velocity +`Jetpack.Thrust` (original applies velocity on a fake jump button), capped at `Jetpack.MaxUp`. Fuel `Jetpack.FuelSeconds` regenerates on the ground.
* **Sound:** the original's custom mp3 is not allowed (no downloads), so use a stock CS2 sound, or none.
* **Modes:** a freeday or a warday variant (flags as the existing days).

### 5.4 No-scope chance (NoScopeRounds)
* `Days.NoScopeChance` (0.0, `noscope_chance`): chance that a normal round's sniper rifles are no-scope only (reuse the existing No Scope warday's scope block). Off by default.

## 6. Helpers
| Helper | Origin | Design |
|---|---|---|
| `css_hide` | HidePlayers | Toggle: hide other players from yourself (Visibility predicate per viewer). Only allowed during course games/days or for dead players (never gives an advantage in normal play) |
| Course auto-respawn | CourseAutoRespawn (`sv_course_respawn_enabled`) | While a course game or Deathrun runs with `Courses.AutoRespawn`, a player dying on the course respawns at `course_start` after 1 s (`Round.Timer`); off otherwise |
| AutoBhop zone | MinigamesAutoBhop | Zone type `auto_bhop`: inside it, auto-bhop on. Prefer per-player `ReplicateConVar("sv_autobunnyhopping")` plus the server cvar, if 1.0.368 supports it; else set `sv_autobunnyhopping` and `sv_enablebunnyhopping` only while a game using such a zone runs (Cvars.SetTemp, restored on end) |
| `css_measure` / `css_gap` | MeasureGap (Hymns For Disco) | Admin (`zones` capability): ping point A, ping point B → beam between them plus chat: total, horizontal and vertical distance. For building courses and checking jump gaps |
| Course scoreboard tag | SpeedRuns_Scoreboard (Scoutkllr & Hymns For Disco) | During course games only: the clan tag shows the best time (e.g. `1:23.4`); restored after (Crews owns the tag otherwise; same restore rules as Crews) |

## 7. Config (new sections, versioned like the others)
```jsonc
"Courses":    { "Enabled": true, "MaxCheckpoints": 10, "RecordsPerCourse": 50, "RecordsAllowCheckpoints": false,
                "HudEveryTicks": 6, "AutoRespawn": true, "LinesForEveryone": "games" },
"Games":      { "Enabled": true, "MinPrisoners": 2, "TimeLimitSeconds": 180, "AutoSelectSeconds": 45,
                "LetPrisonersPick": true, "SimonReactSeconds": 2.5, "RedLightSpeed": 15, "Rewards": ["Freeday","FirstLrPick","Points","CrewRespect"] },
"HideAndSeek":{ "Enabled": true, "HideSeconds": 15, "HiderFlashbangs": 1 },
"Deathrun":   { "Enabled": true, "TrapCooldown": 5 },
"Jetpack":    { "Enabled": true, "Thrust": 22, "MaxUp": 350, "FuelSeconds": 4 },
"Days":       { "...": "...", "NoScopeChance": 0.0 }
```

## 8. Permissions (Perms map additions)
* `games.start`: warden only (not an admin flag).
* `games.admin` = `@css/generic`: force-end a game, mark map buttons.
* `courses.admin` = `@css/config`: delete records.
* MeasureGap uses the existing `zones` capability.
* All arguments validated: course names from the map's list only; button indexes from the stored list; menus only, no free-form targets.

## 9. Interaction rules (jailbreak first)
* **LR or day start:** cancels any game and any course run; checkpoints off.
* **Rebels:** a prisoner who rebels during a game is eliminated from it and stays a rebel.
* **Guards:** game participants are prisoners only (except Deathrun's activator). Guards may still kill rebels; killing a non-rebel participant is a normal freekill.
* **Crews:** rewards respect caps and the minimum player count. Crew War day blocks games.
* **Reconnects:** a reconnecting player is never mid-run; state is keyed by slot and cleared on disconnect and map change.

## 10. CS2 / CSS 1.0.368 checks before coding (grep the decompiled API; record results in COMPATIBILITY.md)
1. `Fade` user message (Hide & Seek blindness). If missing, use the CheckTransmit fallback.
2. `CCSPlayerController.ReplicateConVar` (per-player auto-bhop).
3. `func_button` input names in CS2 (`Press`, `Lock`, `Unlock`) through `AcceptInput`.
4. Server tick interval (`Server.TickInterval` or equivalent) for millisecond times.
5. `Buttons` flags for IN_JUMP / IN_DUCK on `CCSPlayerController.Buttons`; already used by the zone editor for attack.
6. Stock sound event names for game cues (music loop for Musical Zones, finish/record sounds).

## 11. Implementation plan (each step = one milestone: build 0/0, tests, TEST_PLAN rows, docs, push)

Owner order: **Warden Games → Days → Helpers first, then Courses, then everything that needs Courses.**
Items that depend on Courses (Course Race, Deathrun, course auto-respawn, course scoreboard tag) wait for block B.

| Step | Work | Files | Tests | Acceptance | Model |
|---|---|---|---|---|---|
| 0 | API checks (§10) | COMPATIBILITY.md | n/a | Each item marked available or fallback | Sonnet |
| **A. Warden Games, Days, Helpers (plan phases 2–4)** ||||||
| A1 | Games framework + menu + rewards + picker queue | `Core/WardenGames.cs`, `Core/Api.cs` (Jb.Games), `WardenGamesModule.cs` | Reward once per round, queue order, gate rules | The warden can start or end a no-op test game; gates enforced | Opus |
| A2 | Games: Win Zone (`mgwin`), Last One Standing | `Games/*.cs` | Tie rules | Correct winner each game | Sonnet |
| A3 | Games: Simon Says, Red Light / Green Light, Musical Zones | `Games/*.cs` | Judging rules as pure functions (button/velocity sample → verdict) | Auto-judging matches the rules table | Opus |
| A4 | Map Buttons (mark, store, press, auto-select) | `WardenGamesModule`, `maps/game_buttons` | JSON round-trip | Marked buttons fire on a multigame map | Sonnet |
| A5 | Hide & Seek day | `HideAndSeekDay.cs` | n/a | Hide time, blindness or fallback, can't-attack hiders, win rules | Sonnet |
| A6 | Jetpack day, No-scope chance | `JetpackDay.cs`, DaysModule config | Thrust/fuel math | Day playable; chance 0 = never | Sonnet |
| A7 | Helpers that don't need Courses: `!hide`, AutoBhop zone, MeasureGap | `GameHelpersModule.cs` | Distance math | Each helper limited to its scope (`!hide`: games/days or dead players) | Sonnet |
| **B. Courses (plan phase 1)** ||||||
| B1 | Course core logic | `Core/Courses.cs`, `Core/Api.cs` (Jb.Courses) | State machine (start/split/end/fail/cancel), ms from ticks, record ordering, CP-run exclusion, teleport rule | ≥20 unit tests pass | Opus |
| B2 | Course zone types + run tracking + HUD | `Modules/Games/CoursesModule.cs`, lang | Zone field rules | Course built with `!zm`, timed runs with splits on a test map | Opus |
| B3 | Checkpoints + records + commands | `CoursesModule.Commands.cs` | Record JSON round-trip, top-N trim | Commands work only in course scope; records persist across map change | Sonnet |
| **C. Everything that needs Courses** ||||||
| C1 | Course Race game | `Games/CourseRaceGame.cs` | Tie rule (same tick → lower ms, then slot) | First valid finisher wins | Sonnet |
| C2 | Deathrun day + activator queue | `DeathrunDay.cs` | Queue order | Activator chosen fairly; finish/death win rules | Sonnet |
| C3 | Course auto-respawn, course scoreboard tag | `CoursesModule`, `GameHelpersModule` | n/a | Only during course games/days; tags restored | Sonnet |
| **D. Wrap-up** ||||||
| D1 | Review pass (≤2 agents) + docs | FEATURE_INVENTORY, CHANGELOG, TEST_PLAN, BUILD_AND_TEST | n/a | Findings fixed; inventory rows "Implemented, unverified" | Opus |

* **Rough size:** block A ≈ 2,500 lines, B ≈ 1,800, C ≈ 600; ~60 new unit tests.
* **Order:** 0 → A1 → A2 → A3 → A4 → A5 → A6 → A7 → B1 → B2 → B3 → C1 → C2 → C3 → D1. A5–A7 can run in any order after A1. While Courses don't exist, the Games menu hides course games (`CanStart` returns "no course system").
* **Credits** (code headers and docs):
  * "Based on SMPlugins by hlstriker";
  * SpeedRuns Stats: Hymns For Disco;
  * SpeedRuns Scoreboard: Scoutkllr & Hymns For Disco;
  * MeasureGap: Hymns For Disco.
