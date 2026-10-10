# Crews — a gang system for SWBS Jailbreak (design proposal, not built)

Status: **proposal** for the owner. Nothing here is implemented. Built on this plugin's own systems (JSON storage, zones,
points, days, chat), so no external plugin, database or credit shop is needed.

## Why not copy eGo's Gangs
eGo's Gangs are an external plugin: a persistent group plus a credit-funded **cosmetic perk shop** (warden icon colour, LR
colour, special-day colour, bomb icon, paint colour, cells perk). Progress is bought, nothing in the round itself depends on
the gang. Crews keep the idea (prisoners belong to a persistent group with an identity) and change the engine:

| | eGo Gangs | Crews |
|---|---|---|
| What you get | cosmetic perks bought with credits | status, coordination and map-based "turf", earned by conduct |
| Currency | credits (economy, database) | **reputation**: cannot be bought, decays, lost for freekilling |
| Tie to the round | colours on LR / days | cells (zones), Last Requests, a dedicated Crew War day, rebel/CT-ban rules |
| Storage | external plugin + DB | JSON (`Storage` ns `crews`), keyed by SteamID64 |
| Balance rule | pay-to-look-good | **no combat stats, ever**; only information, convenience, identity |

## Core design
1. **Roster** — `!crew create <name> <TAG>`, `invite`, `accept`, `kick`, `leave`, `promote`. Ranks Boss / Lieutenant / Member,
   max 6 members (config), 3–4 letter tag + colour. Create/leave cooldowns; name and tag go through the existing clan-tag
   filter. Persisted by SteamID64; a crew with no members disbands.
2. **Identity** — prisoners show `[TAG]` in chat and as scoreboard clan tag (guards never do). Crew chat `!crew say <msg>` is
   visible to crewmates, dead ones included, and is written to the admin log.
3. **Turf** — with the Zone Manager, each cell block is a zone. The crew that survived best last round *claims* the block:
   its members get their stable spawn spots there (next to each other) and a ring/banner in the crew colour (real beam
   entities) appears in front of the cell door. Pure cohesion, no combat advantage.
4. **Reputation (rep)** — earned per round for clean play: surviving, finishing a freeday without rebelling, winning an LR,
   winning a Crew War. Lost for freekills, CT-ban, abusing LR. Rep sets the crew **level** (more roster slots, more banner
   styles). Rep decays weekly, so it stays about recent conduct.
5. **Favors** — a crew may spend rep on at most one favor per round, picked before the cells open. Information and
   convenience only, e.g. *Lookout* (crewmates glow to each other for 5 s), *Banner* (colour of their LR beacon),
   *Early bird* (first pick of cell). Nothing that changes damage, speed or health.
6. **Crew War day** — a new special day (uses the Days API): prisoners fight as crews against each other (or all crews
   against the guards); the winner earns rep. Optional guard-side counterpart ("squads") is left for later.
7. **Fair play** — CT-banned players cannot be Boss; crew members get no protection from rebel rules or special treatment;
   `!crewadmin disband|kick|rename` under a `crews.admin` capability; every crew action is logged.

## Integration points
Zone Manager (turf) · Points service (rep) · Days API (Crew War) · Teams/CT bans · Chat · Logger · Perms (`crews.admin`) ·
Storage. Needs no engine feature beyond what the plugin already uses (beams, glow via CheckTransmit, clan tags).

## Phases
- **v1** (small): roster, tag, crew chat, persistence, admin tools.
- **v2**: rep + levels, turf and banners (needs the Zone Manager).
- **v3**: favors, Crew War day.

## Open questions for the owner
Max crew size, whether guards get squads, weekly rep decay, and whether crew chat may be shown to admins live or only logged.
