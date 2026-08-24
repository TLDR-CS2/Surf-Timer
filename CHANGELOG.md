# Changelog

All notable changes to SurfTimer are documented here.

## 0.1.0 - 2026-08-24

Initial release.

### Timer and routes

- Mapper-trigger-only Surf timing with leave-start semantics.
- Linear maps, ordered checkpoints, staged maps, and multiple bonus routes.
- Main, stage, and bonus restarts and direct route teleport commands.
- Engine-time microsecond records with PB, WR, Top 10, splits, and completion counts.

### Replays and HUD

- Versioned compressed 64 Hz PB replays for main, stage, and bonus routes.
- Native first-person playback through BotController ABI 18 with compatibility fallback.
- Configurable 64 Hz HUD, speed gradient, timer state, progress, keys, and spectator HUD.

### Rankings and persistence

- Shared MariaDB records across multiple servers.
- Dynamic Points with Tier 1–7 weighting, Top 100 placement decay, stage mastery, bonuses, groups, and titles.
- Persistent preferences, player statistics, PB histories, validation telemetry, and admin auditing.
- Thirteen forward-only schema migrations with advisory-lock coordination.

### Server features

- Practice locations, teleport navigation, noclip, and configurable noclip speed.
- Toggleable nominations and RTV with tier-filtered pools, extension, early close, and delayed map changes.
- Sixteen bundled map definitions covering Tier 1–7, stages, checkpoints, and multi-bonus maps.
- Read-only website and JSON API for maps, records, players, history, activity, and Points.
- Backup, restore, release validation, SharpTimer import, load-test, and deployment tooling.

### Known limitations

- BotController API ABI 18 is a dependency of this release's replay implementation.
- The always-running WR replay bot is disabled.
- Windows is the currently tested server environment.
- SharpTimer import requires validation against the operator's source database before committing changes.
