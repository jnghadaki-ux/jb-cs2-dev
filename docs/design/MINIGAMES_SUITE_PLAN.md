# Minigames & game-mode suite: porting plan (session 3, rev. 2)

Status: **Plan only.** Nothing built. Full design and step-by-step implementation plan: `MINIGAMES_SUITE_DESIGN.md`. Scope (owner): plugins written by **hlstriker and the contributors to his
SMPlugins repo**, from the owner's SMPlugins-master upload. The author fields were checked in every file. The parts that
make sense become SwbsJailbreak **games and days** on our existing systems (Zones, Days API, menus, HUD, CheckTransmit,
Crews), not separate server types. **BlockMaker and Blocks are excluded** (owner): nothing ported depends on them.

## Source inventory (author from each plugin's header)

| Source | Author | Size | What it is |
|---|---|---|---|
| `Mods/HideAndSeek/HideAndSeek.sp` | hlstriker | 424 | Hide & Seek: hiders get `hns_hide_time` (15 s), seekers' screens faded black meanwhile |
| `Mods/SpeedRuns/SpeedRuns_Core.sp` | hlstriker | 2,299 | Course timer core |
| `SpeedRuns_Checkpoints.sp`, `_Teleport.sp` | hlstriker | 1,171 / 1,545 | Checkpoints (save/teleport back), course teleports |
| `ZoneTypes/ZoneType_Timer_Start / _End / _EndStart`, `ZoneTypeHelper_StartEndLines` | hlstriker | 348 / 200 / 396 / 961 | Timer zone types drawn as start/end lines |
| `SpeedRuns_Points.sp`, `_Experience.sp`, `_Spectate.sp`, `_MapTiersForMapVotes.sp` | hlstriker | 176 / 387 / 297 / 236 | Points, XP, spectate runners, map tiers |
| `SpeedRuns_Stats.sp` | **Hymns For Disco** | 463 | Run statistics |
| `SpeedRuns_Ranks.sp` | **FrostJacked** | 547 | Rank titles |
| `SpeedRuns_Scoreboard.sp` | **Scoutkllr & Hymns For Disco** | 88 | Timer info on the scoreboard |
| `Plugins/EntityPatches/MultigamesGameSelect` | hlstriker | 277 | Marks map `func_button`s that select a game; auto-select after 45 s |
| `Plugins/MultigamesQueue` | hlstriker | 163 | Fair queue for who selects the multigame |
| `Plugins/UserPointsForMinigamesWinner` | hlstriker | 112 | Points for the minigames winner |
| `Plugins/MinigamesAutoBhop` | hlstriker | 391 | Auto-bhop only on named bhop levels |
| `Plugins/DeathrunQueue` | hlstriker | 246 | Queue for the trap-button presser |
| `Plugins/CourseAutoRespawn` | hlstriker | 388 | Auto respawn on course maps |
| `Plugins/HidePlayers` | hlstriker | 286 | `!hide` other players |
| `Plugins/Jetpacks` | hlstriker | 141 | Jetpack flying |
| `Plugins/NoScopeRounds` | hlstriker | 158 | Chance of a no-scope-only round |
| `Plugins/MeasureGap` | **Hymns For Disco** | 215 | Measures the distance between 2 points via a menu (course/map building tool) |
| `Plugins/EntityPatches/*` (12 others) | hlstriker | n/a | CS:GO map-entity fixes |

Already handled elsewhere:
* ThrowingKnife and CustomWeapons (hlstriker): superseded (FEATURE_INVENTORY).
* Heal, Teamswitch, TimedMuteGag, Noclip, DisableKillFeed: ported.
* MovementStyles: bhop-server styles, not jailbreak.

## The suite, in dependency order

### Phase 1: Courses (SpeedRuns Core + Checkpoints + Timer zone types + Stats)
* **Zone types:** Course Start, Course End, Course End/Start (chained stages), drawn as lines like
  `ZoneTypeHelper_StartEndLines`. A run starts on leaving Start and finishes on entering End.
* **Run features:** HUD timer, stage splits, `!restart`, checkpoints (`!cp` save / `!tp` back, only during course games so they can't be abused in normal play).
* **Records:** best time per player per course per map, `!records`. Hymns For Disco's Stats provides the per-run stats.
* **Jailbreak use:** warden races and obstacle courses judged by the plugin. The result can reward the winner: freeday, first LR pick, or capped Crew respect.
* **CS2:** feasible (Zones polling; exact finish tick from a per-tick sweep like the blockade). **Effort:** M.
* **Acceptance:** an admin builds a course with `!zm`; a timed run shows HUD + splits; best times persist per map; "first finisher" is available to games.

### Phase 2: Warden Games (MultigamesGameSelect + MultigamesQueue + UserPointsForMinigamesWinner)
* **Game selection:** a warden "Games" menu; the plugin runs the game and names the winner; the reward goes to the winner (UserPointsForMinigamesWinner → our points / Crew respect).
* **Map game buttons:** admins mark `func_button`s as game selectors with ping selection (same flow as the cell editor's doors). The plugin presses the chosen one, auto-selecting after 45 s as in the original. Verify the CS2 `func_button` input name.
* **Queue:** MultigamesQueue fairness for who picks when the warden hands the choice to prisoners.
* **Judged games** (real engine data only):
  * Course race (Phase 1)
  * Last One Standing in a zone
  * Simon Says (real jump and crouch buttons)
  * Red Light / Green Light (real velocity)
  * Musical Zones
* **Effort:** M–L. **Acceptance:** each game starts from the menu, the right winner is named, marked map buttons fire.

### Phase 3: Days and modes
* **Hide & Seek day** (`Mods/HideAndSeek`, hlstriker):
  * Prisoners hide for 15 s while guards are held.
  * Seekers' black screen: use the CS2 `Fade` user message if CSS 1.0.368 can send it (verify). Otherwise freeze the seekers and hide every hider from them through CheckTransmit until the hide time ends.
  * Compare with our existing hide-type days first. **Effort:** S.
* **Deathrun day** (DeathrunQueue): one guard (queue order) operates the map's trap buttons (marked like game buttons); prisoners run a Phase 1 course to the end. **Effort:** M.
* **Jetpack day** (Jetpacks): hold jump to fly (velocity boost), as a fun warday or freeday. **Effort:** S.
* **No-scope rounds** (NoScopeRounds): only as a Days option ("random no-scope chance"), since the No Scope warday and LR already exist. **Effort:** S.

### Phase 4: Helpers
* **HidePlayers** (`!hide`, CheckTransmit through our `Visibility`): during courses and games. S.
* **CourseAutoRespawn:** respawn at Course Start while a course game runs. S.
* **MinigamesAutoBhop:** auto-bhop only inside "bhop" zones. Needs per-player `ReplicateConVar` for `sv_autobunnyhopping` (verify in 1.0.368); else server-wide only during the game. S.
* **MeasureGap** (Hymns For Disco): admin menu to measure the distance between two points (two pings), for building courses and checking jump gaps. S.
* **SpeedRuns Scoreboard** (Scoutkllr & Hymns For Disco): best course time in the scoreboard clan tag during games. Crews uses the clan tag for prisoners, so do this only during course games. S.

### Not ported (reason)
* **SpeedRuns Ranks** (FrostJacked), Experience, MapTiers, Spectate: speedrun-server progression, outside jailbreak; Spectate overlaps the planned spectate menu.
* **SpeedRuns Teleport:** course teleport destinations already exist as Teleport / Teleport Destination zones.
* **Replays:** no reliable bot-playback API in CSS 1.0.368.
* **EntityPatches** (12, hlstriker): CS:GO map-entity fixes. Revisit one only if a CS2 jail map shows the same bug (e.g. FixTriggerPush, OpenAreaportals).
* **BlockMaker / Blocks:** excluded by the owner.

## Order and credits
* Order (owner): **2 → 3 → 4, then 1, then the parts that need Courses** (Course Race, Deathrun, course auto-respawn, course scoreboard tag). Step list: `MINIGAMES_SUITE_DESIGN.md` §11.
* Weight: Phases 1–2 are new systems (per-tick timing, judging): heavy work. Phases 3–4 are lighter.
* Credits in code and docs: "based on SMPlugins by hlstriker", and Hymns For Disco / FrostJacked / Scoutkllr where their plugin is the source.
* Each phase = one milestone: build 0/0, unit tests for pure logic (timing, splits, judging rules), TEST_PLAN rows, push.
