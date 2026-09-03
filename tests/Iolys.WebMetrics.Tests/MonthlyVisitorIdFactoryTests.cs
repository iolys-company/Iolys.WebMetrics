using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Iolys.WebMetrics.Tests;

[TestClass]
public sealed class MonthlyVisitorIdFactoryTests
{
    [TestMethod]
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

        Assert.AreEqual(first, second);
        Assert.AreNotEqual(first, nextMonth);
        StringAssert.Matches(first, new Regex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant));
        Assert.AreEqual(32L, new FileInfo(Path.Combine(
            fixture.DataDirectory,
            "test-metrics.visitor.key")).Length);
    }
}
