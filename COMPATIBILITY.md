# Compatibility

Last updated: 2026-10-10 (session 3: .NET 8 only). Nothing below has been tested on a live CS2 server yet — see TEST_PLAN.md.

## Verified facts (with evidence)

| Item | Finding | Evidence |
|---|---|---|
| CounterStrikeSharp 1.0.376 runtime | Targets **.NET 10** (`tfm net10.0`, `Microsoft.NETCore.App 10.0.0`, `rollForward Major`) | `addons/counterstrikesharp/api/CounterStrikeSharp.API.runtimeconfig.json` inside the uploaded `counterstrikesharp-windows-1.0.376.zip` |
| CSS NuGet packages ≤ 1.0.368 | Ship `lib/net8.0` only | Downloaded `.nupkg` for 1.0.330, 1.0.346, 1.0.355, 1.0.362, 1.0.364, 1.0.366, 1.0.368 |
| CSS NuGet packages ≥ 1.0.370 | Ship `lib/net10.0` only | Downloaded `.nupkg` for 1.0.370, 1.0.372, 1.0.374, 1.0.375, 1.0.376 |
| Latest stable CSS on NuGet (2026-10-10) | 1.0.376 (a `2.0.0-alpha.1` also exists; not targeted) | `api.nuget.org/v3-flatcontainer/counterstrikesharp.api/index.json` |
| eGo Jailbreak release | Built for `.NETCoreApp,Version=v10.0` against CSS 1.0.376 | `Jailbreak.deps.json` in the uploaded eGo release |
| Ray trace API (`Modules.Utils.Trace`, `TraceOptions`, `Contents`) | Present in 1.0.376, **absent** in 1.0.368 | Decompiled both API assemblies |
| `player_ping` game event (`EventPlayerPing`: userid, entityid, x, y, z, urgent) | Present in 1.0.368 | Decompiled 1.0.368 API (`CounterStrikeSharp.API.Core/EventPlayerPing.cs`) |
| `CCSPlayerController.ReplicateConVar(conVar, value)` | Present in 1.0.368 (not needed: per-player auto-bhop is done server-side by `Movement.SetAutoBhop`) | Decompiled `CCSPlayerController.cs` |
| `Server.TickCount`, `Server.TickInterval` (1/64), `Server.TickedTime` | Present in 1.0.368 | Decompiled `Server.cs` |
| `CUserMessageFade` screen fade (`UserMessage.FindIdByName`) | Present in 1.0.368, wrapped by `Fx.Fade` (Hide & Seek seekers' black screen) | Decompiled `Modules/UserMessages`; `Core/Effects.cs` |
| `func_button` inputs `Unlock` + `Press` via `AcceptInput` | Used by cell doors and map game buttons (same call); runtime unverified | `Modules/Cells/CellsModule.cs` |

## Target: .NET 8 only (owner decision D13)

The owner's servers run CounterStrikeSharp on .NET 8 (the "with runtime" package bundles .NET 8). The plugin therefore
targets **`net8.0` only**, compiled against **CounterStrikeSharp.API 1.0.368** (the last CSS release built for .NET 8).
There is no `net10.0` build any more; do not add one.

Consequence: CSS 1.0.368 has no managed ray-trace API. Aimed features use real engine data instead:

| Feature | Aim source on CSS 1.0.368 |
|---|---|
| Warden pointer laser | The warden's **ping** (stock CS2 `player_ping` key, middle mouse by default) shows the pointer there; double-tap USE uses the newest ping or bullet impact |
| Warden menu rings / healing area | Recent ping (≤ `Warden.PingMaxAge` s), else the warden's feet |
| Warden line / gesture ring / paintballs | Bullet impacts (`bullet_impact`), as in the original |
| Cell door editor | Pinged entity (or nearest door within 64 u of the ping), else the door nearest the crosshair direction |
| LR "teleport next to" | Anchor's own spot (no hull trace for a free spot; players do not collide) |
| RPG warday | **Blocked**: rockets need a world-collision point. Planned: the engine's bullet trace (`weapon_fire` + `bullet_impact`). |

## Target server stack (recommended)

| Component | Version | Notes |
|---|---|---|
| Counter-Strike 2 dedicated server | current Steam build | CSS gamedata must match the game build. |
| Metamod:Source | 2.0 dev build matching the CS2 update (2.0.0-git1474 was uploaded) | 1.12 is for Source 1 games and will not work with CS2. |
| CounterStrikeSharp | **1.0.368 with runtime** (or another .NET 8 CSS build ≤ 1.0.368) | Bundles .NET 8. |
| This plugin | `net8.0` build (`SwbsJailbreak-<ver>-net8.zip`) | `addons/counterstrikesharp/plugins/SwbsJailbreak/` |

rver has no .NET 10 runtime installed, download the with-runtime zip from the CSS GitHub releases page. |
| This plugin | `net10.0` build | `addons/counterstrikesharp/plugins/SwbsJailbreak/` |

## Not yet verified (needs a live server)

* That the plugin loads on CSS 1.0.368 (Windows and Linux) — and whether CS2's current game build still works with
  CSS 1.0.368 gamedata (owner to confirm on the test server).
* That `player_ping` fires for every ping with the pinged point in x/y/z and the entity index in entityid.
* Every gameplay feature — see TEST_PLAN.md.
* User-message ids for screen fade/shake are resolved at runtime by name (`CUserMessageFade`, `CUserMessageShake`)
  with fallbacks 106/120; the fallbacks are unverified.
