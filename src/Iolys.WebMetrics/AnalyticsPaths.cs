using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Iolys.WebMetrics;

internal sealed partial class AnalyticsPaths
{
    private readonly string _filePrefix;

    public AnalyticsPaths(IOptions<WebMetricsOptions> options, IWebHostEnvironment environment)
    {
        var configuredDirectory = options.Value.DataDirectory;
        if (string.IsNullOrWhiteSpace(configuredDirectory))
        {
            throw new InvalidOperationException("WebMetrics:DataDirectory must be configured.");
        }

        if (!ValidPrefix().IsMatch(options.Value.DatabasePrefix))
        {
            throw new InvalidOperationException(
                "WebMetrics:DatabasePrefix may contain only letters, numbers, dots, hyphens and underscores.");
        }

        DataDirectory = Path.GetFullPath(Path.IsPathRooted(configuredDirectory)
            ? configuredDirectory
            : Path.Combine(environment.ContentRootPath, configuredDirectory));
        DatabasePrefix = options.Value.DatabasePrefix;
        _filePrefix = DatabasePrefix + "-";
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }
    public string DatabasePrefix { get; }
    public string LegacyDatabasePath => Path.Combine(DataDirectory, DatabasePrefix + ".db");
    public string LegacyMigrationMarkerPath => LegacyDatabasePath + ".migrated";

    public string GetShardPath(DateOnly day) =>
        Path.Combine(DataDirectory, $"{_filePrefix}{day:yyyy-MM}.db");

    public IReadOnlyList<(DateOnly Month, string Path)> EnumerateShards()
    {
        var shards = new List<(DateOnly Month, string Path)>();
        foreach (var path in Directory.EnumerateFiles(DataDirectory, $"{_filePrefix}????-??.db"))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var value = fileName[_filePrefix.Length..];
            if (DateOnly.TryParseExact(
                    value + "-01",
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var month))
            {
                shards.Add((month, path));
            }
        }

        return shards.OrderBy(item => item.Month).ToArray();
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidPrefix();
}
