# SWBS Ultimate Jailbreak for Counter-Strike 2

A CounterStrikeSharp rebuild of hlstriker's **SMPlugins Ultimate Jailbreak** (the Swoobles / SWBS CS:GO jailbreak) together
with the server plugins and admin tools that ran beside it, as **one CS2 plugin** (`SwbsJailbreak`). The original gameplay,
commands, numbers and identity are kept; everything is rebuilt on CounterStrikeSharp APIs with real CS2 effects (beams,
particles, glow, sounds, HUD), never text stand-ins.

| | |
|---|---|
| Version | **0.10.0** test build |
| Platform | CS2 + Metamod:Source 2.0 + CounterStrikeSharp **1.0.368 with runtime (.NET 8)** |
| Target | `net8.0` **only** (CounterStrikeSharp 1.0.370+ is .NET 10 and is not targeted) |
| Status | Builds with 0 warnings / 0 errors, 178 unit tests pass. **Not yet verified on a live CS2 server**: every gameplay feature is "Implemented, unverified" until the in-game checklist (TEST_PLAN.md) is run |
| Download | **`releases/SwbsJailbreak-0.10.0-server-package.zip`** (plugin + recommended server cfg + INSTALL.txt + docs), or just the plugin: `releases/SwbsJailbreak-0.10.0-net8.zip` |

## Credits
* **Created by Gdaki**: the project and all work before it was handed to Claude Code (including the SimpleAdmin mod).
* **Enhanced with Claude Code**: the CS2 / CounterStrikeSharp rebuild and later enhancements.
* **Owner's provenance note** (the owner's statement, not verified by this project): Gdaki states that this mod is also based
  on a confirmed working CounterStrikeSharp version of the same mod that Gdaki made previously. Checked on 2026-10-12: the
  only earlier CounterStrikeSharp jailbreak project supplied, `Jailbreak-CS2-CounterStrikeSharp_5` (a multi-plugin
  `Jailbreak.Core` project, `net8.0`, module author string "Jailbreak Ecosystem"), states in its own README that it has
  **never been compiled against a real CounterStrikeSharp install**, so it cannot be shown to be a confirmed working version.
  No code from it is in this plugin (no identical code lines, no references to its namespaces); this plugin is written from
  the SMPlugins sources, with eGo only as a behavioural reference. This plugin's own status is unchanged: **not yet verified
  on a live server**. If the confirmed working version is a different one, supply it and its running evidence.
* **Inspired by**: designed after **HlStriker** (SMPlugins Ultimate Jailbreak) and the **Swbs development/lead admins**.
* SMPlugins contributors whose plugins are ported: **Hymns For Disco** (MeasureGap, SpeedRuns Stats), **FrostJacked**,
  **Scoutkllr** (design reference for the planned course features).
* Original SMPlugins © hlstriker (www.swoobles.com). eGo Jailbreak was used only as a behavioural reference; no code or
  branding was copied.

## What is in it

**Ultimate Jailbreak core (SMPlugins port)**
* **Warden**: `!w` queue and selection, auto warden when nobody queues (optional), warden bonus HP/armour only while 1–2 guards
  are alive, ping pointer laser and rings (CS2 ping = the engine's crosshair point), paint / line / ring gestures on bullet
  impacts, markers, countdown timer, warden menu (heal area, rings, colours, days, cells, Warden Games, crew shakedown).
* **Cells**: automatic cell-door detection plus a root editor (`!celldoors_edit`, ping a door), `!open` / `!o` for admins,
  original root `!cdo`.
* **Last Requests**: all **25** original LRs (Freeday ×4, Draw, Rebel ×8, Weapon Battle ×5, Contests ×7), LR teleport spots,
  rebel points, beacons, freedays.
* **Days**: **22 of 23** original days (freedays and wardays incl. Hunger Games, Zombie, Freeze Tag, Capture the Flag, Jihad,
  CT Retake, Horror Movie, Throwing Knives, One In The Chamber ...). The RPG warday is blocked on CSS 1.0.368 (no world
  collision source; see FEATURE_GAPS.md).
* **Teams**: guard queue, 2:1 ratio, jointeam rules, persistent CT bans. **Rebels**, **last guard**, **logger** (kill/damage
  log), **mute** rules, **effects**, **points**, **player models**, **settings** (spawn loadout, late-join respawn ...).
* **Admin**: SourceMod basecommands / funcommands / basecomm / basebans equivalents, respawn, heal, teamswitch, timed
  mute/gag, the owner's SimpleAdmin features, `!admin` menu. Permissions are capabilities mapped to CSS flags (`Core/Perms.cs`,
  overridable in config); no hard-coded SteamIDs.
* **Zone Manager**: a remake of SMPlugins ZoneManager + zone types (`!zm`): Kill, Push, Blockade, Respawn, Teleport, Teleport
  Destination, Weapon Strip, Damage Blocker, Named, Notify, Cell Block, Auto Bhop. Boxes are edited in game with beams only the
  editing admin sees; saved per map.
* **`!commands`**: lists the commands each player may use.

**Additions (owner requests, built on the same systems)**
* **Crews**: a prisoner gang system built around earned respect (no buying), levels, turf on Cell Block zones, favors
  (information / identity only), crew chat, clan tags, rivals, warden shakedown, admin tools, and the **Crew War** warday
  (docs/design/CREWS.md).
* **Warden Games** (from SMPlugins MultigamesGameSelect / MultigamesQueue / UserPointsForMinigamesWinner): `!games` runs and
  judges Win Zone (`mgwin`), Last One Standing, Simon Says, Red Light / Green Light, Musical Zones and the map's own games
  (`!gamebuttons`), with a fair prisoner picker queue and one reward per round (points or crew respect). Judging uses real
  engine input: buttons, velocity, eye pitch, zones.
* **Game days and helpers**: Hide and Seek (SMPlugins HideAndSeek), Jetpack day (Jetpacks), random no-scope rounds
  (NoScopeRounds), `!hide` (HidePlayers), Auto Bhop zones (MinigamesAutoBhop), `!measure` / `!gap` (MeasureGap).

Full feature-by-feature status with the original `.sp` sources: **FEATURE_INVENTORY.md**. What is missing or limited:
**FEATURE_GAPS.md**.

## Install (CS2 dedicated server)

1. Install **Metamod:Source 2.0** (matching the current CS2 build) and add `Game csgo/addons/metamod` to `gameinfo.gi`.
2. Install **CounterStrikeSharp 1.0.368 "with runtime"** (.NET 8) into `game/csgo/addons/counterstrikesharp`.
3. From `releases/SwbsJailbreak-0.10.0-server-package.zip`, copy `addons/` and `cfg/` into `game/csgo/` (creates
   `addons/counterstrikesharp/plugins/SwbsJailbreak/`), and add `exec swbs_jailbreak` to `cfg/server.cfg`. The zip's
   INSTALL.txt has the full steps and download links (CounterStrikeSharp `v1.0.368` with runtime).
4. Admins go in CounterStrikeSharp `configs/admins.json` (flags such as `@css/generic`, `@css/ban`, `@css/config`, `@css/root`).
5. Start the server; the console prints `SWBS Ultimate Jailbreak v0.10.0 loaded: <n> modules`.
6. Config: `addons/counterstrikesharp/configs/plugins/SwbsJailbreak/SwbsJailbreak.json` (generated on first start). Data
   (bans, CT bans, points, crews) in `.../SwbsJailbreak/data/`, per-map documents (cell doors, LR/rebel points, zones, crew
   turf, game buttons, enabled days) in `.../SwbsJailbreak/maps/`.
7. Reload after editing config: `css_plugins reload SwbsJailbreak` (or change map); phrases: `css_jb_reload`.

Recommended cvars, first-time map setup (cell doors, LR spot, rebel points, zones, game buttons) and the full test checklist:
**BUILD_AND_TEST.md** and **TEST_PLAN.md**.

## Build from source

```bash
dotnet restore
dotnet build -c Release     # net8.0 only, expect 0 warnings / 0 errors
dotnet test  -c Release     # 178 unit tests
./build.sh                  # Windows: build.ps1  ->  artifacts/SwbsJailbreak-<version>-net8.zip
```

The package contains `SwbsJailbreak.dll`, `.deps.json`, `.pdb` and `lang/en/*.json`; CounterStrikeSharp itself is not
bundled (compile-only reference to CounterStrikeSharp.API 1.0.368).

## Commands

All commands work as `!name` / `/name` in chat and `css_name` in the console (SMPlugins `sm_` names are kept in the
descriptions). Admin commands check their capability in code; `!commands` shows each player only what they may use. This
list is generated from the `[ConsoleCommand]` attributes in the source (150 commands).

### Admin (71)
| Command | Description |
|---|---|
| `!kick` | sm_kick (#userid/name) [reason] |
| `!ban` | sm_ban (#userid/name) (minutes/0) [reason] |
| `!addban` | sm_addban (minutes/0) (steamid) [reason] |
| `!banip` | sm_banip (ip/#userid/name) (minutes/0) [reason] |
| `!unban` | sm_unban (steamid/ip) |
| `!banlist` | List active bans |
| `!warn` | Warn a player: !warn (target) (reason) |
| `!warns` | Show a player's warnings: !warns (target) |
| `!unwarn` | Clear a player's warnings: !unwarn (target) |
| `!mute` | sm_mute (player) [minutes] [reason] - remove voice |
| `!timed_mute` | Timed voice mute |
| `!gag` | sm_gag (player) [minutes] [reason] - remove text chat |
| `!timed_gag` | Timed chat gag |
| `!silence` | sm_silence (player) [minutes] [reason] - remove voice and chat |
| `!timed_silence` | Timed silence |
| `!unmute` | sm_unmute (player) |
| `!timed_unmute` | Remove a timed mute |
| `!ungag` | sm_ungag (player) |
| `!timed_ungag` | Remove a timed gag |
| `!unsilence` | sm_unsilence (player) |
| `!timed_unsilence` | Remove a timed silence |
| `!check_mute` | Show a player's mute status |
| `!check_gag` | Show a player's gag status |
| `!check_silence` | Show a player's silence status |
| `!say` | sm_say (message) - admin message to everyone |
| `!csay` | sm_csay (message) - centre message to everyone |
| `!hsay` | sm_hsay (message) - hint message to everyone |
| `!psay` | sm_psay (player) (message) - private message |
| `!chat` | sm_chat (message) - message to all admins |
| `!asay` | Message to all admins |
| `!beacon` | sm_beacon (#userid/name) [0/1] - toggle a beacon ring on a player |
| `!timebomb` | sm_timebomb (#userid/name) [0/1] |
| `!firebomb` | sm_firebomb (#userid/name) [0/1] |
| `!freezebomb` | sm_freezebomb (#userid/name) [0/1] |
| `!freeze` | sm_freeze (#userid/name) [time] |
| `!unfreeze` | Unfreeze a player |
| `!burn` | sm_burn (#userid/name) [time] |
| `!gravity` | sm_gravity (#userid/name) [amount] - leave amount off to reset (0.0 - 5.0) |
| `!noclip` | sm_noclip (#userid/name) - toggle noclip |
| `!blind` | sm_blind (#userid/name) [amount 0-255] - leave amount off to reset |
| `!drug` | sm_drug (#userid/name) [0/1] |
| `!admin` | Open the admin menu |
| `!adminmenu` | Open the admin menu |
| `!slap` | sm_slap (#userid/name) [damage] |
| `!slay` | sm_slay (#userid/name) |
| `!rename` | sm_rename (#userid/name) (new name) |
| `!respawn` | sm_respawn (#steamid/#userid/name) - respawn a dead player |
| `!heal` | sm_heal (#steamid/#userid/name) - heal a player to 100 health |
| `!hp` | Set player health: !hp (target) (amount) |
| `!armor` | Set player armour: !armor (target) (amount) [helmet 0/1] |
| `!god` | Toggle god mode: !god (target) [0/1] |
| `!ungod` | Disable god mode: !ungod (target) |
| `!give` | Give an item: !give (target) (weapon) |
| `!bring` | Teleport a player to you: !bring (target) |
| `!goto` | Teleport to a player: !goto (target) |
| `!team` | Move a player to a team: !team (target) (t/ct/spec) |
| `!teamswitch` | Switch a player's team now: !ts (target) [t/ct/spec] |
| `!ts` | Switch a player's team now |
| `!teamswitch_death` | Switch a player's team when they die: !tsd (target) |
| `!tsd` | Switch a player's team when they die |
| `!teamswitch_roundend` | Switch a player's team at round end: !tsr (target) |
| `!tsr` | Switch a player's team at round end |
| `!map` | sm_map (map/workshop id) - change map |
| `!rcon` | sm_rcon (args) - run a server command |
| `!cvar` | sm_cvar (cvar) [value] |
| `!execcfg` | sm_execcfg (filename) |
| `!reloadadmins` | sm_reloadadmins - reload CSS admins, groups and overrides |
| `!rr` | Restart the round (mp_restartgame 1) |
| `!restartround` | Restart the round |
| `!who` | sm_who [#userid/name] - show players' admin access |
| `!admins` | List online admins |

### Cells (9)
| Command | Description |
|---|---|
| `!celldoors_open` | sm_celldoors_open - force the cell doors open |
| `!cdo` | sm_cdo - force the cell doors open |
| `!open` | sm_open - (admin) open the cell doors |
| `!o` | sm_o - (admin) open the cell doors |
| `!celldoors_edit` | sm_celldoors_edit - in-game cell door editor |
| `!celldoors_list` | sm_celldoors_list - list door-type entities on this map (console) |
| `!celldoors_add` | sm_celldoors_add (#index/targetname) - add a cell door (live, use _save to keep) |
| `!celldoors_remove` | sm_celldoors_remove (#index/targetname) - remove a cell door (live) |
| `!celldoors_save` | sm_celldoors_save - save the live cell door list for this map |

### Commands (14)
| Command | Description |
|---|---|
| `!g` | sm_g (message) - message the guard team |
| `!freekill` | sm_freekill (msg) - let admins know you were freekilled |
| `!whokilled` | sm_whokilled (who) - check who killed a player |
| `!wk` | sm_wk (who) - check who killed a player |
| `!1up` | sm_1up (target) - respawn a player where they died |
| `!xup` | sm_xup (seconds) - respawn prisoners that died in the last X seconds |
| `!roll` | sm_roll [max] - warden rolls a random number |
| `!jbrules` | sm_jbrules - jailbreak rules link |
| `!definitions` | sm_definitions - jailbreak definitions link |
| `!defs` | sm_defs - jailbreak definitions link |
| `!speed` | sm_speed (target) (0.0 to 10.0) - set a player's speed |
| `!timer` | sm_timer (seconds) - warden countdown timer |
| `!stoptimer` | sm_stoptimer - stop the countdown timer |
| `!xray` | sm_xray - see everyone's outline through walls while dead |

### Crews (6)
| Command | Description |
|---|---|
| `!crew` | sm_crew [create/invite/accept/decline/leave/kick/promote/demote/boss/motd/rival/favor/info/disband] - crews |
| `!cr` | sm_cr (message) - crew chat |
| `!crewsay` | sm_crewsay (message) - crew chat |
| `!crews` | sm_crews - crew leaderboard |
| `!crewadmin` | sm_crewadmin - manage crews (admin) |
| `!shakedown` | sm_shakedown - (warden) order a shakedown on a crew: its members glow for the guards |

### Days (7)
| Command | Description |
|---|---|
| `!ctf_setflag` | Save your position as this map's CTF flag / defender spawn |
| `!ctf_setcapture` | Save your position as this map's CTF capture point / attacker spawn |
| `!day` | sm_day - open the days menu (warden) |
| `!warday` | sm_warday - open the warday menu (warden) |
| `!freeday` | sm_freeday - open the freeday menu (warden) |
| `!de` | sm_de - edit the enabled days for this map |
| `!daysedit` | sm_daysedit - edit the enabled days for this map |

### Extras (6)
| Command | Description |
|---|---|
| `!bhopcheck` | sm_bhopcheck (#userid/name) - bhop log (console) |
| `!bhc` | sm_bhc (#userid/name) - bhop log (console) |
| `!bhce` | sm_bhce (#userid/name) - extended bhop log (console) |
| `!perfsound` | sm_perfsound - toggle the perfect-jump sound |
| `!commands` | sm_commands - list the commands you can use (!commands console prints them to your console) |
| `!admintag` | sm_admintag - toggle your admin clan tag |

### Games (5)
| Command | Description |
|---|---|
| `!hide` | sm_hide - hide the other players (dead players and warden game players) |
| `!measure` | sm_measure - (admin) measure the distance between two pinged points |
| `!gap` | sm_gap - (admin) measure the distance between two pinged points |
| `!gamebuttons` | sm_gamebuttons - (admin) mark the map buttons that start its games |
| `!games` | sm_games - (warden) start, control or end a warden game |

### LastRequest (8)
| Command | Description |
|---|---|
| `!lr_setpoint` | Save your position as this map's LR teleport point |
| `!rebelpoint_add` | Add your position as a rebel spawn point for this map |
| `!rebelpoint_clear` | Remove all rebel points of this map |
| `!lrs` | sm_lrs - how many players can use last request this round |
| `!lr` | sm_lr - open the last request menu |
| `!lastrequest` | sm_lastrequest - open the last request menu |
| `!givefreeday` | sm_givefreeday (#steamid/#userid/name) - give a player a freeday |
| `!abortlr` | sm_abortlr (#steamid/#userid/name) - abort a player's last request |

### Logger (2)
| Command | Description |
|---|---|
| `!log` | sm_log (player) (num) - List the actions relating to (player) (last (num) lines). |
| `!logs` | sm_log (player) (num) - List the actions relating to (player) (last (num) lines). |

### Mute (1)
| Command | Description |
|---|---|
| `!wmute` | Toggle Warden mute powers. |

### PlayerExtras (1)
| Command | Description |
|---|---|
| `!points` | sm_points - show your jailbreak points |

### Teams (4)
| Command | Description |
|---|---|
| `!guard` | sm_guard - join the guard queue |
| `!ctban` | sm_ctban (#steamid/#userid/name) (minutes) \"reason\" - ban a player from the CT team |
| `!unctban` | sm_unctban (#steamid/#userid/name) - remove a CT ban |
| `!check_ctban` | sm_check_ctban (#steamid/#userid/name) - check a CT ban |

### Warden (12)
| Command | Description |
|---|---|
| `!wm` | Opens the warden's menu. |
| `!cc` | Sets the clients render color. |
| `!w` | Adds you to the warden queue. |
| `!warden` | Adds you to the warden queue. |
| `!uw` | Allows the warden to unwarden themselves. |
| `!wwho` | Displays the current warden. |
| `!kiss` | Kisses the player you're looking at. |
| `!smooch` | Kisses the player you're looking at. |
| `!rw` | css_rw - Removes the current warden. |
| `!sw` | Sets the current warden. |
| `!setw` | Sets the current warden. |
| `!setwarden` | Sets the current warden. |

### Zones (4)
| Command | Description |
|---|---|
| `!zonemanager` | sm_zonemanager - opens the zone manager |
| `!zm` | sm_zm - opens the zone manager |
| `!showtriggernames` | sm_showtriggernames - show trigger names in console as you touch them |
| `!zones_list` | List this map's zones (id, type, name, position) |

## Configuration sections

`SwbsJailbreak.json` has one section per module: Admin, BhopCheck, CaptureTheFlag, Cells, CommandList, Commands, Contests,
Crews, Days, DaysExtra, DaysLarge, Effects, FreezeTag, GameDays, Games, General (incl. `Permissions` overrides), Jihad,
LastGuard, LastRequest, Logger, Mute, PlayerModels, Points, Rpg, ServerTweaks, Settings, Teams, Warden, WardenMenu,
WeaponBattle, WeaponSelection, Zones. Defaults follow the original SMPlugins cvars; each module's options are documented in
its config class (`Modules/<Module>/*Config*`).

## Repository layout

```
src/SwbsJailbreak/              the plugin
  JailbreakPlugin.cs            entry point (BasePlugin), module discovery, events, listeners
  Core/                         framework: services (Api.cs), menus, HUD, chat, storage, permissions, aim (player_ping),
                                zones, glow, visibility, damage, movement, effects, crews/games pure logic
  Modules/<Area>/               one folder per system (Warden, Cells, LastRequest, Days, Teams, Zones, Crews, Games, Admin ...)
  lang/en/*.json                all phrases
tests/SwbsJailbreak.Tests/      unit tests (xUnit, net8.0)
releases/                       packaged builds
docs/                           developer guide, specs, designs (Zone Manager, Crews, Minigames), research
build.sh / build.ps1            package script
```

## Documentation index

| File | What it is |
|---|---|
| PROJECT_STATUS.md | Overall status, counts, next priorities |
| FEATURE_INVENTORY.md | Every original SMPlugins feature: source, status, location |
| FEATURE_GAPS.md | Missing / limited features and why (engine limits, blocked items, eGo comparison) |
| BUILD_AND_TEST.md | Versions, build, install, map setup |
| docs/LOCAL_TEST_SERVER.md | Quick guide: local CS2 test server (SteamCMD, Metamod, CounterStrikeSharp, plugin) incl. the CSS / game-version caveat |
| TEST_PLAN.md | In-game test checklist |
| COMPATIBILITY.md | Pinned versions and verified CounterStrikeSharp 1.0.368 API facts |
| ARCHITECTURE.md, docs/DEVELOPER.md | Design, module conventions, how to add a module |
| DECISIONS.md | Architectural and compatibility decisions |
| CHANGELOG.md | Changes per version |
| SESSION_HANDOFF.md, RESUME_PROMPT.md | Where development stands and how to resume |
| docs/design/*.md | Zone Manager, Crews, Minigames suite design and plan |
| docs/research/JB_FEATURE_RESEARCH.md | CS:GO / CS:S jailbreak plugin research |
| CLAUDE.md | Standing project rules (owner rules + master prompt) |
