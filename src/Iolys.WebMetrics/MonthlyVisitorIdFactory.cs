using System.Security.Cryptography;
using System.Text;

namespace Iolys.WebMetrics;

internal sealed class MonthlyVisitorIdFactory
{
    private const int KeyLength = 32;
    private readonly string _keyPath;
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private byte[]? _key;

    public MonthlyVisitorIdFactory(AnalyticsPaths paths)
    {
        _keyPath = Path.Combine(paths.DataDirectory, $"{paths.DatabasePrefix}.visitor.key");
    }

    public async Task<string> CreateAsync(
        DateTimeOffset occurredAt,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var key = await GetKeyAsync(cancellationToken);
        var month = occurredAt.UtcDateTime.ToString("yyyy-MM");
        var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Limit(context.Request.Headers.UserAgent.ToString(), 512);
        var language = Limit(context.Request.Headers.AcceptLanguage.ToString(), 128);
        var material = Encoding.UTF8.GetBytes($"{month}\n{ipAddress}\n{userAgent}\n{language}");
        var hash = HMACSHA256.HashData(key, material);
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private async Task<byte[]> GetKeyAsync(CancellationToken cancellationToken)
    {
        if (_key is not null)
        {
            return _key;
        }

        await _keyLock.WaitAsync(cancellationToken);
        try
        {
            if (_key is not null)
            {
                return _key;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
            if (File.Exists(_keyPath))
            {
                _key = await File.ReadAllBytesAsync(_keyPath, cancellationToken);
            }
            else
            {
                var candidate = RandomNumberGenerator.GetBytes(KeyLength);
                try
                {
                    await using var stream = new FileStream(
                        _keyPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 4096,
                        FileOptions.Asynchronous | FileOptions.WriteThrough);
                    await stream.WriteAsync(candidate, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    _key = candidate;
                }
                catch (IOException) when (File.Exists(_keyPath))
                {
                    _key = await File.ReadAllBytesAsync(_keyPath, cancellationToken);
                }
            }

            if (_key.Length != KeyLength)
            {
                throw new InvalidOperationException(
                    $"The analytics visitor key at '{_keyPath}' must contain exactly {KeyLength} bytes.");
            }

            return _key;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
