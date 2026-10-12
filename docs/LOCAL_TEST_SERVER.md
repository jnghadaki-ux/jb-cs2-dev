# Local CS2 test server on a Windows laptop (quick guide)

Checked 2026-10-12. You run a CS2 **dedicated server** on the laptop and join it from CS2 on the same laptop, then work
through `TEST_PLAN.md`. About 20 minutes of work plus the CS2 server download.

**Verified from official sources:** the CounterStrikeSharp (CSS) download names and which game build each release targets.
**From standard practice** (Valve, Metamod and SteamCMD sites could not be reached when this was written): the SteamCMD,
Metamod and launch steps. If a step is rejected, send me the console text.

## Versions you need

| Component | Version | Notes |
|---|---|---|
| Windows | 10 or 11, 64-bit | Keep about 60 GB free disk (approximate; the server grows with updates) |
| CS2 dedicated server | **Newest** from SteamCMD (app id **730**) | The game on the laptop must be updated to the same build, or you cannot join |
| SteamCMD | Current Windows `steamcmd.zip` | Downloads/updates the server |
| Metamod:Source | **2.0 (dev/master), Windows 64-bit**. Your upload `mmsource-2.0.0-git1474-windows` is this kind; a newer dev build is fine | NOT the 1.12 build (`mmsource-1.12.0-git1227`): that one is for older Source games, not CS2 |
| CounterStrikeSharp | **1.0.368, `counterstrikesharp-with-runtime-windows-1.0.368.zip`** | "With runtime" bundles .NET 8: install no .NET separately. Do **not** use the 1.0.376 zip you uploaded: it is the .NET 10 line and needs a .NET 10 runtime |
| SWBS Jailbreak plugin | **0.10.0**, `releases/SwbsJailbreak-0.10.0-server-package.zip` | Built for `net8.0` against CSS 1.0.368 |
| .NET SDK | 8 | Only if you rebuild the plugin from source |

### Read this first: CSS 1.0.368 is old
CSS is rebuilt for almost every CS2 update (release notes on GitHub): **1.0.368 targets game build 1.41.6.1**; 1.0.371 /
373 / 375 / **376 (newest seen)** target 1.41.6.9 / 1.41.7.7 / 1.41.8.2 / 1.41.8.4 and are all .NET 10. A fresh server
install is the newest game, so CSS 1.0.368 may fail to load, log signature / gamedata errors, or crash (usually at map change
or round start). I could not test this from here.
1. After step 3, type `version` in the server console and compare the build with the numbers above.
2. **Safest:** use the CSS and CS2 versions your own working servers run (you told me they run CSS on .NET 8). Tell me the exact
   versions and I will treat them as the target.
3. **Fallback only if you want to experiment:** CSS 1.0.376 matches the newest game, but I have **not** verified that this
   plugin loads on it, and your rule is no .NET 10 work. It would need the "with runtime" Windows build of 1.0.376.

## 1. Download the server (SteamCMD)
1. Create `C:\steamcmd`, unzip `steamcmd.zip` there. Use folders **without spaces**.
2. Open PowerShell (not as administrator is fine) and run (also saved as `windows\update_server.bat` in the package, `scripts/windows/` in the repo):
   ```powershell
   C:\steamcmd\steamcmd.exe +force_install_dir C:\cs2server +login anonymous +app_update 730 validate +quit
   ```
   Anonymous login is enough: no purchase, no token. Files land in `C:\cs2server\game\csgo\`. Run it again after any CS2 patch.
   Also update CS2 itself in Steam so the client matches.

## 2. First start with no plugins (prove the base works)
Run `windows\start_server.bat` (copy it to `C:\cs2server\`), or:
```powershell
cd C:\cs2server
game\bin\win64\cs2.exe -dedicated -console -insecure +sv_lan 1 +game_type 0 +game_mode 0 +map de_dust2
```
* The first run triggers a Windows Firewall prompt: allow **Private networks**.
* `+sv_lan 1` + `-insecure` means LAN only and **no game server login token (GSLT) needed** (a token is only for public servers).
* In CS2: Settings → Game → **Enable Developer Console**, then press `~` and type `connect 127.0.0.1`.
* The server console is the window that opened: type `version` and note the build. Type `quit` to stop.

## 3. Install Metamod
1. Extract the Metamod 2.0 Windows zip so that `C:\cs2server\game\csgo\addons\metamod\` exists (the `addons` folder goes
   **directly** inside `csgo`, not in a subfolder; check with `dir C:\cs2server\game\csgo\addons`).
2. Open `C:\cs2server\game\csgo\gameinfo.gi` in Notepad. In the `SearchPaths` block add this line **above** the existing
   `Game    csgo` line:
   ```
   Game    csgo/addons/metamod
   ```
   **Every CS2 update overwrites `gameinfo.gi`: re-add the line after each update** (the usual reason plugins "stop loading").
3. Start the server, type `meta list` in the server console: Metamod should answer.

## 4. Install CounterStrikeSharp
1. Download `counterstrikesharp-with-runtime-windows-1.0.368.zip` from
   `https://github.com/roflmuffin/CounterStrikeSharp/releases/tag/v1.0.368`.
2. Extract into `C:\cs2server\game\csgo\` (creates `addons\counterstrikesharp\`).
3. Restart the server. `meta list` should now list CounterStrikeSharp; `css_plugins list` should answer.

## 5. Install the plugin
1. Unzip `SwbsJailbreak-0.10.0-server-package.zip`. Copy its `addons` and `cfg` folders into `C:\cs2server\game\csgo\`
   (merge). The plugin ends up in `addons\counterstrikesharp\plugins\SwbsJailbreak\`.
2. Add `exec swbs_jailbreak` as the last line of `C:\cs2server\game\csgo\cfg\server.cfg` (create it if missing).
3. Make yourself admin: edit `C:\cs2server\game\csgo\addons\counterstrikesharp\configs\admins.json` with **your** SteamID64
   (look it up at steamid.io; nothing is hard-coded in the plugin):
   ```json
   {
     "YourName": { "identity": "7656119XXXXXXXXXX", "flags": [ "@css/root" ] }
   }
   ```
4. Restart. Expected line: `SWBS Ultimate Jailbreak v0.10.0 loaded: <n> modules`; `css_plugins list` shows it loaded.
   After editing the plugin config: `css_plugins reload SwbsJailbreak`.

## 6. Maps and testing
* **Smoke test on any map** (`de_dust2`): `!commands`, `!w`, `!zm`, `!crew`, `!admin`, `!measure`.
* **Jailbreak features** (cells, last requests, days, Warden Games) need a **jailbreak map** and at least 2 prisoners + 1 guard.
  Get a CS2 jailbreak map from the Steam Workshop and note its ID. Workshop downloads on a dedicated server need a Steam Web API
  key: add `-authkey <your key>` to the launch line, then run `host_workshop_map <id>` in the server console. (No specific map
  verified.)
* One laptop: join as a guard, add bots as prisoners (`bot_add_t`, `bot_add_ct`). Crews ignore bots and menus need a human, so a
  second human (LAN friend) tests more. Others join with `connect <your laptop's LAN IP>`; open UDP **27015** in the firewall.
* Checklist: `TEST_PLAN.md`. First-time map setup (cells, LR spot, rebel points): `BUILD_AND_TEST.md` §4.
* Windows note (unverified): another CS2 jailbreak plugin (destoer's) documents a Windows-only problem in its damage hook. This
  plugin routes every damage rule (last requests, days, games) through one damage listener, so if damage rules misbehave **only
  on Windows**, tell me.

## 7. What to send back
The server console text from startup to the first round, every line with `error`, `exception`, `signature`, `gamedata` or
`crash`, the CSS log files in `addons\counterstrikesharp\logs\`, and which TEST_PLAN numbers passed or failed.

## Troubleshooting
| Symptom | Likely cause / fix |
|---|---|
| `meta list` unknown command | Metamod not loaded: `gameinfo.gi` line missing or overwritten by an update; or `addons` nested one folder too deep |
| Metamod works, CSS missing | CSS extracted to the wrong place, or CSS is older than the game build (see "CSS 1.0.368 is old") |
| CSS works, plugin missing | Folder must be `plugins\SwbsJailbreak\SwbsJailbreak.dll`; read `addons\counterstrikesharp\logs\` |
| Crash at map change / round start | CSS gamedata out of date for this game build |
| Cannot join | Wrong IP, firewall (UDP 27015), or client and server game builds differ: update both |
| "No access" on commands | Missing flag in `admins.json` (`@css/root` covers everything) |
