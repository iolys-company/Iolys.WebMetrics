using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iolys.WebMetrics.Tests;

internal sealed class StoreFixture : IDisposable
{
    private StoreFixture(DateTimeOffset utcNow)
    {
        DataDirectory = Path.Combine(
            Path.GetTempPath(),
            "Iolys.WebMetrics.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);

        Options = new WebMetricsOptions
        {
            DataDirectory = DataDirectory,
            DatabasePrefix = "test-metrics"
        };
        var wrappedOptions = Microsoft.Extensions.Options.Options.Create(Options);
        Paths = new AnalyticsPaths(wrappedOptions, new TestWebHostEnvironment(DataDirectory));
        DbContextFactory = new AnalyticsDbContextFactory(NullLoggerFactory.Instance);
        TimeProvider = new MutableTimeProvider(utcNow);
        Store = new AnalyticsStore(
            Paths,
            DbContextFactory,
            TimeProvider,
            wrappedOptions,
            NullLogger<AnalyticsStore>.Instance);
    }

    public string DataDirectory { get; }

    public WebMetricsOptions Options { get; }

    public AnalyticsPaths Paths { get; }

    public AnalyticsDbContextFactory DbContextFactory { get; }

    public MutableTimeProvider TimeProvider { get; }

    public AnalyticsStore Store { get; }

    public static StoreFixture Create(DateTimeOffset? utcNow = null) =>
        new(utcNow ?? new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

    public Task RecordAsync(
        string visitorId,
        AnalyticsEventKind kind,
        string path,
        string source = "",
        string medium = "",
        string campaign = "",
        string referrerHost = "") =>
        Store.RecordAsync(
            new AnalyticsEvent(
                TimeProvider.UtcNow,
                visitorId,
                kind,
                path,
                source,
                medium,
                campaign,
                referrerHost),
            CancellationToken.None);

    public void Dispose()
    {
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }

    internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Iolys.WebMetrics.Tests";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = contentRootPath;

        public string EnvironmentName { get; set; } = "Testing";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
