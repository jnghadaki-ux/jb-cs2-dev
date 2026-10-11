# Feature gaps, engine limits and open questions

## Not ported yet (18 jailbreak-relevant) — original size in lines
(Ported in session 3: CommandList → `!commands`, HidePlayers → `!hide`.)
| # | Plugin | What it does (original) | Commands | Lines | CS2 approach |
|---|---|---|---|---:|---|
| 1 | AFKManager | moves/kicks players who go AFK (3 cvars) | — | 239 | tick/button check, move to spec then kick; skip warden/LR players |
| 2 | RotatingMessages | rotating chat messages from a file | `sm_reloadmessages` | 209 | timer + JSON list |
| 3 | ServerRules | shows server rules | `sm_rules` | 47 | chat lines / link (no MOTD browser in CS2) |
| 4 | Information | shows server info | `sm_info` | 46 | chat |
| 5 | InfoMenus | menus listing URLs | — | 200 | JbMenu + chat links |
| 6 | HelpMenu | menu linking to help pages | `sm_helpmenu`, `sm_reloadhelpmenu` | 202 | JbMenu + chat links |
| 7 | MOTD | the Swoobles MOTD page | — | 132 | chat link (CS2 has no MOTD browser) |
| 8 | ShowSpectators | shows who is spectating a player | `sm_speclist` | 161 | HUD layer |
| 9 | SpectateMenu | menu to spectate players | `sm_specmenu` | 150 | JbMenu + observer target |
| 10 | NoTextSpam | stops chat text spam | — | 123 | chat listener |
| 11 | RadioSpamBlock | blocks radio command spam | — | 71 | command listener |
| 12 | StopMapMusic | per-player map music mute/volume | `sm_music`, `sm_pitch`, `sm_stopmusic`, `sm_stopsound`, `sm_volume` | 324 | CS2 music is sound events — needs in-game investigation |
| 13 | StopWeaponSounds | per-player weapon sound toggle | `sm_stopsound` | 123 | sound-event filter — needs in-game investigation |
| 14 | UnlimitedReserveAmmo | reserve ammo refilled (500 on reload) | — | 49 | reload event / ammo property |
| 15 | DisableFallDrownDamage | blocks fall and drown damage (+ damage sounds) | — | 60 | `DamageService` handler |
| 16 | FixGravityOnNewRound | resets `sv_gravity 800` each round and fixes player gravity on spawn | — | 70 | partly covered by `Movement`; add the cvar reset |
| 17 | MapArmour | per-map armour on/off | `sm_armour`, `sm_noarmour` (root) | 169 | per-map JSON flag + Settings loadout |
| 18 | Parachute | parachutes (also used by the LR API: saved, removed during an LR, restored after) | `sm_giveparachute`, `sm_removeparachute` | 218 | glide while holding USE; hook into LR start/end |

Out of scope for now: Store / donator items / UserPoints economy (`IPointsService` hook exists).

## eGo features our plugin lacks (reference comparison, session 3)
Source: decompile of the eGo release (21 modules, ~22k lines) — behaviour only, nothing copied. These are **not** in the
original SMPlugins, so each is an optional addition; adopt by owner choice and make it config-toggleable.
"Effort": S = hours, M = a day, L = several days. All are CSS 1.0.368 feasible unless noted.

| # | eGo feature | What it does | Ours today | Effort |
|---|---|---|---|---|
| 1 | Rebel tracking (`Rebel`) | T who hurt a CT outside an LR is marked a rebel (turns red for a set time), pardon by warden/admin; Last Guard marks all Ts rebels | none (only LR "Rebel" categories) | M |
| 2 | Warden `!open` / `!o` | eGo: warden opens cells | **Done as ADMIN-ONLY** (owner decision): `!open` / `!o`, capability `cells.open` = @css/generic, cooldown `Cells.AdminOpenCooldown`; root-only `!cdo` unchanged | done |
| 3 | `!pass <player>` / `!fire` | warden hands warden to a chosen player; admin forces a pass | `!uw`, admin `!rw` | S |
| 4 | `!peace [seconds]` | only the warden may talk for N seconds | none | S |
| 5 | `!count` | counts prisoners inside the warden's marker | none | S |
| 6 | Special treatment `!st` / `!treat` | warden flags a prisoner as privileged (exempt from rebel rules, icon) | none | M |
| 7 | `!chicken`, `!soccer` / `!spawnball` | warden spawns a chicken / a soccer ball (capped) | none | S |
| 8 | Auto warden, queue "favor" | auto-assign a warden when nobody queued | **Done**: `Warden.AutoWarden` (default on) + `AutoWardenMode` roundrobin/random; the original already fell back to a round-robin guard. Queue "favor" not done | done / favor open |
| 9 | Warden bonuses | extra HP/armor | **Done, only with few guards** (owner rule): with ≤ `Warden.BonusMaxGuards` (2) guards alive, +`BonusHpPerStep`/`BonusArmorPerStep` per step (2 alive = 1 step, 1 alive = 2 steps, stacks as guards die; optional speed); none above the limit | done |
| 10 | Marker shapes + customization | 10 shapes (circle…star, heart, jelly bean, among-us), 9 colours, resizable, `!markertype` / `!markercolor` | rings (4 colours, sizes) + pointer/line | M |
| 11 | Warden paint trail | warden draws in the air with a (rainbow) beam trail | paintball splats on bullet impacts | M |
| 12 | Icons above heads | world-text icon over the warden, special-treatment prisoners, bomb carrier | none (clan tag / HUD only) | M |
| 13 | Logs: button + breakable events | `!logs` also records who pressed `func_button` / broke `func_breakable` | round/kill/LR events only | S |
| 14 | LR: Coinflip, Rock-Paper-Scissors | menu-only duels | not in the 25 LRs | S |
| 15 | LR: plain Knife Fight; LR time limit; `!rebel` during LR | | only Knife Backstab; slay timers; Rebel category | S |
| 16 | Days: BHop, Custom, Fog, Ghost, GunGame, HE, Hide & Seek, Teleport, Speedrun | 9 extra special days | not among the 23 (Hide & Seek exists as an original SWBS mod) | **later** (owner) |
| 17 | Days: Rocket Jump | rocket-jump FFA | RPG day blocked (no trace) | blocked |
| 18 | Day rules | cooldown rounds between days, must start within N s of round start, random "shuffle bag" pick, zone/cell/armory-restricted days | warden 15 s window only | M |
| 19 | RTD (`!rtd`, `!rtdstats`, auto-RTD) | roll the dice at round end / while dead: ammo, armor, HP, bomb, fake bomb, colour, transparent, random teleport, chat spy, guaranteed warden, no-weapon, cannot-scope/use/knife, credits | none | M |
| 20 | Zone manager | box / sphere / hull zones in SQL, random zone generator, movement restrictor, "near open" test | **Done: SMPlugins ZoneManager remade** (Zones module, 12 types incl. Blockade push-back, Notify, Cell Block) | done |
| 21 | Player trails, rainbow colourizer | cosmetic beam trails and colour cycling | none | M |
| 22 | `css_debug` test commands | end round, force LR / last guard, mark rebel / ST, pardon, set time, RTD, zone — a tester's toolbox | none (would speed up the v1 test) | S |
| 23 | Gangs + perks, credits, MySQL stats | external economy | **Done differently: Crews** (respect earned by play, turf, favors, Crew War day) — docs/design/CREWS.md | done |

Also in the owner's earlier 12-plugin attempt (not eGo): Hot Potato / Sumo / Fruit Ninja LRs, CT queue, round-flow cvars,
contraband, Discord logging, SQLite leaderboards.

Where ours is ahead of eGo: 25 LRs (eGo 8), 23 days (eGo 14), effects, CT bans + team ratio, cell-door editor, Jihad, weapon
selection, 71 admin commands, last-guard beacons.

## Zone Manager / Crews limits
* Zones detect players by polling hull overlap (16 checks/s, blockades every tick), not engine touch events; grenades and other
  entities do not trigger zones (the original's touch also fired for props; no jailbreak type used that).
* Blockade cannot be a real solid wall from managed code: players are pushed back (swept test against tunnelling); thrown
  grenades and bullets pass through.
* Flashlight and Hide Toggle zone types are not registered (no CS2 flashlight effect; Hide Toggle could reuse the `!hide` CheckTransmit code, not done yet).
* Crew respect / turf need real players: nothing is earned below `Crews.MinPlayersForRespect` (6) humans online.

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
