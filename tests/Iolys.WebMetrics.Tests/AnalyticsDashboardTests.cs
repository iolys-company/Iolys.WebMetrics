namespace Iolys.WebMetrics.Tests;

[TestClass]
public sealed class AnalyticsDashboardTests
{
    [TestMethod]
    public void EmptyDashboardReportsNoActivity()
    {
        var dashboard = AnalyticsDashboard.Empty;

        Assert.AreEqual(0L, dashboard.TodayViews);
        Assert.AreEqual(0L, dashboard.TodayVisitors);
        Assert.AreEqual(0L, dashboard.PeriodViews);
        Assert.AreEqual(0L, dashboard.PeriodVisitors);
        Assert.AreEqual(0L, dashboard.TotalViews);
        Assert.AreEqual(0L, dashboard.TotalVisitors);
        Assert.AreEqual(0L, dashboard.PeriodNotFound);
    }

    [TestMethod]
    public void EmptyDashboardExposesEmptyDimensions()
    {
        var dashboard = AnalyticsDashboard.Empty;

        Assert.IsEmpty(dashboard.Daily);
        Assert.IsEmpty(dashboard.TopPages);
        Assert.IsEmpty(dashboard.Sources);
        Assert.IsEmpty(dashboard.Referrers);
        Assert.IsEmpty(dashboard.UtmSources);
        Assert.IsEmpty(dashboard.UtmMediums);
        Assert.IsEmpty(dashboard.Campaigns);
        Assert.IsEmpty(dashboard.NotFound);
        Assert.IsEmpty(dashboard.Months);
    }

    [TestMethod]
    public void EmptyDashboardIsCached() =>
        Assert.AreSame(AnalyticsDashboard.Empty, AnalyticsDashboard.Empty);
}
