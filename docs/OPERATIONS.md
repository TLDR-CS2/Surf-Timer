# Operations

## Shared database and rulesets

Give each instance a unique `ServerId`. Set `CatalogAuthorityServerId` to the same designated server ID on every instance. The owner publishes shared tiers, enabled flags and route counts; voting pools remain local.

Ownership is stored in `st_catalog_authority`. An empty setting lets the first eligible server claim it. Changing the config does not transfer an existing claim. Stop the instances and update the ownership row when transferring it.

Migration 019 adds a competitive fingerprint per map in `st_competitive_rulesets`. The authority's first valid finish establishes the baseline. Other instances must match it. Configure the authority before accepting runs. Movement and route settings contribute to the fingerprint; Workshop binary revisions do not. Changing monitored competitive settings invalidates active runs.

Runs waiting for an absent baseline stay queued and recover after the authority establishes a matching baseline. Mismatches and missing legacy fingerprints go to quarantine. Existing leaderboard records retain their legacy status. Before replacing a baseline, archive or migrate the affected leaderboard so incompatible rulesets do not share records.

## Pending runs

Run envelopes and replays are flushed to `pending-runs` in the Swiftly plugin data directory before database submission. Back up this directory, including `quarantine`, and preserve it during upgrades.

Recovery runs every 15 seconds, processes at most 32 files per pass, and backs off failed saves for up to five minutes. Runs waiting for authority approval do not block other maps. Each transaction writes a unique run ID to `st_run_receipts`, so retries do not duplicate completions. Finish timestamps survive recovery. Queue order is local to each server; cross-server backfills cannot reconstruct all historical PB changes.

`!stadmin` reports pending count and bytes, oldest file age, quarantine count, retry deadline, current fingerprint and stage certification. Disk-write failures require operator intervention: a run that could not be written is not safely queued.

Invalid JSON, rejected rulesets and uncertified independent-stage envelopes move to `pending-runs/quarantine`. Check the logged reason, fix the cause, then move reviewed files back to `pending-runs` while the plugin is stopped. Preserve filenames and run IDs. Do not rewrite fingerprints or certification flags to admit rejected runs. Older quarantined files are not reclassified automatically.

There is no automatic queue expiry or disk quota. Monitor disk use. Receipts and moderation archives also need a retention policy that accounts for outstanding retries.

## Migrations and moderation

Migrations are forward-only and use a database advisory lock. Applied file checksums are verified; supported partially applied column additions can resume. Do not edit deployed migrations. For older migrations, the first stored checksum becomes the baseline.

| Migration | Tables or behavior |
|---|---|
| 016 | Run receipts and catalog ownership |
| 017 | Record archives |
| 018 | Run ruleset provenance |
| 019 | Competitive map baselines |

Admin commands require `surftimer.admin`:

- `!stinvalidate <SteamID64> <main|stage|bonus> <index> <reason>` archives a record on the current map and returns its archive ID. Main uses index `0`.
- `!strestore <archive ID>` restores archived evidence. A replacement record can block restoration.
- `!stdeletepb` also archives evidence.

Moderation records the actor and reason and preserves replay evidence. A new PB without a capture removes the previous PB's replay.

## Independent stages and practice

`!s <stage>` and `!rs` are practice teleports. `!stageattempt <stage>` starts an independent attempt from zero velocity. Stage 1 starts on leaving main start; later stages use entry boundaries. The final stage ends at map end.

Independent attempts are unranked unless the map sets `IndependentStageRecordsCertified: true`. Certify only after checking equivalent starting geometry and entry conditions against full-run splits. Maps with detected checkpoints remain unranked until per-stage checkpoint requirements are supported.

`!saveloc [name]` accepts nonnumeric names up to 32 characters; names are case-insensitive and saving an existing name replaces it. `!tele [name|number]`, `!locs`, `!delloc [name|number]` and `!clearlocs` manage locations. Locations last for the session/map. Clearing them leaves practice/noclip active.

## Settings and hot reload

Saved preferences load on connect and hot reload. Commands issued before loading completes ask the player to retry; those commands are not queued. A failed load can be retried by the next settings command. `!sounds` toggles private finish feedback.

`Enabled=false` skips service startup. Reload after enabling the plugin. `DebugLogging=true` adds startup diagnostics.

Same-process hot reload preserves vote deadlines, extensions and RTV locks. A process restart starts a fresh vote timer. Inspect runtime map failures with `!stquarantine`, retest the current map with `!stquarantine retest`, or clear a map's failures with `!stquarantine reinstate <exact-map-name>`. Enabled flags and tier filters still apply after reinstatement.

## Validation

Run `tools/Test-All.ps1` for local builds and regression checks. For a disposable database, use `tools/storage-tests/Run-Isolated.ps1 -MariaDbBin <bin-directory> -DotNetPath <dotnet-executable>`.

`-IncludeLiveDatabase` uses the local database configuration unless a test connection is supplied. Review the [storage test setup](../tools/storage-tests/README.md) before using it.

Complete [in-game validation](IN-GAME-VALIDATION.md) before a release. Automated checks do not cover native movement hooks, trigger ordering, replay camera behavior or finish audio in CS2.
