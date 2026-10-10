# Feature gaps, engine limits and open questions

## Not ported yet (19 jailbreak-relevant + 1 optional) — original size in lines
| # | Plugin | What it does (original) | Commands | Lines | CS2 approach |
|---|---|---|---|---:|---|
| 1 | AFKManager | moves/kicks players who go AFK (3 cvars) | — | 239 | tick/button check, move to spec then kick; skip warden/LR players |
| 2 | RotatingMessages | rotating chat messages from a file | `sm_reloadmessages` | 209 | timer + JSON list |
| 3 | ServerRules | shows server rules | `sm_rules` | 47 | chat lines / link (no MOTD browser in CS2) |
| 4 | Information | shows server info | `sm_info` | 46 | chat |
| 5 | InfoMenus | menus listing URLs | — | 200 | JbMenu + chat links |
| 6 | HelpMenu | menu linking to help pages | `sm_helpmenu`, `sm_reloadhelpmenu` | 202 | JbMenu + chat links |
| 7 | CommandList | lists commands a player may use | `sm_commands` | 77 | built from our registered commands + Perms |
| 8 | MOTD | the Swoobles MOTD page | — | 132 | chat link (CS2 has no MOTD browser) |
| 9 | ShowSpectators | shows who is spectating a player | `sm_speclist` | 161 | HUD layer |
| 10 | SpectateMenu | menu to spectate players | `sm_specmenu` | 150 | JbMenu + observer target |
| 11 | NoTextSpam | stops chat text spam | — | 123 | chat listener |
| 12 | RadioSpamBlock | blocks radio command spam | — | 71 | command listener |
| 13 | StopMapMusic | per-player map music mute/volume | `sm_music`, `sm_pitch`, `sm_stopmusic`, `sm_stopsound`, `sm_volume` | 324 | CS2 music is sound events — needs in-game investigation |
| 14 | StopWeaponSounds | per-player weapon sound toggle | `sm_stopsound` | 123 | sound-event filter — needs in-game investigation |
| 15 | UnlimitedReserveAmmo | reserve ammo refilled (500 on reload) | — | 49 | reload event / ammo property |
| 16 | DisableFallDrownDamage | blocks fall and drown damage (+ damage sounds) | — | 60 | `DamageService` handler |
| 17 | FixGravityOnNewRound | resets `sv_gravity 800` each round and fixes player gravity on spawn | — | 70 | partly covered by `Movement`; add the cvar reset |
| 18 | MapArmour | per-map armour on/off | `sm_armour`, `sm_noarmour` (root) | 169 | per-map JSON flag + Settings loadout |
| 19 | Parachute | parachutes (also used by the LR API: saved, removed during an LR, restored after) | `sm_giveparachute`, `sm_removeparachute` | 218 | glide while holding USE; hook into LR start/end |
| 20 | HidePlayers (optional) | players hide other players | `sm_hide` | 285 | CheckTransmit; only a transitive store dependency |

Out of scope for now: Store / donator items / UserPoints economy (`IPointsService` hook exists).

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
| Ray traces on CSS 1.0.368 (the only target) | Not available | Aim = the player's ping (`player_ping`: engine crosshair point + entity) or bullet impacts; LR "teleport next to" uses the anchor's spot. |
| RPG warday rocket collision | **Blocked** (no trace) | Day not registered. Plan: launcher = real gun with no bullet damage, rocket flies to the shot's `bullet_impact` point; needs `weapon_accuracy_nospread 1` during the day for airborne accuracy. |

## Needs in-game confirmation (unverified assumptions)
* Default CS2 asset paths: player agent models, `weapon_knife_default_ct.vmdl`, dust crate model, particle and sound event names.
* `item_heavyassaultsuit` availability with `mp_weapons_allow_heavyassaultsuit 1`.
* Hiding player pawns with CheckTransmit during LRs (`LastRequest.HidePlayersDuringLr`) — disable if any client issue appears.
* TerminateRound pre-hook parameter order (used only for the "next round end reason" override).
* `CMsgSosStartSoundEvent.source_entity_index` field (breakable sound filter).
* `player_ping` carries the pinged point in x/y/z and the entity index in entityid (CSS 1.0.368 event class verified; game behaviour not).
* Current CS2 game build still compatible with CSS 1.0.368 gamedata (owner to confirm).

## Missing fundamental systems (for a production server)
* No database: all persistence is JSON files on one server (no cross-server bans/stats).
* No web panel / MOTD pages.
* No live-server verification yet (Level 2/3 tests pending).
