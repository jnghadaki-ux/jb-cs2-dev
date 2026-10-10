# Commands module (`Modules/Commands`)

Ports `Mods/UltimateJailbreak/Commands/UltJB_Command_*.sp` except CTBan (Teams module). Status of all rows:
**Implemented, unverified**.

| Command | Who | Original behaviour kept | CS2 notes |
|---|---|---|---|
| `!g <msg>` | alive non-guards | Message reaches guards + chat admins (+ sender), 2 s flood delay, "!g ..." hidden from public chat, sound. | Chat line is intercepted in the `say` listener; sound is a configurable stock sound event. |
| `!freekill <msg>` | dead prisoners (not admins/guards) | Shows "[FREEKILL] X freekilled by Y?" + message to guards/admins/sender; 30 s cooldown; logged. | Killer name captured from player_death. |
| `!whokilled` / `!wk <t>` | slay | Shows who killed a dead player this round. | — |
| `!1up <t>`, `!xup <seconds>` | kick | Respawn at last non-ducking position/angles (sampled every 0.3 s); xup = prisoners who died in the last X seconds. | controller.Respawn() then teleport next frame. |
| `!roll [max]` | warden | Random 1..max (default 100), 1 s cooldown, announced to all. | — |
| `!jbrules`, `!definitions`/`!defs` | everyone | Originally opened the rules web page in the MOTD browser. | **Partial**: CS2 has no MOTD/web viewer; the configured URL is printed to chat and console. |
| `!speed <t> <0-10>` | kick | Sets movement speed. | **Partial**: CS:GO's m_flLaggedMovementValue (time scale) does not exist in CS2; uses the Movement velocity multiplier (re-applied every tick). |
| `!timer <10-180>`, `!stoptimer` | warden | Countdown in centre HUD, chat every 10 s, completion sound; stops on round end, warden death, LR available or day start. | Centre HUD through the compositor. |
| `!xray` | ban | Admin sees team-coloured outlines of all alive players through walls while dead/spectating; 3 s toggle delay; sv_force_transmit_players forced on while used. | prop_dynamic glow (CGlowProperty type 3) following each pawn; CheckTransmit hides outlines from everyone except dead x-ray users and the outlined player. |

## Test procedure
1. As warden: `!roll`, `!roll 6` → chat result; spam → "wait a second".
2. As warden: `!timer 15` → HUD countdown, chat at 10, sound at 0; `!stoptimer` stops it.
3. As a prisoner, type `!g hello` → only guards/admins see it; it does not appear in public chat.
4. Get killed as a prisoner, type `!freekill he shot me` → guards/admins see the report; repeat → cooldown message.
5. Admin: `!wk <name>` → killer shown; `!1up <name>` → respawned where they died.
6. Admin (with @css/ban): die, `!xray` → red/blue outlines through walls; alive players never see them.
7. `!speed <name> 2` → faster movement; `!speed <name> 1` resets.
