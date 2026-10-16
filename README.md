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

Type `!commands` in game for the full command list (also [below](#command-list)), or `!jbrules` for the rules. Admin tiers: [below](#admin-tiers).

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

---

## Admin tiers

The plugin adds four admin groups under root to CounterStrikeSharp's `configs/admin_groups.json` (only when missing; your own groups are never changed). Each tier has everything of the tiers below it, and a higher immunity so a lower tier cannot target a higher one. Restart the server once after the first load so CounterStrikeSharp reads them.

| Tier | Group | Immunity | Adds |
|---|---|---|---|
| Level 1 | `#swbs/level1` | 10 | admin menu and chat, jail logs, warden control, open cells, start/stop days, abort LRs, warden games, mute/gag/silence, slay/slap/beacon/freeze |
| Level 2 | `#swbs/level2` | 20 | kick, move/swap teams, `!1up`/`!xup`, timed CT bans and bans, crew admin |
| Level 3 | `#swbs/level3` | 30 | permanent CT bans and unbans, day/LR editing, respawn, noclip/god/give and other fun commands, map change |
| Lead | `#swbs/lead` | 50 | zone editor and server cvars |
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

Every command of the plugin (159 commands, generated from the code). Type them in chat with `!` (or `/` to hide the message) or in the console with `css_` (`!bhc` = `css_bhc`). **Who** is the lowest admin tier that can use the command (see Admin tiers); "Everyone" commands may still be limited to the warden, guards or prisoners by the game rules.

<details><summary><b>Admin</b> (60)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!addban` |  | Root | &lt;minutes\|0&gt; &lt;steamid&gt; [reason] |
| `!admin` | !adminmenu | Level 1 | Open the admin menu |
| `!adminlevels` |  | Level 1 | the SWBS admin tiers and what each can do |
| `!adminlog` |  | Level 2 | [lines] [filter] - recent admin activity (console) |
| `!admins` |  | Everyone | List online admins |
| `!armor` |  | Level 3 | Set player armour: !armor &lt;target&gt; &lt;amount&gt; [helmet 0/1] |
| `!ban` |  | Level 2 | &lt;#userid\|name&gt; &lt;minutes\|0&gt; [reason] |
| `!banip` |  | Level 2 | &lt;ip\|#userid\|name&gt; &lt;minutes\|0&gt; [reason] |
| `!banlist` |  | Level 2 | List active bans |
| `!beacon` |  | Level 1 | &lt;#userid\|name&gt; [0/1] - toggle a beacon ring on a player |
| `!blind` |  | Level 1 | &lt;#userid\|name&gt; [amount 0-255] - leave amount off to reset |
| `!bring` |  | Level 1 | Teleport a player to you: !bring &lt;target&gt; |
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
| `!god` |  | Level 3 | Toggle god mode: !god &lt;target&gt; [0/1] |
| `!goto` |  | Level 1 | Teleport to a player: !goto &lt;target&gt; |
| `!gravity` |  | Level 1 | &lt;#userid\|name&gt; [amount] - leave amount off to reset (0.0 - 5.0) |
| `!heal` |  | Root | &lt;#steamid\|#userid\|name&gt; - heal a player to 100 health |
| `!hp` |  | Level 3 | Set player health: !hp &lt;target&gt; &lt;amount&gt; |
| `!kick` |  | Level 2 | &lt;#userid\|name&gt; [reason] |
| `!map` |  | Level 3 | &lt;map\|workshop id&gt; - change map |
| `!mute` | !timed_mute | Level 1 | &lt;player&gt; [minutes] [reason] - remove voice |
| `!mylevel` |  | Everyone | your admin tier |
| `!noclip` |  | Level 3 | &lt;#userid\|name&gt; - toggle noclip |
| `!psay` |  | Level 1 | &lt;player&gt; &lt;message&gt; - private message |
| `!rcon` |  | Root | &lt;args&gt; - run a server command |
| `!reloadadmins` |  | Level 2 | reload CSS admins, groups and overrides |
| `!rename` |  | Level 1 | &lt;#userid\|name&gt; &lt;new name&gt; |
| `!respawn` |  | Level 3 | &lt;#steamid\|#userid\|name&gt; - respawn a dead player |
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
| `!unban` |  | Level 3 | &lt;steamid\|ip&gt; |
| `!unfreeze` |  | Level 1 | Unfreeze a player |
| `!ungag` | !timed_ungag | Level 1 | &lt;player&gt; |
| `!ungod` |  | Level 3 | Disable god mode: !ungod &lt;target&gt; |
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

<details><summary><b>Extras</b> (9)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!admintag` |  | Level 1 | toggle your admin clan tag |
| `!bhce` |  | Everyone | &lt;#userid\|name&gt; - extended bhop log (console) |
| `!bhopcheck` | !bhc | Everyone | &lt;#userid\|name&gt; - bhop log (console) |
| `!commands` |  | Everyone | list the commands you can use (!commands console prints them to your console) |
| `!giveparachute` |  | Level 2 | &lt;target&gt; - give a parachute (when Parachute.Everyone is off) |
| `!music` | !stopmusic | Everyone | turn map music on or off for you |
| `!perfsound` |  | Everyone | toggle the perfect-jump sound |
| `!removeparachute` |  | Level 2 | &lt;target&gt; - remove a parachute |
| `!workshoppack` |  | Root | point the sound settings at the SWBS workshop pack again |

</details>

<details><summary><b>General commands</b> (12)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!1up` |  | Level 2 | &lt;target&gt; - respawn a player where they died |
| `!definitions` | !defs | Everyone | jailbreak definitions link |
| `!fkslay` |  | Level 1 | &lt;prisoner&gt; - slay the guard who freekilled a prisoner and respawn the prisoner |
| `!freekill` |  | Level 1 | &lt;msg&gt; - let admins know you were freekilled |
| `!g` |  | Level 1 | &lt;message&gt; - message the guard team |
| `!jbrules` |  | Everyone | jailbreak rules link |
| `!roll` |  | Everyone | [max] - warden rolls a random number |
| `!speed` |  | Level 2 | &lt;target&gt; &lt;0.0 to 10.0&gt; - set a player's speed |
| `!stoptimer` |  | Everyone | stop the countdown timer |
| `!timer` |  | Everyone | &lt;seconds&gt; - warden countdown timer |
| `!whokilled` | !wk | Level 1 | &lt;who&gt; - check who killed a player |
| `!xup` |  | Level 2 | &lt;seconds&gt; - respawn prisoners that died in the last X seconds |

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
| `!log` | !logs, !sm_log, !sm_logs | Level 2 | &lt;player&gt; &lt;num&gt; - List the actions relating to &lt;player&gt; (last &lt;num&gt; lines). |

</details>

<details><summary><b>Store, points and player extras</b> (17)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!bet` |  | Everyone | &lt;player&gt; &lt;points\|all&gt; - (dead) bet on who gets a last request, or during LRs on who wins a duel |
| `!cleartitle` |  | Level 1 | &lt;player&gt; - remove a player's chat title |
| `!givepoints` |  | Root | &lt;target&gt; &lt;amount&gt; - give jailbreak points |
| `!lrstats` |  | Everyone | [player] - last request duel wins and losses per LR |
| `!maprating` |  | Everyone | [map] - show a map's rating |
| `!points` |  | Root | [target] - show your jailbreak points (admins: anyone's) |
| `!rank` | !top | Everyone | [category\|player] - jailbreak leaderboard and your position |
| `!ratemap` | !rm | Everyone | rate the current map |
| `!ratewarden` | !rate | Everyone | rate the last round's warden from 1 to 5 |
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
| `!unctban` |  | Level 2 | &lt;#steamid\|#userid\|name&gt; - remove a CT ban |

</details>

<details><summary><b>Voice</b> (1)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!wmute` | !sm_wmute | Everyone | Toggle Warden mute powers. |

</details>

<details><summary><b>Warden</b> (18)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!blindfold` | !bf | Level 1 | [player\|off] - (warden/deputy) black out a prisoner's screen; again to remove |
| `!cc` |  | Everyone | Sets the clients render color. |
| `!deputy` |  | Level 1 | [player] - (warden) appoint a guard as deputy; no name removes the deputy |
| `!extend` |  | Level 1 | [seconds] - (warden) add time to the round |
| `!ff` | !friendlyfire | Level 1 | (warden/deputy) toggle friendly fire between prisoners |
| `!kiss` | !smooch | Everyone | Kisses the player you're looking at (or !kiss &lt;player&gt;). |
| `!rand` | !random | Level 1 | (warden/deputy) pick a random alive prisoner |
| `!repeat` |  | Level 1 | (prisoner) ask the warden to repeat the last order |
| `!rw` |  | Level 2 | Removes the current warden. |
| `!split` | !teams | Level 1 | [2-4\|off] - (warden/deputy) split the alive prisoners into colour teams |
| `!splitun` | !unsplit | Level 1 | (warden/deputy) remove the !split colour teams |
| `!spotlight` | !spot | Level 1 | [player] - (warden/deputy) the prisoner you look at glows for guards |
| `!sw` | !setw, !setwarden | Root | Sets the current warden. |
| `!uw` |  | Everyone | Allows the warden to unwarden themselves. |
| `!votewarden` | !vw | Everyone | vote to remove the current warden (type again to take your vote back) |
| `!w` | !warden | Everyone | Adds you to the warden queue. |
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

<details><summary><b>Zones</b> (4)</summary>

| Command | Aliases | Who | Description |
|---|---|---|---|
| `!showtriggernames` |  | Lead | show trigger names in console as you touch them |
| `!spawnzone` | !addspawn | Lead | [t\|ct\|all] - add a round-start spawn zone at your feet and edit it |
| `!zonemanager` | !zm | Lead | opens the zone manager |
| `!zones_list` |  | Lead | List this map's zones (id, type, name, position) |

</details>
