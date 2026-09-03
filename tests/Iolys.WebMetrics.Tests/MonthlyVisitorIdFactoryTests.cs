using System.Net;
using Microsoft.AspNetCore.Http;

namespace Iolys.WebMetrics.Tests;

public sealed class MonthlyVisitorIdFactoryTests
{
    [Fact]
    public async Task IdentifierIsStableWithinMonthAndRotatesBetweenMonths()
    {
        using var fixture = StoreFixture.Create();
        var factory = new MonthlyVisitorIdFactory(fixture.Paths);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Request.Headers.UserAgent = "Example Browser";
        context.Request.Headers.AcceptLanguage = "fr-FR";

        var first = await factory.CreateAsync(
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            context,
            CancellationToken.None);
        var second = await factory.CreateAsync(
            new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.Zero),
            context,
            CancellationToken.None);
        var nextMonth = await factory.CreateAsync(
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            context,
            CancellationToken.None);

        Assert.Equal(first, second);
        Assert.NotEqual(first, nextMonth);
        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.Equal(32, new FileInfo(Path.Combine(
            fixture.DataDirectory,
            "test-metrics.visitor.key")).Length);
    }
}
