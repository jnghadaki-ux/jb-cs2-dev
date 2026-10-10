# Build, install and test

## 1. Versions

| Component | Version | Notes |
|---|---|---|
| CounterStrikeSharp (recommended) | **1.0.376** (any 1.0.370+) | .NET 10 runtime (`net10.0` build, full feature set incl. ray traces) |
| CounterStrikeSharp (legacy) | **1.0.368** or older | .NET 8 runtime (`net8.0` build; no ray-trace API → see COMPATIBILITY.md) |
| MetaMod:Source | 2.0 (build 1474+ for current CS2) | install the build that matches your CS2 server update |
| .NET SDK to build | 10.0 (builds both targets) | 8.0 SDK can build only `net8.0` |

Pick the package that matches the CounterStrikeSharp you install; never mix them.

## 2. Build

```bash
dotnet restore
dotnet build -c Release            # builds net8.0 and net10.0
dotnet test  -c Release            # 21 unit tests (round state machine, storage, targets, config)
./build.sh                         # (Windows: build.ps1) -> artifacts/SwbsJailbreak-<ver>-net10.zip and -net8.zip
```

Expected: `0 Warning(s) 0 Error(s)`, tests `Passed: 21`. Each zip holds `addons/counterstrikesharp/plugins/SwbsJailbreak/`
(`SwbsJailbreak.dll`, `.deps.json`, `.pdb`, `lang/en/*.json`). CounterStrikeSharp itself is **not** bundled (the package
reference is compile-only).

## 3. Install on a CS2 dedicated server

1. Install MetaMod:Source into `game/csgo/addons/metamod` and add the `Game csgo/addons/metamod` line to `gameinfo.gi`.
2. Install CounterStrikeSharp **with runtime** (1.0.376 for the net10 package) into `game/csgo/addons/counterstrikesharp`.
3. Unzip `SwbsJailbreak-<ver>-net10.zip` into `game/csgo/` (it creates `addons/counterstrikesharp/plugins/SwbsJailbreak`).
4. Recommended server cvars (put in `cfg/server.cfg`; the plugin also forces the first few at map start):
   ```
   mp_autoteambalance 0
   mp_limitteams 0
   mp_force_pick_time 999999
   mp_do_warmup_period 0
   mp_give_player_c4 0
   mp_teamname_1 Guards
   mp_teamname_2 Prisoners
   mp_roundtime 10
   mp_freezetime 3
   sv_disable_immunity_alpha 1
   ```
5. Admins: CounterStrikeSharp `configs/admins.json`. Default capability → flag mapping is in `Core/Perms.cs` and can be
   changed in the plugin config (`General.Permissions`). Root (`@css/root`) can open/edit cells; `@css/generic` admin
   commands; `@css/ban` CT bans; `@css/unban` `!daysedit`; `@css/config` LR/rebel points.
6. Start the server, then check the console for `SWBS Ultimate Jailbreak v0.9.0 loaded: <n> modules` and `css_plugins list`.
7. Config is generated at `addons/counterstrikesharp/configs/plugins/SwbsJailbreak/SwbsJailbreak.json`. Data (bans, CT bans,
   points, play time) lives in `addons/counterstrikesharp/configs/plugins/SwbsJailbreak/data/` and per-map documents (cell
   doors, LR/rebel points, enabled days) in `addons/counterstrikesharp/configs/plugins/SwbsJailbreak/maps/`.
8. Reload after editing config: `css_plugins reload SwbsJailbreak` (or change map). Phrases: `css_jb_reload`.

## 4. First-time map setup (once per jailbreak map)

| What | How |
|---|---|
| Cell doors | Usually automatic (original name lists or auto-detect). Check with root: `!celldoors_edit` → look at doors, "Add/Remove", "Save doors.". Console alternative: `css_celldoors_list`, `css_celldoors_add <#>`, `css_celldoors_save`. |
| LR teleport spot | Stand in the LR area: `!lr_setpoint` |
| Rebel / free-for-all spawns | Walk around and `!rebelpoint_add` many times (free-for-all days need 90 by default, `Days.FreeForAllMinRebelPoints`) |
| Disabled days for the map | `!daysedit` |

## 5. Tests

* Level 1 (automated): `dotnet test` — see TEST_PLAN.md §1.
* Level 2/3 (server + in game): TEST_PLAN.md §2–§3. Run them on the owner's CS2 server; record results in TEST_PLAN.md.
