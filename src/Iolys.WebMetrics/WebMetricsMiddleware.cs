using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Iolys.WebMetrics;

internal sealed class WebMetricsMiddleware(
    RequestDelegate next,
    TimeProvider timeProvider,
    MonthlyVisitorIdFactory visitorIdFactory,
    IOptions<WebMetricsOptions> options,
    ILogger<WebMetricsMiddleware> logger)
{
    private static readonly string[] BotMarkers =
    [
        "bot",
        "crawler",
        "spider",
        "slurp",
        "bingpreview",
        "facebookexternalhit",
        "linkedinbot",
        "whatsapp",
        "telegrambot",
        "discordbot"
    ];

    private readonly WebMetricsOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context, AnalyticsStore store)
    {
        await next(context);

        var kind = GetEventKind(context);
        if (kind is null)
        {
            return;
        }

        try
        {
            var occurredAt = timeProvider.GetUtcNow();
            var analyticsEvent = new AnalyticsEvent(
                occurredAt,
                await visitorIdFactory.CreateAsync(occurredAt, context, context.RequestAborted),
                kind.Value,
                NormalizePath(context.Request.Path.Value),
                kind == AnalyticsEventKind.PageView ? GetSource(context.Request.Query) : string.Empty,
                kind == AnalyticsEventKind.PageView
                    ? NormalizeDimension(FirstValue(context.Request.Query["utm_medium"]))
                    : string.Empty,
                kind == AnalyticsEventKind.PageView
                    ? NormalizeDimension(FirstValue(context.Request.Query["utm_campaign"]))
                    : string.Empty,
                kind == AnalyticsEventKind.PageView ? GetReferrerHost(context) : string.Empty);
            await store.RecordAsync(analyticsEvent, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client disconnected before the analytics event could be persisted.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to persist an analytics event.");
        }
    }

    private AnalyticsEventKind? GetEventKind(HttpContext context)
    {
        if ((!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            || IsExcluded(context.Request.Path)
            || IsPrefetch(context.Request.Headers)
            || IsBot(context.Request.Headers.UserAgent.ToString()))
        {
            return null;
        }

        if (context.Response.StatusCode == StatusCodes.Status404NotFound)
        {
            return AnalyticsEventKind.NotFound;
        }

        if (!HttpMethods.IsGet(context.Request.Method)
            || context.Response.StatusCode != StatusCodes.Status200OK
            || context.Response.ContentType is not { } contentType
            || !contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return AnalyticsEventKind.PageView;
    }

    private bool IsExcluded(PathString path) => _options.ExcludedPathPrefixes.Any(prefix =>
        !string.IsNullOrWhiteSpace(prefix)
        && path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    private string NormalizePath(string? value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? "/" : value;
        path = path.Length > 1 ? path.TrimEnd('/') : path;
        return Limit(path.ToLowerInvariant(), _options.MaximumPathLength);
    }

    private string GetSource(IQueryCollection query)
    {
        var source = NormalizeDimension(FirstValue(query["utm_source"]));
        if (source.Length == 0)
        {
            source = NormalizeDimension(FirstValue(query["source"]));
        }
        if (source.Length == 0)
        {
            source = NormalizeDimension(FirstValue(query["ref"]));
        }

        return source;
    }

    private string NormalizeDimension(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(character => !char.IsControl(character))
            .ToArray());
        return Limit(normalized, _options.MaximumDimensionLength);
    }

    private static string GetReferrerHost(HttpContext context)
    {
        var referrer = context.Request.GetTypedHeaders().Referer;
        if (referrer is null || string.IsNullOrWhiteSpace(referrer.Host))
        {
            return string.Empty;
        }

        var requestHost = NormalizeHost(context.Request.Host.Host);
        var referrerHost = NormalizeHost(referrer.Host);
        return string.Equals(requestHost, referrerHost, StringComparison.OrdinalIgnoreCase)
            ? "internal"
            : Limit(referrerHost, 253);
    }

    private static bool IsPrefetch(IHeaderDictionary headers) =>
        HeaderContains(headers["Purpose"], "prefetch")
        || HeaderContains(headers["Sec-Purpose"], "prefetch");

    private static bool IsBot(string userAgent) =>
        BotMarkers.Any(marker => userAgent.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeHost(string host)
    {
        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        return normalized.StartsWith("www.", StringComparison.Ordinal) ? normalized[4..] : normalized;
    }

    private static string FirstValue(StringValues values) => values.Count > 0 ? values[0] ?? string.Empty : string.Empty;

    private static bool HeaderContains(StringValues values, string expected) =>
        values.Any(value => value?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true);

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
