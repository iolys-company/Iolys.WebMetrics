using Microsoft.Extensions.Options;

namespace Iolys.WebMetrics;

internal sealed class AnalyticsMaintenanceService(
    AnalyticsStore store,
    IOptions<WebMetricsOptions> options,
    ILogger<AnalyticsMaintenanceService> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Clamp(options.Value.CompactionCheckHours, 1, 24));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await store.CompactExpiredShardsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Automatic analytics compaction failed; it will be retried later.");
            }
        }
    }
}
