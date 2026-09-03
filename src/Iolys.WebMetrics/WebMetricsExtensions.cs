using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class WebMetricsServiceCollectionExtensions
    {
        public static IServiceCollection AddWebMetrics(
            this IServiceCollection services,
            IConfigurationSection configuration)
        {
            services.AddOptions<Iolys.WebMetrics.WebMetricsOptions>()
                .Bind(configuration)
                .Validate(
                    options => options.CompactionCheckHours is >= 1 and <= 24,
                    "WebMetrics:CompactionCheckHours must be between 1 and 24.")
                .Validate(
                    options => options.MaximumPathLength > 0 && options.MaximumDimensionLength > 0,
                    "WebMetrics maximum lengths must be positive.")
                .ValidateOnStart();
            services.TryAddSingleton<TimeProvider>(TimeProvider.System);
            services.AddSingleton<Iolys.WebMetrics.AnalyticsPaths>();
            services.AddSingleton<Iolys.WebMetrics.AnalyticsDbContextFactory>();
            services.AddSingleton<Iolys.WebMetrics.MonthlyVisitorIdFactory>();
            services.AddSingleton<Iolys.WebMetrics.AnalyticsStore>();
            services.AddSingleton<Iolys.WebMetrics.IAnalyticsReportReader>(provider =>
                provider.GetRequiredService<Iolys.WebMetrics.AnalyticsStore>());
            services.AddSingleton<Iolys.WebMetrics.IAnalyticsNotFoundManager>(provider =>
                provider.GetRequiredService<Iolys.WebMetrics.AnalyticsStore>());
            services.AddHostedService<Iolys.WebMetrics.AnalyticsMaintenanceService>();
            return services;
        }
    }
}

namespace Microsoft.AspNetCore.Builder
{
    public static class WebMetricsApplicationBuilderExtensions
    {
        public static IApplicationBuilder UseWebMetrics(this IApplicationBuilder app) =>
            app.UseMiddleware<Iolys.WebMetrics.WebMetricsMiddleware>();
    }
}
