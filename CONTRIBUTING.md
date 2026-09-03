# Contributing

Contributions, bug reports, and documentation improvements are welcome.

## Development setup

Install a .NET 10 SDK compatible with `global.json`, then run:

```console
dotnet restore Iolys.WebMetrics.slnx
dotnet build Iolys.WebMetrics.slnx --configuration Release --no-restore
dotnet test tests/Iolys.WebMetrics.Tests --configuration Release --no-build
```

## Pull requests

- Open pull requests against the `develop` branch.
- Keep changes focused and explain their observable behavior.
- Add tests for bug fixes and new behavior.
- Preserve existing monthly database compatibility or document the migration.
- Update `README.md` and `CHANGELOG.md` when public behavior changes.
- Never commit analytics databases, visitor keys, credentials, or production request data.

## Releases

Regular changes are merged into `develop`. To prepare a release:

1. Update `VersionPrefix` in `src/Iolys.WebMetrics/Iolys.WebMetrics.csproj`.
2. Set `VersionSuffix` for a prerelease, or remove it for a stable release.
3. Update `CHANGELOG.md`.
4. Open a pull request from `develop` to `main`.

After the pull request passes CI and is merged, the release workflow creates the NuGet packages, a matching Git tag, and a GitHub Release. It deliberately does not publish to NuGet.org.

By contributing, you agree that your contribution is licensed under the repository's MIT License.
