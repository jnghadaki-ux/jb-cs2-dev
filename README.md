# SWBS Ultimate Jailbreak for CS2

A full Jailbreak game mode for **Counter-Strike 2**, written as a [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin (C# / .NET 10).

**Created by Gdaki.** Inspired by the SMPlugins fork.

> **Status:** test build. It compiles cleanly and passes its unit tests; keep it on a test server until you've checked it on your own setup.

---

## Features

**Core jailbreak**
- **Warden** system: queue, `!w` / `!uw`, warden menu and tools (rings, pointer, bullet line, markers), warden votes and admin controls.
- **Cell doors:** auto-detection per map, `!open`, an editor for maps that need it, and an automatic open at round start.
- **Teams:** guard / prisoner balancing, CT bans (timed and permanent), swap tools and team-switch rules.
- **Rebel, freeday and freekill tracking**, `!freekill` reports, kill logging and a jail log.
- **Last Guard** mode, guard weapon selection, reserve ammo, and guard tools such as `!frisk` and `!shakedown`.

**Last Requests (LR)**
Dozens of duels and contests: weapon battles, knife / grenade / shot-for-shot style duels, mind games, rebel and special LRs, contests and races, a teleporter LR, a tournament mode and freeday LRs.

**Days (freedays and wardays)**
Capture the Flag, Freeze Tag, Gun Game, Speedrun (with per-map records), Spy, RPG, Crew War, plus many simple and large-scale days. Admins start them from `!day`; `!daysedit` turns days on and off.

**Warden games and game days**
Buttons-based map games, jetpack and other game days, and a game browser for the warden. Plugin-run games that work on any map: Simon Says, Red Light, Musical Zones, Duck Hunt and **Death Crate** (a crate thrown around a ring of prisoners; the last survivor wins).

**Crews, points and the store**
- **Crews** (clans) with tags, favors, a crew war day and admin tools.
- **Points** earned for kills and wins.
- **Store** (`!store`): bullet tracers, paintballs, kill effects, trails, auras, sparkles, chat colours and titles, body size and transparency, hats, kill and grenade sounds, player models.
- **Betting** on rounds, **contraband** and snitch rewards, **leaderboards** and per-player stats.

**Server tools**
- **Admin suite:** kick / ban / slay / slap / freeze / beacon / gag / mute / silence and timed variants, respawn, teleport (`!goto`, `!bring`), map change, rcon, ban lists and an admin activity log.
- **Admin points** for testing: `!givepoints`, `!takepoints`, `!setpoints`.
- **Zones editor** (`!zones`) for LR spots, Speedrun finish lines and more.
- AFK handling, bhop checks, chat formatting and anti-spam, parachute, map ratings and map music.
- Everything is configurable from one JSON file; all messages are translatable.

Type `!commands` in game for the full command list (also [below](#command-list)), or `!jbrules` for the rules. See also: [using menus](#menus), [the zone manager](#zone-manager), [map voting](#map-voting), [point pools](#point-pools), [prisoner shop](#prisoner-shop-sabotage-and-perks), [demos](#demos), [admin tiers](#admin-tiers).

---

## Shared with the other SWBS servers

The Store (`!store`), the Zone Manager (`!zones`) and BhopCheck (`!bhc`) are the same code on every SWBS server: the
movement servers (`cs2-bhop-dev`), minigames / DM / PUG / 1v1 (`cs2-mg-dev`) and jailbreak (`jb-cs2-dev`). The code
lives in `src/Swbs.Common`, which is kept identical in all three repos. See
[`src/Swbs.Common/README.md`](src/Swbs.Common/README.md) for how to edit it and what each game mode changes.

Store points and store items can also follow players across every server. Set `Backend` in
`addons/counterstrikesharp/configs/swbs_shared.json` (written on first start) to `folder` (servers on one machine) or
`mysql` (servers on different machines), using the same settings on every server. Each server's existing points and
items are merged in automatically the first time a player joins.

## Requirements

| Component | Version |
|---|---|
| Counter-Strike 2 dedicated server | newest build (SteamCMD app `730`) |
| [Metamod:Source](https://www.sourcemm.net/downloads.php?branch=dev) | 2.0 (dev / master build), **not** 1.12 |
| [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/releases) | **1.0.376 or newer**, the `with-runtime` download (it bundles .NET 10) |
| [MultiAddonManager](https://github.com/Source2ZE/MultiAddonManager/releases) | newest release (Metamod plugin): mounts the SWBS Workshop packs and pre-downloads vote maps |

Always pair the newest CS2 build with the newest CounterStrikeSharp release. If Valve ships a game update, wait for the matching CSS release.

---

## Installation

1. **Install the server** with SteamCMD: `+login anonymous +app_update 730 validate`.
2. **Install Metamod:Source 2.0** into `game/csgo/` and make sure `game/csgo/gameinfo.gi` loads it (the Metamod instructions cover this line).
3. **Install CounterStrikeSharp** (`with-runtime`) by extracting its `addons` folder into `game/csgo/`.
4. **Install MultiAddonManager** by extracting its release into `game/csgo/` (it is a Metamod plugin, next to CounterStrikeSharp). Put the SWBS Workshop pack ids in its `mm_extra_addons` cvar (in `game/csgo/cfg/multiaddonmanager/multiaddonmanager.cfg`, or later in game with `!addon add <id or link>`), so every player downloads the models, sounds and hats.
5. **Install the plugin.** Download the latest `SwbsJailbreak-<version>-net10.zip` from [`releases/`](releases) and extract it into `game/csgo/`. You should end up with
   `game/csgo/addons/counterstrikesharp/plugins/SwbsJailbreak/SwbsJailbreak.dll`.
   Remove any older `SwbsJailbreak` folder first.
6. **Add yourself as an admin** in `game/csgo/addons/counterstrikesharp/configs/admins.json`:
   ```json
   {
     "YourName": {
       "identity": "7656119XXXXXXXXXX",
       "flags": [ "@css/root" ]
     }
   }
   ```
   CounterStrikeSharp only recognises admins by their Steam-verified ID, so a server needs a game server token (`+sv_setsteamaccount <token>`). For a local test server started with `-insecure`, set `General.TrustUnverifiedSteamIds` to `true` in the plugin config (test servers only).
7. **Start the server.** The console should print `SWBS Ultimate Jailbreak v<version> loaded`. A config file is created on first run at
   `game/csgo/addons/counterstrikesharp/configs/plugins/SwbsJailbreak/SwbsJailbreak.json`, with every setting documented by its name. Restart (or change map) after editing it.

### Per-map setup

Cell doors are auto-detected on most maps. For anything unusual, stand on the map as a root admin and use:

- `!celldoors_edit` to mark the cell doors, then save them,
- `!zm` (or `!zones`) to draw LR areas, spawn zones and other zones (see the [zone manager guide](#zone-manager)),
- `!lr_setpoint` and `!rebelpoint_add` to set the LR teleport point and rebel spawn points.

Settings are saved per map, so this is a one-time step.

---

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
./build.sh          # Linux / macOS / WSL: runs the build and writes artifacts/SwbsJailbreak-<version>-net10.zip
./build.ps1         # Windows PowerShell
dotnet test         # unit tests
```

---

## Credits

- **Gdaki**: creator and maintainer of this CS2 version.
- Inspired by the SMPlugins fork.
- Built on [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) by roflmuffin and contributors, and [Metamod:Source](https://www.sourcemm.net/).

---

## Menus

Every menu in the plugin (warden, admin, store, LR, days, games, zones, …) works the same way.

**Opening a menu**

| Command | Menu | Who |
|---|---|---|
| `!wm` | Warden menu | Warden |
| `!day`, `!freeday`, `!warday` | Days | Warden |
| `!games` | Warden games | Warden |
| `!lr` | Last request | Last prisoner alive |
| `!store` | Store, points and player extras | Everyone |
| `!menustyle` | Your menu look: world panel or classic, theme, position, size | Everyone |
| `!admin` | Admin menu | Admins |
| `!zm` (`!zones`) | Zone manager, [see below](#zone-manager) | Lead / root |
| `!celldoors_edit` | Cell door editor | Lead / root |
| `!wbeacon` | Warden pointer & beacon look: tracer colour, beacon colour and size, thickness, how long the beacon lasts (saved at once) | Lead / root |

**Reading a menu**

- The **title** (gold) shows the page you are on, e.g. `Store (1/3)`. The grey line under it is a hint or the current state.
- Each page holds up to **6 items**, numbered 1–6.
- The item with **►** in green is the one under your cursor.
- **Grey items with no number** are locked: not available right now, or you lack the rank, points or permission.
- The footer lists the keys you can use on that menu.

**World menus**

While you are alive, every menu is drawn as a **themed panel floating in front of you**. It is made of point_worldtext
in the world, not the centre-screen text. What the panel shows:

- coloured text on a translucent backing plate, with an accent bar;
- numbered rows and a gently pulsing **►** cursor;
- values lined up on the right: green ON / red OFF, prices, levels and counts;
- page dots, the menus you came from, and key hints.

The panel follows you as you move and turn, and re-centres when you look up or down. Only you can see it. When you are
dead or spectating, the classic centre menu shows instead, and the centre HUD keeps showing the mode's own panels while
a world menu is open. The keys are the same.

`!menustyle` (`!menutheme`, `!menusettings`) sets your own look, and it is saved:

| Setting | Choices |
|---|---|
| Style | World panel, Classic (centre). `!menustyle world` / `!menustyle center` switch directly. |
| Theme | SWBS Gold, Crimson, Azure, Emerald, Violet, Ember, Coop, Neon, Mono (`!menutheme azure`). By default each server uses its mode's theme: DM crimson, Surf and Bhop azure, KZ and Course emerald, Jailbreak ember, 1v1 violet, the others gold. |
| Position | Left, Centre, Right |
| Size | Small, Normal, Large |

Server owners set the defaults in the `WorldMenu` config section:

- `Enabled`, `DefaultStyle`, `DefaultTheme`, `DefaultPosition` and `DefaultSize`.
- Font: `FontName` and `FontSize`.
- Placement: `Distance`, `LeftEdge`, `TopEdge`, `PitchStep` and `MaxPitch`.
- Appearance: `CharWidth`, `PanelBorder`, and the animation, pulse and fade settings.
- Optional move, select and open sounds.

Plugin code can give an item a right-hand `Tag`, a `Color`, or add a section heading with `AddHeader`.

**Controls**

| Action | Keys (centre HUD) | Chat / console |
|---|---|---|
| Move the cursor | **W** (up) / **S** (down) | — |
| Choose the item under the cursor | **E** | — |
| Choose item 1–6 directly | — | `!1` … `!6` |
| Back: previous page, or the menu before this one | **R** | `!7` |
| Next page | — | `!8` |
| Exit | — | `!9` |

- The number commands always work, even when the HUD keys are switched off. In the console they are `css_1` … `css_9`. You can bind them to keys yourself, e.g. `bind 1 "slot1; css_1"`.
- While a menu is open, W/S also move the cursor (you still walk), E selects, and R goes back. Close the menu with `!9` (or R on the first menu) if it gets in the way.
- A menu closes by itself after **30 seconds** with no input, or when another menu replaces it.

**Server settings** (`General` in `SwbsJailbreak.json`)

| Setting | Default | What it does |
|---|---|---|
| `MenuStyle` | `Both` | `Center`: centre-screen HUD only. `Chat`: numbered lines in chat, `!1`–`!9` only. `Both`: HUD plus the chat lines. Use `Chat` if the centre HUD ever flickers on your server. |
| `MenuWasd` | `true` | W/S/E/R navigation on the centre HUD. When off, only `!1`–`!9` work. |
| `MenuTimeoutSeconds` | `30` | Idle time before an open menu closes. |
| `MenuChatEcho` | `false` | With `Center`, also print the items in chat. |

---

## Zone manager

Zones are invisible boxes on the map that do something when a player is inside them: LR spots, kill areas, jump pads, armory alerts, extra spawns and more. They are saved **per map**, so you set a map up once.

Who can use it: Lead and root (`@css/config`).

### Quick start: make a zone

1. Open the editor with `!zm` (or `!zones`). Turn on **Enable Noclip** if you need to fly around.
2. Stand where the zone should be and choose **Add new zone**. A box appears at your feet. The zone you are editing is drawn in **green**, other nearby zones in **red**.
3. **Edit size** — look at the side you want to change, then:
   - hold **left click** to push that side out, or **right click** to pull it in (4 units per step);
   - or ping a spot (**middle mouse**, the CS2 ping key) and choose **Expand to my last ping**.

   For an exact box, use **Set size from two corners** instead: stand in (or ping) one corner and choose **Corner A**, then go to the opposite corner and choose **Corner B**.
4. **Edit position** (if it needs moving) — look around and the zone follows your view. Hold left click to move it further away and right click to bring it closer. **Move zone to my feet** / **my last ping** snaps it into place.
5. **Edit type** — pick what the zone does (table below). Then use **Edit type data** to fill in its settings, such as a name, a team or timers.
6. Optionally choose **Set label** to add a note for other admins, e.g. "cell block A roof".
7. Press **Done** and then **Save zones**. Edits also save automatically every 15 seconds, when you close the editor, and at map end.

### Editing or removing an existing zone

- **Select the zone I'm in / pinging** — stand inside the zone, or ping it, to pick it straight away.
- **Select a zone** — choose this, then **Next** / **Previous** to cycle through zones by id.
- With a zone selected you can **Edit** it, **Teleport to** it, **Duplicate** it (the copy is stacked on top, handy for rows of identical areas), or **Delete** it (asks Yes/No).
- **Show all nearby zones** draws every zone around you, not just the closest few.
- `!zones_list` prints every zone on the map to the console (id, type, name, position).
- `!zones_ents [radius]` lists map entities near your ping (or you) with their class and name.

### Zone types

| Type | What it does | Type data |
|---|---|---|
| **Spawn** | At round start, moves players of its team to a random spot inside it. Use this to fix maps with bad spawns. Shortcut: `!spawnzone t\|ct\|all` makes one at your feet. | Team: Prisoners / Guards / Everyone |
| **Teleport Destination** | A named spot. The jailbreak uses `lr_tele` (LR teleport); Capture the Flag uses `ctf_flag` / `ctf_capture`. | Name |
| **Teleport** | Touching it sends the player to the Teleport Destination with the same name. | Destination name |
| **Named** | A named area other features look up, e.g. the Speedrun finish line. Name one `deathcrate` to fix where the Death Crate ring goes (otherwise it goes where the warden pings). | Name |
| **Notify (area alert)** | Tells guards, everyone or admins when someone enters, e.g. "Prisoner entered Armory". | Area name, triggered by, alert goes to |
| **Cell Block** | Marks a cell block. Crews claim these as turf. | Block name (A, B, Upper, …) |
| **Kill** | Kills anyone who touches it, optionally only between two timers. | Enabled / disabled timer (s) |
| **Blockade** | An invisible wall that can turn solid or open on timers. Drawn red when `Zones.ShowBlockades` is on. | Solid / not-solid timer (s) |
| **Push (jump pad)** | Launches players in the direction set with **Edit angles**. | Force (0 = 600) |
| **Respawn** | Sends the player back to a spawn point of their team. | — |
| **Weapon Strip** | Removes all weapons on entry. | — |
| **Damage Blocker** | Blocks the chosen damage types (fall, bullets, …) for players inside. | Damage types (tick boxes) |
| **Map Input (fix buttons)** | Sends an input to map entities when someone presses E inside it, walks in, or at round start. Use it to repair broken buttons, doors and map games ([see below](#fixing-broken-map-buttons-and-games)). | Target, Input, Value, Activation, Cooldown |

**Edit angles**: look in the direction you want, then choose **Set angle**. Jump pads launch that way, and Spawn zones make players face that way.

### Fixing broken map buttons and games

Many jailbreak maps were ported from CS:GO, and some of their buttons, doors or minigames do nothing in CS2. A **Map Input** zone fires the map's own logic for it.

**Broken button** (the most common case):
1. Stand at the button, open `!zm` and choose **Add new zone**. Size the zone so it covers the button and the spot where players stand to press it.
2. **Edit type** → **Map Input (fix buttons)**. Leave **Target** and **Input** empty.
3. Save. Pressing **E** inside the zone now presses every button inside the zone, with the player as the presser.

**Door, relay or minigame that needs a specific entity:**
1. Ping the broken thing (middle mouse) and type `!zones_ents`. The console lists nearby entities with their class and name, nearest first. Add a radius for a bigger search, e.g. `!zones_ents 600`.
2. Make a Map Input zone and fill in **Edit type data**:
   - **Target**: the entity name, e.g. `game_start_relay`. End it with `*` to hit many at once, e.g. `cell_door*`.
   - **Input**: what to send — `Press` (buttons), `Open` / `Close` / `Toggle` (doors), `Trigger` (logic_relay), `Enable` / `Disable`, `Unlock`, … Empty uses the usual input for that class.
   - **Value**: only for inputs that take a parameter (e.g. `SetValue`), otherwise empty.
   - **Activation**: Press E inside (anyone / guards only / prisoners only), Enter (anyone / guards / prisoners), or Round start.
   - **Cooldown**: seconds before it can fire again (0 = 1 s).
3. Test it, then save. If nothing happens, the server console logs `map input zone #id: no entity matched` the first time; check the name with `!zones_ents`.

Limits: Map Input repairs map *logic* (buttons, relays, doors, triggers). It cannot fix map physics that broke in CS2, such as thrusters or spinning physics props. The map's own **Death Crate** is usually broken this way: use the plugin's Death Crate game instead (`!games`, then Death Crate). Optionally put a Named zone called `deathcrate` in the arena so the ring always lands there. Only use it on buttons that are really broken, or a working button fires twice.

### Other tools

- **Import trigger from map** — turns one of the map's own trigger areas into a zone that follows it. `!showtriggernames` prints trigger names in the console as you walk through them, so you know which one to import.
- **Import zones from another map** — copies a layout from a similar map (only when `Zones.CanImportFromAnotherMap` is on).
- Zone files live in `configs/plugins/SwbsJailbreak/maps/zones/<map>.json`. Back them up, or copy them between servers.

---

## Map voting

Players vote on the next map; maps are **Workshop maps**. By default a map lasts **30 minutes** (`MapVote.MapTimeLimitMinutes`, set as `mp_timelimit`). When the time is up the map changes at the end of the round, or at most `MapVote.MapEndMaxWaitSeconds` (120 s) later when the round does not end (a long last request, a special day).

**Player commands**

| Command | What it does |
|---|---|
| `!rtv` (`!votechange`, `!rockthevote`) | Vote to change the map. When 60% of the players on a team have typed it (`MapVote.RtvRatio`; AFK players moved to spectators do not count, a spectator who votes does; `MapVote.RtvMinVotes` sets a minimum), a map vote starts with a "Don't change the map" option. The winner loads at the end of the current round. |
| `!nominate [map]` (`!nom`) | Put a map on the next ballot (no name = pick from a menu). One per player, as many as the ballot has places; recently played maps are left out. |
| `!revote` | Change your vote while the map vote is open. |
| `!nextmap` | The next map, once voted. |
| `!timeleft` | Time left on this map (or that it ends at the end of this round). |
| `!maplist` (`!maps`) | Every map you can vote for. |
| `!currentmap` | The map you are on. |

A vote also starts by itself 3 minutes before the time limit (`MapVote.EndVoteMinutesBefore`), with an "Extend this map" option (`MapVote.MaxExtends`). Every map change counts down on screen ("Changing to X in 5..."), loads a Workshop map with `host_workshop_map <id>` and a stock map with `changelevel`, and tries the other way (or the same command again) if the map has not changed `MapVote.ChangeCheckSeconds` later.

**Setting up the maps (admins)**

1. Put your jailbreak maps in a Steam Workshop collection and set `MapVote.WorkshopCollectionId` to its id (the number in the collection's Steam link). The plugin reads the collection and every map's **name** from Steam, so you never type map ids by hand.
2. `!mapsetup` (Level 3+): every map by name with **[ON]/[OFF]** switches, and "Refresh map names from Steam".
3. `!mapadd <id or Steam link>` adds a single map (its name loads from Steam); `!mapremove <name or id>` switches one off.
4. `!mapchange [name, id or Steam link]` (Level 3+, also `!changemap` and `!map`): changes the map after a short countdown (`MapVote.AdminChangeCountdownSeconds`), ending the round and the map. Any map of the server's Workshop collection also works by name. No name opens a menu of your maps by name. It loads Workshop maps with `host_workshop_map`, checks the map really changed, and tries another way (or tells you) if not. `!setnextmap <map>` picks the next map (loaded when the map ends), `!extendmap [minutes]` adds time to this map (up to `MapVote.MaxExtends` times; `!extend` stays the warden's round extend), and `!setmapend <minutes>` (also `!settimeleft`) ends the map that many minutes from now (`0` = at the end of this round). Map changes go to the admin log.
5. `!startjb` (Level 3+, also `!jbstart` and `!startmode`): on a default map (e.g. the server booted into de_dust2), it sets casual mode, kicks bots, ends warmup and loads your collection. On a jailbreak map it restarts the game in jailbreak mode.

**MultiAddonManager** (required, see [Requirements](#requirements)): `!mapadd` also downloads the map to the server ahead of time, and `!addon add|remove|list <id or link>` (Lead+) mounts Workshop addons such as the store pack, with names from Steam.

---

## Point pools

Prisoners can put their points behind what they want the warden to run: a **warden game**, a **map game** or a **day**.

- `!pool` (also `!pools`, `!fund`): pick a category, then an option, then an amount (50-1000).
- **Starting** a pool is only possible in the **first 60 seconds** of a round, **once per player per round**, with at most **3 pools per category**. Anyone can **join** an open pool until the warden chooses, a day starts or last requests begin. One contribution per category per player per round. Only the start of a pool is announced in chat; the totals show on the guards' HUD.
- The warden sees each pot in `!games` and `!day` (the biggest is marked ★).
- When the warden starts a pooled option, **its pot is shared evenly by the current guards**. Every other pool is refunded in full (at round end, when a day starts or when last requests start). Guards cannot fund pools.
- Settings: `Pools` section (start window, limits, amounts, categories).

---

## Prisoner shop (sabotage and perks)

Prisoners spend points on effects that last **the whole round**: `!sabotage` (`!sab`) for the shop, `!perk` for perks only.

**On by default**

| Sabotage (against the guards, all round) | Effect | Cost |
|---|---|---|
| Smoke screen | A random guard is partly blinded for 4 s every 35-50 s | 600 |
| Bad trip | One guard has a mild drug sway all round; it passes on if they die | 700 |
| Hot floor | Every 45 s the guard standing still longest burns for 3 s (cannot kill) | 650 |
| Heavy boots | All guards 8% slower | 800 |
| Jammed radio | The warden's pointer and beacon black out 10 s of every 40 s | 600 |
| Paranoia | Fake sounds near a random guard every 30-60 s | 450 |

| Perk (for you, until you die) | Effect | Cost |
|---|---|---|
| Thick skin | +15 max HP, 1 HP every 3 s up to 115 | 500 |
| Light feet | +8% speed, 15% less fall damage | 450 |
| Shadow | Guards deal 15% less damage to you while you crouch | 550 |
| Second wind | The first time you would drop below 20 HP you heal to 50 | 600 |

**Off by default** (switch on with `!sabotagesetup`): curses (Frostbite, Static, Gravity well, Rusty guns, Butterfingers, Echo, Glowstick, Blackout pulse), skills (Vampire, Long jump, Impulse, Lucky, Adrenaline, Ghost step, Scavenger, Double jump, Iron lungs, Contraband) and team auras the prisoners fund together (Riot, Solidarity, Underground).

**Rules**: buy in the first 2 minutes of a round, never in days or last requests, not in the round you joined; 1 sabotage and 1 perk per prisoner per round; each sabotage once per round and at most 2 against the guards; everything stops when last requests or a day start or only 1 guard is left; nothing can kill. The guards see what is running on their HUD (the buyer stays anonymous). Points spent on sabotages are **shared by the guards who survive the round**.

**Admins**: `!sabotagesetup` (Lead+) switches every effect on or off for the server; costs and limits are in the `Sabotage` config section.

---

## Demos

Every game is recorded with SourceTV, like on the old servers:

- **Recording**: each map is recorded from start to the end of the game into `game/csgo/demos/<server>_<map>_<date-time>.dem`. Watch it with CS2's own viewer (`playdemo demos/<name>`). The jail log of every round is printed into the demo's console, so it shows up while you watch.
- **Console log**: a `.log` with the same name sits next to each demo: chat, kills, round starts/ends, connects and every jail log line (LRs, days, admin actions), each stamped with the **demo time** (`[12:41] ...`).
- `!demo` (everyone): the demo being recorded and the current time in it, so players can quote it in reports. `!demos` (admins): the last 15 games in the console.
- **Demo database**: set `Demos.UploadUrl` (and `Demos.UploadToken`) and each finished game (demo + log, zipped) is sent there as a `multipart/form-data` POST with the fields `server`, `name`, `map`, `started`, `ended`, `duration`, `players` and `file`. Every server can upload to the same database. `demos/index.json` lists every game on the server.
- **Housekeeping**: games nobody played are deleted, local files older than 14 days are removed (`Demos.KeepDays`), and recording pauses when less than 2 GB is free.

**Server setup** (once, in `server.cfg`, then restart; SourceTV cannot start mid-map):
```
tv_enable 1
tv_delay 30
tv_maxclients 0
tv_name "SWBS SourceTV"
```
SourceTV takes one player slot.

---

## Admin tiers

The plugin adds four admin groups under root to CounterStrikeSharp's `configs/admin_groups.json` and reloads them. The tiers are a ladder: Level 1 < Level 2 < Level 3 < Lead, and each tier always has everything the tiers below it have. On every start the plugin adds any lower-tier flag a higher tier is missing, including flags you added to a lower tier yourself. Your other groups, your immunities and any extra flags you gave a tier are never removed. A higher tier also has a higher immunity, so a lower tier cannot target a higher one. Put an admin in a tier with `"groups": ["#swbs/level2"]` in `admins.json`. Admins given flags directly instead of a group are ranked by their flags (for example `@css/ban` = Level 2) and also get everything below that rank. Targeting follows the same ladder, with root above Lead, even when `admins.json` gives no immunity.

| Tier | Group | Immunity | Adds |
|---|---|---|---|
| Level 1 | `#swbs/level1` | 10 | admin menu and chat, jail logs (`!log`), warden control (`!sw`/`!rw`), open cells, start/stop days, abort LRs, warden games, mute/gag/silence, slay/slap/beacon/freeze, `!respawn`/`!1up`/`!xup` |
| Level 2 | `#swbs/level2` | 20 | kick, move/swap teams, timed CT bans, warnings, crew admin |
| Level 3 | `#swbs/level3` | 30 | server bans (`!ban`, `!banip`, `!addban`), permanent CT bans, day/LR editing, teleport (`!bring`/`!goto`), `!gravity`, bhop logs (`!bhc`), give/hp/heal/armor/speed/parachutes and other fun commands (not noclip/god), map change |
| Lead | `#swbs/lead` | 50 | unban (`!unban`, `!unctban`), `!noclip`, `!god`/`!ungod`, `!wbeacon`, zone editor and server cvars |
| Root | `@css/root` (a flag, not a group) | 100 | everything, including rcon, the cell door editor, points and server setup |

In `admins.json`:

```json
{
  "SomeAdmin": { "identity": "7656119XXXXXXXXXX", "groups": [ "#swbs/level2" ] },
  "Owner":     { "identity": "7656119XXXXXXXXXX", "flags": [ "@css/root" ], "immunity": 100 }
}
```

In game: `!adminlevels` lists the tiers, `!mylevel` shows yours.

---

## Command list

Every command of the plugin (183 commands, generated from the code). Type them in chat with `!` (or `/` to hide the message) or in the console with `css_` (`!bhc` = `css_bhc`). **Who** is the lowest admin tier that can use the command (see Admin tiers); "Everyone" commands may still be limited to the warden, guards or prisoners by the game rules.

<details><summary><b>Admin</b> (60)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!addban` |  | Level 3 | &lt;minutes\|0&gt; &lt;steamid&gt; [reason] |
| `!admin` | !adminmenu | Level 1 | Open the admin menu |
| `!adminlevels` |  | Level 1 | the SWBS admin tiers and what each can do |
| `!adminlog` |  | Level 2 | [lines] [filter] - recent admin activity (console) |
| `!admins` |  | Everyone | List online admins |
| `!armor` |  | Level 3 | Set player armour: !armor &lt;target&gt; &lt;amount&gt; [helmet 0/1] |
| `!ban` |  | Level 3 | &lt;#userid\|name&gt; &lt;minutes\|0&gt; [reason] |
| `!banip` |  | Level 3 | &lt;ip\|#userid\|name&gt; &lt;minutes\|0&gt; [reason] |
| `!banlist` |  | Level 3 | List active bans |
| `!beacon` |  | Level 1 | &lt;#userid\|name&gt; [0/1] - toggle a beacon ring on a player |
| `!blind` |  | Level 1 | &lt;#userid\|name&gt; [amount 0-255] - leave amount off to reset |
| `!bring` |  | Level 3 | Teleport a player to you: !bring &lt;target&gt; |
| `!burn` |  | Level 1 | &lt;#userid\|name&gt; [time] |
| `!chat` | !asay | Level 1 | &lt;message&gt; - message to all admins |
| `!check_mute` | !check_gag, !check_silence | Level 1 | Show a player's mute status |
| `!csay` | !hsay | Level 1 | &lt;message&gt; - centre message to everyone |
| `!cvar` |  | Lead | &lt;cvar&gt; [value] |
| `!drug` |  | Level 1 | &lt;#userid\|name&gt; [0/1] |
| `!execcfg` |  | Lead | &lt;filename&gt; |
| `!firebomb` |  | Level 1 | &lt;#userid\|name&gt; [0/1] |
| `!freeze` |  | Level 1 | &lt;#userid\|name&gt; [time] |
| `!freezebomb` |  | Level 1 | &lt;#userid\|name&gt; [0/1] |
| `!gag` | !timed_gag | Level 1 | &lt;player&gt; [minutes] [reason] - remove text chat |
| `!give` |  | Level 3 | Give an item: !give &lt;target&gt; &lt;weapon&gt; |
| `!god` |  | Lead | Toggle god mode: !god &lt;target&gt; [0/1] |
| `!goto` |  | Level 3 | Teleport to a player: !goto &lt;target&gt; |
| `!gravity` |  | Level 3 | &lt;#userid\|name&gt; [amount] - leave amount off to reset (0.0 - 5.0) |
| `!heal` |  | Level 3 | &lt;#steamid\|#userid\|name&gt; - heal a player to 100 health |
| `!hp` |  | Level 3 | Set player health: !hp &lt;target&gt; &lt;amount&gt; |
| `!kick` |  | Level 2 | &lt;#userid\|name&gt; [reason] |
| `!map` |  | Level 3 | &lt;map\|workshop id&gt; - change map |
| `!mute` | !timed_mute | Level 1 | &lt;player&gt; [minutes] [reason] - remove voice |
| `!mylevel` |  | Everyone | your admin tier |
| `!noclip` |  | Lead | &lt;#userid\|name&gt; - toggle noclip |
| `!psay` |  | Level 1 | &lt;player&gt; &lt;message&gt; - private message |
| `!rcon` |  | Root | &lt;args&gt; - run a server command |
| `!reloadadmins` |  | Level 2 | reload CSS admins, groups and overrides |
| `!rename` |  | Level 1 | &lt;#userid\|name&gt; &lt;new name&gt; |
| `!respawn` |  | Level 1 | &lt;#steamid\|#userid\|name&gt; - respawn a dead player |
| `!rr` | !restartround | Level 3 | Restart the round (mp_restartgame 1) |
| `!say` |  | Level 1 | &lt;message&gt; - admin message to everyone |
| `!silence` | !timed_silence | Level 1 | &lt;player&gt; [minutes] [reason] - remove voice and chat |
| `!slap` |  | Level 1 | &lt;#userid\|name&gt; [damage] |
| `!slay` |  | Level 1 | &lt;#userid\|name&gt; |
| `!swapteams` |  | Level 2 | Swap guards and prisoners (CT-banned players stay prisoners) and restart the round |
| `!team` |  | Level 2 | Move a player to a team: !team &lt;target&gt; &lt;t\|ct\|spec&gt; |
| `!teamswitch` | !ts | Level 2 | Switch a player's team now: !ts &lt;target&gt; [t\|ct\|spec] (both teams: !swapteams) |
| `!teamswitch_death` | !tsd | Level 2 | Switch a player's team when they die: !tsd &lt;target&gt; |
| `!teamswitch_roundend` | !tsr | Level 2 | Switch a player's team at round end: !tsr &lt;target&gt; |
| `!timebomb` |  | Level 1 | &lt;#userid\|name&gt; [0/1] |
| `!unban` |  | Lead | &lt;steamid\|ip&gt; |
| `!unfreeze` |  | Level 1 | Unfreeze a player |
| `!ungag` | !timed_ungag | Level 1 | &lt;player&gt; |
| `!ungod` |  | Lead | Disable god mode: !ungod &lt;target&gt; |
| `!unmute` | !timed_unmute | Level 1 | &lt;player&gt; |
| `!unsilence` | !timed_unsilence | Level 1 | &lt;player&gt; |
| `!unwarn` |  | Level 2 | Clear a player's warnings: !unwarn &lt;target&gt; |
| `!warn` |  | Level 2 | Warn a player: !warn &lt;target&gt; &lt;reason&gt; |
| `!warns` |  | Level 2 | Show a player's warnings: !warns &lt;target&gt; |
| `!who` |  | Level 1 | [#userid\|name] - show players' admin access |

</details>

<details><summary><b>Cells</b> (7)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!celldoors_add` |  | Root | &lt;#index\|targetname&gt; - add a cell door (live, use _save to keep) |
| `!celldoors_edit` |  | Root | in-game cell door editor |
| `!celldoors_list` |  | Root | list door-type entities on this map (console) |
| `!celldoors_open` | !cdo | Root | force the cell doors open |
| `!celldoors_remove` |  | Root | &lt;#index\|targetname&gt; - remove a cell door (live) |
| `!celldoors_save` |  | Root | save the live cell door list for this map |
| `!open` | !o | Level 1 | (admin) open the cell doors |

</details>

<details><summary><b>Crews</b> (5)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!cr` | !crewsay | Everyone | &lt;message&gt; - crew chat |
| `!crew` |  | Everyone | [create\|invite\|accept\|decline\|leave\|kick\|promote\|demote\|boss\|motd\|rival\|favor\|info\|disband] - crews |
| `!crewadmin` |  | Level 2 | manage crews (admin) |
| `!crews` |  | Everyone | crew leaderboard |
| `!shakedown` |  | Everyone | (warden) order a shakedown on a crew: its members glow for the guards |

</details>

<details><summary><b>Days</b> (7)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!ctf_setcapture` |  | Lead | Save your position as this map's CTF capture point / attacker spawn |
| `!ctf_setflag` |  | Lead | Save your position as this map's CTF flag / defender spawn |
| `!day` |  | Everyone | open the days menu (warden) |
| `!de` | !daysedit | Level 3 | turn days on/off (this map or every map) |
| `!freeday` |  | Everyone | open the freeday menu (warden) |
| `!speedrun` | !sr | Everyone | best Speedrun day times on this map |
| `!warday` |  | Everyone | open the warday menu (warden) |

</details>

<details><summary><b>Extras</b> (28)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!addon` | !addons | Lead | &lt;add\|remove\|list&gt; [workshop id\|link] - (admin) mount Workshop addons with MultiAddonManager |
| `!admintag` |  | Level 1 | toggle your admin clan tag |
| `!bhce` |  | Level 3 | &lt;#userid\|name&gt; - extended bhop log (console) |
| `!bhopcheck` | !bhc | Level 3 | &lt;#userid\|name&gt; - bhop log (console) |
| `!commands` |  | Everyone | list the commands you can use (!commands console prints them to your console) |
| `!currentmap` |  | Everyone | show the current map |
| `!demo` |  | Everyone | the demo being recorded and the time in it (quote it in reports) |
| `!demos` |  | Level 1 | (admin) the last recorded games (console) |
| `!extendmap` |  | Level 3 | [minutes] - (admin) extend the map |
| `!giveparachute` |  | Level 3 | &lt;target&gt; - give a parachute (when Parachute.Everyone is off) |
| `!mapadd` |  | Level 3 | &lt;workshop id\|url&gt; - (admin) add a Workshop map to the vote list (its name comes from Steam) |
| `!mapchange` | !changemap | Level 3 | [map name\|workshop id\|link] - (admin) change the map now (no name = pick from a menu) |
| `!maplist` | !maps | Everyone | list the maps you can vote for |
| `!mapremove` |  | Level 3 | &lt;workshop id\|name&gt; - (admin) take a map off the vote list |
| `!mapsetup` |  | Level 3 | (admin) switch maps on/off for voting, refresh map names from Steam |
| `!menustyle` | !menutheme, !menusettings | Everyone | [world\|center] - your menu style, theme, position and size |
| `!music` | !stopmusic | Everyone | turn map music on or off for you |
| `!nextmap` |  | Everyone | show the next map |
| `!nominate` | !nom | Everyone | [map] - put a map on the next map vote |
| `!perfsound` |  | Everyone | toggle the perfect-jump sound |
| `!removeparachute` |  | Level 3 | &lt;target&gt; - remove a parachute |
| `!revote` |  | Everyone | change your map vote while the vote is open |
| `!rtv` | !rockthevote, !votechange | Everyone | rock the vote: vote to change the map |
| `!setmapend` | !settimeleft | Level 3 | &lt;minutes&gt; - (admin) end the map this many minutes from now (0 = at the end of this round) |
| `!setnextmap` |  | Level 3 | &lt;map\|workshop id\|link&gt; - (admin) set the next map |
| `!startjb` | !jbstart, !startmode | Level 3 | [collection id\|map] - (admin) start jailbreak: from a default map, load the Workshop jailbreak maps |
| `!timeleft` |  | Everyone | time left on this map |
| `!workshoppack` |  | Root | point the sound settings at the SWBS workshop pack again |

</details>

<details><summary><b>General commands</b> (12)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!1up` |  | Level 1 | &lt;target&gt; - respawn a player where they died |
| `!definitions` | !defs | Everyone | jailbreak definitions link |
| `!fkslay` |  | Level 1 | &lt;prisoner&gt; - slay the guard who freekilled a prisoner and respawn the prisoner |
| `!freekill` |  | Everyone | &lt;msg&gt; - let admins know you were freekilled |
| `!g` | !gc, !guardchat | Everyone | &lt;message&gt; - (prisoners) private message to the guards |
| `!jbrules` |  | Everyone | jailbreak rules link |
| `!roll` |  | Everyone | [max] - warden rolls a random number |
| `!speed` |  | Level 3 | &lt;target&gt; &lt;0.0 to 10.0&gt; - set a player's speed |
| `!stoptimer` |  | Everyone | stop the countdown timer |
| `!timer` |  | Everyone | &lt;seconds&gt; - warden countdown timer |
| `!whokilled` | !wk | Level 1 | &lt;who&gt; - check who killed a player |
| `!xup` |  | Level 1 | &lt;seconds&gt; - respawn prisoners that died in the last X seconds |

</details>

<details><summary><b>Last requests</b> (9)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!abortlr` |  | Level 1 | &lt;#steamid\|#userid\|name&gt; - abort a player's last request |
| `!givefreeday` |  | Level 1 | &lt;#steamid\|#userid\|name&gt; - give a player a freeday |
| `!lr` | !lastrequest | Everyone | open the last request menu |
| `!lr_setpoint` |  | Lead | Save your position as this map's LR teleport point |
| `!lre` | !lredit | Level 3 | turn last requests on/off (this map or every map) |
| `!lrlog` |  | Everyone | [count] - the last requests of this map and who won |
| `!lrs` |  | Everyone | how many players can use last request this round |
| `!rebelpoint_add` |  | Lead | Add your position as a rebel spawn point for this map |
| `!rebelpoint_clear` |  | Lead | Remove all rebel points of this map |

</details>

<details><summary><b>Logs</b> (1)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!log` | !logs, !sm_log, !sm_logs | Level 1 | &lt;player&gt; &lt;num&gt; - List the actions relating to &lt;player&gt; (last &lt;num&gt; lines). |

</details>

<details><summary><b>Store, points and player extras</b> (21)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!bet` |  | Everyone | &lt;player&gt; &lt;points\|all&gt; - (dead) bet on who gets a last request, or during LRs on who wins a duel |
| `!cleartitle` |  | Level 1 | &lt;player&gt; - remove a player's chat title |
| `!givepoints` |  | Root | &lt;target&gt; &lt;amount&gt; - give jailbreak points |
| `!lrstats` |  | Everyone | [player] - last request duel wins and losses per LR |
| `!maprating` |  | Everyone | [map] - show a map's rating |
| `!perk` |  | Everyone | (prisoners) buy a perk or skill for this round |
| `!points` |  | Everyone | [target] - show your jailbreak points (admins: anyone's) |
| `!pool` | !pools, !fund | Everyone | put points behind a warden game, map game or day you want (pools start in the first minute) |
| `!rank` | !top | Everyone | [category\|player] - jailbreak leaderboard and your position |
| `!ratemap` | !rm | Everyone | rate the current map |
| `!ratewarden` | !rate | Everyone | rate the last round's warden from 1 to 5 |
| `!sabotage` | !sab | Everyone | (prisoners) buy an effect on the guards, a perk, or chip in for a team aura |
| `!sabotagesetup` |  | Lead | (admin) switch shop effects on/off for this server |
| `!search` | !frisk | Everyone | [prisoner] - (guard) frisk the prisoner in front of you for contraband |
| `!setpoints` |  | Root | &lt;target&gt; &lt;amount&gt; - set a player's jailbreak points |
| `!snitch` | !tip | Everyone | &lt;prisoner&gt; - (prisoner) anonymously tip the guards that someone carries a weapon |
| `!stats` | !jbstats | Everyone | [player] - show jailbreak statistics |
| `!store` | !shop | Everyone | spend jailbreak points on effects, chat colours, titles and more |
| `!takepoints` |  | Root | &lt;target&gt; &lt;amount&gt; - take jailbreak points away |
| `!title` |  | Everyone | &lt;text\|off&gt; - set your chat title (store item) |
| `!topmaps` |  | Everyone | best rated maps |

</details>

<details><summary><b>Teams</b> (4)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!check_ctban` |  | Level 2 | &lt;#steamid\|#userid\|name&gt; - check a CT ban |
| `!ctban` |  | Level 2 | &lt;#steamid\|#userid\|name&gt; &lt;minutes&gt; "reason" - ban a player from the CT team |
| `!guard` |  | Everyone | join the guard queue |
| `!unctban` |  | Lead | &lt;#steamid\|#userid\|name&gt; - remove a CT ban |

</details>

<details><summary><b>Voice</b> (1)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!wmute` | !sm_wmute | Everyone | Toggle Warden mute powers. |

</details>

<details><summary><b>Warden</b> (19)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!blindfold` | !bf | Everyone | [player\|off] - (warden/deputy) black out a prisoner's screen; again to remove |
| `!cc` |  | Everyone | Sets the clients render color. |
| `!deputy` |  | Everyone | [player] - (warden) appoint a guard as deputy; no name removes the deputy |
| `!extend` |  | Everyone | [seconds] - (warden) add time to the round |
| `!ff` | !friendlyfire | Everyone | (warden/deputy) toggle friendly fire between prisoners |
| `!kiss` | !smooch | Everyone | Kisses the player you're looking at (or !kiss &lt;player&gt;). |
| `!rand` | !random | Everyone | (warden/deputy) pick a random alive prisoner |
| `!repeat` |  | Everyone | (prisoner) ask the warden to repeat the last order |
| `!rw` |  | Level 1 | Removes the current warden. |
| `!split` | !teams | Everyone | [2-4\|off] - (warden/deputy) split the alive prisoners into colour teams |
| `!splitun` | !unsplit | Everyone | (warden/deputy) remove the !split colour teams |
| `!spotlight` | !spot | Everyone | [player] - (warden/deputy) the prisoner you look at glows for guards |
| `!sw` | !setw, !setwarden | Level 1 | Sets the current warden. |
| `!uw` |  | Everyone | Allows the warden to unwarden themselves. |
| `!votewarden` | !vw | Everyone | vote to remove the current warden (type again to take your vote back) |
| `!w` | !warden | Everyone | Adds you to the warden queue. |
| `!wbeacon` | !wardenbeacon | Lead | [color\|beaconcolor &lt;name\|r g b\|same&gt;, size\|width\|time\|tracer &lt;n&gt;, on\|off, reset] - warden pointer and beacon settings |
| `!wm` |  | Everyone | Opens the warden's menu. |
| `!wwho` |  | Everyone | Displays the current warden. |

</details>

<details><summary><b>Warden games</b> (4)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!gamebuttons` |  | Level 1 | (admin) mark the map buttons that start its games |
| `!games` |  | Everyone | (warden) start, control or end a warden game |
| `!hide` |  | Everyone | hide the other players (dead players and warden game players) |
| `!measure` | !gap | Lead | (admin) measure the distance between two pinged points |

</details>

<details><summary><b>Weapons</b> (1)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!guns` | !gun | Everyone | (guard) choose your weapons |

</details>

<details><summary><b>Zones</b> (5)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!showtriggernames` |  | Lead | show trigger names in console as you touch them |
| `!spawnzone` | !addspawn | Lead | [t\|ct\|all] - add a round-start spawn zone at your feet and edit it |
| `!zonemanager` | !zm, !zones | Lead | opens the zone manager |
| `!zones_ents` |  | Lead | [radius] - list map entities near your last ping (or you): class, name, distance - for Map Input zones |
| `!zones_list` |  | Lead | List this map's zones (id, type, name, position) |

</details>

