# Admin module (`Modules/Admin`)

Ports SourceMod 1.6 `funcommands`, `playercommands`, `basecommands`, `basecomm`, `basebans`, `basechat` (as used
on SWBS), the SMPlugins plugins **Respawn**, **Heal**, **Teamswitch**, **TimedMuteGag**, **LogAdminActivity**, and
the owner's **SimpleAdmin** commands (hp, armor, give, bring, goto, team, god, warn, banlist, admins, rr, admin menu).
All statuses are **Implemented, unverified** (no live server in session 1).

| Command(s) | Capability (config `General.Permissions`) | Original | CS2 implementation / differences |
|---|---|---|---|
| `!beacon <t> [0/1]` | slay | funcommands | Real expanding beam ring every 1 s (team colour, radius `BeaconRadius` 375) + sound event. |
| `!timebomb/!firebomb/!freezebomb <t> [0/1]` | slay | funcommands | 10 s fuse, colour fade, white ring at radius/3, centre HUD countdown to everyone; timebomb = explosion particle + kill (+ radius damage 220·(r-d)/r in mode 1/2); firebomb = burn in radius; freezebomb = freeze in radius. |
| `!freeze <t> [sec]`, `!unfreeze` | slay | funcommands | MOVETYPE_OBSOLETE + blue tint; auto-unfreeze after `FreezeDuration` (10 s). |
| `!burn <t> [sec]` | slay | funcommands | **Partial**: CS2 has no IgniteEntity → damage over time (`BurnDamagePerSecond`) + flame particle parented to the pawn + orange tint. Particle name is configurable and unverified. |
| `!gravity <t> [0-5]` | slay | funcommands | Movement gravity multiplier (re-applied every tick, reset on death/round). |
| `!noclip <t>` | fun | funcommands | MOVETYPE_NOCLIP toggle. |
| `!blind <t> [0-255]` | slay | funcommands | Black screen fade (stay-out) at given alpha; `BlindUseFlash=true` uses the flashbang overlay refreshed every 3 s (method confirmed on the owner's server). |
| `!drug <t> [0/1]` | slay | funcommands | **Partial**: CS2 cannot roll the client camera; uses random colour fades + screen shake + FOV wobble each second. |
| `!slap <t> [dmg]`, `!slay <t>`, `!rename <t> <name>` | slay | playercommands | Random push + damage; CommitSuicide; networked name change. |
| `!respawn [t]` (menu without args), `!heal <t>` | respawn / root | SMPlugins Respawn, Heal | controller.Respawn(); heal to 100. |
| `!hp`, `!armor`, `!give`, `!bring`, `!goto`, `!god`, `!ungod` | fun / slay | SimpleAdmin | God mode blocks damage in the damage pipeline (works regardless of TakesDamage). |
| `!team <t> <t/ct/spec>`, `!ts/!teamswitch`, `!tsd`, `!tsr` | teams | Teamswitch + SimpleAdmin | Alive players keep their gear (SwitchTeam). |
| `!mute/!gag/!silence <t> [minutes] [reason]` and `un*`, `!timed_*`, `!check_mute/gag/silence` | mute | basecomm + TimedMuteGag | No time = until disconnect; minutes = stored in `data/comms.json` and re-applied on join. Gag blocks `say`/`say_team` (commands still work). |
| `!say`, `!csay/!hsay`, `!psay`, `!chat/!asay`, `@msg` in team chat | admin | basechat | csay renders in the centre HUD for 6 s. |
| `!kick <t> [reason]` | kick | basecommands | `kickid <userid> "<reason>"`. |
| `!ban <t> <min|0> [reason]`, `!addban`, `!banip`, `!unban`, `!banlist` | ban / unban / rcon | basebans + SimpleAdmin | Plugin ban list (`data/bans.json`, `data/ipbans.json`), enforced on authorisation/connect. |
| `!warn`, `!warns`, `!unwarn` | teams | SimpleAdmin | Stored in `data/warnings.json`. |
| `!map`, `!rr`, `!rcon`, `!cvar`, `!execcfg`, `!who`, `!reloadadmins`, `!admins` | map / rcon / cvar / config / admin / ban | basecommands | Workshop ids → `host_workshop_map`. Protected cvars need rcon. |
| `!admin` | admin | adminmenu | Player list → per-player actions; Jailbreak controls (cells, warden, days, LRs via contracts); Server (restart, map list, reload admins). |

Every action is announced per `ShowActivity` (0/1/2) and appended to `data/admin_activity.log` and the jail log.

## In-game test procedure
1. Add yourself to `addons/counterstrikesharp/configs/admins.json` with `@css/root`; `css_admins_reload`.
2. With a bot (`bot_add_t`): `!beacon @t` → rings pulse every second; `!beacon @t 0` stops them.
3. `!timebomb @t` → countdown HUD for everyone, colour fades, explosion + death at 0.
4. `!freeze @t 5` → bot cannot move for 5 s and is tinted blue. `!burn @t 5` → HP drops, flame particle visible (if the particle name is valid).
5. `!blind <you> 200` (needs a second human or test on yourself) → screen darkens; `!blind <you>` clears.
6. `!mute <name> 5 test` then reconnect that player → still muted; `!check_mute <name>` shows minutes left.
7. `!ban <name> 1 test` → kicked; rejoin within a minute → kicked again with reason; after 1 minute can join.
8. `!admin` → navigate with W/S/E/R or !1-!9; every action works and is logged in `data/admin_activity.log`.
