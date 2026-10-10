# Architecture

SWBS Ultimate Jailbreak for CS2 — created by **Gdaki**, designed after **HlStriker, Swbs development/lead admins**.

## Shape

One CounterStrikeSharp plugin (`SwbsJailbreak.dll`, folder `addons/counterstrikesharp/plugins/SwbsJailbreak/`)
made of a small **core framework** and many **modules**. Each module corresponds to one or more original
SMPlugins SourceMod plugins and can be disabled in the config without stopping the others.

```
JailbreakPlugin (BasePlugin)
 ├─ lifecycle: Load → config → phrases → storage → core listeners → discover modules (reflection, sorted by Order)
 │             → RegisterAllAttributes(module) → module.OnLoad → OnAllModulesLoaded → save config
 ├─ dispatch:  map start/end, client put-in-server/disconnect, round_prestart/start/freeze_end/end,
 │             player_spawn (next frame, alive T/CT only), player_death, player_team → every module
 ├─ per tick:  Movement (speed/gravity) every tick; HUD + menus at 16 Hz
 └─ unload:    modules (reverse order) → timers → weapon/damage hooks → cvars restored → storage flushed
```

## Core framework (`src/SwbsJailbreak/Core`)

| File | Responsibility | Replaces (SourceMod) |
|---|---|---|
| `JbModule.cs` | Module base: Order, Enabled, lifecycle hooks, config section access | one `.smx` per subsystem |
| `JbPlayer.cs`, `Players.cs` | Single source of per-player state (slot based, never caches entities), typed per-module state bags with automatic per-round reset, player queries | client index arrays in every plugin |
| `Api.cs` | Contracts `Jb.*` (warden, cells, LR, days, effects, weapon selection, settings, last guard, log, jihad, models, teams, points, mute, rebels) with null objects; `JbEvents` | natives + global forwards (`ultjb_*.inc`) |
| `RoundStateMachine.cs`, `Round.cs` | Authoritative round state (Inactive / WaitingForPlayers / FreezeTime / Active / Ended + cells-opened), legal transitions, round-scoped timers, TerminateRound, round time, temp cvars | round_start/round_end bookkeeping scattered across plugins |
| `Config.cs` | One JSON config, one section per module, defaults written back | per-plugin cvars / `.cfg` |
| `Lang.cs`, `Chat.cs` | Per-module phrase files, colour tags, chat/console/centre output, command replies | translations + hls_color_chat |
| `Hud.cs` | Centre HTML compositor (priorities, expiry) | hint text / PrintHintText |
| `Menu.cs` | `JbMenu` (number keys !1-!9 through CSS MenuManager + W/S/E/R) | Menu/Panel |
| `PlayerExtensions.cs` | HP/armour/money, colour/alpha, freeze/noclip, teleport, strip/give, ammo, clan tag, name, aim trace | SetEntityHealth, SetEntityRenderColor, … |
| `Effects.cs` | `Fx` beams/rings/beacon pulse/particles, screen fade/shake, FOV; `Sound` | TE_* temp entities, Fade/Shake usermessages, EmitSound |
| `Damage.cs` | `DamageService`: one OnPlayerTakeDamagePre listener, prioritised handlers | SDKHook_OnTakeDamage in many plugins |
| `Weapons.cs` | Weapon catalogue; `WeaponService` pickup (CanAcquire, hooked only while needed) and drop rules | SDKHook_WeaponCanUse / drop command hooks |
| `Movement.cs` | Stackable speed/gravity multipliers re-applied every tick | m_flLaggedMovementValue / SetEntityGravity |
| `Perms.cs` | Capability → CSS flag mapping, immunity | Admins library / CheckCommandAccess |
| `Storage.cs` | JSON key/value persistence + per-map documents | MySQL Database* libraries, ClientCookies |
| `Targets.cs` | Target strings incl. magic filters | ProcessTargetString + MagicTargetFilter |

## Modules (`src/SwbsJailbreak/Modules`)

See `FEATURE_INVENTORY.md` for the feature-level status and `docs/modules/*.md` for each module's design and test steps.

| Module folder | Original SMPlugins source | Contract |
|---|---|---|
| Warden | UltJB_Warden.sp, UltJB_WardenMenu.sp | `Jb.Warden` |
| LastGuard | UltJB_LastGuard.sp | `Jb.LastGuard` |
| Cells | UltJB_API_CellDoors.sp, UltJB_CellDoors.sp | `Jb.Cells` |
| Logger | UltJB_Logger.sp | `Jb.Log` |
| Mute | UltJB_MuteManager.sp, UltJB_VoiceChat.sp | `Jb.Mute` |
| Jihad | UltJB_Jihad.sp | `Jb.Jihad` |
| WeaponSelection | UltJB_API_WeaponSelection.sp | `Jb.WeaponSelection` |
| Settings | UltJB_Settings.sp | `Jb.Settings` |
| Teams | UltJB_TeamRatio.sp, UltJB_Command_CTBan.sp, TimedPunishments | `Jb.Teams` |
| Points | UltJB_Points.sp | `Jb.Points` |
| PlayerModels | UltJB_PlayerModels.sp | `Jb.Models` |
| LastRequest | UltJB_API_LastRequest.sp + LastRequests/*.sp | `Jb.LastRequests` |
| Days | UltJB_API_Days.sp + Days/*.sp | `Jb.Days` |
| Effects | UltJB_API_Effects.sp + Effects/*.sp | `Jb.Effects` |
| Commands | Commands/UltJB_Command_*.sp | — |
| Admin | SourceMod funcommands/playercommands/basecommands/basecomm/basebans + owner's SimpleAdmin | — |
| Extras | SMPlugins `Plugins/*` used on the jailbreak server (BhopCheck, AFK, NoBlock, …) | — |

(Modules are being implemented in waves; this table lists the intended layout. Check `PROJECT_STATUS.md` for what
currently exists.)

## Design rules

* Modules talk only through `Jb.*` and `JbEvents`; a disabled module leaves a null object behind.
* No entity/controller references kept across frames; every timer/callback re-validates the player.
* Everything round-scoped is torn down on round end (round timers, HUD layers, beams, pickup rules, movement keys).
* Every networked field write is followed by `Utilities.SetStateChanged`.
* No text stand-ins for in-world effects (see DECISIONS.md D10).
* The plugin targets net8.0 only (CSS 1.0.368, DECISIONS.md D13). There is no managed ray trace on that API line:
  aimed features read real engine aim data from `Core/Aim` (last `player_ping` per player: point + pinged entity) or
  `bullet_impact`, never invented points.
