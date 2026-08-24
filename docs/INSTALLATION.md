# SurfTimer standalone installation

This procedure installs SurfTimer as a portable SwiftlyS2 plugin. None of the paths, ports, server IDs,
map rotations, or credentials used by the repository's local development servers are required.

## Requirements

- A Counter-Strike 2 dedicated server with SwiftlyS2 1.4.5 or a compatible newer version.
- The .NET 10 runtime supported by SwiftlyS2.
- A MariaDB/MySQL database reachable through a named SwiftlyS2 database connection.
- BotController with shared API ABI 18 for the current development build's replay types and native replay integration.
- A unique lowercase SurfTimer `ServerId` for every server instance sharing the database.

CS2FlashingHtmlHudFix is recommended but optional.

## Install

1. Verify the release ZIP against its adjacent `.sha256` file.
2. Extract the `SurfTimer` directory from the ZIP into:

   ```text
   <cs2>/game/csgo/addons/swiftlys2/plugins/SurfTimer
   ```

3. Install BotController according to its own instructions. Ensure its API assembly is available to the
   plugin runtime and that ABI 18 is reported by `surftimer_status` after startup.
4. Start the server once. SwiftlyS2 creates the effective plugin configuration under:

   ```text
   <cs2>/game/csgo/addons/swiftlys2/configs/plugins/surf_timer/config.jsonc
   ```

5. Configure a SwiftlyS2 database connection and set `DatabaseConnection` to its logical name.
6. Replace the generated `change-me` value with a unique server ID such as `community-easy-1`.
7. Configure the voting tier range and map pool for this particular server.
8. Restart the server. SurfTimer applies MariaDB migrations under a database advisory lock, allowing
   several instances to share one database safely.

The effective configuration lives in SwiftlyS2's `configs/plugins/surf_timer` directory, outside the
plugin payload. Replacing or upgrading the plugin directory must not replace that effective configuration.

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

Expected results:

- The configured unique server ID appears in status.
- Database status is `ready` and the health check is `healthy`.
- BotController reports `abi-18` for the current development build.
- The catalog has no configuration failures.
- The loaded map reports `valid` and its required mapper triggers are compatible.

## Multiple servers

Use the same database connection target for global records, but give every server a different `ServerId`.
Configurations and voting pools remain local to each SwiftlyS2 instance. Starting multiple upgraded
instances concurrently is supported; only one obtains the migration advisory lock at a time.

## Upgrade

1. Back up MariaDB and retain the SHA-256 sidecar.
2. Stop the CS2 instance.
3. Snapshot the existing `plugins/SurfTimer` directory.
4. Replace that plugin directory with the new release payload.
5. Do not replace `configs/plugins/surf_timer/config.jsonc`.
6. Start the server and repeat the first-start verification commands.

Database migrations are forward-only. Roll back plugin binaries only when the release notes explicitly
state that the older build can operate against the migrated schema.
