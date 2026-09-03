namespace Iolys.WebMetrics;

public sealed record DailyAnalytics(DateOnly Day, long Views, long Visitors, long NotFound);

public sealed record PageAnalytics(string Path, long Views, long Visitors);

public sealed record SourceAnalytics(string Source, string Medium, long Views, long Visitors);

public sealed record UtmSourceAnalytics(string Source, long Views, long Visitors);

public sealed record UtmMediumAnalytics(string Medium, long Views, long Visitors);

public sealed record CampaignAnalytics(
    string Source,
    string Medium,
    string Campaign,
    long Views,
    long Visitors);

public sealed record NotFoundAnalytics(
    string Path,
    long Hits,
    long Visitors,
    DateOnly LastSeen);

public enum NotFoundAnalyticsView
{
    Tracked,
    Ignored
}

public sealed record AnalyticsMonth(
    string Month,
    bool Compacted,
    long FileSize);

public sealed record AnalyticsDashboard(
    long TodayViews,
    long TodayVisitors,
    long PeriodViews,
    long PeriodVisitors,
    long TotalViews,
    long TotalVisitors,
    long PeriodNotFound,
    IReadOnlyList<DailyAnalytics> Daily,
    IReadOnlyList<PageAnalytics> TopPages,
    IReadOnlyList<SourceAnalytics> Sources,
    IReadOnlyList<UtmSourceAnalytics> UtmSources,
    IReadOnlyList<UtmMediumAnalytics> UtmMediums,
    IReadOnlyList<CampaignAnalytics> Campaigns,
    IReadOnlyList<NotFoundAnalytics> NotFound,
    IReadOnlyList<AnalyticsMonth> Months);

public interface IAnalyticsReportReader
{
    Task<AnalyticsDashboard> GetDashboardAsync(
        int days,
        NotFoundAnalyticsView notFoundView = NotFoundAnalyticsView.Tracked,
        CancellationToken cancellationToken = default);
}

public interface IAnalyticsNotFoundManager
{
    Task DeleteNotFoundAsync(string path, CancellationToken cancellationToken = default);
}
