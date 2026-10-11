# CS:GO / CS:S jailbreak plugins: features worth reinventing for CS2 (research, session 3)

Report only (owner rule 8): nothing here is built. Before building an item, check it against `FEATURE_INVENTORY.md`
(SwbsJailbreak may already have it under an SMPlugins name) and get the owner's go-ahead.

**Method (kept small: weekly usage 76 %).** The module lists below come from the plugins' real source trees on GitHub
(listing every `.sp` file in each repo, plus the LR list in `hosties/lastrequest.inc` and the item list in
`myjailshop.sp`). Feature descriptions come from the module names and the known behaviour of these widely used plugins.
Each module's code was not re-read line by line. AlliedModders pages cannot be fetched from this container.

Feasibility marks are for CS2 on CounterStrikeSharp 1.0.368:
* **OK** = buildable with existing services (menus, HUD, glow, beams, DamageService, Zones, `player_ping` / `bullet_impact`).
* **Sub** = needs a CS2 substitute.
* **No** = blocked (GSLT risk, custom models, world trace).

---

## 1. SM_Hosties v2: `dataviruset/sm-hosties` (CS:S, then CS:GO)
The original jailbreak base plugin. Modules: `lastrequest`, `checkplayers`, `control`, `freekillers`, `gunsafety`,
`muteprisoners`, `noblock`, `respawn`, `rules`, `startweapons`, `teamoverlays`, `gamedescription`.

| Feature | What it does | CS2 |
|---|---|---|
| Last requests | Knife Fight, Shot4Shot, Gun Toss, Chicken Fight, Hot Potato, Dodgeball, No-Scope, Rock-Paper-Scissors, Race, Russian Roulette, Jump Contest, Mag4Mag, Rebel. Compare with our 25; likely gaps are Chicken Fight, Hot Potato, RPS, Jump Contest, Mag4Mag | OK (Gun Toss distance from the dropped gun's resting origin) |
| Freekillers | Counts guards' kills of non-rebel prisoners. Over the limit, the guard is punished (slay, kick or ban) | OK |
| Gun safety | Guards dropping guns: warnings and anti-abuse of dropped guns | OK |
| Mute prisoners | Prisoners are muted for the first N seconds of the round, and dead players are muted | OK (verified: `VoiceFlags`, `SetListenOverride` exist in 1.0.368) |
| Check players | `!checkplayers` lists rebels, i.e. who may legally be killed | OK |
| Rules | `!rules` menu | OK (already in our gaps list) |
| Team overlays | Screen overlay per team at round start | Sub (HUD message, no overlay materials) |

## 2. MyJailbreak: `shanapu/MyJailbreak` (CS:GO; rewrite of Franug's Special Jailbreak)
The most complete CS:GO jailbreak suite.

**Warden add-ons:**
* backstab protection
* bullet sparks
* cell doors
* colour
* countdown
* counter (counts prisoners in an area)
* **deputy** (second warden)
* **disarm** (shooting a gun out of a hand)
* extend time
* freedays
* friendly fire
* glow
* gun drop
* **handcuffs**
* laser
* marker
* **math quiz**
* mute
* noblock
* **orders** (preset order list)
* painter
* **random kill**
* rebel
* reminder
* **shoot guns**
* **withheld LR**

**Requests:** **refuse**, **capitulation**, **heal**, **repeat**, **freekill report**, **kill reason**.

**Event days:**
* War
* FFA
* Zombie
* Hide
* Catch & Freeze
* Torch Relay
* Drunk
* Duck Hunt
* Cowboy
* Knife
* Noscope
* One in the Chamber
* Arms Race
* HE Battle
* Zeus
* Ghosts
* Suicide (bomber)
* Deal Damage
* Freeday
* Teleport

**Core:** last guard, ratio (with CT-ban and VIP bridges), player tags, icons, HUD, beacon, fog.

**Add-ons:** gangs, reputation, team games, store-credit bridges, rank bridges, devzones.

| Feature worth reinventing | What it does | CS2 |
|---|---|---|
| Requests: refuse / capitulation / heal / repeat | Refuse a game (limited per round, shown to the warden); surrender (stripped, safe); ask for heal; ask the warden to repeat | OK |
| Freekill report + kill reason | The victim reports a freekill. The killer must give a reason; admins see it | OK |
| Last guard rule | The last guard alive can start "last guard": HP boost, every prisoner becomes a rebel | OK |
| Deputy warden | The warden appoints a second guard with limited warden tools | OK |
| Handcuffs | Guard taser or knife-hit cuffs a prisoner (frozen, can't shoot) until uncuffed | OK (movement freeze + weapon block) |
| Disarm / shoot guns | Shooting a prisoner's hand or gun makes them drop it | Sub (no hitbox data in 1.0.368 → drop on any non-lethal hit, or skip) |
| Math quiz | The warden starts a quiz; the first right chat answer wins | OK |
| Orders | Warden picks from a configurable list of standard orders, broadcast with HUD | OK |
| Random kill | The warden slays a random prisoner (anti-camping, used sparingly) | OK |
| Counter | Counts prisoners inside a marked area for the warden | OK (Zones or ping ring + position check) |
| Withheld LR | The warden can delay or forbid LR for a round | OK |
| Bullet sparks / beacon / fog | Visual helpers; beacon rings on LR players; fog day atmosphere | OK (Fx rings, `env_fog_controller` to be verified) |
| Event days | Catch & Freeze, Torch Relay, Drunk, Duck Hunt, Cowboy, One in the Chamber, Arms Race, HE Battle, Zeus, Ghosts, Deal Damage, Suicide, Teleport | OK mostly (check overlap with our 22 days) |
| Reputation / team games | Player reputation score; team mini-games | OK, low value |

## 3. destoer jailbreak: `destoer/counter_strike_jailbreak` (CS:S and CS:GO; base of his unmaintained CS2 port)
Modules:
* **Core:** gangs, block, circle, colour, cookies, guns, database, laser, mute, **stuck**, warday, markers, CT ban.
* **LRs:** knife fight, shot for shot, gun toss, race, rebel, dodgeball, grenade, no scope, headshot only, russian roulette, scout knife, shotgun war, **sumo**, **crash**, custom, combo key, stats.
* **Special days:** deathmatch, dodgeball, FFD (free-for-all day), FFDG (free-for-all day, GG variant), grenade, gungame, headshot, hide, hook, knife, **laserwars**, scoutknifes, **skywars**, spawn, **spectre**, **tank**, VIP, zombie.

| Feature worth reinventing | What it does | CS2 |
|---|---|---|
| Stuck | `!stuck` frees a player stuck in another player | OK (player-vs-player overlap only, no wall trace) |
| Sumo LR | Push the opponent off a platform or out of a ring | OK (ring zone + knockback) |
| Crash LR | Original concept to be re-read before building | Sub (needs design) |
| Combo key LR | Type a shown key combo fastest | OK |
| Shotgun war / scout knife / headshot-only / grenade LRs | Weapon-restricted duels | OK |
| LR stats | Saved LR wins/losses per player | OK (our Storage) |
| Days: Skywars, Spectre, Tank, VIP, Gungame, Hook, Laser Wars | Skywars: fight high up, fall = dead; Spectre: one invisible knifer vs all; Tank: one huge-HP player; VIP: protect a VIP; Gungame: weapon progression; Hook: grappling hook; Laser Wars: laser guns | OK: Skywars, Spectre, Tank, VIP, Gungame. Sub: Laser Wars (`bullet_impact` beam). No: Hook (needs a trace to the hook point; the ping point could substitute, but it's awkward) |
| Circle / block | Warden circle; block (collision) toggle | Have similar (warden rings, noblock): check |

## 4. MyJailShop: `shanapu/MyJailShop` (CS:GO; redux of Dkmuniz Jail Shop)
Credits system (earned per round or alive win, saved, prisoners only by default, buy time in cells, disabled on LR and
event days). Items:
* weapons: AWP, Deagle, knife, taser, throwing knife
* grenades: molotov, fire grenade, poison smoke, teleport smoke
* health: heal, health, revive
* movement: bhop, froggy jump, gravity, no-clip, random teleport
* stealth: invisible, fake model, wallhack
* other: no damage, vampire, paperclip (handcuff key), open doors, bird
* separate plugins: blackout, door, freeday, HUD, ice grenade, jetpack, jihad, parachute, tactical shield

| Verdict | Reason |
|---|---|
| Not recommended as a shop | Pay-to-win items (wallhack, no damage, invisibility) break SMPlugins balance; Crews was deliberately built with no buying |
| Ideas worth taking without a shop | Paperclip (escape from handcuffs, if handcuffs are built); parachute (already a planned small plugin); fake model is **No** (custom models / GSLT) |

## 5. Ecosystem plugins (CS:GO/CS:S) referenced by the above
* CT bans (databomb, R1KO, addicted), TeamBans (Bara). We have persistent CT bans.
* VoiceAnnounceEx: shows who is talking, used for warden mic priority. OK (voice API exists).
* Gangs plugins: MyJailbreak gangs, destoer gangs. We have Crews (new design).
* Store bridges (Zephyrus store, sm-store), ranks (RankMe, Kento RankMe), Stamm VIP, MostActive. Skip: external economies.

---

## Shortlist (if the owner approves)
1. **Requests:** refuse, capitulation, heal, repeat, plus freekill report and kill reason (MyJailbreak). Core flow.
2. **Last guard rule** (MyJailbreak) and **freekiller limits** (Hosties).
3. **Mute prisoners at round start / warden mic priority** (Hosties, MyJailbreak). The voice API is verified.
4. **Deputy warden, handcuffs, math quiz, orders list, withheld LR** (MyJailbreak warden add-ons).
5. **Missing LRs:** Chicken Fight, Hot Potato, RPS, Jump Contest, Mag4Mag (Hosties); Sumo, Combo Key (destoer). After comparing with our 25.
6. **Days:** Skywars, Spectre, Tank, VIP, Gungame (destoer); Catch & Freeze, Torch Relay, Duck Hunt, Deal Damage (MyJailbreak). After comparing with our 22.

**Do not port:** fake/custom models, knife or skin changes (GSLT); hook and true laser hit detection (no trace in 1.0.368);
shop items that grant wallhack, invisibility or immunity.
