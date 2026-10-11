# Jailbreak features from CS:GO / CS:S that never reached CS2 (research, session 3)

Report only (owner rule 8). Nothing here is built. Before building any item, check `FEATURE_INVENTORY.md` (some may
already exist under SMPlugins names), then get the owner's go-ahead.

## How this was researched
* The research was kept small on purpose (weekly usage 76 %). A 9-agent sweep was started and then stopped by the owner.
* What was actually checked:
  * the README of destoer's CS2 jailbreak plugin (the main open CS2 jailbreak, now **"NO LONGER MAINTAINED"**);
  * a web search on MyJailbreak, which confirmed the project, its MyJailShop shop and its gangs threads.
* The CS:GO/CS:S feature list comes from what is publicly known about the main SourceMod families:
  * **SM_Hosties** (dataviruset), the source of the classic last requests (LRs);
  * **MyJailbreak** (shanapu), which bundles Franug's Special Jailbreak and other warden plugins;
  * **MyJailShop**, plus jail gang plugins;
  * destoer's CS:GO/CS:S jailbreak.
* **Source:** AlliedModders pages cannot be fetched from this container, so treat the CS:GO/CS:S side as recalled knowledge, not freshly re-read source.
* **CS2 side:** the "never ported" evidence is destoer's own unported to-do list. That list names T laser, auto unstuck, handicap, warden ring, the Crash and Sumo LRs, LR beacons, Laser Wars and Zombie. eGo is closed and commercial; our comparison with it is in `FEATURE_GAPS.md`.

## Candidates

| # | Feature (origin) | What it does | CS2 / CSS 1.0.368 feasibility | Effort | Value |
|---|---|---|---|---|---|
| 1 | Prisoner requests (MyJailbreak "Request") | `!refuse` (limited per round, shown to the warden), `!capitulate` (surrender: the prisoner is stripped and becomes safe), `!heal` request, `!repeat` (ask the warden to repeat an order) | Feasible: chat, HUD, glow and menus already exist | S | High |
| 2 | Last guard rule (MyJailbreak) | When one guard is left, they may announce "last guard", get HP and free kill rights, and all prisoners become rebels | Feasible: guard count plus a menu or vote | S | High |
| 3 | Freekill report and kill log (Hosties family, jail report plugins) | `!freekill` lets the victim report their killer. Admins get a menu with the last N kills (who, weapon, the victim's rebel state, the warden's orders active then) and can slay, respawn or warn | Feasible: kill events plus our rebel state | M | High |
| 4 | Handicap (destoer to-do) | Guard HP or armour scales with the prisoner:guard ratio | Feasible. We have a warden bonus for 1–2 guards (partial) | S | Med |
| 5 | Auto unstuck (destoer to-do) | `!stuck` / automatic: a player stuck in another player or a prop is nudged to free space | Feasible with care: no trace, so test free spots with hull overlap against players and zones only (walls cannot be tested) | S | Med |
| 6 | Sumo LR (destoer CS:GO) | Knife-less push fight on a platform; the first to fall off or leave the ring loses | Feasible: a ring zone (Zones), knockback by velocity on hit (DamageService) | M | Med |
| 7 | Crash LR (destoer CS:GO) | Both players get one object or projectile to hit the other; the first hit wins | Substitute: needs a design check (uses projectiles or decoys, no trace) | M | Low |
| 8 | LR beacons (Hosties) | Pulsing ring and a beep on both LR players so everyone can find them | Feasible: Fx.Ring plus EmitSound (check if our LR already does this) | S | Med |
| 9 | Laser Wars day (destoer to-do) | Everyone gets a laser gun; the beam is drawn and kills | Substitute: real gun, beam drawn from the eye to the shot's `bullet_impact` point. Same pattern as the planned RPG fix | M | Med |
| 10 | Zombie day (MyJailbreak, destoer to-do) | Guards are high-HP knife zombies with speed and gravity changes; prisoners get guns; the infected turn | Feasible (check first that it is not already among our 22 days) | M | High |
| 11 | MyJailbreak event days | Catch & Freeze (guards freeze prisoners, prisoners unfreeze each other), Torch Relay (the burning player passes the fire on), Drunk, Duck Hunt (guards with pump shotguns vs fast prisoners), Cowboy (revolvers), One in the Chamber, Arms Race, Zeus, No-scope, Ghosts (invisible), Deal Damage (team damage total wins), Suicide Bomber | Mostly feasible: speed, gravity, render alpha, burn (`IgniteEntity`, verify), weapons. Ghosts uses alpha or CheckTransmit hiding. Check overlap with our days first | S–M each | Med |
| 12 | Day vote and day cooldown (MyJailbreak) | Prisoners or guards vote the next day; each day has a rounds cooldown and a player minimum | Feasible: menus plus `DayDefinition.CanStart` (exists) | S | Med |
| 13 | Math / first-to-answer quiz (MyJailbreak warden) | The warden starts a quiz; the first correct chat answer wins (freeday, or picks the next order) | Feasible: the ChatInput pattern, but listening to everyone | S | Med |
| 14 | Countdown / timer tool (MyJailbreak warden) | The warden starts a visible countdown (start or stop) for orders | Feasible: HUD (check whether the warden menu already has it) | S | Med |
| 15 | Warden mic priority / timed mute of prisoners (Hosties mute, MyJailbreak) | Prisoners are muted for the first N seconds of the round; dead players are muted; the warden can mute prisoners for a while | **Feasible (verified):** CSS 1.0.368 has `CCSPlayerController.VoiceFlags` (enum `VoiceFlags`) and `SetListenOverride(sender, ListenOverride)` | S | High |
| 16 | Prisoner painter (MyJailbreak "T painter") | The warden can let prisoners paint for a game | Feasible: our paint already uses `bullet_impact` | S | Low |
| 17 | Warden ring / T laser (destoer to-do) | A ring under the warden; the warden can lend the laser pointer to a prisoner | Feasible: Fx.Ring, our ping pointer | S | Low |
| 18 | Heal zone type | A zone that heals players inside (infirmary) | Feasible: a new zone type (Zones module) | S | Med |
| 19 | Jail shop and credits (MyJailShop, Dkmuniz) | Credits for playing, spent on short perks (HP, speed burst, smoke, brief invisibility) | Feasible, but it **changes balance**, against SMPlugins identity. Crews was deliberately built with no buying | M | Low (skip unless the owner wants it) |
| 20 | Jail levels / RPG | XP and level perks | Feasible, balance concern, same as #19 | M | Low |
| 21 | Classic Hosties LRs we may lack | Chicken Fight (jump on the head), Hot Potato (hold the deagle, last holder dies), Russian Roulette, Rock-Paper-Scissors, Mag4Mag, Jumping contest | Feasible (no trace needed). **Compare with our 25 LRs first**; several are probably already there under SMPlugins names | S–M each | Med |

### Do not port
* Custom player or arms models, knives or skins (GSLT risk; also needs a Workshop addon).
* Anything needing world traces: proper "laser kill" detection, wall checks.

## Suggested shortlist (if the owner approves)
1. Prisoner requests (#1), last guard rule (#2), freekill report and kill log (#3): core jailbreak flow that CS2 servers lack today.
2. LR beacons (#8), Sumo LR (#6), Zombie day (#10) if not already present.
3. Warden mic priority / timed prisoner mute (#15): the voice API is confirmed in CSS 1.0.368.
