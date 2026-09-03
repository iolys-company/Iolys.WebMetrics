using Microsoft.EntityFrameworkCore;

namespace Iolys.WebMetrics.Tests;

[TestClass]
public sealed class AnalyticsStoreTests
{
    [TestMethod]
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

        Assert.AreEqual(3L, dashboard.TodayViews);
        Assert.AreEqual(2L, dashboard.TodayVisitors);
        Assert.AreEqual(3L, dashboard.PeriodViews);
        Assert.AreEqual(2L, dashboard.PeriodVisitors);
        Assert.AreEqual(1L, dashboard.PeriodNotFound);

        Assert.HasCount(1, dashboard.TopPages.Where(item => item.Path == "/"));
        var home = dashboard.TopPages.Single(item => item.Path == "/");
        Assert.AreEqual(2L, home.Views);
        Assert.AreEqual(1L, home.Visitors);

        Assert.HasCount(1, dashboard.Campaigns);
        var campaign = dashboard.Campaigns.Single();
        Assert.AreEqual("newsletter", campaign.Source);
        Assert.AreEqual("email", campaign.Medium);
        Assert.AreEqual("launch", campaign.Campaign);
        Assert.AreEqual(2L, campaign.Views);

        Assert.HasCount(1, dashboard.NotFound);
        var notFound = dashboard.NotFound.Single();
        Assert.AreEqual("/missing", notFound.Path);
        Assert.AreEqual(1L, notFound.Hits);
    }

    [TestMethod]
    public async Task IgnoredNotFoundPathsAreReportedSeparately()
    {
        using var fixture = StoreFixture.Create();
        await fixture.RecordAsync("visitor-a", AnalyticsEventKind.NotFound, "/missing");
        await fixture.RecordAsync("visitor-b", AnalyticsEventKind.NotFound, "/wp-admin/setup.php");

        var tracked = await fixture.Store.GetDashboardAsync(30, NotFoundAnalyticsView.Tracked);
        var ignored = await fixture.Store.GetDashboardAsync(30, NotFoundAnalyticsView.Ignored);

        Assert.HasCount(1, tracked.NotFound);
        Assert.AreEqual("/missing", tracked.NotFound.Single().Path);
        Assert.HasCount(1, ignored.NotFound);
        Assert.AreEqual("/wp-admin/setup.php", ignored.NotFound.Single().Path);
    }

    [TestMethod]
    public async Task DeleteNotFoundRemovesEventsAndRollups()
    {
        using var fixture = StoreFixture.Create();
        await fixture.RecordAsync("visitor-a", AnalyticsEventKind.NotFound, "/missing");

        await fixture.Store.DeleteNotFoundAsync("/missing");

        var dashboard = await fixture.Store.GetDashboardAsync(30);
        Assert.IsEmpty(dashboard.NotFound);
        Assert.AreEqual(0L, dashboard.PeriodNotFound);
    }

    [TestMethod]
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
        Assert.AreEqual(0, await context.Events.CountAsync());
        Assert.AreEqual(1, await context.DailyRollups.CountAsync());
        Assert.AreEqual("1", await context.Metadata
            .Where(item => item.Key == "compacted")
            .Select(item => item.Value)
            .SingleAsync());

        var dashboard = await fixture.Store.GetDashboardAsync(0);
        Assert.AreEqual(1L, dashboard.TotalViews);
        Assert.AreEqual(1L, dashboard.TotalVisitors);
        Assert.IsTrue(dashboard.Months.Any(month => month.Month == "2026-01" && month.Compacted));
    }
}
