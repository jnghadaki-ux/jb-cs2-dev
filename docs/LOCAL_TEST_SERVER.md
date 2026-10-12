# Local CS2 test server for SWBS Jailbreak (quick guide)

Checked 2026-10-12. Goal: a dedicated CS2 server on your own PC that you join from the same PC (or your LAN) to run
`TEST_PLAN.md`. Time: ~20 minutes plus the CS2 server download (~35 GB).

**Verified from official sources:** CounterStrikeSharp (CSS) download names and which game version each release targets.
**From standard practice (Valve / Metamod sites could not be reached when this was written):** SteamCMD, Metamod and launch
steps. If a step is rejected, say so and send me the console text.

## 0. Read this first: the version problem
CSS is rebuilt for almost every CS2 game update, and a fresh server install is always the **newest** game.

| CSS release | Built for CS2 build (from its release notes) | .NET |
|---|---|---|
| **1.0.368** (what this plugin is built against) | 1.41.6.1 | 8 |
| 1.0.371 / 1.0.373 / 1.0.375 / **1.0.376 (latest seen)** | 1.41.6.9 / 1.41.7.7 / 1.41.8.2 / 1.41.8.4 | 10 |

So **CSS 1.0.368 is several game updates old**. On a current game it may not load, may log signature / gamedata errors, or may
crash (typically at map change or round start). I could not test this from here. Check your server's game version after
step 2 (`version` in the server console) and compare. Your options:
1. **Use the combination your own working servers run** (you told me they run CSS on .NET 8). That is the safest choice: tell
   me its exact CSS version and CS2 build and I will treat it as the target.
2. **If your local server is on the newest game and 1.0.368 fails:** CSS 1.0.376 is the newest release that tracks the game,
   but it is the .NET 10 build. The plugin is built for `net8.0` against the 1.0.368 API; I have **not** verified that it loads
   on 1.0.376, and your rule is to not work on .NET 10. Try it only as a throwaway local experiment and send me the console
   output.

## 1. Install the server files
1. Install SteamCMD (Windows: unzip `steamcmd.zip` to e.g. `C:\steamcmd`; Linux: `sudo apt install steamcmd`, or the tarball).
2. Download the CS2 dedicated server (app id **730**, anonymous login, no purchase needed):
   ```
   steamcmd +force_install_dir C:\cs2server +login anonymous +app_update 730 validate +quit
   ```
   Linux: `steamcmd +force_install_dir ~/cs2server +login anonymous +app_update 730 validate +quit`.
   Server files end up in `C:\cs2server\game\csgo\`. Re-run the same command to update after a CS2 patch.

## 2. First start (no plugins yet), to prove the base works
Windows (from `C:\cs2server`):
```
game\bin\win64\cs2.exe -dedicated -console -insecure +sv_lan 1 +game_type 0 +game_mode 0 +map de_dust2
```
Linux: same arguments with `game/bin/linuxsteamrt64/cs2` (or `game/cs2.sh`).
* `+sv_lan 1` keeps it LAN-only, and with `-insecure` (no VAC on the server) you do **not** need a game server login token
  (GSLT). A token is only for public internet servers.
* In the server console, run `version` and note the build (for the check in section 0).
* On your game client: enable the developer console (Settings → Game → Enable Developer Console), then
  `connect 127.0.0.1` (same PC) or `connect <server LAN IP>`. Allow UDP port 27015 in the firewall for LAN players.
* Stop the server (`quit`).

## 3. Install Metamod:Source
1. Download the **Metamod:Source 2.0 dev (master) build for your OS** (sourcemm.net → Downloads → Dev builds). Take the newest.
2. Extract so you get `game\csgo\addons\metamod\` (and `metamod.vdf`).
3. Edit `game\csgo\gameinfo.gi`: in the `SearchPaths` block add this line **above** the existing `Game csgo` line:
   ```
   Game    csgo/addons/metamod
   ```
   **A CS2 update overwrites `gameinfo.gi`: re-add the line after every update** (the usual reason "plugins stopped loading").
4. Start the server, type `meta list` in the server console. It should list Metamod.

## 4. Install CounterStrikeSharp
1. Download from `https://github.com/roflmuffin/CounterStrikeSharp/releases/tag/v1.0.368` the asset
   `counterstrikesharp-with-runtime-windows-1.0.368.zip` (or `...-linux-1.0.368.zip`). Use "with runtime" the first time.
2. Extract into `game\csgo\` (it creates `addons\counterstrikesharp\`).
3. Restart, then in the server console: `meta list` (shows CounterStrikeSharp) and `css_plugins list`.

## 5. Install the SWBS Jailbreak plugin
1. From the repo: `releases/SwbsJailbreak-0.10.0-server-package.zip`. Copy its `addons\` and `cfg\` folders into `game\csgo\`
   (merge). Plugin ends up in `addons\counterstrikesharp\plugins\SwbsJailbreak\`.
2. Add `exec swbs_jailbreak` to the end of `game\csgo\cfg\server.cfg` (create the file if missing).
3. Make yourself admin: edit `game\csgo\addons\counterstrikesharp\configs\admins.json` (use **your** SteamID64; nothing is
   hard-coded in the plugin):
   ```json
   {
     "YourName": { "identity": "7656119XXXXXXXXXX", "flags": [ "@css/root" ] }
   }
   ```
4. Restart. Expected console line: `SWBS Ultimate Jailbreak v0.10.0 loaded: <n> modules`; `css_plugins list` shows it as loaded.
   Edited the config? `css_plugins reload SwbsJailbreak`.

## 6. A map and the test
* Jailbreak behaviour needs a **jailbreak map** (cells, armory, buttons). Pick a CS2 jailbreak map from the Steam Workshop and
  note its Workshop ID. A dedicated server needs a Steam Web API key to download Workshop maps: add `-authkey <your key>` to
  the launch line, then `host_workshop_map <workshop id>` in the server console. (I did not verify any specific map.)
* Quick smoke test on **any** map (`de_dust2`): `!commands`, `!w`, `!zm`, `!crew`, `!admin`, `!measure`.
* Cells, last requests, days and Warden Games need a jail map and **at least 2 prisoners and 1 guard**: join with extra players,
  or add bots (`bot_add_t`, `bot_add_ct`). Bots work for basics, but Crews ignore them and menus need a human, so real players test more.
* First-time map setup and the checklist: `BUILD_AND_TEST.md` §4 and `TEST_PLAN.md`.

## 7. What to send back
The server console output from startup to the first round, and any line containing `error`, `exception`, `signature`,
`gamedata` or `crash`; plus which TEST_PLAN numbers passed or failed.

## Troubleshooting
| Symptom | Likely cause / fix |
|---|---|
| `meta list` says unknown command | Metamod not loaded: `gameinfo.gi` line missing or overwritten by an update (re-add it) |
| Metamod loads, CSS missing | CSS extracted to the wrong folder, or CSS is for an older game build (section 0) |
| CSS loads, plugin missing | Wrong folder name (must be `plugins\SwbsJailbreak\SwbsJailbreak.dll`) or a .NET / API mismatch (read the CSS log in `addons\counterstrikesharp\logs\`) |
| Crash at map change or round start | CSS gamedata out of date for this game build (section 0) |
| Cannot join | Wrong IP, UDP 27015 blocked, or the client and server game versions differ (update both) |
| Commands answer "no access" | Missing flag in `admins.json` (`@css/root` for everything) |
