using Microsoft.EntityFrameworkCore;

namespace Iolys.WebMetrics.Tests;

[TestClass]
public sealed class VisitAttributionTests
{
    [TestMethod]
    public async Task InternalPagesKeepGoogleEntryAndDoNotChangeStoredEvents()
    {
        using var fixture = StoreFixture.Create();
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/", referrerHost: "google.com");
        fixture.TimeProvider.UtcNow += TimeSpan.FromMinutes(1);
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/blog", referrerHost: "internal");
        fixture.TimeProvider.UtcNow += TimeSpan.FromMinutes(1);
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/blog/article", referrerHost: "internal");

        var dashboard = await fixture.Store.GetDashboardAsync(30);
        Assert.AreEqual(1L, dashboard.PeriodVisitors);
        Assert.AreEqual(new SourceAnalytics("google.com", "referral", 3, 1), dashboard.Sources.Single());
        await using var context = fixture.DbContextFactory.Create(fixture.Paths.GetShardPath(new DateOnly(2026, 8, 20)));
        Assert.AreEqual(2, await context.Events.CountAsync(item => item.ReferrerHost == "internal"));
    }

    [TestMethod]
    public async Task DirectVisitAndOtherVisitorsStaySeparate()
    {
        using var fixture = StoreFixture.Create();
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/", referrerHost: "google.com");
        await fixture.RecordAsync("b", AnalyticsEventKind.PageView, "/");
        fixture.TimeProvider.UtcNow += TimeSpan.FromMinutes(1);
        await fixture.RecordAsync("b", AnalyticsEventKind.PageView, "/blog", referrerHost: "internal");
        var dashboard = await fixture.Store.GetDashboardAsync(30);
        Assert.AreEqual(new SourceAnalytics("direct", "none", 2, 1), dashboard.Sources.Single(item => item.Source == "direct"));
        Assert.AreEqual(2L, dashboard.PeriodVisitors);
    }

    [TestMethod]
    public async Task CampaignPersistsThroughInternalLinksAndMissingReferrers()
    {
        using var fixture = StoreFixture.Create();
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/", "newsletter", "email", "launch");
        fixture.TimeProvider.UtcNow += TimeSpan.FromMinutes(1);
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/blog", "menu", "navigation", "internal-banner", "internal");
        fixture.TimeProvider.UtcNow += TimeSpan.FromMinutes(1);
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/article");
        var dashboard = await fixture.Store.GetDashboardAsync(30);
        Assert.AreEqual(new SourceAnalytics("newsletter", "email", 3, 1), dashboard.Sources.Single());
        Assert.AreEqual(new UtmSourceAnalytics("newsletter", 3, 1), dashboard.UtmSources.Single());
        Assert.AreEqual(new UtmMediumAnalytics("email", 3, 1), dashboard.UtmMediums.Single());
        Assert.AreEqual(new CampaignAnalytics("newsletter", "email", "launch", 3, 1), dashboard.Campaigns.Single());
    }

    [TestMethod]
    public async Task NewExternalEntryOrCampaignStartsNewAttribution()
    {
        using var fixture = StoreFixture.Create();
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/", referrerHost: "google.com");
        fixture.TimeProvider.UtcNow += TimeSpan.FromMinutes(1);
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/blog", "newsletter", "email", "launch");
        fixture.TimeProvider.UtcNow += TimeSpan.FromMinutes(1);
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/article", referrerHost: "internal");
        fixture.TimeProvider.UtcNow += TimeSpan.FromMinutes(1);
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/", referrerHost: "example.com");
        var dashboard = await fixture.Store.GetDashboardAsync(30);
        Assert.HasCount(3, dashboard.Sources);
        Assert.AreEqual(1L, dashboard.PeriodVisitors);
        Assert.AreEqual(3L, dashboard.Sources.Sum(item => item.Visitors));
        Assert.AreEqual(2L, dashboard.Sources.Single(item => item.Source == "newsletter").Views);
        Assert.AreEqual(1L, dashboard.Sources.Single(item => item.Source == "example.com").Views);
    }

    [TestMethod]
    public async Task ThirtyMinutesOfInactivityStartsNewVisit()
    {
        using var fixture = StoreFixture.Create();
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/", referrerHost: "google.com");
        fixture.TimeProvider.UtcNow += TimeSpan.FromMinutes(29);
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/blog", referrerHost: "internal");
        fixture.TimeProvider.UtcNow += TimeSpan.FromMinutes(30);
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/");
        fixture.TimeProvider.UtcNow += TimeSpan.FromMinutes(1);
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/blog", referrerHost: "internal");
        var dashboard = await fixture.Store.GetDashboardAsync(30);
        Assert.AreEqual(new SourceAnalytics("google.com", "referral", 2, 1), dashboard.Sources.Single(item => item.Source == "google.com"));
        Assert.AreEqual(new SourceAnalytics("direct", "none", 2, 1), dashboard.Sources.Single(item => item.Source == "direct"));
    }

    [TestMethod]
    public async Task InternalEntryWithoutRecentLandingPageIsUnknown()
    {
        using var fixture = StoreFixture.Create();
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/blog", referrerHost: "internal");
        await fixture.RecordAsync("b", AnalyticsEventKind.PageView, "/", referrerHost: "google.com");
        fixture.TimeProvider.UtcNow += TimeSpan.FromMinutes(30);
        await fixture.RecordAsync("b", AnalyticsEventKind.PageView, "/blog", referrerHost: "internal");
        var dashboard = await fixture.Store.GetDashboardAsync(30);
        Assert.AreEqual(new SourceAnalytics("unknown", "unknown", 2, 2), dashboard.Sources.Single(item => item.Source == "unknown"));
        Assert.IsFalse(dashboard.Sources.Any(item => item.Source == "internal"));
    }

    [TestMethod]
    public async Task PeriodFilterKeepsLandingFromPreviousDay()
    {
        using var fixture = StoreFixture.Create(new DateTimeOffset(2026, 8, 19, 23, 55, 0, TimeSpan.Zero));
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/", referrerHost: "google.com");
        fixture.TimeProvider.UtcNow += TimeSpan.FromMinutes(10);
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/blog", referrerHost: "internal");
        var dashboard = await fixture.Store.GetDashboardAsync(1);
        Assert.AreEqual(new SourceAnalytics("google.com", "referral", 1, 1), dashboard.Sources.Single());
        Assert.AreEqual(1L, dashboard.PeriodViews);
    }

    [TestMethod]
    public async Task CompactionPreservesCorrectedAttribution()
    {
        using var fixture = StoreFixture.Create(new DateTimeOffset(2026, 8, 31, 23, 50, 0, TimeSpan.Zero));
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/", "newsletter", "email", "launch");
        fixture.TimeProvider.UtcNow += TimeSpan.FromMinutes(1);
        await fixture.RecordAsync("a", AnalyticsEventKind.PageView, "/blog", referrerHost: "internal");
        var before = await fixture.Store.GetDashboardAsync(0);
        fixture.TimeProvider.UtcNow = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        await fixture.Store.CompactExpiredShardsAsync();
        var after = await fixture.Store.GetDashboardAsync(0);
        CollectionAssert.AreEqual(before.Sources.ToArray(), after.Sources.ToArray());
        CollectionAssert.AreEqual(before.Campaigns.ToArray(), after.Campaigns.ToArray());
        Assert.AreEqual(before.PeriodViews, after.PeriodViews);
        Assert.AreEqual(before.PeriodVisitors, after.PeriodVisitors);
    }

    [TestMethod]
    public async Task PreviouslyCompactedInternalCountsAreReportedAsUnknown()
    {
        using var fixture = StoreFixture.Create();
        await fixture.Store.InitializeAsync();
        await using var context = fixture.DbContextFactory.Create(fixture.Paths.GetShardPath(new DateOnly(2026, 8, 20)));
        context.SourceRollups.Add(new AnalyticsSourceRollupEntity
        {
            Day = "2026-08-19",
            Source = "internal",
            Medium = "internal",
            Views = 985,
            Visitors = 367
        });
        await context.SaveChangesAsync();
        var dashboard = await fixture.Store.GetDashboardAsync(30);
        Assert.AreEqual(new SourceAnalytics("unknown", "unknown", 985, 367), dashboard.Sources.Single());
    }
}
