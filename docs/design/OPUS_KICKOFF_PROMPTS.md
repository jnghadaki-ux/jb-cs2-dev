# Kickoff prompts for the two Opus sessions (run separately)

Run `/model claude-opus-5-5` first. Both sessions read `CLAUDE.md` (owner rules, .NET 8 only, credits) automatically.
**Use separate branches** so the two sessions never push to the same branch, and merge Zone Manager first:

| Session | Branch (create from `claude/swbs-jailbreak-cs2`) | Files it owns |
|---|---|---|
| Zone Manager | `claude/zone-manager` | `Core/Zones*.cs`, `Modules/Zones/*`, `lang/en/zones.json`, zone tests |
| Crews | `claude/crews` | `Modules/Crews/*`, `lang/en/crews.json`, crew tests |

Shared files both will want to edit (append only, then rebase before pushing): `CHANGELOG.md`, `FEATURE_GAPS.md`,
`FEATURE_INVENTORY.md`, `PROJECT_STATUS.md`, `SESSION_HANDOFF.md`. Modules are auto-discovered, so no edit to
`JailbreakPlugin.cs` should be needed. Crews v1 does not depend on zones; Crews v2 (turf) must wait for the Zone Manager merge.

## Prompt 1 — Zone Manager
```
Read CLAUDE.md, SESSION_HANDOFF.md, then docs/design/ZONE_MANAGER_PLAN.md. Build the plan in phases on branch
claude/zone-manager (from claude/swbs-jailbreak-cs2): phase 1 first (ZoneGeometry + ZoneService + JSON persistence +
editor + Teleport/Named/Kill/DamageBlocker/WeaponStrip types + migration of !lr_setpoint / !rebelpoint_add / !ctf_set*),
unit-test the geometry, persistence and enter/exit sequencing, keep build at 0 warnings / 0 errors, update TEST_PLAN.md,
FEATURE_INVENTORY.md and docs, push at the end. Original sources: SMPlugins Libraries/ZoneManager and Plugins/ZoneTypes
(re-extract the owner's SMPlugins-master upload if not on disk). net8.0 / CSS 1.0.368 only; no ray trace (use Core/Aim pings).
Usage: 5-hour __%, weekly __%.
```

## Prompt 2 — Crews
```
Read CLAUDE.md, SESSION_HANDOFF.md, then docs/design/CREWS.md. Build Crews v1 only (roster, tag, crew chat, persistence,
admin tools, logging) on branch claude/crews (from claude/swbs-jailbreak-cs2) as a new module under Modules/Crews with
config switch Crews.Enabled. Owner answers to the open questions: max crew size __; guard squads (yes/no/later) __;
weekly rep decay __; admins see crew chat (live / logged only) __. Follow the design's fairness rules (no combat stats).
Unit-test the engine-free parts (roster rules, cooldowns, persistence), 0 warnings / 0 errors, update docs, push.
Do not start v2 (turf) until the Zone Manager is merged. net8.0 / CSS 1.0.368 only.
Usage: 5-hour __%, weekly __%.
```
