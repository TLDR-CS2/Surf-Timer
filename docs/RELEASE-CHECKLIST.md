# Release checklist

## Before tagging

- Run `tools/Test-All.ps1 -IncludeLiveDatabase -IncludeReleasePackage` against a disposable or development database.
- Complete the outstanding in-game map, route, voting, replay, and clean-install acceptance tests.
- Confirm `BuildInfo.Version`, project versions, website version, changelog, and tag agree.
- Build the release with `tools/release/Build-Release.ps1`.
- Verify the generated ZIP and `.sha256` sidecar.
- Confirm the ZIP contains no credentials, effective server configuration, development symbols, or local paths.
- Create and verify a database backup before upgrading an existing community.

## GitHub release

- Commit the release source.
- Create an annotated `v0.1.0` tag on the tested commit.
- Push the branch and tag.
- Create a GitHub Release using `CHANGELOG.md` as the basis for release notes.
- Attach `SurfTimer-0.1.0.zip` and `SurfTimer-0.1.0.zip.sha256`.
- Mark it as a pre-release if the in-game and clean-install acceptance checklist is incomplete.

## After publishing

- Download the attachments from GitHub and verify their SHA-256 independently.
- Install the downloaded artifact on a clean SwiftlyS2 instance.
- Run the diagnostics in `docs/INSTALLATION.md`.
- Record any discovered incompatibility in the release notes before promoting it beyond pre-release status.
