namespace Iolys.WebMetrics.Tests;

[TestClass]
public sealed class WebMetricsOptionsTests
{
    private readonly WebMetricsOptions _options = new();

    [TestMethod]
    [DataRow("/wp-admin")]
    [DataRow("//WP-ADMIN/install")]
    [DataRow("/index.php")]
    public void ExcludedNotFoundPathIsRecognized(string path) =>
        Assert.IsTrue(_options.IsExcludedNotFoundPath(path));

    [TestMethod]
    [DataRow("/")]
    [DataRow("/articles/privacy")]
    [DataRow("/php-is-not-a-suffix")]
    public void RegularNotFoundPathIsNotExcluded(string path) =>
        Assert.IsFalse(_options.IsExcludedNotFoundPath(path));
}
