# Feature gaps, engine limits and open questions

## Not ported yet (planned)
| Item | Priority | Note |
|---|---|---|
| AFKManager, RotatingMessages, ServerRules/Information/HelpMenu/CommandList/MOTD | Medium | Chat/menu based; rules pages open as chat links (CS2 has no MOTD browser). |
| ShowSpectators, SpectateMenu, NoTextSpam, RadioSpamBlock, StopMapMusic, StopWeaponSounds | Medium | |
| Parachute, UnlimitedReserveAmmo, DisableFallDrownDamage, FixGravityOnNewRound, MapArmour | Low | |
| Store / donator items / UserPoints economy | Out of scope for now | `IPointsService` hook exists. |

## CS2 engine / API limits (cannot be reproduced 1:1)
| Original feature | CS2 status | What the port does |
|---|---|---|
| Third Person effect | Unsupported | Not registered. |
| Round-end screen overlay (r_screenoverlay) | Unsupported | Centre banner + short screen tint. |
| Custom Swoobles models (prisoners, finish line, rocket launcher, missile, crate) | Not loadable (CS:GO .mdl) | Stock CS2 models or beams; config accepts workshop `.vmdl` paths when the server mounts an addon. |
| Custom mp3 sounds (LR activated, kiss, akbar, cowbell, win music, RPG) | Need a soundevent addon | Stock CS2 sound events by default; every sound is a config value. |
| Breach charge (jihad) | Removed from CS2 | C4 item (visible on back / radar). |
| Drunk camera roll | No server camera control | FOV swing + fade + shake. |
| Per-player legacy fog (Horror Movie) | Uncertain on CS2 renderer | Uses the per-player fog controller handle; verify in game. |
| SDKHooks touch / trace attack / weapon equip hooks | Not in CSS | Position polling (crates, knives, finish line), damage pipeline, CanAcquire hook, state checks. |
| Damage from code (SDKHooks_TakeDamage) | No managed CTakeDamageInfo constructor | Direct health changes + kill with death-event rewrite (kill feed credit). Non-lethal code damage fires no player_hurt. |
| Ray traces on CSS ≤ 1.0.368 (net8 build) | Not available | Real positions only (player origin / crosshair-direction entity pick); RPG day disabled on net8. |

## Needs in-game confirmation (unverified assumptions)
* Default CS2 asset paths: player agent models, `weapon_knife_default_ct.vmdl`, dust crate model, particle and sound event names.
* `item_heavyassaultsuit` availability with `mp_weapons_allow_heavyassaultsuit 1`.
* Hiding player pawns with CheckTransmit during LRs (`LastRequest.HidePlayersDuringLr`) — disable if any client issue appears.
* TerminateRound pre-hook parameter order (used only for the "next round end reason" override).
* `CMsgSosStartSoundEvent.source_entity_index` field (breakable sound filter).

## Missing fundamental systems (for a production server)
* No database: all persistence is JSON files on one server (no cross-server bans/stats).
* No web panel / MOTD pages.
* No live-server verification yet (Level 2/3 tests pending).
