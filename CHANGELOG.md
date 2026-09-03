# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog, and the project follows Semantic Versioning.

## [Unreleased]

### Changed

- Migrated the test suite from xUnit to MSTest 4.
- Made `develop` the primary development branch and restricted package publishing to automated releases from `main`.

### Added

- Initial open-source extraction of the server-side ASP.NET Core metrics library.
- Monthly SQLite shards with automatic event compaction.
- Page, visitor, source, UTM campaign, and 404 reporting APIs.
- Sample application, automated tests, package metadata, and CI workflows.
- Automated NuGet publishing, package creation, Git tagging, and GitHub Releases for merges to `main`.
