# Changelog

## Unreleased — 2026-09-07

- Fix native replay stall handling, cursor timing and compatibility interpolation; hide weapons during replay viewing.
- Preserve Metamod/SwiftlyS2 loader order in local server updates.
- Queue runs awaiting the authority ruleset baseline and recover them automatically after approval.
- Preserve saved preferences across early commands and hot reload; serialize player preference saves.
- Repair map restart caches, hot-reload trigger discovery and stale deferred actions.
- Honor global enablement and diagnostic logging; add private finish audio preferences.
- Persist failed saves locally with idempotent database receipts, reconcile PB replay replacement, and protect catalog ownership.
- Add reversible record moderation, migration checksums/recovery, and run ruleset provenance.
- Add independent stage attempts, named practice locations and per-viewer replay controls; bound replay capture duration.
- Preserve timed-vote deadlines/extensions on hot reload and add quarantine administration.
- Correct tied ranks, empty route discovery and unranked profiles; isolate website panel failures and cancel stale requests.
- Stage complete deployment payloads, preflight all targets, keep unique rollback snapshots and test rollback after injected failures.
- Add portable SDK/runtime selection and regression coverage. Live-server/database validation remains a release requirement.

## 1.0.0 - 2026-08-26

First stable release.

- Linear, staged and bonus timing from mapper triggers
- PB, WR, Top 10, splits and completion tracking
- Main, stage and bonus replay recording and playback
- 64 Hz HUD with speed, keys, progress and spectator information
- Practice locations, teleports and noclip
- Tier 1–7 Points, groups and player rankings
- Persistent player settings and profiles
- Tier-filtered nominations, RTV, timed votes and extensions
- Shared MariaDB records across multiple servers
- Sixteen bundled map definitions
- Read-only leaderboard website and JSON API
- Backup, validation, release and SharpTimer import tools
