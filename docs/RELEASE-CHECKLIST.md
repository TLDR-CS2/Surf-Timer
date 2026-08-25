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
