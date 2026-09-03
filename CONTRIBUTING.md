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

By contributing, you agree that your contribution is licensed under the repository's MIT License.
