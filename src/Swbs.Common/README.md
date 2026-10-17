# Swbs.Common

The Store, the Zone Manager, BhopCheck (`!bhc`), shared player data and the round variations. They are compiled into every SWBS CS2 plugin:
Bhop, Surf, Kz, Course, SurfRpgDm (`cs2-bhop-dev`), Minigames, Deathmatch, PUG, 1v1 (`cs2-mg-dev`) and Jailbreak
(`jb-cs2-dev`).

**This folder is identical in all three repos.** Do not edit it in one repo only. Edit it, then run:

```bash
src/Swbs.Common/sync.sh ../cs2-mg-dev ../jb-cs2-dev   # from cs2-bhop-dev; name the other two from elsewhere
```

That refreshes `MANIFEST` and copies the folder into the other repos. Every repo's tests compare the folder with
`MANIFEST`, so an edit made in one place without syncing fails the tests.

## What differs per game mode

Nothing in this folder. Each repo implements the members of `CommonHost` (declared in `CommonHost.cs`) once, in
its own file. A missing member is a build error.

| | Movement servers | Minigames / DM / PUG / 1v1 | Jailbreak |
|---|---|---|---|
| Adapter | `Swbs.Shared/Core/CommonHost.Movement.cs` | `Swbs.Shared/Core/CommonHost.Modes.cs` | `SwbsJailbreak/Core/CommonHost.Jailbreak.cs` |
| Store effects / hats | always on | the mode's `ModeRules` (off in a live PUG) | off on wardays and in non-freeday LRs |
| Body items | always | warmup or the mode allows it | warmup, freedays, freeday LRs |
| Team names | Terrorists / Counter-Terrorists | Terrorists / Counter-Terrorists | Prisoners / Guards |
| Extra zone types | timer zones (SwbsBhop) | | Notify, Cell Block |
| Zone checks | every tick | every tick | every 4 ticks |

Per-mode wording goes in a language file that loads after the shared one (for example `lang/en/store_mode.json`).
Files load in name order, and a later file overrides an earlier one key by key.

## Round variations

`Rounds/` holds the round variations: timed rounds with a vote, and 16 variations such as RPG Arena, Throwing Knives,
Dodgeball, Gun Game and Bounty Hunter. They are the jailbreak days, rebuilt as deathmatch rounds. The module loads
only when the plugin implements `IRoundsHost`, which SwbsDeathmatch (tag `dm`) and SwbsSurfRpgDm (tag `surf`) do. On
every other plugin it is disabled, and its config sections are never written.

What the host gives the module:

- `RoundTags`, which decide the variations it runs.
- `AddRoundStat`, which records the round MVP in the mode's stats.
- `HighlightPlayer`, which marks the bounty or the juggernaut (a glow on DM, a tint on SurfRPGDM).

What the mode's code checks in return:

- `Rounds.OwnsLoadout`: skip the spawn loadout.
- `Rounds.AllowRespawn(player)`: skip a respawn.

A mode can add its own variation by subclassing `RoundVariation` and passing it to `RoundsModule.Instance.Register`.

## Shared points and store items

Every server reads `addons/counterstrikesharp/configs/swbs_shared.json`. The first start writes it with comments:

| `Backend` | Use when | Set |
|---|---|---|
| `local` (default) | each server keeps its own points and items, as before | nothing |
| `folder` | all servers are on the same machine | `Folder`: a path every server can write, e.g. `/home/cs2/swbs-shared` |
| `mysql` | servers are on different machines | `MySql`: host, port, database, user, password (the table is created) |

Give every server the same settings and restart them.

- Only the store currency (`points`) and store items (`store`) are shared. How points are earned stays per mode.
  Playtime, bans, records and everything else stay per server.
- Points are saved as additions (`+10`, `-500`), not overwrites, so servers changing the same player at once never
  lose points.
- `MergeExistingData` (on by default) handles the switch-over. The first time each server sees a player, it adds
  that server's old points to the shared balance and combines the store items, once per server. Then it marks the
  player in its local `data/shared_merged.json`. The old local files are left as a backup.
- If the folder or database cannot be reached, affected players' shared points and items are read-only. They show
  0, nothing is saved, and the server retries in the background, so an outage never overwrites a real balance. If
  `swbs_shared.json` itself is wrong, the error is logged and points and the store stay read-only. The plugin does
  not quietly fall back to local data.
- `MySqlConnector.dll` ships next to each plugin (the build scripts copy it).

To test against a real database, set `SWBS_TEST_MYSQL="host;port;database;user;password"` before `dotnet test`.
The test creates and drops its own table.
