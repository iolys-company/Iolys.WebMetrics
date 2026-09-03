using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iolys.WebMetrics.Tests;

[TestClass]
public sealed class WebMetricsMiddlewareTests
{
    [TestMethod]
    public async Task MiddlewareRecordsHtmlPageViewAndNormalizesDimensions()
    {
        using var fixture = StoreFixture.Create();
        var middleware = CreateMiddleware(fixture);
        var context = CreateContext(
            "/Docs/",
            "?utm_source=LinkedIn&utm_medium=Social&utm_campaign=Launch");

        await middleware.InvokeAsync(context, fixture.Store);

        var dashboard = await fixture.Store.GetDashboardAsync(30);
        Assert.AreEqual(1L, dashboard.TodayViews);
        Assert.HasCount(1, dashboard.TopPages);
        Assert.AreEqual("/docs", dashboard.TopPages.Single().Path);
        Assert.HasCount(1, dashboard.UtmSources);
        Assert.AreEqual("linkedin", dashboard.UtmSources.Single().Source);
        Assert.HasCount(1, dashboard.UtmMediums);
        Assert.AreEqual("social", dashboard.UtmMediums.Single().Medium);
        Assert.HasCount(1, dashboard.Campaigns);
        Assert.AreEqual("launch", dashboard.Campaigns.Single().Campaign);
    }

    [TestMethod]
    public async Task MiddlewareIgnoresBotsAndExcludedPaths()
    {
        using var fixture = StoreFixture.Create();
        var middleware = CreateMiddleware(fixture);
        var bot = CreateContext("/article");
        bot.Request.Headers.UserAgent = "ExampleBot/1.0";
        var admin = CreateContext("/admin/stats");

        await middleware.InvokeAsync(bot, fixture.Store);
        await middleware.InvokeAsync(admin, fixture.Store);

        var dashboard = await fixture.Store.GetDashboardAsync(30);
        Assert.AreEqual(0L, dashboard.TodayViews);
    }

    private static WebMetricsMiddleware CreateMiddleware(StoreFixture fixture) =>
        new(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/html; charset=utf-8";
                return Task.CompletedTask;
            },
            fixture.TimeProvider,
            new MonthlyVisitorIdFactory(fixture.Paths),
            Options.Create(fixture.Options),
            NullLogger<WebMetricsMiddleware>.Instance);

    private static DefaultHttpContext CreateContext(string path, string queryString = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(queryString);
        context.Request.Headers.UserAgent = "Example Browser";
        context.Request.Headers.AcceptLanguage = "en-US";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        return context;
    }
}
