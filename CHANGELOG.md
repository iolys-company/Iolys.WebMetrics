# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog, and the project follows Semantic Versioning.

## [0.2.0] - 2026-09-19

### Added

- Report referring sites as their own dashboard dimension through `AnalyticsDashboard.Referrers`, counted from each visit's entry referrer host and independent of UTM parameters.
- Store referring hosts in a new `referrer_rollup` table so archived months keep the dimension; shards are upgraded to schema version 4 automatically, rebuilding history from the existing `referral` source rows.

### Changed

- **Breaking:** `AnalyticsDashboard` takes a `Referrers` list between `Sources` and `UtmSources`; code constructing the record positionally must be updated.

## [0.1.2] - 2026-09-15

### Fixed

- Attribute internal navigation and subsequent referrer-less pages to the visit's entry source and campaign, with a 30-minute inactivity timeout.
- Recalculate attribution for existing raw events and preserve it during monthly compaction without changing the database schema or adding cookies.
- Report unrecoverable internal entry sources and previously compacted internal counts as `unknown`.
- Document that visitor counts across source and campaign groups overlap and must not be added into a unique-visitor total.

## [0.1.1] - 2026-09-03

### Changed

- Added Iolys branding to the NuGet package README.

## [0.1.0] - 2026-09-03

### Changed

- Migrated the test suite from xUnit to MSTest 4.
- Made `develop` the primary development branch and restricted package publishing to automated releases from `main`.
- Separated short-lived preview packages from stable `main` releases.

### Added

- Initial open-source extraction of the server-side ASP.NET Core metrics library.
- Monthly SQLite shards with automatic event compaction.
- Page, visitor, source, UTM campaign, and 404 reporting APIs.
- Sample application, automated tests, package metadata, and CI workflows.
- Automated NuGet publishing, package creation, Git tagging, and GitHub Releases for merges to `main`.
