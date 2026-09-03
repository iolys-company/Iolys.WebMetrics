using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Iolys.WebMetrics;

internal sealed class AnalyticsStore : IAnalyticsReportReader, IAnalyticsNotFoundManager
{
    private const string CurrentSchemaVersion = "3";
    private readonly AnalyticsPaths _paths;
    private readonly AnalyticsDbContextFactory _dbContextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly WebMetricsOptions _options;
    private readonly ILogger<AnalyticsStore> _logger;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly SemaphoreSlim _compactionLock = new(1, 1);
    private readonly HashSet<string> _initializedShards = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _initialized;

    public AnalyticsStore(
        AnalyticsPaths paths,
        AnalyticsDbContextFactory dbContextFactory,
        TimeProvider timeProvider,
        IOptions<WebMetricsOptions> options,
        ILogger<AnalyticsStore> logger)
    {
        _paths = paths;
        _dbContextFactory = dbContextFactory;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
            var currentMonth = FirstDayOfMonth(today);
            await EnsureShardAsync(_paths.GetShardPath(today), currentMonth, cancellationToken);
            await MigrateLegacyDatabaseAsync(today, cancellationToken);
            await CompactExpiredShardsCoreAsync(today, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    internal async Task RecordAsync(AnalyticsEvent analyticsEvent, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var day = DateOnly.FromDateTime(analyticsEvent.OccurredAt.UtcDateTime);
        var shardPath = _paths.GetShardPath(day);
        await EnsureShardAsync(shardPath, FirstDayOfMonth(day), cancellationToken);

        await using var context = _dbContextFactory.Create(shardPath);
        context.Events.Add(new AnalyticsEventEntity
        {
            OccurredAt = analyticsEvent.OccurredAt.ToString("O", CultureInfo.InvariantCulture),
            Day = FormatDay(day),
            VisitorId = analyticsEvent.VisitorId,
            Kind = analyticsEvent.Kind,
            Path = analyticsEvent.Path,
            UtmSource = analyticsEvent.UtmSource,
            UtmMedium = analyticsEvent.UtmMedium,
            UtmCampaign = analyticsEvent.UtmCampaign,
            ReferrerHost = analyticsEvent.ReferrerHost
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task CompactExpiredShardsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        await CompactExpiredShardsCoreAsync(today, cancellationToken);
    }

    public async Task<AnalyticsDashboard> GetDashboardAsync(
        int days,
        NotFoundAnalyticsView notFoundView = NotFoundAnalyticsView.Tracked,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var since = days > 0 ? today.AddDays(1 - days) : (DateOnly?)null;
        var accumulator = new DashboardAccumulator();
        bool IncludeNotFoundPath(string? path) => notFoundView == NotFoundAnalyticsView.Ignored
            ? _options.IsExcludedNotFoundPath(path)
            : !_options.IsExcludedNotFoundPath(path);

        foreach (var (month, path) in _paths.EnumerateShards())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureShardAsync(path, month, cancellationToken);
            await ReadShardAsync(
                path,
                month,
                since,
                today,
                IncludeNotFoundPath,
                accumulator,
                cancellationToken);
        }

        return accumulator.Build(today);
    }

    public async Task DeleteNotFoundAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await InitializeAsync(cancellationToken);

        await _compactionLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var (month, shardPath) in _paths.EnumerateShards())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EnsureShardAsync(shardPath, month, cancellationToken);

                await using var context = _dbContextFactory.Create(shardPath);
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                var rollups = await context.NotFoundRollups
                    .Where(item => item.Path == path)
                    .ToListAsync(cancellationToken);

                await context.Events
                    .Where(item => item.Kind == AnalyticsEventKind.NotFound && item.Path == path)
                    .ExecuteDeleteAsync(cancellationToken);

                if (rollups.Count > 0)
                {
                    foreach (var day in rollups.GroupBy(item => item.Day))
                    {
                        var dailyRollup = await context.DailyRollups.SingleOrDefaultAsync(
                            item => item.Day == day.Key,
                            cancellationToken);
                        if (dailyRollup is not null)
                        {
                            dailyRollup.NotFoundHits = Math.Max(
                                0,
                                dailyRollup.NotFoundHits - day.Sum(item => item.Hits));
                        }
                    }

                    var monthSummary = await context.MonthSummaries.SingleOrDefaultAsync(
                        item => item.Id == 1,
                        cancellationToken);
                    if (monthSummary is not null)
                    {
                        monthSummary.NotFoundHits = Math.Max(
                            0,
                            monthSummary.NotFoundHits - rollups.Sum(item => item.Hits));
                    }

                    context.NotFoundRollups.RemoveRange(rollups);
                    await context.SaveChangesAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            _compactionLock.Release();
        }
    }

    private async Task CompactExpiredShardsCoreAsync(DateOnly today, CancellationToken cancellationToken)
    {
        await _compactionLock.WaitAsync(cancellationToken);
        try
        {
            var currentMonth = FirstDayOfMonth(today);
            foreach (var (month, path) in _paths.EnumerateShards().Where(item => item.Month < currentMonth))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EnsureShardAsync(path, month, cancellationToken);
                await CompactShardAsync(month, path, cancellationToken);
            }
        }
        finally
        {
            _compactionLock.Release();
        }
    }

    private async Task CompactShardAsync(DateOnly month, string path, CancellationToken cancellationToken)
    {
        await using var context = _dbContextFactory.Create(path);
        var compacted = await context.Metadata.SingleAsync(
            item => item.Key == "compacted",
            cancellationToken);
        if (compacted.Value == "1")
        {
            return;
        }

        var pageViews = context.Events
            .AsNoTracking()
            .Where(item => item.Kind == AnalyticsEventKind.PageView);
        var notFound = context.Events
            .AsNoTracking()
            .Where(item => item.Kind == AnalyticsEventKind.NotFound);

        var totalViews = await pageViews.LongCountAsync(cancellationToken);
        var totalVisitors = await pageViews.Select(item => item.VisitorId).Distinct().LongCountAsync(cancellationToken);
        var totalNotFound = await notFound.LongCountAsync(cancellationToken);

        var dailyViews = await pageViews
            .GroupBy(item => item.Day)
            .Select(group => new CountedDay(
                group.Key,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount()))
            .ToListAsync(cancellationToken);
        var dailyNotFound = await notFound
            .GroupBy(item => item.Day)
            .Select(group => new CountedDay(
                group.Key,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount()))
            .ToListAsync(cancellationToken);
        var pages = await pageViews
            .GroupBy(item => new { item.Day, item.Path })
            .Select(group => new CountedPage(
                group.Key.Day,
                group.Key.Path,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount()))
            .ToListAsync(cancellationToken);
        var sources = await pageViews
            .GroupBy(item => new
            {
                Source = item.UtmSource != ""
                    ? item.UtmSource
                    : item.ReferrerHost != "" ? item.ReferrerHost : "direct",
                Medium = item.UtmMedium != ""
                    ? item.UtmMedium
                    : item.ReferrerHost == "internal"
                        ? "internal"
                        : item.ReferrerHost != "" ? "referral" : "none",
                item.Day
            })
            .Select(group => new CountedSource(
                group.Key.Day,
                group.Key.Source,
                group.Key.Medium,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount()))
            .ToListAsync(cancellationToken);
        var utmSources = await pageViews
            .Where(item => item.UtmSource != "")
            .GroupBy(item => new { item.Day, Source = item.UtmSource })
            .Select(group => new CountedUtmSource(
                group.Key.Day,
                group.Key.Source,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount()))
            .ToListAsync(cancellationToken);
        var utmMediums = await pageViews
            .Where(item => item.UtmMedium != "")
            .GroupBy(item => new { item.Day, Medium = item.UtmMedium })
            .Select(group => new CountedUtmMedium(
                group.Key.Day,
                group.Key.Medium,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount()))
            .ToListAsync(cancellationToken);
        var campaigns = await pageViews
            .Where(item => item.UtmSource != "" || item.UtmMedium != "" || item.UtmCampaign != "")
            .GroupBy(item => new
            {
                item.Day,
                Source = item.UtmSource,
                Medium = item.UtmMedium,
                Campaign = item.UtmCampaign
            })
            .Select(group => new CountedCampaign(
                group.Key.Day,
                group.Key.Source,
                group.Key.Medium,
                group.Key.Campaign,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount()))
            .ToListAsync(cancellationToken);
        var notFoundPaths = await notFound
            .GroupBy(item => new { item.Day, item.Path })
            .Select(group => new CountedPage(
                group.Key.Day,
                group.Key.Path,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount()))
            .ToListAsync(cancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await MergeMonthSummaryAsync(context, month, totalViews, totalVisitors, totalNotFound, cancellationToken);
        await MergeDailyAsync(context, dailyViews, dailyNotFound, cancellationToken);
        await MergePagesAsync(context, pages, cancellationToken);
        await MergeSourcesAsync(context, sources, cancellationToken);
        await MergeUtmSourcesAsync(context, utmSources, cancellationToken);
        await MergeUtmMediumsAsync(context, utmMediums, cancellationToken);
        await MergeCampaignsAsync(context, campaigns, cancellationToken);
        await MergeNotFoundAsync(context, notFoundPaths, cancellationToken);
        compacted.Value = "1";
        await context.SaveChangesAsync(cancellationToken);
        await context.Events.ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        try
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("VACUUM;", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("PRAGMA optimize;", cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Analytics shard {Month} was compacted but SQLite could not reclaim its unused pages immediately.",
                FormatMonth(month));
        }

        _logger.LogInformation("Compacted and anonymized analytics shard {Month} at {Path}.", FormatMonth(month), path);
    }

    private async Task EnsureShardAsync(string path, DateOnly month, CancellationToken cancellationToken)
    {
        lock (_initializedShards)
        {
            if (_initializedShards.Contains(path))
            {
                return;
            }
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            lock (_initializedShards)
            {
                if (_initializedShards.Contains(path))
                {
                    return;
                }
            }

            await using var context = _dbContextFactory.Create(path);
            await context.Database.OpenConnectionAsync(cancellationToken);
            await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous = NORMAL;", cancellationToken);

            var metadataTableExists = await context.Database
                .SqlQueryRaw<long>(
                    "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'metadata'")
                .SingleAsync(cancellationToken) > 0;
            if (!metadataTableExists)
            {
                var existingTables = await context.Database
                    .SqlQueryRaw<long>(
                        "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'")
                    .SingleAsync(cancellationToken);
                if (existingTables > 0)
                {
                    throw new InvalidOperationException($"Unsupported analytics database schema at '{path}'.");
                }

                await context.Database.EnsureCreatedAsync(cancellationToken);
            }
            else
            {
                var schemaVersion = await context.Metadata.AsNoTracking()
                    .Where(item => item.Key == "schema_version")
                    .Select(item => item.Value)
                    .SingleOrDefaultAsync(cancellationToken);
                if (schemaVersion == "1")
                {
                    await UpgradeVersionOneShardAsync(context, month, cancellationToken);
                }
                else if (schemaVersion == "2")
                {
                    await UpgradeVersionTwoShardAsync(context, cancellationToken);
                }
                else if (schemaVersion != CurrentSchemaVersion)
                {
                    throw new InvalidOperationException(
                        $"Analytics database '{path}' uses unsupported schema version '{schemaVersion ?? "unknown"}'.");
                }
            }

            lock (_initializedShards)
            {
                _initializedShards.Add(path);
            }
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async Task UpgradeVersionOneShardAsync(
        AnalyticsDbContext context,
        DateOnly month,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var monthValue = FormatMonth(month);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            CREATE TABLE IF NOT EXISTS month_summary (
                id INTEGER NOT NULL PRIMARY KEY,
                month TEXT NOT NULL,
                views INTEGER NOT NULL,
                visitors INTEGER NOT NULL,
                not_found_hits INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS daily_rollup (
                day TEXT NOT NULL PRIMARY KEY,
                views INTEGER NOT NULL,
                visitors INTEGER NOT NULL,
                not_found_hits INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS page_rollup (
                day TEXT NOT NULL,
                path TEXT NOT NULL,
                views INTEGER NOT NULL,
                visitors INTEGER NOT NULL,
                PRIMARY KEY (day, path)
            );

            CREATE TABLE IF NOT EXISTS source_rollup (
                day TEXT NOT NULL,
                source TEXT NOT NULL,
                medium TEXT NOT NULL,
                views INTEGER NOT NULL,
                visitors INTEGER NOT NULL,
                PRIMARY KEY (day, source, medium)
            );

            CREATE TABLE IF NOT EXISTS utm_source_rollup (
                day TEXT NOT NULL,
                source TEXT NOT NULL,
                views INTEGER NOT NULL,
                visitors INTEGER NOT NULL,
                PRIMARY KEY (day, source)
            );

            CREATE TABLE IF NOT EXISTS utm_medium_rollup (
                day TEXT NOT NULL,
                medium TEXT NOT NULL,
                views INTEGER NOT NULL,
                visitors INTEGER NOT NULL,
                PRIMARY KEY (day, medium)
            );

            CREATE TABLE IF NOT EXISTS campaign_rollup (
                day TEXT NOT NULL,
                source TEXT NOT NULL,
                medium TEXT NOT NULL,
                campaign TEXT NOT NULL,
                views INTEGER NOT NULL,
                visitors INTEGER NOT NULL,
                PRIMARY KEY (day, source, medium, campaign)
            );

            CREATE TABLE IF NOT EXISTS not_found_rollup_v2 (
                day TEXT NOT NULL,
                path TEXT NOT NULL,
                hits INTEGER NOT NULL,
                visitors INTEGER NOT NULL,
                PRIMARY KEY (day, path)
            );

            INSERT OR REPLACE INTO month_summary (id, month, views, visitors, not_found_hits)
            SELECT
                1,
                {monthValue},
                (SELECT COALESCE(SUM(views), 0) FROM page_view_rollup),
                (SELECT COUNT(DISTINCT NULLIF(visitor_id, '')) FROM page_view_rollup),
                (SELECT COALESCE(SUM(hits), 0) FROM not_found_rollup)
            WHERE EXISTS (SELECT 1 FROM page_view_rollup) OR EXISTS (SELECT 1 FROM not_found_rollup);

            INSERT OR REPLACE INTO daily_rollup (day, views, visitors, not_found_hits)
            SELECT
                days.day,
                COALESCE((SELECT SUM(views) FROM page_view_rollup WHERE day = days.day), 0),
                COALESCE((SELECT COUNT(DISTINCT NULLIF(visitor_id, '')) FROM page_view_rollup WHERE day = days.day), 0),
                COALESCE((SELECT SUM(hits) FROM not_found_rollup WHERE day = days.day), 0)
            FROM (
                SELECT day FROM page_view_rollup
                UNION
                SELECT day FROM not_found_rollup
            ) AS days;

            INSERT OR REPLACE INTO page_rollup (day, path, views, visitors)
            SELECT day, path, SUM(views), COUNT(DISTINCT NULLIF(visitor_id, ''))
            FROM page_view_rollup
            GROUP BY day, path;

            INSERT OR REPLACE INTO source_rollup (day, source, medium, views, visitors)
            SELECT
                day,
                CASE WHEN utm_source <> '' THEN utm_source
                     WHEN referrer_host <> '' THEN referrer_host
                     ELSE 'direct' END,
                CASE WHEN utm_medium <> '' THEN utm_medium
                     WHEN referrer_host = 'internal' THEN 'internal'
                     WHEN referrer_host <> '' THEN 'referral'
                     ELSE 'none' END,
                SUM(views),
                COUNT(DISTINCT NULLIF(visitor_id, ''))
            FROM page_view_rollup
            GROUP BY day,
                CASE WHEN utm_source <> '' THEN utm_source
                     WHEN referrer_host <> '' THEN referrer_host
                     ELSE 'direct' END,
                CASE WHEN utm_medium <> '' THEN utm_medium
                     WHEN referrer_host = 'internal' THEN 'internal'
                     WHEN referrer_host <> '' THEN 'referral'
                     ELSE 'none' END;

            INSERT OR REPLACE INTO utm_source_rollup (day, source, views, visitors)
            SELECT day, utm_source, SUM(views), COUNT(DISTINCT NULLIF(visitor_id, ''))
            FROM page_view_rollup
            WHERE utm_source <> ''
            GROUP BY day, utm_source;

            INSERT OR REPLACE INTO utm_medium_rollup (day, medium, views, visitors)
            SELECT day, utm_medium, SUM(views), COUNT(DISTINCT NULLIF(visitor_id, ''))
            FROM page_view_rollup
            WHERE utm_medium <> ''
            GROUP BY day, utm_medium;

            INSERT OR REPLACE INTO campaign_rollup (day, source, medium, campaign, views, visitors)
            SELECT day, utm_source, utm_medium, utm_campaign, SUM(views), COUNT(DISTINCT NULLIF(visitor_id, ''))
            FROM page_view_rollup
            WHERE utm_source <> '' OR utm_medium <> '' OR utm_campaign <> ''
            GROUP BY day, utm_source, utm_medium, utm_campaign;

            INSERT OR REPLACE INTO not_found_rollup_v2 (day, path, hits, visitors)
            SELECT day, path, SUM(hits), COUNT(DISTINCT NULLIF(visitor_id, ''))
            FROM not_found_rollup
            GROUP BY day, path;

            DROP VIEW IF EXISTS page_views_query;
            DROP VIEW IF EXISTS not_found_query;
            DROP TABLE page_view_rollup;
            DROP TABLE not_found_rollup;
            UPDATE metadata SET value = '3' WHERE key = 'schema_version';
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task UpgradeVersionTwoShardAsync(
        AnalyticsDbContext context,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS utm_medium_rollup (
                day TEXT NOT NULL,
                medium TEXT NOT NULL,
                views INTEGER NOT NULL,
                visitors INTEGER NOT NULL,
                PRIMARY KEY (day, medium)
            );

            INSERT OR REPLACE INTO utm_medium_rollup (day, medium, views, visitors)
            SELECT day, medium, SUM(views), SUM(visitors)
            FROM campaign_rollup
            WHERE medium <> ''
            GROUP BY day, medium;

            UPDATE metadata SET value = '3' WHERE key = 'schema_version';
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task MigrateLegacyDatabaseAsync(DateOnly today, CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.LegacyDatabasePath) || File.Exists(_paths.LegacyMigrationMarkerPath))
        {
            return;
        }

        await using var legacyContext = _dbContextFactory.Create(_paths.LegacyDatabasePath, readOnly: true);
        var pageViewsTableExists = await legacyContext.Database
            .SqlQueryRaw<long>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'page_views'")
            .SingleAsync(cancellationToken) > 0;
        if (!pageViewsTableExists)
        {
            return;
        }

        var rows = await legacyContext.Database.SqlQueryRaw<LegacyPageViewRow>("""
                SELECT
                    day AS Day,
                    path AS Path,
                    utm_source AS UtmSource,
                    utm_medium AS UtmMedium,
                    utm_campaign AS UtmCampaign,
                    referrer_host AS ReferrerHost,
                    views AS Views
                FROM page_views
                """)
            .ToListAsync(cancellationToken);

        var currentMonth = FirstDayOfMonth(today);
        foreach (var group in rows
                     .Where(row => TryParseDay(row.Day, out _))
                     .GroupBy(row => FirstDayOfMonth(ParseDay(row.Day))))
        {
            var month = group.Key;
            var shardPath = _paths.GetShardPath(month);
            await EnsureShardAsync(shardPath, month, cancellationToken);
            await using var context = _dbContextFactory.Create(shardPath);
            await ImportLegacyRowsAsync(context, month, group.ToArray(), month < currentMonth, cancellationToken);
        }

        await File.WriteAllTextAsync(
            _paths.LegacyMigrationMarkerPath,
            $"Migrated at {_timeProvider.GetUtcNow():O}{Environment.NewLine}",
            cancellationToken);
        _logger.LogInformation("Migrated legacy analytics database {Path} into monthly EF Core shards.", _paths.LegacyDatabasePath);
    }

    private static async Task ImportLegacyRowsAsync(
        AnalyticsDbContext context,
        DateOnly month,
        IReadOnlyCollection<LegacyPageViewRow> rows,
        bool compacted,
        CancellationToken cancellationToken)
    {
        var summary = await context.MonthSummaries.SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        var importedViews = rows.Sum(item => item.Views);
        if (summary is null)
        {
            context.MonthSummaries.Add(new AnalyticsMonthSummaryEntity
            {
                Id = 1,
                Month = FormatMonth(month),
                Views = importedViews,
                Visitors = 0,
                NotFoundHits = 0
            });
        }
        else
        {
            summary.Views = Math.Max(summary.Views, importedViews);
        }

        var daily = await context.DailyRollups.ToDictionaryAsync(item => item.Day, cancellationToken);
        foreach (var item in rows.GroupBy(row => row.Day))
        {
            var views = item.Sum(row => row.Views);
            if (daily.TryGetValue(item.Key, out var existing))
            {
                existing.Views = Math.Max(existing.Views, views);
            }
            else
            {
                context.DailyRollups.Add(new AnalyticsDailyRollupEntity
                {
                    Day = item.Key,
                    Views = views,
                    Visitors = 0,
                    NotFoundHits = 0
                });
            }
        }

        await MergeLegacyPagesAsync(context, rows, cancellationToken);
        await MergeLegacySourcesAsync(context, rows, cancellationToken);
        await MergeLegacyUtmSourcesAsync(context, rows, cancellationToken);
        await MergeLegacyUtmMediumsAsync(context, rows, cancellationToken);
        await MergeLegacyCampaignsAsync(context, rows, cancellationToken);

        if (compacted)
        {
            var metadata = await context.Metadata.SingleAsync(item => item.Key == "compacted", cancellationToken);
            metadata.Value = "1";
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task ReadShardAsync(
        string path,
        DateOnly month,
        DateOnly? since,
        DateOnly today,
        Func<string?, bool> includeNotFoundPath,
        DashboardAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        await using var context = _dbContextFactory.Create(path, readOnly: true);
        var sinceValue = since.HasValue ? FormatDay(since.Value) : null;
        var todayValue = FormatDay(today);
        var pageEvents = ApplyPeriod(
            context.Events.AsNoTracking().Where(item => item.Kind == AnalyticsEventKind.PageView),
            sinceValue,
            todayValue);
        var notFoundEvents = ApplyPeriod(
            context.Events.AsNoTracking().Where(item => item.Kind == AnalyticsEventKind.NotFound),
            sinceValue,
            todayValue);
        var rollupDays = ApplyPeriod(context.DailyRollups.AsNoTracking(), sinceValue, todayValue);

        var summary = await context.MonthSummaries.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == 1,
            cancellationToken);
        var allEventViews = await context.Events.AsNoTracking()
            .Where(item => item.Kind == AnalyticsEventKind.PageView)
            .LongCountAsync(cancellationToken);
        var allEventVisitors = await context.Events.AsNoTracking()
            .Where(item => item.Kind == AnalyticsEventKind.PageView)
            .Select(item => item.VisitorId)
            .Distinct()
            .LongCountAsync(cancellationToken);
        accumulator.TotalViews += (summary?.Views ?? 0) + allEventViews;
        accumulator.TotalVisitors += (summary?.Visitors ?? 0) + allEventVisitors;

        var periodEventViews = await pageEvents.LongCountAsync(cancellationToken);
        var periodEventVisitors = await pageEvents.Select(item => item.VisitorId)
            .Distinct()
            .LongCountAsync(cancellationToken);
        var periodRollupViews = await SumAsync(rollupDays.Select(item => item.Views), cancellationToken);
        var fullMonthIncluded = IsFullMonthIncluded(month, since, today);
        var periodRollupVisitors = fullMonthIncluded && summary is not null
            ? summary.Visitors
            : await SumAsync(rollupDays.Select(item => item.Visitors), cancellationToken);
        accumulator.PeriodViews += periodEventViews + periodRollupViews;
        accumulator.PeriodVisitors += periodEventVisitors + periodRollupVisitors;

        await ReadDetailedEventsAsync(
            pageEvents,
            notFoundEvents,
            includeNotFoundPath,
            accumulator,
            cancellationToken);
        await ReadRollupsAsync(
            context,
            sinceValue,
            todayValue,
            includeNotFoundPath,
            accumulator,
            cancellationToken);

        var compacted = await context.Metadata.AsNoTracking()
            .Where(item => item.Key == "compacted")
            .Select(item => item.Value)
            .SingleAsync(cancellationToken) == "1";
        accumulator.Months.Add(new AnalyticsMonth(
            FormatMonth(month),
            compacted,
            new FileInfo(path).Length));
    }

    private static async Task ReadDetailedEventsAsync(
        IQueryable<AnalyticsEventEntity> pageViews,
        IQueryable<AnalyticsEventEntity> notFound,
        Func<string?, bool> includeNotFoundPath,
        DashboardAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var dailyViews = await pageViews
            .GroupBy(item => item.Day)
            .Select(group => new CountedDay(
                group.Key,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount()))
            .ToListAsync(cancellationToken);
        foreach (var item in dailyViews)
        {
            accumulator.GetDaily(ParseDay(item.Day)).AddViews(item.Count, item.Visitors);
        }

        var dailyNotFound = await notFound
            .GroupBy(item => new { item.Day, item.Path })
            .Select(group => new CountedPage(
                group.Key.Day,
                group.Key.Path,
                group.LongCount(),
                0))
            .ToListAsync(cancellationToken);
        foreach (var item in dailyNotFound.Where(item => includeNotFoundPath(item.Path)))
        {
            accumulator.GetDaily(ParseDay(item.Day)).NotFound += item.Count;
        }

        var pages = await pageViews
            .GroupBy(item => item.Path)
            .Select(group => new CountedMetric(
                group.Key,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount()))
            .ToListAsync(cancellationToken);
        foreach (var item in pages)
        {
            accumulator.GetPage(item.Key).Add(item.Count, item.Visitors);
        }

        var sources = await pageViews
            .GroupBy(item => new
            {
                Source = item.UtmSource != ""
                    ? item.UtmSource
                    : item.ReferrerHost != "" ? item.ReferrerHost : "direct",
                Medium = item.UtmMedium != ""
                    ? item.UtmMedium
                    : item.ReferrerHost == "internal"
                        ? "internal"
                        : item.ReferrerHost != "" ? "referral" : "none"
            })
            .Select(group => new CountedSource(
                string.Empty,
                group.Key.Source,
                group.Key.Medium,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount()))
            .ToListAsync(cancellationToken);
        foreach (var item in sources)
        {
            accumulator.GetSource(item.Source, item.Medium).Add(item.Count, item.Visitors);
        }

        var utmSources = await pageViews
            .Where(item => item.UtmSource != "")
            .GroupBy(item => item.UtmSource)
            .Select(group => new CountedMetric(
                group.Key,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount()))
            .ToListAsync(cancellationToken);
        foreach (var item in utmSources)
        {
            accumulator.GetUtmSource(item.Key).Add(item.Count, item.Visitors);
        }

        var utmMediums = await pageViews
            .Where(item => item.UtmMedium != "")
            .GroupBy(item => item.UtmMedium)
            .Select(group => new CountedMetric(
                group.Key,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount()))
            .ToListAsync(cancellationToken);
        foreach (var item in utmMediums)
        {
            accumulator.GetUtmMedium(item.Key).Add(item.Count, item.Visitors);
        }

        var campaigns = await pageViews
            .Where(item => item.UtmSource != "" || item.UtmMedium != "" || item.UtmCampaign != "")
            .GroupBy(item => new { item.UtmSource, item.UtmMedium, item.UtmCampaign })
            .Select(group => new CountedCampaign(
                string.Empty,
                group.Key.UtmSource,
                group.Key.UtmMedium,
                group.Key.UtmCampaign,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount()))
            .ToListAsync(cancellationToken);
        foreach (var item in campaigns)
        {
            accumulator.GetCampaign(item.Source, item.Medium, item.Campaign).Add(item.Count, item.Visitors);
        }

        var notFoundPaths = await notFound
            .GroupBy(item => item.Path)
            .Select(group => new CountedNotFound(
                group.Key,
                group.LongCount(),
                group.Select(item => item.VisitorId).Distinct().LongCount(),
                group.Max(item => item.Day)!))
            .ToListAsync(cancellationToken);
        foreach (var item in notFoundPaths.Where(item => includeNotFoundPath(item.Path)))
        {
            accumulator.GetNotFound(item.Path).Add(item.Count, item.Visitors, ParseDay(item.LastSeen));
        }
    }

    private static async Task ReadRollupsAsync(
        AnalyticsDbContext context,
        string? since,
        string today,
        Func<string?, bool> includeNotFoundPath,
        DashboardAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var daily = await ApplyPeriod(context.DailyRollups.AsNoTracking(), since, today)
            .ToListAsync(cancellationToken);
        foreach (var item in daily)
        {
            var target = accumulator.GetDaily(ParseDay(item.Day));
            target.AddViews(item.Views, item.Visitors);
        }

        var pages = await ApplyPeriod(context.PageRollups.AsNoTracking(), since, today)
            .GroupBy(item => item.Path)
            .Select(group => new CountedMetric(
                group.Key,
                group.Sum(item => item.Views),
                group.Sum(item => item.Visitors)))
            .ToListAsync(cancellationToken);
        foreach (var item in pages)
        {
            accumulator.GetPage(item.Key).Add(item.Count, item.Visitors);
        }

        var sources = await ApplyPeriod(context.SourceRollups.AsNoTracking(), since, today)
            .GroupBy(item => new { item.Source, item.Medium })
            .Select(group => new CountedSource(
                string.Empty,
                group.Key.Source,
                group.Key.Medium,
                group.Sum(item => item.Views),
                group.Sum(item => item.Visitors)))
            .ToListAsync(cancellationToken);
        foreach (var item in sources)
        {
            accumulator.GetSource(item.Source, item.Medium).Add(item.Count, item.Visitors);
        }

        var utmSources = await ApplyPeriod(context.UtmSourceRollups.AsNoTracking(), since, today)
            .GroupBy(item => item.Source)
            .Select(group => new CountedMetric(
                group.Key,
                group.Sum(item => item.Views),
                group.Sum(item => item.Visitors)))
            .ToListAsync(cancellationToken);
        foreach (var item in utmSources)
        {
            accumulator.GetUtmSource(item.Key).Add(item.Count, item.Visitors);
        }

        var utmMediums = await ApplyPeriod(context.UtmMediumRollups.AsNoTracking(), since, today)
            .GroupBy(item => item.Medium)
            .Select(group => new CountedMetric(
                group.Key,
                group.Sum(item => item.Views),
                group.Sum(item => item.Visitors)))
            .ToListAsync(cancellationToken);
        foreach (var item in utmMediums)
        {
            accumulator.GetUtmMedium(item.Key).Add(item.Count, item.Visitors);
        }

        var campaigns = await ApplyPeriod(context.CampaignRollups.AsNoTracking(), since, today)
            .GroupBy(item => new { item.Source, item.Medium, item.Campaign })
            .Select(group => new CountedCampaign(
                string.Empty,
                group.Key.Source,
                group.Key.Medium,
                group.Key.Campaign,
                group.Sum(item => item.Views),
                group.Sum(item => item.Visitors)))
            .ToListAsync(cancellationToken);
        foreach (var item in campaigns)
        {
            accumulator.GetCampaign(item.Source, item.Medium, item.Campaign).Add(item.Count, item.Visitors);
        }

        var notFound = (await ApplyPeriod(context.NotFoundRollups.AsNoTracking(), since, today)
                .ToListAsync(cancellationToken))
            .Where(item => includeNotFoundPath(item.Path))
            .ToArray();
        foreach (var day in notFound.GroupBy(item => item.Day))
        {
            accumulator.GetDaily(ParseDay(day.Key)).NotFound += day.Sum(item => item.Hits);
        }

        foreach (var path in notFound.GroupBy(item => item.Path))
        {
            accumulator.GetNotFound(path.Key).Add(
                path.Sum(item => item.Hits),
                path.Sum(item => item.Visitors),
                ParseDay(path.Max(item => item.Day)!));
        }
    }

    private static async Task MergeMonthSummaryAsync(
        AnalyticsDbContext context,
        DateOnly month,
        long views,
        long visitors,
        long notFoundHits,
        CancellationToken cancellationToken)
    {
        var summary = await context.MonthSummaries.SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (summary is null)
        {
            context.MonthSummaries.Add(new AnalyticsMonthSummaryEntity
            {
                Id = 1,
                Month = FormatMonth(month),
                Views = views,
                Visitors = visitors,
                NotFoundHits = notFoundHits
            });
        }
        else
        {
            summary.Views += views;
            summary.Visitors += visitors;
            summary.NotFoundHits += notFoundHits;
        }
    }

    private static async Task MergeDailyAsync(
        AnalyticsDbContext context,
        IReadOnlyCollection<CountedDay> pageViews,
        IReadOnlyCollection<CountedDay> notFound,
        CancellationToken cancellationToken)
    {
        var existing = await context.DailyRollups.ToDictionaryAsync(item => item.Day, cancellationToken);
        foreach (var item in pageViews)
        {
            var target = GetOrCreate(existing, item.Day, () =>
            {
                var created = new AnalyticsDailyRollupEntity { Day = item.Day };
                context.DailyRollups.Add(created);
                return created;
            });
            target.Views += item.Count;
            target.Visitors += item.Visitors;
        }

        foreach (var item in notFound)
        {
            var target = GetOrCreate(existing, item.Day, () =>
            {
                var created = new AnalyticsDailyRollupEntity { Day = item.Day };
                context.DailyRollups.Add(created);
                return created;
            });
            target.NotFoundHits += item.Count;
        }
    }

    private static async Task MergePagesAsync(
        AnalyticsDbContext context,
        IReadOnlyCollection<CountedPage> values,
        CancellationToken cancellationToken)
    {
        var existing = await context.PageRollups.ToDictionaryAsync(item => (item.Day, item.Path), cancellationToken);
        foreach (var item in values)
        {
            var key = (item.Day, item.Path);
            var target = GetOrCreate(existing, key, () =>
            {
                var created = new AnalyticsPageRollupEntity { Day = item.Day, Path = item.Path };
                context.PageRollups.Add(created);
                return created;
            });
            target.Views += item.Count;
            target.Visitors += item.Visitors;
        }
    }

    private static async Task MergeSourcesAsync(
        AnalyticsDbContext context,
        IReadOnlyCollection<CountedSource> values,
        CancellationToken cancellationToken)
    {
        var existing = await context.SourceRollups.ToDictionaryAsync(
            item => (item.Day, item.Source, item.Medium),
            cancellationToken);
        foreach (var item in values)
        {
            var key = (item.Day, item.Source, item.Medium);
            var target = GetOrCreate(existing, key, () =>
            {
                var created = new AnalyticsSourceRollupEntity
                {
                    Day = item.Day,
                    Source = item.Source,
                    Medium = item.Medium
                };
                context.SourceRollups.Add(created);
                return created;
            });
            target.Views += item.Count;
            target.Visitors += item.Visitors;
        }
    }

    private static async Task MergeUtmSourcesAsync(
        AnalyticsDbContext context,
        IReadOnlyCollection<CountedUtmSource> values,
        CancellationToken cancellationToken)
    {
        var existing = await context.UtmSourceRollups.ToDictionaryAsync(
            item => (item.Day, item.Source),
            cancellationToken);
        foreach (var item in values)
        {
            var key = (item.Day, item.Source);
            var target = GetOrCreate(existing, key, () =>
            {
                var created = new AnalyticsUtmSourceRollupEntity { Day = item.Day, Source = item.Source };
                context.UtmSourceRollups.Add(created);
                return created;
            });
            target.Views += item.Count;
            target.Visitors += item.Visitors;
        }
    }

    private static async Task MergeUtmMediumsAsync(
        AnalyticsDbContext context,
        IReadOnlyCollection<CountedUtmMedium> values,
        CancellationToken cancellationToken)
    {
        var existing = await context.UtmMediumRollups.ToDictionaryAsync(
            item => (item.Day, item.Medium),
            cancellationToken);
        foreach (var item in values)
        {
            var key = (item.Day, item.Medium);
            var target = GetOrCreate(existing, key, () =>
            {
                var created = new AnalyticsUtmMediumRollupEntity { Day = item.Day, Medium = item.Medium };
                context.UtmMediumRollups.Add(created);
                return created;
            });
            target.Views += item.Count;
            target.Visitors += item.Visitors;
        }
    }

    private static async Task MergeCampaignsAsync(
        AnalyticsDbContext context,
        IReadOnlyCollection<CountedCampaign> values,
        CancellationToken cancellationToken)
    {
        var existing = await context.CampaignRollups.ToDictionaryAsync(
            item => (item.Day, item.Source, item.Medium, item.Campaign),
            cancellationToken);
        foreach (var item in values)
        {
            var key = (item.Day, item.Source, item.Medium, item.Campaign);
            var target = GetOrCreate(existing, key, () =>
            {
                var created = new AnalyticsCampaignRollupEntity
                {
                    Day = item.Day,
                    Source = item.Source,
                    Medium = item.Medium,
                    Campaign = item.Campaign
                };
                context.CampaignRollups.Add(created);
                return created;
            });
            target.Views += item.Count;
            target.Visitors += item.Visitors;
        }
    }

    private static async Task MergeNotFoundAsync(
        AnalyticsDbContext context,
        IReadOnlyCollection<CountedPage> values,
        CancellationToken cancellationToken)
    {
        var existing = await context.NotFoundRollups.ToDictionaryAsync(item => (item.Day, item.Path), cancellationToken);
        foreach (var item in values)
        {
            var key = (item.Day, item.Path);
            var target = GetOrCreate(existing, key, () =>
            {
                var created = new AnalyticsNotFoundRollupEntity { Day = item.Day, Path = item.Path };
                context.NotFoundRollups.Add(created);
                return created;
            });
            target.Hits += item.Count;
            target.Visitors += item.Visitors;
        }
    }

    private static async Task MergeLegacyPagesAsync(
        AnalyticsDbContext context,
        IReadOnlyCollection<LegacyPageViewRow> rows,
        CancellationToken cancellationToken)
    {
        var existing = await context.PageRollups.ToDictionaryAsync(item => (item.Day, item.Path), cancellationToken);
        foreach (var group in rows.GroupBy(item => (item.Day, item.Path)))
        {
            var views = group.Sum(item => item.Views);
            if (existing.TryGetValue(group.Key, out var target))
            {
                target.Views = Math.Max(target.Views, views);
            }
            else
            {
                context.PageRollups.Add(new AnalyticsPageRollupEntity
                {
                    Day = group.Key.Day,
                    Path = group.Key.Path,
                    Views = views
                });
            }
        }
    }

    private static async Task MergeLegacySourcesAsync(
        AnalyticsDbContext context,
        IReadOnlyCollection<LegacyPageViewRow> rows,
        CancellationToken cancellationToken)
    {
        var existing = await context.SourceRollups.ToDictionaryAsync(
            item => (item.Day, item.Source, item.Medium),
            cancellationToken);
        foreach (var group in rows.GroupBy(item =>
                     (item.Day, Source: GetSource(item), Medium: GetMedium(item))))
        {
            var views = group.Sum(item => item.Views);
            if (existing.TryGetValue(group.Key, out var target))
            {
                target.Views = Math.Max(target.Views, views);
            }
            else
            {
                context.SourceRollups.Add(new AnalyticsSourceRollupEntity
                {
                    Day = group.Key.Day,
                    Source = group.Key.Source,
                    Medium = group.Key.Medium,
                    Views = views
                });
            }
        }
    }

    private static async Task MergeLegacyUtmSourcesAsync(
        AnalyticsDbContext context,
        IReadOnlyCollection<LegacyPageViewRow> rows,
        CancellationToken cancellationToken)
    {
        var existing = await context.UtmSourceRollups.ToDictionaryAsync(
            item => (item.Day, item.Source),
            cancellationToken);
        foreach (var group in rows.Where(item => item.UtmSource != "").GroupBy(item => (item.Day, item.UtmSource)))
        {
            var views = group.Sum(item => item.Views);
            if (existing.TryGetValue(group.Key, out var target))
            {
                target.Views = Math.Max(target.Views, views);
            }
            else
            {
                context.UtmSourceRollups.Add(new AnalyticsUtmSourceRollupEntity
                {
                    Day = group.Key.Day,
                    Source = group.Key.UtmSource,
                    Views = views
                });
            }
        }
    }

    private static async Task MergeLegacyCampaignsAsync(
        AnalyticsDbContext context,
        IReadOnlyCollection<LegacyPageViewRow> rows,
        CancellationToken cancellationToken)
    {
        var existing = await context.CampaignRollups.ToDictionaryAsync(
            item => (item.Day, item.Source, item.Medium, item.Campaign),
            cancellationToken);
        foreach (var group in rows
                     .Where(item => item.UtmSource != "" || item.UtmMedium != "" || item.UtmCampaign != "")
                     .GroupBy(item => (item.Day, item.UtmSource, item.UtmMedium, item.UtmCampaign)))
        {
            var views = group.Sum(item => item.Views);
            if (existing.TryGetValue(group.Key, out var target))
            {
                target.Views = Math.Max(target.Views, views);
            }
            else
            {
                context.CampaignRollups.Add(new AnalyticsCampaignRollupEntity
                {
                    Day = group.Key.Day,
                    Source = group.Key.UtmSource,
                    Medium = group.Key.UtmMedium,
                    Campaign = group.Key.UtmCampaign,
                    Views = views
                });
            }
        }
    }

    private static async Task MergeLegacyUtmMediumsAsync(
        AnalyticsDbContext context,
        IReadOnlyCollection<LegacyPageViewRow> rows,
        CancellationToken cancellationToken)
    {
        var existing = await context.UtmMediumRollups.ToDictionaryAsync(
            item => (item.Day, item.Medium),
            cancellationToken);
        foreach (var group in rows.Where(item => item.UtmMedium != "").GroupBy(item => (item.Day, item.UtmMedium)))
        {
            var views = group.Sum(item => item.Views);
            if (existing.TryGetValue(group.Key, out var target))
            {
                target.Views = Math.Max(target.Views, views);
            }
            else
            {
                context.UtmMediumRollups.Add(new AnalyticsUtmMediumRollupEntity
                {
                    Day = group.Key.Day,
                    Medium = group.Key.UtmMedium,
                    Views = views
                });
            }
        }
    }

    private static IQueryable<AnalyticsEventEntity> ApplyPeriod(
        IQueryable<AnalyticsEventEntity> query,
        string? since,
        string today)
    {
        query = query.Where(item => item.Day.CompareTo(today) <= 0);
        return since is null ? query : query.Where(item => item.Day.CompareTo(since) >= 0);
    }

    private static IQueryable<AnalyticsDailyRollupEntity> ApplyPeriod(
        IQueryable<AnalyticsDailyRollupEntity> query,
        string? since,
        string today)
    {
        query = query.Where(item => item.Day.CompareTo(today) <= 0);
        return since is null ? query : query.Where(item => item.Day.CompareTo(since) >= 0);
    }

    private static IQueryable<AnalyticsPageRollupEntity> ApplyPeriod(
        IQueryable<AnalyticsPageRollupEntity> query,
        string? since,
        string today) => query.Where(item => item.Day.CompareTo(today) <= 0
            && (since == null || item.Day.CompareTo(since) >= 0));

    private static IQueryable<AnalyticsSourceRollupEntity> ApplyPeriod(
        IQueryable<AnalyticsSourceRollupEntity> query,
        string? since,
        string today) => query.Where(item => item.Day.CompareTo(today) <= 0
            && (since == null || item.Day.CompareTo(since) >= 0));

    private static IQueryable<AnalyticsUtmSourceRollupEntity> ApplyPeriod(
        IQueryable<AnalyticsUtmSourceRollupEntity> query,
        string? since,
        string today) => query.Where(item => item.Day.CompareTo(today) <= 0
            && (since == null || item.Day.CompareTo(since) >= 0));

    private static IQueryable<AnalyticsUtmMediumRollupEntity> ApplyPeriod(
        IQueryable<AnalyticsUtmMediumRollupEntity> query,
        string? since,
        string today) => query.Where(item => item.Day.CompareTo(today) <= 0
            && (since == null || item.Day.CompareTo(since) >= 0));

    private static IQueryable<AnalyticsCampaignRollupEntity> ApplyPeriod(
        IQueryable<AnalyticsCampaignRollupEntity> query,
        string? since,
        string today) => query.Where(item => item.Day.CompareTo(today) <= 0
            && (since == null || item.Day.CompareTo(since) >= 0));

    private static IQueryable<AnalyticsNotFoundRollupEntity> ApplyPeriod(
        IQueryable<AnalyticsNotFoundRollupEntity> query,
        string? since,
        string today) => query.Where(item => item.Day.CompareTo(today) <= 0
            && (since == null || item.Day.CompareTo(since) >= 0));

    private static async Task<long> SumAsync(IQueryable<long> query, CancellationToken cancellationToken) =>
        await query.Select(value => (long?)value).SumAsync(cancellationToken) ?? 0;

    private static bool IsFullMonthIncluded(DateOnly month, DateOnly? since, DateOnly today)
    {
        var lastDay = month.AddMonths(1).AddDays(-1);
        return (!since.HasValue || since.Value <= month) && today >= lastDay;
    }

    private static DateOnly FirstDayOfMonth(DateOnly day) => new(day.Year, day.Month, 1);
    private static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string FormatMonth(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    private static DateOnly ParseDay(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static bool TryParseDay(string value, out DateOnly day) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static string GetSource(LegacyPageViewRow row) => row.UtmSource != ""
        ? row.UtmSource
        : row.ReferrerHost != "" ? row.ReferrerHost : "direct";

    private static string GetMedium(LegacyPageViewRow row) => row.UtmMedium != ""
        ? row.UtmMedium
        : row.ReferrerHost == "internal" ? "internal" : row.ReferrerHost != "" ? "referral" : "none";

    private static TValue GetOrCreate<TKey, TValue>(
        IDictionary<TKey, TValue> dictionary,
        TKey key,
        Func<TValue> factory)
        where TKey : notnull
    {
        if (!dictionary.TryGetValue(key, out var value))
        {
            value = factory();
            dictionary.Add(key, value);
        }

        return value;
    }

    private sealed class LegacyPageViewRow
    {
        public required string Day { get; init; }
        public required string Path { get; init; }
        public required string UtmSource { get; init; }
        public required string UtmMedium { get; init; }
        public required string UtmCampaign { get; init; }
        public required string ReferrerHost { get; init; }
        public long Views { get; init; }
    }

    private sealed record CountedDay(string Day, long Count, long Visitors);
    private sealed record CountedMetric(string Key, long Count, long Visitors);
    private sealed record CountedPage(string Day, string Path, long Count, long Visitors);
    private sealed record CountedSource(string Day, string Source, string Medium, long Count, long Visitors);
    private sealed record CountedUtmSource(string Day, string Source, long Count, long Visitors);
    private sealed record CountedUtmMedium(string Day, string Medium, long Count, long Visitors);
    private sealed record CountedCampaign(
        string Day,
        string Source,
        string Medium,
        string Campaign,
        long Count,
        long Visitors);
    private sealed record CountedNotFound(string Path, long Count, long Visitors, string LastSeen);

    private class MutableMetric
    {
        public long Count { get; private set; }
        public long Visitors { get; private set; }

        public void Add(long count, long visitors)
        {
            Count += count;
            Visitors += visitors;
        }
    }

    private sealed class MutableDaily
    {
        public long Views { get; private set; }
        public long Visitors { get; private set; }
        public long NotFound { get; set; }

        public void AddViews(long views, long visitors)
        {
            Views += views;
            Visitors += visitors;
        }
    }

    private sealed class MutableNotFound : MutableMetric
    {
        public DateOnly LastSeen { get; private set; }

        public void Add(long hits, long visitors, DateOnly lastSeen)
        {
            base.Add(hits, visitors);
            if (lastSeen > LastSeen)
            {
                LastSeen = lastSeen;
            }
        }
    }

    private sealed class DashboardAccumulator
    {
        private readonly Dictionary<DateOnly, MutableDaily> _daily = [];
        private readonly Dictionary<string, MutableMetric> _pages = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Source, string Medium), MutableMetric> _sources = [];
        private readonly Dictionary<string, MutableMetric> _utmSources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MutableMetric> _utmMediums = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Source, string Medium, string Campaign), MutableMetric> _campaigns = [];
        private readonly Dictionary<string, MutableNotFound> _notFound = new(StringComparer.Ordinal);

        public long PeriodViews { get; set; }
        public long PeriodVisitors { get; set; }
        public long TotalViews { get; set; }
        public long TotalVisitors { get; set; }
        public List<AnalyticsMonth> Months { get; } = [];

        public MutableDaily GetDaily(DateOnly day) => GetOrCreate(_daily, day, static () => new MutableDaily());
        public MutableMetric GetPage(string path) => GetOrCreate(_pages, path, static () => new MutableMetric());
        public MutableMetric GetSource(string source, string medium) =>
            GetOrCreate(_sources, (source, medium), static () => new MutableMetric());
        public MutableMetric GetUtmSource(string source) =>
            GetOrCreate(_utmSources, source, static () => new MutableMetric());
        public MutableMetric GetUtmMedium(string medium) =>
            GetOrCreate(_utmMediums, medium, static () => new MutableMetric());
        public MutableMetric GetCampaign(string source, string medium, string campaign) =>
            GetOrCreate(_campaigns, (source, medium, campaign), static () => new MutableMetric());
        public MutableNotFound GetNotFound(string path) =>
            GetOrCreate(_notFound, path, static () => new MutableNotFound());

        public AnalyticsDashboard Build(DateOnly today)
        {
            _daily.TryGetValue(today, out var todayStats);
            return new AnalyticsDashboard(
                todayStats?.Views ?? 0,
                todayStats?.Visitors ?? 0,
                PeriodViews,
                PeriodVisitors,
                TotalViews,
                TotalVisitors,
                _daily.Values.Sum(item => item.NotFound),
                _daily.OrderBy(item => item.Key)
                    .Select(item => new DailyAnalytics(
                        item.Key,
                        item.Value.Views,
                        item.Value.Visitors,
                        item.Value.NotFound))
                    .ToArray(),
                _pages.OrderByDescending(item => item.Value.Count).ThenBy(item => item.Key)
                    .Take(30)
                    .Select(item => new PageAnalytics(item.Key, item.Value.Count, item.Value.Visitors))
                    .ToArray(),
                _sources.OrderByDescending(item => item.Value.Count).ThenBy(item => item.Key.Source)
                    .Take(30)
                    .Select(item => new SourceAnalytics(
                        item.Key.Source,
                        item.Key.Medium,
                        item.Value.Count,
                        item.Value.Visitors))
                    .ToArray(),
                _utmSources.OrderByDescending(item => item.Value.Count).ThenBy(item => item.Key)
                    .Take(30)
                    .Select(item => new UtmSourceAnalytics(item.Key, item.Value.Count, item.Value.Visitors))
                    .ToArray(),
                _utmMediums.OrderByDescending(item => item.Value.Count).ThenBy(item => item.Key)
                    .Take(30)
                    .Select(item => new UtmMediumAnalytics(item.Key, item.Value.Count, item.Value.Visitors))
                    .ToArray(),
                _campaigns.OrderByDescending(item => item.Value.Count).ThenBy(item => item.Key.Campaign)
                    .Take(100)
                    .Select(item => new CampaignAnalytics(
                        item.Key.Source,
                        item.Key.Medium,
                        item.Key.Campaign,
                        item.Value.Count,
                        item.Value.Visitors))
                    .ToArray(),
                _notFound.OrderByDescending(item => item.Value.Count).ThenBy(item => item.Key)
                    .Take(100)
                    .Select(item => new NotFoundAnalytics(
                        item.Key,
                        item.Value.Count,
                        item.Value.Visitors,
                        item.Value.LastSeen))
                    .ToArray(),
                Months.OrderByDescending(item => item.Month).ToArray());
        }
    }
}
