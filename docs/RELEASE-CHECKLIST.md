# Release checklist

- Set the same version in `BuildInfo`, both project files, the web API and `CHANGELOG.md`.
- Run `tools/Test-All.ps1 -IncludeLiveDatabase -IncludeReleasePackage`.
- Complete in-game tests for timing, routes, HUD, replays and voting.
- Back up the database.
- Run `tools/release/Build-Release.ps1`.
- Verify the ZIP and SHA-256 file.
- Confirm the package contains no credentials, server configs, symbols or machine-specific paths.
- Tag the tested commit and create the GitHub release.
- Attach the ZIP and `.sha256` file.
- Install the downloaded release on a clean SwiftlyS2 server and run the checks in [INSTALLATION.md](INSTALLATION.md).

## Regression checks

- Run the default local suites (including Node frontend and isolated payload swap failure tests) before opting into live checks.
- On a staging database, apply migrations 016–019, test partially applied column recovery and checksum rejection, and verify single-owner catalog writes.
- Interrupt database connectivity during a finish; verify the pending JSON survives restart and recovery increments completion counts only once.
- Verify main/bonus/stage PB replacement without a replay clears the old replay and moderation preserves/restores evidence.
- In CS2, test restart immediately after map change, hot reload on an already spawned map, and zero/stale trigger handling.
- Test independent stage 1 start exit, later-stage entry timing, final-stage finish, and invalidation when movement settings change.
- Verify personal replay controls, named locations, replay capture limits, `!sounds off`, and private PB feedback.
- Verify same-process hot reload preserves vote deadlines/extensions and disconnects remove ballots/nominations; exercise quarantine retest/reinstate.
- Review [operations](OPERATIONS.md) and record actual live test results; local builds alone do not certify engine/database behavior.
- Complete [the in-game validation checklist](IN-GAME-VALIDATION.md), including settings restoration and automatic recovery after ruleset approval.
