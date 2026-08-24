# Installing SurfTimer

## Requirements

- Counter-Strike 2 Dedicated Server
- SwiftlyS2 1.4.5 or newer
- .NET 10
- MariaDB/MySQL configured in SwiftlyS2
- BotController shared API ABI 18

CS2FlashingHtmlHudFix is optional.

## Install

1. Verify the release ZIP against the accompanying `.sha256` file.
2. Extract the `SurfTimer` directory from the ZIP into:

   ```text
   <cs2>/game/csgo/addons/swiftlys2/plugins/SurfTimer
   ```

3. Install BotController and its API assembly.
4. Start the server once. SwiftlyS2 creates:

   ```text
   <cs2>/game/csgo/addons/swiftlys2/configs/plugins/surf_timer/config.jsonc
   ```

5. Set `DatabaseConnection` to the SwiftlyS2 connection name.
6. Replace `change-me` with a unique lowercase server ID.
7. Configure this server's map pool and voting tier range.
8. Restart the server.

Do not put the live config inside the plugin directory. SwiftlyS2 keeps it separately so plugin upgrades do not overwrite it.

## First-start verification

Run these commands from the server console:

```text
surftimer_version
surftimer_status
surftimer_db_health
surftimer_catalog_check
```

Then load a configured map and run:

```text
surftimer_map_info
surftimer_map_check
```

Check that:

- The expected server ID is shown.
- The database is `ready` and `healthy`.
- BotController reports `abi-18`.
- The catalog has no errors.
- The loaded map is valid and compatible.

## Multiple servers

Point every instance at the same database and give each one a different `ServerId`. Map pools and voting settings remain local to the instance. Schema migrations are protected by a database lock.

## Upgrade

1. Back up MariaDB and retain the SHA-256 sidecar.
2. Stop the CS2 instance.
3. Snapshot the existing `plugins/SurfTimer` directory.
4. Replace that plugin directory with the new release payload.
5. Do not replace `configs/plugins/surf_timer/config.jsonc`.
6. Start the server and repeat the first-start verification commands.

Migrations are forward-only. Do not run an older plugin against a newer schema unless the release notes say it is supported.
