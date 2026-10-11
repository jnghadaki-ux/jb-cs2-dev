# Crews — prisoner gangs for SWBS Jailbreak (built, session 3)

Status: **Implemented, unverified** (`Modules/Crews`, rules in `Core/Crews.cs`, 30 unit tests). Config section `Crews`.
Everything uses this plugin's own systems: JSON storage, Zone Manager zones, Days API, glow, beams, HUD, chat.

## The idea
eGo's Gangs are an external plugin plus a credit-funded **cosmetic perk shop**. Crews keep a persistent prisoner group
but make it about **play, territory and reputation**:

| | eGo Gangs | Crews |
|---|---|---|
| What you get | cosmetic perks bought with credits | identity, coordination, turf, status — earned |
| Currency | credits (economy + database) | **respect**: only earned by clean play, decays weekly, cannot be bought |
| Tie to the round | colours on LR / days | cell blocks (zones), last requests, Crew War day, warden shakedown |
| Storage | external plugin + MySQL | JSON (`data/crews.json`, `maps/crew_turf/<map>.json`) |
| Balance | pay-to-look-good | **never changes damage, health or speed** (outside the Crew War day) |

## Systems
1. **Roster** — Boss / Lieutenants / Members. `!crew` menu or subcommands: `create <name> <TAG>`, `invite`, `accept`,
   `decline`, `kick`, `promote`, `demote`, `boss` (transfer), `motd`, `rival`, `favor`, `info [TAG]`, `leave`, `disband`.
   Invites expire (60 s). Leaving puts you on a 10 min cooldown, disbanding the Boss on 60 min. A Boss who leaves is
   replaced by the top Lieutenant (then top member); an empty crew disbands. CT-banned players cannot found or lead a crew.
   Names 3–24 chars, tags 2–5 letters/digits, both unique and checked against `Crews.BannedWords`.
2. **Identity** — the crew tag is the prisoner's scoreboard clan tag (guards never; an admin tag wins). Crew colour (8
   colours, more unlock with level). Crew chat `!cr <msg>`: dead members only reach dead crewmates (no ghosting); logged.
   `!crews` leaderboard, `!crew info <TAG>`.
3. **Respect & levels** — earned per member, per round (cap 10): survive the round +2, prisoners win +3, win an LR duel +5,
   hold turf +1, take turf +5; Crew War win +25 to the crew, +5 per survivor, +10 if the rival crew was beaten. Nothing
   below 6 humans online. Spendable respect decays 10 %/week; lifetime **legacy** sets the level (100/300/700/1500/3000/6000):
   more roster slots (4 → 8), colours, a third Lieutenant at level 5.
4. **Turf** — admins mark cell blocks with **Cell Block** zones (`!zm`). When the cells open (everyone is still in their
   cell) the crew with the most living members inside a block (min 2) takes it; ties keep the holder. Held blocks show a
   beam ring and pillar in the crew colour, and the holder's members spawn inside their block next round. Saved per map.
5. **Favors** — one per crew per round, called by the Boss or a Lieutenant with respect: *Crew Banner* (10, lvl 1) beam
   pillar rendezvous point; *Crew Colours* (15, lvl 2) members glow in the crew colour for everyone (15 s); *Lookout* (25, lvl 3)
   crewmates see each other through walls (8 s); *Roll Call* (20, lvl 4) HUD list of crewmates and the named zone each is in
   (30 s). Information and identity only.
6. **Crew pings** — a crew member's CS2 ping also draws a ring in the crew colour that only crewmates can see.
7. **Warden shakedown** — `!shakedown` (warden, once per round): the chosen crew's living members glow red for the guards
   for 10 s. Powerful crews attract attention.
8. **Crew War** (warday) — crews fight each other (prisoners without a crew alone), crewmates cannot hurt each other,
   guards are untouchable referees with knives, each crew starts together at a rebel point, AK + Deagle + armour. Last
   crew standing wins; the round ends as a prisoner win. Needs 2 crews with 2+ living members (hidden from the day menu otherwise).
9. **Admin** — `!crewadmin` (capability `crews.admin`, default `@css/ban`): rename, retag, set respect, kick, disband; logged.

## Not done / ideas for later
Guard-side squads; crew statistics history; seasonal resets; crew-vs-crew challenges outside wardays.
