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
Buttons-based map games, jetpack and other game days, and a game browser for the warden.

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

Type `!commands` in game for the full command list, or `!jbrules` for the rules.

---

## Requirements

| Component | Version |
|---|---|
| Counter-Strike 2 dedicated server | newest build (SteamCMD app `730`) |
| [Metamod:Source](https://www.sourcemm.net/downloads.php?branch=dev) | 2.0 (dev / master build), **not** 1.12 |
| [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/releases) | **1.0.376 or newer**, the `with-runtime` download (it bundles .NET 10) |

Always pair the newest CS2 build with the newest CounterStrikeSharp release. If Valve ships a game update, wait for the matching CSS release.

---

## Installation

1. **Install the server** with SteamCMD: `+login anonymous +app_update 730 validate`.
2. **Install Metamod:Source 2.0** into `game/csgo/` and make sure `game/csgo/gameinfo.gi` loads it (the Metamod instructions cover this line).
3. **Install CounterStrikeSharp** (`with-runtime`) by extracting its `addons` folder into `game/csgo/`.
4. **Install the plugin.** Download the latest `SwbsJailbreak-<version>-net10.zip` from [`releases/`](releases) and extract it into `game/csgo/`. You should end up with
   `game/csgo/addons/counterstrikesharp/plugins/SwbsJailbreak/SwbsJailbreak.dll`.
   Remove any older `SwbsJailbreak` folder first.
5. **Add yourself as an admin** in `game/csgo/addons/counterstrikesharp/configs/admins.json`:
   ```json
   {
     "YourName": {
       "identity": "7656119XXXXXXXXXX",
       "flags": [ "@css/root" ]
     }
   }
   ```
   CounterStrikeSharp only recognises admins by their Steam-verified ID, so a server needs a game server token (`+sv_setsteamaccount <token>`). For a local test server started with `-insecure`, set `General.TrustUnverifiedSteamIds` to `true` in the plugin config (test servers only).
6. **Start the server.** The console should print `SWBS Ultimate Jailbreak v<version> loaded`. A config file is created on first run at
   `game/csgo/addons/counterstrikesharp/configs/plugins/SwbsJailbreak/SwbsJailbreak.json`, with every setting documented by its name. Restart (or change map) after editing it.

### Per-map setup

Cell doors are auto-detected on most maps. For anything unusual, stand on the map as a root admin and use:

- `!celldoors_edit` to mark the cell doors, then save them,
- `!zones` to draw LR areas and other zones,
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
