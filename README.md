# SurfTimer

A full-featured, Surf-only Counter-Strike 2 timer built for [SwiftlyS2](https://github.com/swiftly-solution/swiftlys2), .NET 10, and MariaDB. SurfTimer is designed for communities that run several servers over one global records database while keeping each server's configuration and map pool independent.

> **Status:** initial `0.1.0` release. Configuration, APIs, and forward-only migrations may evolve in later releases.

## Current features

- Map-trigger timing: leaving the configured start zone starts the timer.
- Mapper-created triggers are authoritative; SurfTimer does not create manual or fallback timing zones.
- Integer-microsecond timing with ordered checkpoints and strict stage validation.
- 64 Hz HUD with timer, speed gradient, status, progress, and key display.
- Global MariaDB PBs, WRs, ranks, top ten, completions, splits, players, maps, and preferences.
- Shared records across multiple CS2 servers with unique server IDs.
- Versioned compressed 64 Hz PB replays with native first-person playback, replay keys, top-ten playback, and individual PB storage.
- Practice saved locations, teleport navigation, noclip, and per-player 500–2000 u/s noclip speed.
- Staged maps: splits, HUD progress, stage teleports/restarts, PBs, WRs, ranks, and top ten.
- Automatic migrations, health diagnostics, admin permissions, and audit logging.
- Dry-run-first SharpTimer MariaDB record importer.
- Read-only global web API and responsive leaderboard/player website.
- Recent global PB/WR activity, PB improvement history, map averages/medians, and detailed player statistics.
- Dynamic Points rankings with exponential Tier 1–7 weighting, Top 100 placement decay, stage mastery, bonus completions, a capped per-tier map portfolio, and Group 1–5 times.
- Complete main, bonus, and stage PB improvement history.
- Configurable map nominations, panel selection, 60-second RTV voting, early completion when everyone votes, delayed map changes, tier filtering, and map extension.
- Runtime mapper-trigger compatibility reports and static catalog configuration audits.
- Spectator HUD, player profiles, map statistics, bonus routes, and stage replays.

Tested on Windows across two local server instances with maps from Tier 1 through Tier 7.

## Dependencies

- Counter-Strike 2 Dedicated Server
- [SwiftlyS2](https://swiftlys2.net/) 1.4.5 or a compatible newer version
- [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) runtime; SDK required to build
- MariaDB/MySQL configured through SwiftlyS2 (MariaDB is tested)
- Windows is the currently tested server environment

Optional companion: [CS2FlashingHtmlHudFix](https://github.com/girlglock/CS2FlashingHtmlHudFix).

[BotController](https://github.com/nicedayzhu/cs2-bot-controller) and its shared API ABI 18 are currently required by the `0.1.0` build because the versioned replay format and native first-person integration use its public replay types. The third-party `BotControllerApi.dll` is intentionally not redistributed; install BotController on the server and place its API assembly at `lib/BotControllerApi.dll` when building SurfTimer. Compatibility playback remains available when the shared interface cannot be acquired after the API assembly is present.

## Build and install

See [`docs/INSTALLATION.md`](docs/INSTALLATION.md) for the portable clean-server procedure. The release ZIP contains only the generic SurfTimer plugin payload and release metadata; local server paths, ports, server IDs, credentials, and development scripts are not packaged.

Clone the repository, add the BotController API reference assembly, then publish. The assembly is currently a build-time dependency even when replay-bot behavior is not enabled at runtime.

```powershell
dotnet restore
dotnet publish -c Release
```

Copy `build/publish/SurfTimer` to:

```text
<cs2>/game/csgo/addons/swiftlys2/plugins/SurfTimer
```

SwiftlyS2 creates `addons/swiftlys2/configs/plugins/surf_timer/config.jsonc`. Configure a SwiftlyS2 database connection named `surftimer` and assign every server sharing it a unique lowercase `ServerId`. Migrations run automatically at startup under a MariaDB advisory lock.

Example plugin configuration:

```jsonc
{
  "SurfTimer": {
    "Enabled": true,
    "DatabaseConnection": "surftimer",
    "ServerId": "surf-1",
    "HudRefreshRateHz": 64,
    "MapVoting": {
      "Enabled": true,
      "RockTheVoteEnabled": true,
      "NominationEnabled": true,
      "VoteDurationSeconds": 60,
      "ExtendMapMinutes": 10,
      "MinimumTier": 1,
      "MaximumTier": 2
    }
  }
}
```

Use a unique `ServerId` on every live instance. All instances should resolve the `surftimer` SwiftlyS2 connection to the same MariaDB database for global records.

Verify installation:

```text
surftimer_status
surftimer_db_health
surftimer_map_info
surftimer_map_check
surftimer_catalog_check
```

### Maintainer release tooling

Build a versioned ZIP, release manifest, and SHA-256 sidecar:

```powershell
.\tools\release\Build-Release.ps1
```

The build derives the database schema version from the packaged migrations and validates the ZIP,
required payload, archive paths, version, migration set, and SHA-256 sidecar. An existing package can
be checked independently with `tools/release/Test-ReleasePackage.ps1 -PackagePath <zip>`.

The following helper is specifically for this repository's local Windows test instances and is not the public installation mechanism:

```powershell
.\tools\release\Install-Release.ps1 -PackagePath .\artifacts\SurfTimer-0.1.0.zip
```

It requires the local test instances to be stopped and creates a rollback snapshot before updating their plugin payloads.

The installer snapshots the previous plugin payload under `backups/deployments`, updates both local Swiftly instances, and never modifies their isolated `configs` or `data` directories.

After restarting both instances, run the deployment smoke test:

```powershell
.\tools\release\Test-LocalDeployment.ps1
```

If an upgrade fails, stop both servers and restore a named snapshot explicitly:

```powershell
.\tools\release\Restore-Release.ps1 -SnapshotPath .\backups\deployments\YYYYMMDD-HHMMSS -ConfirmRollback
```

Rollback first creates another safety snapshot of the currently installed payload. Configs, database credentials, player data, and logs remain outside the replaced plugin directories.

## Local Windows development

The scripts assume a local server at `C:\CS2Server`:

```powershell
.\tools\local-server\Update-Server.ps1
.\tools\local-server\Deploy-Plugin.ps1
.\tools\local-server\Start-LocalServer.ps1
.\tools\local-server\Start-LocalServer.ps1 -Map surf_kitsune
.\tools\local-server\Stop-LocalServer.ps1
.\tools\local-server\Start-Surf3.ps1
.\tools\local-server\Stop-Surf3.ps1
```

Local database credentials and plugin configuration are excluded from Git.

Run the fast build and regression gate with:

```powershell
.\tools\Test-All.ps1
```

Add `-IncludeLiveDatabase` for the read-only consistency/API checks and
`-IncludeDownloadedMaps` when the configured Workshop VPKs are present locally.

The supplied development profiles model an easy-server pool (`Tier 1–2`) and a hard-server pool (`Tier 1–7`). Further servers can reuse the same database by adding another unique server ID and an isolated SwiftlyS2 configuration root.

## Map configuration

Map timing is driven entirely by named mapper triggers. Bundled definitions live under `resources/configs/maps`; per-server overrides are stored in SwiftlyS2's SurfTimer data directory so upgrades do not overwrite them.

```json
{
  "Enabled": true,
  "Tier": 1,
  "StartTrigger": "map_start",
  "EndTrigger": "map_end",
  "CheckpointPrefix": "map_cp",
  "CheckpointCount": 2,
  "StagePrefix": "stage",
  "StageCount": 0,
  "BonusPrefix": "bonus",
  "BonusCount": 0,
  "MaxVelocity": 3500
}
```

For staged maps, stage 1 uses the main start trigger and subsequent stage boundaries use the configured prefix, such as `stage2_start`. Bonuses use names such as `bonus1_start` and `bonus1_end`.

After loading a new map, inspect and certify it with:

```text
surftimer_dump_triggers
surftimer_map_check
```

`surftimer_catalog_check` validates every JSON definition. Actual entity availability can only be checked while the corresponding map is loaded.

## Leaderboard website

The website reads the same shared MariaDB database as every game server. It exposes global records only; server history and database credentials are never sent to the browser.

```powershell
.\tools\local-server\Start-Web.ps1
.\tools\local-server\Stop-Web.ps1
```

Open `http://127.0.0.1:5080`. Map and player pages have shareable paths such as `/maps/surf_boreas` and `/players/76561198079156085`. The local launcher builds the ASP.NET Core site, runs it in the background, and writes logs under `tools/local-server/.runtime`. The API includes health, route discovery, map/route leaderboards, stage records, player search, ranked PB lists, and global player profiles.

Public API list endpoints accept `page` and `pageSize` (maximum 100). Popular read responses use a 10–30 second in-memory cache, and API traffic is limited per client to 120 requests per minute. Static website requests are not rate limited.

Production can use `SURFTIMER_DB_CONNECTION_STRING`, or `SURFTIMER_DB_HOST`, `SURFTIMER_DB_PORT`, `SURFTIMER_DB_NAME`, `SURFTIMER_DB_USER`, `SURFTIMER_DB_PASSWORD`, and optional `SURFTIMER_DB_SSL_MODE`. `SURFTIMER_CACHE_RECORDS_SECONDS`, `SURFTIMER_CACHE_METADATA_SECONDS`, and `SURFTIMER_RATE_LIMIT_PER_MINUTE` tune public API behavior. The ignored local database JSON remains a development-only fallback.

Run the read-only live API integration suite with:

```powershell
.\tools\web-api\Test-WebApi.ps1
```

`/api/version` reports the service version, environment, startup time, framework, and effective non-secret cache/rate-limit settings. Requests and failures are emitted as structured JSON logs; public errors expose only a diagnostic request ID.

Map pages show unique surfers, completions, average and median PBs, plus the current WR holder and Group 1–5 classifications. Player pages show overall points/rank, WR counts, best placement, route/replay breakdowns, filterable records, and paginated PB improvements across main, bonus, and stage routes.

To run it directly during development:

```powershell
dotnet run --project .\web\SurfTimer.Web.csproj
```

## Commands

| Area | Commands |
|---|---|
| Timer | `!r`, `!restart` |
| Records | `!pb`, `!wr`, `!top10`, `!top`, `!rank` |
| Replays | `!replay <1-10>`, `!replay stop` |
| Bonuses | `!b <bonus>`, `!rb`, `!bonuspb <bonus>`, `!bonuswr <bonus>`, `!bonustop <bonus>`, `!breplay <bonus> [rank]` |
| Practice | `!saveloc`, `!tele`, `!teleprev`, `!telenext`, `!noclip`, `!ncspeed <500-2000>` |
| Stages | `!s <stage>`, `!stage <stage>`, `!rs`, `!restartstage` |
| Stage records | `!stagepb <stage>`, `!stagewr <stage>`, `!stagetop <stage>` |
| Preferences | `!settings`, `!hud`, `!speed`, `!status`, `!keys`, `!sounds`, `!replayhud` |
| Information | `!help`, `!mapinfo`, `!mapstats` |
| Route discovery | `!stages`, `!bonuses` |
| Overall ranking | `!points [player]`, `!ranks`, `!profile [player]` |
| Map voting | `!rtv`, `!nominate`, `!1`–`!5`, `!6` to extend when available |

Familiar `css_` console aliases are included.

Admin commands require `surftimer.admin`: `!stadmin`, `!stmapreload`, `!stsettier`, `!stmapenable`, `!stmapcheck`, `!stcatalogcheck`, `!stplayer`, `!stvalidate`, `!strecordcheck`, `!stdeletepb`, and main/stage/bonus replay inspection and deletion commands. `!stmapcheck` verifies the configured mapper triggers on the loaded map. `!stcatalogcheck` validates every catalog configuration; inactive maps receive their runtime entity check when loaded. Destructive commands require explicit `confirm` and are audited.

## Database and multiple servers

All instances use the same MariaDB tables and expose global records. Every live instance requires a unique `ServerId`; see [`deploy/servers/README.md`](deploy/servers/README.md).

Migrations cover players, maps and authoritative route counts, main records, checkpoint splits, compressed replays, metadata, preferences, admin auditing, stage records, and PB history for every route type. Database I/O is asynchronous. See [`tools/sharptimer-import/README.md`](tools/sharptimer-import/README.md) for importing current SharpTimer MariaDB times and statistics.

### Overall points policy

Points are calculated dynamically from current PBs. Main-map completion is `25 × 2^(tier-1)`. A placement pool worth ten times that base decays from 100% for WR through the Top 100; ranks 2–10 receive 80% down to 40%, and ranks 11–100 receive `4/rank`. The best 20 map scores in each tier count toward the competitive total. A stage-mastery pool worth 35% of the map placement pool is divided evenly between the map's timed stages and uses the same Top 100 decay. Each distinct completed bonus route awards 10 Points. Groups remain main-map percentile labels: G1 top 1%, G2 1–5%, G3 5–10%, G4 10–25%, and G5 25–50%. Times outside the top 50% have no group. Tier, record, map-enabled, and leaderboard changes recalculate Points automatically.

### Local database operations

Create a consistent online backup and its SHA-256 sidecar:

```powershell
.\tools\local-server\Backup-Database.ps1
```

Run the read-only schema, row-count, writer-attribution, and orphan-row audit:

```powershell
.\tools\local-server\Test-DatabaseConsistency.ps1
```

Restoring is intentionally guarded. Stop every local CS2 instance, verify the selected file, and explicitly confirm the operation:

```powershell
.\tools\local-server\Restore-Database.ps1 -BackupPath .\backups\mariadb\surftimer-YYYYMMDD-HHMMSS.sql -ConfirmRestore
```

Restore requires and verifies the `.sha256` sidecar and checks that the dump contains the core SurfTimer schema before connecting to MariaDB. Run the consistency audit before restarting game servers.

Install the Windows nightly job (04:00 by default) with a 14-backup retention policy:

```powershell
.\tools\local-server\Install-BackupScheduledTask.ps1 -DailyAt 04:00 -KeepLatest 14
```

The job runs a transactional backup followed by the consistency audit, logs under `backups/mariadb/logs`, and only applies retention after both operations succeed. Remove the scheduled task without deleting existing backups using:

```powershell
.\tools\local-server\Remove-BackupScheduledTask.ps1
```

## Roadmap

- Richer in-game rank and points progression displays
- Improved optional always-running WR replay-bot presentation without consuming a public player slot where the CS2/SwiftlyS2 APIs permit it
- Expanded automated map compatibility certification and a larger Tier 1–7 map catalog
- Additional run-validation and anti-cheat integrations
- Production website deployment, future authenticated administration, and community integrations
- SharpTimer import validation against additional real-world schemas and datasets
- Stable release packaging, semantic versioning, upgrade documentation, and license selection

## Architecture

Dependency-injected modules separate player sessions, map lifecycle and compatibility, timing, HUD, commands, storage and migrations, preferences, practice, voting, and replays. Game entities remain on SwiftlyS2's game thread; database work uses asynchronous I/O. Times are stored as integer microseconds, and overall rankings are calculated dynamically from the current global record set.

The implementation status against the original project scope is tracked in [`docs/FEATURE-STATUS.md`](docs/FEATURE-STATUS.md).

## License

This project is currently under private development. No open-source license has been selected, so redistribution or modification rights are not granted until a license file is added.

