# SurfTimer

A Surf timer for Counter-Strike 2 built on [SwiftlyS2](https://github.com/swiftly-solution/swiftlys2).

SurfTimer uses the triggers supplied by each map. There is no zone editor or fallback zone system. Records are stored in MariaDB and can be shared by several game servers.

Version `0.1.0` is an early release. It has been developed and tested on Windows.

## What it supports

- Linear, staged and bonus routes
- Ordered checkpoints and stage splits
- PBs, WRs, Top 10s and completion counts
- 64 Hz HUD with speed, keys and spectator information
- Main, stage and bonus replays
- Practice teleports and noclip
- Player preferences and profiles
- Tier 1–7 Points rankings
- Nominations, RTV and map extension
- Shared records across multiple servers
- A read-only leaderboard website and API
- SharpTimer MariaDB imports through a dry-run tool

See [feature status](docs/FEATURE-STATUS.md) for known gaps and deferred work.

## Requirements

- Counter-Strike 2 Dedicated Server
- [SwiftlyS2](https://swiftlys2.net/) 1.4.5 or newer
- .NET 10
- MariaDB/MySQL configured through SwiftlyS2
- [BotController](https://github.com/nicedayzhu/cs2-bot-controller) shared API ABI 18

BotController is currently a dependency of the replay implementation, not the abandoned always-running replay bot. `BotControllerApi.dll` is not included in this repository or the release ZIP.

[CS2FlashingHtmlHudFix](https://github.com/girlglock/CS2FlashingHtmlHudFix) is optional but recommended.

## Installation

Use a packaged release unless you are developing the plugin. The full process is in [docs/INSTALLATION.md](docs/INSTALLATION.md).

In short:

1. Install SwiftlyS2 and BotController.
2. Configure a MariaDB connection in SwiftlyS2.
3. Extract `SurfTimer` to `addons/swiftlys2/plugins/SurfTimer`.
4. Start the server once so SwiftlyS2 creates the plugin config.
5. Set a unique `ServerId` and the database connection name in `configs/plugins/surf_timer/config.jsonc`.
6. Restart the server.

Check the installation from the server console:

```text
surftimer_status
surftimer_db_health
surftimer_map_info
surftimer_map_check
```

## Building

Place `BotControllerApi.dll` in `lib/`, then run:

```powershell
dotnet restore
dotnet publish -c Release
```

The published plugin is written to `build/publish/SurfTimer`.

To build the release ZIP and checksum:

```powershell
.\tools\release\Build-Release.ps1
```

## Configuration

The effective config is stored by SwiftlyS2, outside the plugin directory:

```text
addons/swiftlys2/configs/plugins/surf_timer/config.jsonc
```

Each server sharing a database needs a different lowercase `ServerId`. Server-specific map pools and voting ranges stay in that server's config.

```jsonc
{
  "SurfTimer": {
    "Enabled": true,
    "DatabaseConnection": "surftimer",
    "ServerId": "community-easy-1",
    "HudRefreshRateHz": 64,
    "MapVoting": {
      "Enabled": true,
      "MinimumTier": 1,
      "MaximumTier": 2,
      "ExtendMapMinutes": 10
    }
  }
}
```

Example multi-server configs are under [deploy/servers](deploy/servers/README.md).

## Maps

Map definitions live in `resources/configs/maps`. They describe existing mapper triggers; they do not create zones.

After adding or updating a map, load it and run:

```text
surftimer_dump_triggers
surftimer_map_check
```

Use `surftimer_catalog_check` to validate the JSON catalog. See the [bundled map list](resources/configs/maps/README.md).

## Commands

| Area | Commands |
|---|---|
| Timer | `!r`, `!restart` |
| Records | `!pb`, `!wr`, `!top10`, `!rank` |
| Stages | `!s <stage>`, `!rs`, `!stagepb`, `!stagewr`, `!stagetop` |
| Bonuses | `!b <bonus>`, `!rb`, `!bonuspb`, `!bonuswr`, `!bonustop` |
| Replays | `!replay`, `!stagereplay`, `!breplay`, `!replay stop` |
| Practice | `!saveloc`, `!tele`, `!teleprev`, `!telenext`, `!noclip`, `!ncspeed` |
| Preferences | `!settings`, `!hud`, `!speed`, `!status`, `!keys`, `!sounds`, `!replayhud` |
| Players | `!points`, `!ranks`, `!profile`, `!mapstats` |
| Maps | `!mapinfo`, `!stages`, `!bonuses`, `!rtv`, `!nominate`, `!1`–`!6` |

Run `!help` in game for the current command summary. Admin commands require `surftimer.admin`; destructive commands also require `confirm` and are audited.

## Points

Main completion starts at `25 × 2^(tier-1)` Points. Placement adds a Top 100 bonus, with WR receiving the full amount. Only the best 20 map scores per tier count toward the competitive total. Stages share a smaller mastery pool, and each completed bonus route awards 10 Points.

Groups describe main-map percentile:

- G1: top 1%
- G2: top 1–5%
- G3: top 5–10%
- G4: top 10–25%
- G5: top 25–50%

Points are calculated from the current records instead of being stored as a permanent total. Tier changes and leaderboard movement therefore update rankings automatically.

## Development

The local Windows scripts assume a test installation at `C:\CS2Server`. They are development helpers, not part of the packaged plugin.

Run the normal regression suite with:

```powershell
.\tools\Test-All.ps1
```

Useful options are `-IncludeLiveDatabase`, `-IncludeDownloadedMaps` and `-IncludeReleasePackage`.

The website can be run directly with:

```powershell
dotnet run --project .\web\SurfTimer.Web.csproj
```

Production website database settings use `SURFTIMER_DB_CONNECTION_STRING` or the documented `SURFTIMER_DB_*` environment variables. The local database file is ignored by Git.

## Database

Migrations run automatically under a MariaDB advisory lock. Multiple servers can start against the same database without racing the schema upgrade.

Operational scripts are provided for backup, validation and restore:

```powershell
.\tools\local-server\Backup-Database.ps1
.\tools\local-server\Test-DatabaseConsistency.ps1
.\tools\local-server\Restore-Database.ps1 -BackupPath <file.sql> -ConfirmRestore
```

Restore requires the matching SHA-256 sidecar. Migrations are forward-only.

## Project notes

- [Installation](docs/INSTALLATION.md)
- [Feature status](docs/FEATURE-STATUS.md)
- [Release checklist](docs/RELEASE-CHECKLIST.md)
- [Changelog](CHANGELOG.md)
- [SharpTimer importer](tools/sharptimer-import/README.md)
- [Security policy](SECURITY.md)

## License

No open-source license has been selected. Unless a license is added, the source may be viewed but no redistribution or modification rights are granted.
