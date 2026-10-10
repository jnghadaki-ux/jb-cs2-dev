# SWBS Ultimate Jailbreak (CS2) – Developer Guide

Created by **Gdaki** – designed after **HlStriker**, SWBS development/lead admins.

This plugin is a single CounterStrikeSharp (CSS) plugin (`SwbsJailbreak.dll`) targeting
**CSS API 1.0.376 / .NET 10** running on **Metamod:Source 2.0**. It ports hlstriker's SMPlugins
*Ultimate Jailbreak* (CS:GO, SourceMod) and its companion plugins. Every original SourceMod plugin became a
**module** (`JbModule`) inside this one assembly.

## Layout

```
src/SwbsJailbreak/
  JailbreakPlugin.cs        entry point; discovers every JbModule by reflection, dispatches lifecycle events
  Core/                     shared framework (do not duplicate these in modules)
    JbModule.cs             module base class + lifecycle hooks
    JbPlayer.cs, Players.cs per-slot player wrapper, typed per-module state bags, player queries
    Api.cs                  cross-module contracts (Jb.* service locator) + JbEvents (old forwards)
    Config.cs               single JSON config, one section per module
    Lang.cs, Chat.cs        phrases (lang/en/<module>.json) + chat/console/centre output with {colour} tags
    Hud.cs, Menu.cs         centre-HTML compositor + menu system (!1-!9 and W/S/E/R)
    PlayerExtensions.cs     SetHp/SetArmor/SetColor/Freeze/Noclip/Strip/Give/Teleport/AimPoint ...
    Effects.cs              Fx (beams, rings, beacon pulse, particles, screen fade/shake, FOV) + Sound
    Damage.cs               DamageService (single OnPlayerTakeDamagePre dispatcher)
    Weapons.cs              weapon catalog + WeaponService (pickup / drop rules)
    Movement.cs             stackable speed / gravity multipliers re-applied every tick
    Round.cs                round phase/time, TerminateRound, round-scoped timers, Cvars (temp cvar overrides)
    Perms.cs                capability → CSS flag mapping (config "General.Permissions")
    Storage.cs              JSON key/value persistence + per-map JSON documents
    Targets.cs              SourceMod style target strings (@all @t @ct #userid name + magic filters)
  Modules/<Area>/...        one folder per area (Warden, Cells, LastRequest, Days, Effects, ...)
  lang/en/<module>.json     phrases, one file per module (flat "key": "text")
```

## Writing a module

```csharp
namespace SwbsJailbreak.Modules.Example;

public sealed class ExampleConfig            // section "Example" in SwbsJailbreak.json
{
    public bool Enabled { get; set; } = true;
    public int Seconds { get; set; } = 10;
}

public sealed class ExampleModule : JbModule
{
    private ExampleConfig Cfg => Section<ExampleConfig>("Example");
    public override int Order => 50;               // 0-9 core services, 10-99 gameplay, 100+ extras
    public override bool Enabled => Cfg.Enabled;

    public override void OnLoad(bool hotReload)
    {
        // Jb.SomeService = this;   (if the module implements a contract)
        DamageService.Register(Name, 10, OnDamage);
    }

    [ConsoleCommand("css_example", "Example command")]   // => !example and /example in chat
    [RequiresPermissions("@css/generic")]                // optional; owners can override in admin_overrides.json
    public void OnExample(CCSPlayerController? player, CommandInfo info)
    {
        Chat.ReplyT(player, info, "example.hello", player?.PlayerName ?? "console");
    }

    public override void OnRoundStart() { }
    public override void OnPlayerSpawn(JbPlayer p) { }   // pawn valid, alive T/CT, one frame after spawn
    private HookResult OnDamage(DamageContext ctx) => HookResult.Continue;
}
```

* Modules are discovered automatically – just add the class. Public methods with CSS attributes
  (`[ConsoleCommand]`, `[GameEventHandler]`, `[ListenerHandler<T>]`, `[EntityOutputHook]`) are registered for you.
  Commands registered with `Plugin.AddCommand` at runtime also work.
* Commands are `css_<name>`; CSS automatically maps `!name` (public) and `/name` (silent) chat triggers.
* Never cache `CCSPlayerController`/pawns across frames: keep a `JbPlayer` (or slot) and re-read `.Controller`.
* Per-player module state: `player.Data<MyState>()`; implement `IRoundScoped` to auto-reset every round.
* Round-only timers: `Round.Timer(seconds, action, repeat)` – killed automatically on round end/map change.
* Damage: `DamageService.Register(owner, priority, handler)`; return `HookResult.Handled` to block, or set
  `ctx.Damage`. Priorities: LR 100, days 90, last guard 80, rebels 50, misc 0.
* Pickups/drops: `WeaponService.SetPickupRule(owner, rule)` / `SetDropRule` and remove them when done.
* Speed/gravity: `Movement.SetSpeed(c, key, mult)` / `SetGravity` and `Clear*`/`ClearKeyAll(key)`.
* Server cvars changed for a day: `Cvars.SetTemp(name, value)` / `Cvars.Restore(name)` / `RestoreAll()`.
* Text: put every player-facing string in `lang/en/<module>.json` and use `Chat.Tell(player, key, args)`,
  `Chat.TellAll(key, args)`, `Lang.T(key, args)`. Colour tags: `{default} {green} {red} {lightred} {gold}
  {lightblue} {blue} {darkblue} {purple} {lightpurple} {grey} {yellow} {olive} {lime} {orange} {silver}`.
  `Chat.Name(player)` gives a team-coloured name.
* Centre HUD: `Hud.Set(player, "module.key", html, priority, seconds)` / `Hud.SetGlobal(...)` /
  `Hud.Clear*`. Never call `PrintToCenterHtml` directly (it would fight the compositor and menus).
* Menus: `var m = new JbMenu("Title"); m.Add("Text", p => ...); m.Open(player);` Submenus: set `Parent`.
* Admin checks: `Perms.Has(player, "capability")` with capability names from `Perms.Defaults`
  (`admin, warden.manage, cells, cells.edit, ctban, ctban.permanent, days, lr, mute, teams, slay, fun,
  respawn, rcon, root, immunity, vip, logs, zones`).
* Persistence: `Storage.Get/Set/Delete/All<T>(namespace, key)`; per-map docs `Storage.LoadMapDoc<T>(kind, map)`.
* Cross-module calls go through `Jb.Warden`, `Jb.Cells`, `Jb.LastRequests`, `Jb.Days`, `Jb.Effects`,
  `Jb.WeaponSelection`, `Jb.Settings`, `Jb.LastGuard`, `Jb.Log`, `Jb.Jihad`, `Jb.Models`, `Jb.Teams`,
  `Jb.Points`, `Jb.Mute`, `Jb.Rebels` and the `JbEvents` events (the original plugins' natives/forwards).

## Source 2 pitfalls (checked against CSS 1.0.376)

* Networked fields need `Utilities.SetStateChanged(entity, "Class", "m_field")` after writing.
* Freeze = `MOVETYPE_OBSOLETE` (+ zero velocity); noclip = `MOVETYPE_NOCLIP`; both set `MoveType` and
  `ActualMoveType` (helpers do this).
* Player colour/alpha: `m_clrRender` + `m_nRenderMode = kRenderTransAlpha` (helper `SetColor`/`SetAlpha`).
* Damage hook: CSS native `Listeners.OnPlayerTakeDamagePre` (used by DamageService) – no gamedata hook needed.
* There are no temp entities: beams are real `beam` entities (`Fx.Beam`, `Fx.Ring`, `Fx.BeaconPulse`) –
  always give them a lifetime or remove them.
* Aim position: `VectorUtil.AimPoint(controller)` (CSS `Trace.TraceEndShape`).
* `ChangeTeam` follows game rules (kills the player); `SwitchTeam` keeps them alive with their weapons.
* Respawn: `controller.Respawn()`; round end: `Round.End(winner)`.
* Client cvars (r_screenoverlay, thirdperson, etc.) cannot be set by the server in CS2.

## Build

```
dotnet build -c Release
```
Output: `src/SwbsJailbreak/bin/Release/net10.0/` → copy `SwbsJailbreak.dll` and the `lang` folder to
`game/csgo/addons/counterstrikesharp/plugins/SwbsJailbreak/`. `build.sh` / `build.ps1` produce a ready zip.
