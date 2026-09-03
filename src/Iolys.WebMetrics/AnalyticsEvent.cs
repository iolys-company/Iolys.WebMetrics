namespace Iolys.WebMetrics;

internal enum AnalyticsEventKind
{
    PageView = 1,
    NotFound = 2
}

internal sealed record AnalyticsEvent(
    DateTimeOffset OccurredAt,
    string VisitorId,
    AnalyticsEventKind Kind,
    string Path,
    string UtmSource,
    string UtmMedium,
    string UtmCampaign,
    string ReferrerHost);
