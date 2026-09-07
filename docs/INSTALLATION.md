# Installation

## Requirements

- Counter-Strike 2 Dedicated Server
- SwiftlyS2 1.4.5 or newer
- .NET 10 runtime
- MariaDB or MySQL
- Metamod with BotController shared API ABI 18

## Install

1. Verify the release ZIP with its `.sha256` file.
2. Extract `SurfTimer` to `game/csgo/addons/swiftlys2/plugins/SurfTimer`.
3. Install BotController through Metamod and check the [loader order and hooks](REPLAYS.md#native-playback-setup).
4. Start the server once.
5. Edit `game/csgo/addons/swiftlys2/configs/plugins/surf_timer/config.jsonc`.
6. Set the SwiftlyS2 database connection name and a unique `ServerId`.
7. Configure the map pool and voting tier range.
8. Restart the server.

The live config is outside the plugin directory and is not replaced during plugin upgrades.

## Verify

Run from the server console:

```text
surftimer_version
surftimer_status
surftimer_db_health
surftimer_catalog_check
```

After loading a configured map:

```text
surftimer_map_info
surftimer_map_check
```

The database should report `ready` and `healthy`, BotController should report `abi-18`, and the map check should be valid.

## Multiple servers

Point each instance at the same database and assign a different `ServerId`. Map pools, tier ranges and vote timers remain local to each server. Database records and Points are shared.

## Upgrade

1. Back up the database.
2. Stop the server.
3. Replace `plugins/SurfTimer` with the new release.
4. Keep the existing `configs/plugins/surf_timer/config.jsonc`.
5. Start the server and run the verification commands.

Database migrations are forward-only. Do not downgrade the plugin after a schema upgrade unless the release notes explicitly permit it.

## Upgrade notes

Preserve the Swiftly plugin data directory, including `pending-runs` and `pending-runs/quarantine`. Queued finishes recover automatically; deleting them loses unsaved runs. Back up the database and test migrations 016–019 on staging. Keep applied migration files unchanged.

Set `CatalogAuthorityServerId` consistently across instances before rollout. The database retains catalog ownership and competitive map baselines. See [operations](OPERATIONS.md) for ownership transfers, pending runs and ruleset quarantine.

`tools/release/Install-Release.ps1 -PackagePath <zip>` verifies and stages the archive, snapshots all targets, then replaces complete plugin payloads. Earlier swaps roll back if a later target fails. Config and data outside `plugins/SurfTimer` are preserved.

`tools/release/Restore-Release.ps1 -SnapshotPath <snapshot> -ConfirmRollback` restores binaries using the snapshot's target mapping. It does not roll back database migrations.
