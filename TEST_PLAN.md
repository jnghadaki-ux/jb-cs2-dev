# Test plan

Record each run as `date / tester / CSS version / package / result` (package = net8 build on CSS 1.0.368). A feature becomes **Verified** in
FEATURE_INVENTORY.md only after its row below passed on a real server.

## 1. Level 1 — automated (done by every build)

`dotnet test -c Release` → 35 tests: round state machine (legal/illegal transitions, duplicate round_end/round_start
rejection, map reset, resync), storage round-trip, target matching (@prisoners, #userid, partial names), config defaults.
Status: **passing** (session 2).

## 2. Level 2 — server load (owner)

| # | Step | Expected |
|---|---|---|
| 2.1 | Start server with the package | Console: `SWBS Ultimate Jailbreak v0.10.0 loaded: <n> modules ...` with no `failed to load` lines |
| 2.2 | `css_plugins list` | SwbsJailbreak listed, author "Gdaki (enhanced with Claude Code) - inspired by HlStriker, SWBS development/lead admins" |
| 2.3 | Check `configs/plugins/SwbsJailbreak/SwbsJailbreak.json` | Generated with all sections |
| 2.4 | `css_plugins reload SwbsJailbreak` twice | No errors, commands work once (no duplicate chat lines) |
| 2.5 | `changelevel` to another JB map | Cells log line `[Cells] <map>: source=...`, no exceptions |
| 2.6 | Console: `[Days] RPG warday is not registered` | Expected on CSS 1.0.368 (RPG blocked, see FEATURE_GAPS.md) |

## 3. Level 3 — in game (at least 2 players + bots: `bot_add_t`, `bot_add_ct`)

### Teams / rounds
1. Join with nobody on CT → 30 s "Selecting guards in" countdown, cells force-open, a random/queued prisoner is moved to CT, round restarts.
2. `jointeam 3` as prisoner when the ratio is full → "[SM] Guards team full..." and the restricted sound.
3. `!guard` → queue position message. Round end → ratio fixed (2 prisoners per guard).
4. `!ctban <name> 5 test` → player moved to T and slain; `!check_ctban`; `!unctban`.

### Settings / spawn
5. Guards spawn with knife + Five-SeveN + healthshot + 100 armor (no helmet); prisoners knife only.
6. Join late as prisoner during the first 60 s with closed cells → respawned once in your cell.
7. Guard suicides (`kill` is blocked, use a fall) → dropped guns disappear.
8. Round end → "GUARDS WIN!/PRISONERS WIN!" banner + tint.

### Warden
9. `!w` as guard → queue message; first warden picked after 10 s countdown (centre HUD), heavy model, "Warden" tag.
10. Warden pings (middle mouse) → orange pointer laser + pulse ring at the pinged spot for 3 s, visible to prisoners too. Double-tap USE → pointer at the newest ping/bullet impact. Shoot twice, triple-tap WALK → line between the impacts; triple-tap DUCK → ring through them.
11. Ping a floor spot, then `!wm` → rings placed at the ping (set/resize/remove; without a ping: at your feet), healing area (heals prisoners +5 every 0.5 s), client colours; `!cc ring` / `!cc remove`.
12. `!uw`, `!rw` (admin), `!sw <name>` (root), `!wwho`, `!kiss`. Warden death → "The warden has died!" + reselection.

### Cells
13. Wait 60 s → cells open; logger shows "Cell doors opened." once. Press the cell button as CT → opened detected.
14. Root `!cdo` → "Admin X forced the cell doors open." `!celldoors_edit` → green glow on doors, yellow box on the pinged / looked-at door, add/remove/save → `maps/cells/<map>.json`.

### Last guard / mute / logger / jihad
15. Kill all guards but one → "is the last guard!", beacons, damage timer.
16. Prisoners muted first 60 s; warden mute power HUD; dead players muted.
17. `!logs` (admin) shows the round's events.
18. Set `Jihad.PercentChanceToGive 100`, `GuardsNeeded 1` → a prisoner gets C4; right click before cells open → refusal; after → bomb on head, pink, explosion damages in 750 u; `drop` → confirm menu.

### Last requests (bots as guards are fine)
19. Kill prisoners until LR slots (8 %, min 1) → "LR initialized", category menu, 20 s timeout → random LR.
20. One LR from each category: Weapon Battle (opponent menu → weapon menu → effect menu → countdown → slay timer), Contest (Race: place start/end, beam finish line; Gun Toss: drop the deagle), Rebel (rebel point teleport), Freeday (suicide → next round freeday, slain if interfering).
21. During an LR: other players invisible to the LR pair, pickups/drops/buys blocked per LR, damage only between opponents.
22. `!abortlr <name>`, `!givefreeday <name>`, `!lrs`, `!lr_setpoint`, `!rebelpoint_add`.

### Days
23. As warden (warden twice this map, within 15 s): `!day` → Freeday/Warday menus; wardays refused once cells are open.
24. Warday freeze countdown → unfreeze; FFA option appears once the map has 90 rebel points.
25. Spot-check each day (one per round): Gang, Jihad (FFA), Taser, No Scope, Fat Kid, Low Gravity, Scoutz Knivez, Weapon Selection, Classic (effect), Heavy Assault (buy time), One In The Chamber, Hunger Games (crates), Zombie, Throwing Knives, CT Retake, Horror Movie (fog), Freeze Tag. (RPG is blocked on CSS 1.0.368.)
26. `!daysedit` disables a day for the map; persists after map change.

### Admin / extras
27. `!admin` menu; slap/slay/beacon/freeze/burn/blind/drug/noclip/give/hp/respawn/team; mute/gag/silence + timed; ban/unban/kick; `!rr`.
27z. **Zones**: `!zm` → add zone at feet, edit position with clicks, size with clicks and "expand to my last ping", two-corner sizing, angles; set type Kill (walk in → die), Kill with enabled timer 30 s (safe before, dies after), Blockade (cannot walk/bhop through an 8-unit wall, red box visible, opens on the not-solid timer), Teleport → Teleport Destination (arrive on the floor centre facing its angle), Weapon Strip, Damage Blocker (fall), Push (jump pad), Notify (guards get "X entered Armory"), Respawn. Teleport Destination `lr_tele` → LR teleports there. Delete, duplicate, label, import a map trigger by name (`!showtriggernames` to find names), change map and back: zones persist. Only the editing admin sees the editor boxes.
27y. **Crews** (6+ humans for respect): `!crew` → found a crew (name + tag typed in chat); invite a second player, accept; tag on the scoreboard as prisoner only; `!cr hi` reaches crewmates, a dead member's line reaches only dead crewmates; promote, demote, kick, transfer boss, leave (cooldown), disband. Survive a round → "+2 respect"; win an LR duel → "+5". With Cell Block zones: 2 members in a block when the cells open → "took cell block", ring + pillar banner, next round they spawn in the block. Favors once per round (Banner, Colours, Lookout = only crewmates see the glow, Roll Call HUD). Crew ping → crew-coloured ring only crewmates see. Warden `!shakedown` → crew glows for guards. `!crews` leaderboard, `!crewadmin` (rename/retag/respect/kick/disband).
27x. **Crew War**: with 2 crews of 2+ living prisoners the day appears in the warday menu (hidden otherwise); crews start together, crewmates cannot hurt each other, guards take and deal no damage; last crew standing → "won the Crew War", round ends as prisoner win, respect granted; `mp_teammates_are_enemies` back to 0 next round.
27a. `!open` / `!o` as a normal player (denied), as `@css/generic` admin (cells open once, chat line, 3 s cooldown message on repeat), `!cdo` still root-only.
27c. Warden: with nobody on `!w`, after the countdown a guard is picked with the "automatically selected" line; `Warden.AutoWardenMode random` picks differently across rounds. With 3+ guards the warden has no bonus; kill guards until 2 alive → "+25 HP / +25 armor"; 1 alive → +50/+50 total; unwarden → bonus taken back, never kills.
27b. `!commands` as a normal player (no admin commands listed, pages with Next/Back), as an admin (admin commands appear), `!commands console` (full list in the console), and `css_commands` from the server console (everything).
28. Commands: `!g`, `!freekill`, `!wk`, `!1up`, `!roll`, `!jbrules`, `!speed`, `!timer`, `!xray`, `!bhc`, `!admintag`, `!points`.

### Robustness
29. Disconnect the warden, an LR prisoner and an LR guard mid-round → no errors, LR ends/continues per flags.
30. `mp_restartgame 1` mid-LR and mid-day → state resets cleanly next round.
31. Plugin reload mid-round → no duplicate timers/menus.

## 4. Results log

| Date | Tester | CSS | Package | Section | Result | Notes |
|---|---|---|---|---|---|---|
| 2026-10-10 | Claude (automated) | — | net8+net10 | §1 | Pass (21/21) | build 0 warnings |
| 2026-10-10 | Claude (automated) | API 1.0.368 | net8 only | §1 | Pass (21/21) | v0.10.0, build 0 warnings 0 errors |
