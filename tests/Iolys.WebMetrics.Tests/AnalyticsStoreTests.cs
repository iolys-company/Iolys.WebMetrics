using Microsoft.EntityFrameworkCore;

namespace Iolys.WebMetrics.Tests;

public sealed class AnalyticsStoreTests
{
    [Fact]
    public async Task DashboardAggregatesViewsVisitorsCampaignsAndNotFound()
    {
        using var fixture = StoreFixture.Create();

        await fixture.RecordAsync(
            "visitor-a",
            AnalyticsEventKind.PageView,
            "/",
            "newsletter",
            "email",
            "launch");
        await fixture.RecordAsync(
            "visitor-a",
            AnalyticsEventKind.PageView,
            "/",
            "newsletter",
            "email",
            "launch");
        await fixture.RecordAsync(
            "visitor-b",
            AnalyticsEventKind.PageView,
            "/about",
            referrerHost: "example.com");
        await fixture.RecordAsync("visitor-b", AnalyticsEventKind.NotFound, "/missing");

        var dashboard = await fixture.Store.GetDashboardAsync(30);

        Assert.Equal(3, dashboard.TodayViews);
        Assert.Equal(2, dashboard.TodayVisitors);
        Assert.Equal(3, dashboard.PeriodViews);
        Assert.Equal(2, dashboard.PeriodVisitors);
        Assert.Equal(1, dashboard.PeriodNotFound);

        var home = Assert.Single(dashboard.TopPages, item => item.Path == "/");
        Assert.Equal(2, home.Views);
        Assert.Equal(1, home.Visitors);

        var campaign = Assert.Single(dashboard.Campaigns);
        Assert.Equal("newsletter", campaign.Source);
        Assert.Equal("email", campaign.Medium);
        Assert.Equal("launch", campaign.Campaign);
        Assert.Equal(2, campaign.Views);

        var notFound = Assert.Single(dashboard.NotFound);
        Assert.Equal("/missing", notFound.Path);
        Assert.Equal(1, notFound.Hits);
    }

    [Fact]
    public async Task IgnoredNotFoundPathsAreReportedSeparately()
    {
        using var fixture = StoreFixture.Create();
        await fixture.RecordAsync("visitor-a", AnalyticsEventKind.NotFound, "/missing");
        await fixture.RecordAsync("visitor-b", AnalyticsEventKind.NotFound, "/wp-admin/setup.php");

        var tracked = await fixture.Store.GetDashboardAsync(30, NotFoundAnalyticsView.Tracked);
        var ignored = await fixture.Store.GetDashboardAsync(30, NotFoundAnalyticsView.Ignored);

        Assert.Equal("/missing", Assert.Single(tracked.NotFound).Path);
        Assert.Equal("/wp-admin/setup.php", Assert.Single(ignored.NotFound).Path);
    }

    [Fact]
    public async Task DeleteNotFoundRemovesEventsAndRollups()
    {
        using var fixture = StoreFixture.Create();
        await fixture.RecordAsync("visitor-a", AnalyticsEventKind.NotFound, "/missing");

        await fixture.Store.DeleteNotFoundAsync("/missing");

        var dashboard = await fixture.Store.GetDashboardAsync(30);
        Assert.Empty(dashboard.NotFound);
        Assert.Equal(0, dashboard.PeriodNotFound);
    }

    [Fact]
    public async Task CompactionReplacesExpiredEventsWithRollups()
    {
        using var fixture = StoreFixture.Create(
            new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero));
        fixture.TimeProvider.UtcNow = new DateTimeOffset(2026, 1, 31, 20, 0, 0, TimeSpan.Zero);
        await fixture.RecordAsync(
            "visitor-a",
            AnalyticsEventKind.PageView,
            "/archive",
            "linkedin",
            "social",
            "january");

        fixture.TimeProvider.UtcNow = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
        await fixture.Store.CompactExpiredShardsAsync();

        var januaryPath = fixture.Paths.GetShardPath(new DateOnly(2026, 1, 1));
        await using var context = fixture.DbContextFactory.Create(januaryPath);
        Assert.Equal(0, await context.Events.CountAsync());
        Assert.Equal(1, await context.DailyRollups.CountAsync());
        Assert.Equal("1", await context.Metadata
            .Where(item => item.Key == "compacted")
            .Select(item => item.Value)
            .SingleAsync());

        var dashboard = await fixture.Store.GetDashboardAsync(0);
        Assert.Equal(1, dashboard.TotalViews);
        Assert.Equal(1, dashboard.TotalVisitors);
        Assert.Contains(dashboard.Months, month => month.Month == "2026-01" && month.Compacted);
    }
}
