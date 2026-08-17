namespace CalibreLibraryCleaner.Infrastructure.Bibliographic;

public sealed record GoogleBooksOptions
{
    public GoogleBooksOptions(
        TimeSpan? requestTimeout = null,
        int maximumResponseBytes = 512 * 1024,
        int maximumResults = 5)
    {
        TimeSpan timeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResponseBytes, 16 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResponseBytes, 2 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 5);
        RequestTimeout = timeout;
        MaximumResponseBytes = maximumResponseBytes;
        MaximumResults = maximumResults;
    }

    public TimeSpan RequestTimeout { get; }
    public int MaximumResponseBytes { get; }
    public int MaximumResults { get; }

    public override string ToString() =>
        $"GoogleBooks(Timeout={RequestTimeout}, ResponseBytes={MaximumResponseBytes}, Results={MaximumResults})";
}
