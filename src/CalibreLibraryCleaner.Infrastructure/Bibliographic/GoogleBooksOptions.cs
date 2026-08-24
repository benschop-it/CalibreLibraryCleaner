namespace CalibreLibraryCleaner.Infrastructure.Bibliographic;

public sealed record GoogleBooksOptions
{
    public GoogleBooksOptions(
        TimeSpan? requestTimeout = null,
        TimeSpan? minimumRequestInterval = null,
        int maximumResponseBytes = 512 * 1024,
        int maximumResults = 5)
    {
        TimeSpan timeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        TimeSpan interval = minimumRequestInterval ?? TimeSpan.FromSeconds(3);
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        if (interval < TimeSpan.Zero || interval > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(minimumRequestInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResponseBytes, 16 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResponseBytes, 2 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 5);
        RequestTimeout = timeout;
        MinimumRequestInterval = interval;
        MaximumResponseBytes = maximumResponseBytes;
        MaximumResults = maximumResults;
    }

    public TimeSpan RequestTimeout { get; }
    public TimeSpan MinimumRequestInterval { get; }
    public int MaximumResponseBytes { get; }
    public int MaximumResults { get; }

    public override string ToString() =>
        $"GoogleBooks(Timeout={RequestTimeout}, Interval={MinimumRequestInterval}, ResponseBytes={MaximumResponseBytes}, Results={MaximumResults})";
}
