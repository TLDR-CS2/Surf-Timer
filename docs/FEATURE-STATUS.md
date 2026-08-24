# Feature status

SurfTimer borrows familiar commands and ideas from SharpTimer, but it is not intended to be a drop-in behavioural clone.

## Implemented

| Area | Current state | Checked by |
|---|---|---|
| Plugin lifecycle | DI-composed SwiftlyS2 plugin, hot-load-aware services and clean unregister paths | Release build and two-instance smoke test |
| Player lifecycle | SteamID64 sessions, auth/connect/disconnect/team/death handling | Lifecycle regression suite and server logs |
| Timing | Engine timestamps, leave-start semantics, ordered checkpoints and stages | Lifecycle regression suite |
| Maps | Mapper-trigger-only definitions, Tier 1–7 metadata, compatibility reports | Catalog and downloaded-VPK trigger suites |
| Routes | Main, staged and multi-bonus runs | Database/API route tests |
| Records | Global PB/WR/top ten, splits, completions and deterministic ties | API suite and database load tests |
| Points | Exponential tiers, Top 100 decay, configured-stage-count mastery, bonuses and ladder titles | Points boundary regression tests and API suite |
| Replays | Versioned compressed 64 Hz main/stage/bonus PB captures and native first-person playback | Replay codec regression suite |
| HUD | 64 Hz timer, speed, status, progress, keys and spectator data | 32-player synthetic soak harness |
| Practice | Saved locations, navigation, noclip and configurable noclip speed | State/invalidation code paths; in-game feel remains manual |
| Preferences | Persistent HUD, speed, status, keys, sounds and replay HUD settings | MariaDB migrations and API/database audits |
| Voting | Toggleable nominations/RTV, tier-filtered pools, early close, delayed change and extension | Configuration validation; final UI behavior is manual |
| Administration | Permission-gated metadata, validation, record/replay deletion and audit entries | Database consistency audit |
| Website/API | Global maps, routes, players, activity, history, profiles and Points | 22-request live API integration suite |
| Operations | Versioned releases, checksums, backup/restore, rollback and multi-server configs | Release and deployment smoke scripts |
| Import | Dry-run-first SharpTimer MariaDB importer | Isolated importer tooling; real source dataset still required |

## Out of scope

- Bhop, KZ and non-Surf modes.
- Manual/fallback zones and an in-game zone editor. Mapper triggers are authoritative.
- Server-specific records or player history views. Records are global across the shared database.
- Tier 8. The catalog stops at Tier 7.

## Deferred

| Feature | Reason / next dependency |
|---|---|
| Always-running WR replay bot | Current CS2 bot lifecycle/spectator integration proved crash-prone; native playback remains stable |
| Bot without a player slot | Requires an API/runtime mechanism not currently demonstrated safely by SwiftlyS2/CS2 |
| SharpTimer production import | Needs a real source database backup and exact schema sample |
| Production web hosting | Deferred until the project is intended to go live |
| Discord/webhook integration | No endpoint, credentials or notification policy selected |
| Anti-cheat enforcement | Telemetry and invalidation exist; punitive policy/integration is intentionally not assumed |
| License | No licence has been selected |

## Still requires in-game testing

Automated tests cannot verify trigger touch behaviour, teleport placement, HUD appearance, menu controls, spectator transitions, sound or replay camera feel. New work in those areas still needs a client test.
