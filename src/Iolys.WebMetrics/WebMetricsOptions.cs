namespace Iolys.WebMetrics;

public sealed class WebMetricsOptions
{
    public const string SectionName = "WebMetrics";

    public string DataDirectory { get; set; } = "data";
    public string DatabasePrefix { get; set; } = "analytics";
    public int CompactionCheckHours { get; set; } = 6;
    public int MaximumPathLength { get; set; } = 1024;
    public int MaximumDimensionLength { get; set; } = 100;
    public string[] ExcludedPathPrefixes { get; set; } = ["/admin", "/healthz", "/error"];
    public string[] ExcludedNotFoundPathPrefixes { get; set; } = ["/wp-admin"];
    public string[] ExcludedNotFoundPathSuffixes { get; set; } = [".php"];

    internal bool IsExcludedNotFoundPath(string? path)
    {
        var value = "/" + (path ?? string.Empty).TrimStart('/');
        var requestPath = new PathString(value);
        return ExcludedNotFoundPathPrefixes.Any(prefix =>
                   !string.IsNullOrWhiteSpace(prefix)
                   && requestPath.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
               || ExcludedNotFoundPathSuffixes.Any(suffix =>
            !string.IsNullOrWhiteSpace(suffix)
            && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
