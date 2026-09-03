# Iolys.WebMetrics

[![CI](https://github.com/iolys-company/Iolys.WebMetrics/actions/workflows/ci.yml/badge.svg)](https://github.com/iolys-company/Iolys.WebMetrics/actions/workflows/ci.yml)

Cookie-free, server-side website metrics for ASP.NET Core, stored locally in monthly SQLite databases.

Iolys.WebMetrics records page views and 404 responses without adding client-side JavaScript, cookies, tracking pixels, or a third-party analytics service. It exposes reporting interfaces so each application can build its own administration UI.

> The project is currently in preview. Review the data model and operational limits before using it in production.

## Features

- Server-side collection for successful HTML page views and 404 responses.
- Monthly pseudonymous visitor identifiers derived with HMAC-SHA256.
- UTM source, medium, and campaign reporting.
- Referrer-host, page, visitor, and 404 summaries.
- One local SQLite database per month.
- Automatic compaction of expired monthly databases into aggregate counters.
- Configurable route and 404-noise exclusions.
- No bundled dashboard: applications own their routes, access control, and presentation.

## Requirements

- .NET 10
- ASP.NET Core
- A writable, persistent local directory

The current implementation is designed for a single application instance using a local filesystem. Do not place the SQLite files on a shared network filesystem or mount the same data directory into multiple running instances.

## Installation

Iolys.WebMetrics is not currently published on NuGet. Clone the repository and reference the library project directly while it is under development:

```xml
<ProjectReference Include="path/to/Iolys.WebMetrics/src/Iolys.WebMetrics/Iolys.WebMetrics.csproj" />
```

## Quick start

Register the services:

```csharp
using Iolys.WebMetrics;

builder.Services.AddWebMetrics(
    builder.Configuration.GetSection(WebMetricsOptions.SectionName));
```

Add the middleware after forwarded headers and routing. Place it before output caching so cached responses are counted:

```csharp
app.UseForwardedHeaders();
app.UseRouting();
app.UseWebMetrics();
app.UseOutputCache();
```

Configure trusted proxies and forwarded headers according to your infrastructure. The resolved remote IP address contributes to the monthly visitor identifier.

Minimal configuration:

```json
{
  "WebMetrics": {
    "DataDirectory": "data",
    "DatabasePrefix": "web-metrics",
    "CompactionCheckHours": 6,
    "ExcludedPathPrefixes": [ "/admin", "/healthz", "/error" ],
    "ExcludedNotFoundPathPrefixes": [ "/wp-admin", "/wp-content", "/.env" ],
    "ExcludedNotFoundPathSuffixes": [ ".php" ]
  }
}
```

Persist both the databases and the visitor-key file. For a containerized application, mount `DataDirectory` as a persistent volume.

## Build an application-specific dashboard

Inject `IAnalyticsReportReader` into a Razor Page, controller, or endpoint:

```csharp
public sealed class StatsModel(IAnalyticsReportReader metrics) : PageModel
{
    public AnalyticsDashboard Dashboard { get; private set; } = default!;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Dashboard = await metrics.GetDashboardAsync(30, cancellationToken: cancellationToken);
    }
}
```

Use `IAnalyticsNotFoundManager` to remove a resolved 404 from current events and compacted rollups:

```csharp
await notFoundManager.DeleteNotFoundAsync("/old-path", cancellationToken);
```

The host application is responsible for authenticating and authorizing every dashboard or management endpoint.

## Data model and privacy properties

For the current month, the library stores:

- UTC timestamp and normalized request path;
- a monthly visitor identifier;
- UTM source, medium, and campaign values;
- the referrer host, without its path or query string;
- whether the response was a page view or a 404.

The visitor identifier is an HMAC of the month, remote IP address, user-agent, and accepted languages. The raw IP address, user-agent, and accepted languages are processed in memory but are not written to the analytics database. The random HMAC key is stored in `DataDirectory` and must remain persistent.

After a month expires, individual events and visitor identifiers are replaced by aggregate daily and dimensional counters. The compacted database remains available for historical reporting.

The middleware uses heuristic bot and prefetch filtering. It does not claim to identify every automated request and it does not implement consent or policy decisions for the host application. Deployers remain responsible for reviewing their configuration and obligations.

## Configuration

| Setting | Default | Purpose |
| --- | --- | --- |
| `DataDirectory` | `data` | Directory containing SQLite shards and the visitor key. |
| `DatabasePrefix` | `analytics` | Prefix used for generated files. |
| `CompactionCheckHours` | `6` | Interval between background compaction checks. |
| `MaximumPathLength` | `1024` | Maximum stored normalized path length. |
| `MaximumDimensionLength` | `100` | Maximum stored UTM dimension length. |
| `ExcludedPathPrefixes` | `/admin`, `/healthz`, `/error` | Request paths excluded from all collection. |
| `ExcludedNotFoundPathPrefixes` | `/wp-admin` | 404 paths reported in the ignored-noise view. |
| `ExcludedNotFoundPathSuffixes` | `.php` | 404 suffixes reported in the ignored-noise view. |

## Run the sample

```console
dotnet run --project samples/Iolys.WebMetrics.Sample
```

Open the displayed local URL, visit a few pages, then open `/admin/metrics`. The JSON reporting endpoint is available only in the Development environment.

## Build and test

```console
dotnet restore Iolys.WebMetrics.slnx
dotnet build Iolys.WebMetrics.slnx --configuration Release --no-restore
dotnet test tests/Iolys.WebMetrics.Tests --configuration Release --no-build
dotnet pack src/Iolys.WebMetrics --configuration Release --no-build
```

## Versioning and support

The package follows Semantic Versioning. While the version is below `1.0.0`, public API and storage-format changes may occur between minor releases and will be documented in [CHANGELOG.md](CHANGELOG.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Please report security concerns through the process described in [SECURITY.md](SECURITY.md).

## License

Iolys.WebMetrics is available under the [MIT License](LICENSE).
