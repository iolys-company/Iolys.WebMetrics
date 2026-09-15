using System.Globalization;

namespace Iolys.WebMetrics;

internal static class VisitAttribution
{
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(30);

    // Reconstruct from raw events so existing, un-compacted history is corrected too.
    // Visitor IDs rotate monthly; never attempt to link people across monthly shards.
    public static IReadOnlyList<AnalyticsEventEntity> Apply(IEnumerable<AnalyticsEventEntity> events)
    {
        var result = new List<AnalyticsEventEntity>();
        foreach (var visitor in events.GroupBy(item => item.VisitorId))
        {
            AnalyticsEventEntity? entry = null;
            DateTimeOffset? previousTime = null;
            foreach (var page in visitor.OrderBy(item => DateTimeOffset.Parse(item.OccurredAt, CultureInfo.InvariantCulture)).ThenBy(item => item.Id))
            {
                var occurredAt = DateTimeOffset.Parse(page.OccurredAt, CultureInfo.InvariantCulture);
                var isInternal = page.ReferrerHost == "internal";
                var hasCampaign = page.UtmSource != "" || page.UtmMedium != "" || page.UtmCampaign != "";
                var isExternalEntry = !isInternal && (page.ReferrerHost != "" || hasCampaign);
                if (entry is null || previousTime is null
                    || occurredAt - previousTime.Value >= InactivityTimeout || isExternalEntry)
                {
                    entry = page;
                }

                result.Add(new AnalyticsEventEntity
                {
                    Id = page.Id,
                    OccurredAt = page.OccurredAt,
                    Day = page.Day,
                    VisitorId = page.VisitorId,
                    Kind = page.Kind,
                    Path = page.Path,
                    UtmSource = entry.UtmSource,
                    UtmMedium = entry.UtmMedium,
                    UtmCampaign = entry.UtmCampaign,
                    ReferrerHost = entry.ReferrerHost == "internal" ? "unknown" : entry.ReferrerHost
                });
                previousTime = occurredAt;
            }
        }

        return result;
    }
}
