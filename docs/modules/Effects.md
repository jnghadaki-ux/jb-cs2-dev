# Effects module (`Modules/Effects`)

Ports `UltJB_API_Effects.sp` and `Effects/*.sp`. Implements `IEffectService` (`Jb.Effects`). Status:
**Implemented, unverified** unless noted.

* Registry with ids starting at 1 (0 = "No effect"), sorted by name, looked up by name (`GetEffectId`).
* `DisplaySelectionMenu`: menu "Select an effect" (no exit button) with "No effect" + every effect; after
  `SelectEffectTime` (15 s, `ultjb_select_effect_time`) a random effect is chosen; selection announced to all.
* Effects are stopped (cleanly, OnStop called) at round end, on disconnect, and by their LR/day.

| Effect | Original | CS2 implementation | Status |
|---|---|---|---|
| Drugs (data = slap delay 0.75) | Screen fade colour cycle every 0.01 s + 0-damage slap | Same cycle speed at 16 updates/s (configurable) via the Fade user message + `Slap(0)` | Implemented, unverified (fade message id resolved by name at runtime) |
| Drunk | FOV swing 30-150 + camera roll ±45-60 every 0.8 s with dark fade | FOV swing + dark fade at each flip + light screen shake | **Partial** – CS2 server cannot roll the client camera |
| Far Out | FOV 160 | `m_iDesiredFOV` 160 | Implemented, unverified (client may clamp FOV) |
| HS Only (1.5) | Player only damaged by headshots or knife | DamageService handler (priority 95) | Implemented, unverified |
| Low Gravity | gravity 0.4 re-applied every think | Movement gravity multiplier (every tick) | Implemented, unverified |
| Speed (1.5) | m_flLaggedMovementValue | Movement velocity multiplier | **Partial** – CS2 has no lagged-movement scale |
| Third Person | `thirdperson` client command | — | **Unsupported** – cheat-protected client command, no server camera control; not registered |

Test: give a player an effect through an LR that uses effects (Drug Dealer) or via a day; or from a debug build
call `Jb.Effects.DisplaySelectionMenu`. Verify each effect visibly applies and fully reverts at round end.
