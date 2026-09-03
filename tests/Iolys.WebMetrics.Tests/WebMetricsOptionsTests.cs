namespace Iolys.WebMetrics.Tests;

public sealed class WebMetricsOptionsTests
{
    private readonly WebMetricsOptions _options = new();

    [Theory]
    [InlineData("/wp-admin")]
    [InlineData("//WP-ADMIN/install")]
    [InlineData("/index.php")]
    public void ExcludedNotFoundPathIsRecognized(string path) =>
        Assert.True(_options.IsExcludedNotFoundPath(path));

    [Theory]
    [InlineData("/")]
    [InlineData("/articles/privacy")]
    [InlineData("/php-is-not-a-suffix")]
    public void RegularNotFoundPathIsNotExcluded(string path) =>
        Assert.False(_options.IsExcludedNotFoundPath(path));
}
