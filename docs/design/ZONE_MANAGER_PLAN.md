# Zone Manager remake — plan (owner approved; not built yet)

Original: `Libraries/ZoneManager/API_ZoneManager.sp` (3,227 lines) + `zone_manager.inc` + 9 zone types
(`Blockade`, `DamageBlocker`, `Flashlight`, `HideToggle`, `Kill`, `Named`, `Respawn`, `Teleport`, `WeaponStrip`; 1,701 lines).
It stored per-map zones in SQL, spawned real trigger entities, let admins create/edit zones in a menu, let plugins register
zone *types* with touch callbacks, and kept per-zone int/string data. The jailbreak used only the Teleport-destination type
(LR teleport spot, CTF spawns). Our plugin currently replaces that with `!lr_setpoint`, `!rebelpoint_add`, `!ctf_set*`.

## Design for CS2 / CSS 1.0.368 (no ray trace, no SDKHooks touch)
* **Detection by polling**, not trigger entities: spawning `trigger_*` from managed code is unverified on CS2, while a
  box test against player origins is exact, testable offline and cheap (≤16 Hz, only zones that have listeners).
* `ZoneGeometry` (pure C#, unit-tested): axis box + yaw, point-in-zone, distance, overlap, random point inside.
* `ZoneService` (`Jb.Zones`): registry per map, JSON in `maps/zones/<map>.json`, typed zones, per-zone int/string data,
  events `Created/Removed/Enter/Exit`, query `Inside(zone)` / `ZonesAt(point)`, `RandomPoint(zone)`.
* `IZoneType` registration (name, data schema, enter/exit/tick callbacks), so modules add types without touching the core.
* **Editor** (`!zones`, capability `zones`): create by standing at / pinging two corners (real beams show the box),
  move, resize, rotate, rename, set type + data, delete, teleport to; all drawn with real beam entities.
* Types, phase 1: Teleport destination, Named, Kill, DamageBlocker, WeaponStrip. Phase 2: Respawn, HideToggle, Blockade
  (as a movement restrictor that pushes players back), Flashlight dropped (no CS2 meaning).
* **Migration**: keep `!lr_setpoint`, `!rebelpoint_add`, `!ctf_set*` working; on load, also read them as zones of type
  Teleport/Named so existing per-map data is not lost; no data format breaks.
* Enables later: cell-block turf (Crews), armory/zone-restricted days, "near cells open" checks, warden `!count` in a zone.

## Acceptance
Build 0/0; unit tests for geometry + persistence + enter/exit event sequencing (incl. disconnect, death, round reset);
manual test: create a Teleport zone, LR teleports into it, zone survives map change; no timers/handlers left after reload.

## Risks
CS2 entity-free zones cannot block movement physically (Blockade is a push-back approximation); ping-based placement
needs the CS2 ping key; coordinates assume the map is not rotated at runtime.
