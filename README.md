# SurfTimer

SurfTimer is a Counter-Strike 2 Surf timer for [SwiftlyS2](https://github.com/swiftly-solution/swiftlys2). It uses mapper-provided triggers and stores global records in MariaDB/MySQL.

## Features

- Linear, staged and bonus routes, plus independent stage attempts
- Ordered checkpoints and stage splits
- PB, WR, Top 10 and completion tracking
- 64 Hz timer HUD with speed, keys and spectator information
- Main, stage and bonus replays with private playback controls
- Named practice locations, teleports and noclip
- Persistent player settings and profiles
- Tier 1–7 Points rankings
- Nominations, RTV, timed votes and map extensions
- Shared records across multiple servers, durable save retries and reversible moderation
- Read-only leaderboard website and JSON API
- Dry-run SharpTimer MariaDB importer

## Requirements

- Counter-Strike 2 Dedicated Server
- [SwiftlyS2](https://swiftlys2.net/) 1.4.5 or newer
- .NET 10 runtime
- MariaDB or MySQL configured in SwiftlyS2
- [BotController](https://github.com/nicedayzhu/cs2-bot-controller) shared API ABI 18

[CS2FlashingHtmlHudFix](https://github.com/girlglock/CS2FlashingHtmlHudFix) is recommended.

## Installation

1. Install SwiftlyS2, Metamod and BotController. Check the [native loader setup](docs/REPLAYS.md#native-playback-setup).
2. Configure a MariaDB/MySQL connection in SwiftlyS2.
3. Extract the release to `game/csgo/addons/swiftlys2/plugins/SurfTimer`.
4. Start the server once to create the plugin config.
5. Set `DatabaseConnection`, `ServerId` and map-voting options in `configs/plugins/surf_timer/config.jsonc`.
6. Restart the server.

Verify the installation from the server console:

```text
surftimer_status
surftimer_db_health
surftimer_catalog_check
surftimer_map_check
```

See [Installation](docs/INSTALLATION.md) for upgrades and multi-server setup.

## Configuration

Each server connected to the same database must have a unique `ServerId`. Records and Points are global; map pools and voting settings are per server.

```jsonc
{
  "SurfTimer": {
    "Enabled": true,
    "DatabaseConnection": "surftimer",
    "ServerId": "surf-easy-1",
    "CatalogAuthorityServerId": "surf-easy-1",
    "MaximumReplayMinutes": 30,
    "HudRefreshRateHz": 64,
    "MapVoting": {
      "Enabled": true,
      "RockTheVoteEnabled": true,
      "NominationEnabled": true,
      "EndOfMapVoteEnabled": true,
      "ForceVoteAfterMinutes": 15,
      "VoteDurationSeconds": 60,
      "ExtendMapMinutes": 10,
      "MaximumMapExtensions": 3,
      "MinimumTier": 1,
      "MaximumTier": 2
    }
  }
}
```

Example server profiles are available in [deploy/servers](deploy/servers/README.md).

## Maps

Map definitions are stored in `resources/configs/maps`. They identify existing mapper triggers; SurfTimer does not create zones. Optional `CancelTriggers` entries invalidate both main and bonus timing when a player crosses a hub/route-transition trigger.

After adding or updating a definition, load the map and run:

```text
surftimer_dump_triggers
surftimer_map_check
```

Run `surftimer_catalog_check` to validate the complete catalog. The bundled maps are listed in [resources/configs/maps](resources/configs/maps/README.md).

## Commands

| Area | Commands |
|---|---|
| Timer | `!r`, `!restart` |
| Records | `!pb`, `!wr`, `!top10`, `!rank` |
| Stages | `!s <stage>`, `!rs`, `!stageattempt <stage>`, `!stagepb`, `!stagewr`, `!stagetop` |
| Bonuses | `!b <bonus>`, `!rb`, `!bonuspb`, `!bonuswr`, `!bonustop` |
| Replays | `!replay`, `!stagereplay`, `!breplay`, `!replay stop`, `!replay pause/resume`, `!replay seek <seconds>`, `!replay speed <0.25-4>` |
| Practice | `!saveloc [name]`, `!tele [name|number]`, `!locs`, `!delloc`, `!clearlocs`, `!teleprev`, `!telenext`, `!noclip`, `!ncspeed` |
| Settings | `!settings`, `!hud`, `!speed`, `!status`, `!keys`, `!sounds`, `!replayhud` |
| Players | `!points`, `!ranks`, `!profile`, `!mapstats` |
| Maps | `!mapinfo`, `!stages`, `!bonuses`, `!rtv`, `!nominate`, `!nextmap`, `!1`–`!6` |

Checkpoint and stage touches show transient PB/WR deltas on the HUD. `SplitComparisons.Reference` selects `PB`, `WR`, `Both`, or `Off`. Map voting ignores players idle beyond `AfkAfterSeconds`, balances random candidates across tiers, and automatically quarantines maps after repeated runtime compatibility failures.

Use `!help` for the in-game command summary. Admin commands require `surftimer.admin`; moderation is audited. Use `!stinvalidate` with a reason and `!strestore` with the returned archive ID. `!stquarantine` lists compatibility failures and supports `retest` and `reinstate <map>`.

## Points

Main completion starts at `25 × 2^(tier-1)` Points. Placement adds a Top 100 bonus, with WR receiving the full amount. The best 20 map scores per tier count toward the competitive total. Stages use a smaller mastery pool, and each completed bonus route awards 10 Points.

Main-map groups are percentile based:

- G1: top 1%
- G2: top 1–5%
- G3: top 5–10%
- G4: top 10–25%
- G5: top 25–50%

Points are calculated from current records, so tier changes and leaderboard movement are reflected automatically.

## Building

Place `BotControllerApi.dll` in `lib/`, then run:

```powershell
dotnet restore
dotnet publish -c Release
```

The published plugin is written to `build/publish/SurfTimer`. Build a release archive with:

```powershell
.\tools\release\Build-Release.ps1
```

For local regression checks, install Node.js and run `tools/Test-All.ps1`. `-DotNetPath` and `-NodePath` accept explicit executables; `-PackagesPath` selects an existing NuGet cache when needed. The .NET resolver checks PATH and then the optional local research SDK; `global.json` selects .NET 10 stable feature bands. The default checks do not write to a live database or install a server.

## Database

Migrations run automatically and are protected by a MariaDB advisory lock. Migrations are forward-only; back up the database before upgrading.

## Additional documentation

- [Installation and upgrades](docs/INSTALLATION.md)
- [Server operations](docs/OPERATIONS.md)
- [Replay setup and troubleshooting](docs/REPLAYS.md)
- [In-game validation](docs/IN-GAME-VALIDATION.md)
- [Changelog](CHANGELOG.md)
- [SharpTimer importer](tools/sharptimer-import/README.md)
- [Security policy](SECURITY.md)

## License

No open-source licence is currently granted. See the repository terms before redistributing or modifying the source.
