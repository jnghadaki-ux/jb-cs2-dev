# Compatibility

Last updated: 2026-10-10 (session 1). Nothing below has been tested on a live CS2 server yet — see TEST_PLAN.md.

## Verified facts (with evidence)

| Item | Finding | Evidence |
|---|---|---|
| CounterStrikeSharp 1.0.376 runtime | Targets **.NET 10** (`tfm net10.0`, `Microsoft.NETCore.App 10.0.0`, `rollForward Major`) | `addons/counterstrikesharp/api/CounterStrikeSharp.API.runtimeconfig.json` inside the uploaded `counterstrikesharp-windows-1.0.376.zip` |
| CSS NuGet packages ≤ 1.0.368 | Ship `lib/net8.0` only | Downloaded `.nupkg` for 1.0.330, 1.0.346, 1.0.355, 1.0.362, 1.0.364, 1.0.366, 1.0.368 |
| CSS NuGet packages ≥ 1.0.370 | Ship `lib/net10.0` only | Downloaded `.nupkg` for 1.0.370, 1.0.372, 1.0.374, 1.0.375, 1.0.376 |
| Latest stable CSS on NuGet (2026-10-10) | 1.0.376 (a `2.0.0-alpha.1` also exists; not targeted) | `api.nuget.org/v3-flatcontainer/counterstrikesharp.api/index.json` |
| eGo Jailbreak release | Built for `.NETCoreApp,Version=v10.0` against CSS 1.0.376 | `Jailbreak.deps.json` in the uploaded eGo release |
| Ray trace API (`Modules.Utils.Trace`, `TraceOptions`, `Contents`) | Present in 1.0.376, **absent** in 1.0.368 | Decompiled both API assemblies |
| Native damage listeners (`OnPlayerTakeDamagePre/Post`), `OnPlayerButtonsChanged`, `OnPlayerChat` | Present in both 1.0.368 and 1.0.376 | Both builds compile against them |

## What this means for the owner's ".NET 8" requirement

The owner reported that plugins only ran on .NET 8. That is correct **for CounterStrikeSharp 1.0.368 and older**.
CounterStrikeSharp 1.0.370 and newer (including the 1.0.376 build supplied for this project) run on .NET 10.

The plugin is therefore multi-targeted from one source tree:

| Build | Compiled against | Install on servers running |
|---|---|---|
| `net8.0` | CounterStrikeSharp.API 1.0.368 | CSS ≤ 1.0.368 (.NET 8 runtime). Should also load on newer CSS because .NET 10 runs .NET 8 assemblies, but this is not verified. |
| `net10.0` | CounterStrikeSharp.API 1.0.376 | CSS 1.0.370 – 1.0.376 (.NET 10 runtime). **Recommended**, has the ray-trace features. |

Feature difference between builds: the warden laser/paint/aim-placed markers use the ray-trace API and are only
aim-accurate in the `net10.0` build; the `net8.0` build places those markers at a real fallback position
(the warden's own position or the last bullet impact) and says so in chat.

## Target server stack (recommended)

| Component | Version | Notes |
|---|---|---|
| Counter-Strike 2 dedicated server | current Steam build | CSS gamedata must match the game build; update CSS when Valve updates CS2. |
| Metamod:Source | 2.0.0-git1474 (uploaded) | CS2 requires the 2.0 dev branch. 1.12 (also uploaded) is for Source 1 games and will not work with CS2. |
| CounterStrikeSharp | 1.0.376 **with runtime** build | The "with-runtime" zip bundles .NET 10. The uploaded zip is the no-runtime build; if the server has no .NET 10 runtime installed, download the with-runtime zip from the CSS GitHub releases page. |
| This plugin | `net10.0` build | `addons/counterstrikesharp/plugins/SwbsJailbreak/` |

## Not yet verified (needs a live server)

* That the plugin loads on CSS 1.0.376 (Windows and Linux).
* That the `net8.0` build loads on CSS 1.0.376 (cross-runtime).
* Every gameplay feature — see TEST_PLAN.md.
* User-message ids for screen fade/shake are resolved at runtime by name (`CUserMessageFade`, `CUserMessageShake`)
  with fallbacks 106/120; the fallbacks are unverified.
