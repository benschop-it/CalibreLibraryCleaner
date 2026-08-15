namespace CalibreLibraryCleaner.Infrastructure.Caches;

public sealed record FormatHashCacheOptions
{
    public FormatHashCacheOptions(
        string? storageRoot = null,
        long maximumEntryBytes = 16 * 1024,
        long maximumTotalBytes = 256L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntryBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEntryBytes, 128 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTotalBytes, maximumEntryBytes);
        StorageRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CalibreLibraryCleaner",
            "format-hashes");
        MaximumEntryBytes = maximumEntryBytes;
        MaximumTotalBytes = maximumTotalBytes;
    }

    public string StorageRoot { get; }
    public long MaximumEntryBytes { get; }
    public long MaximumTotalBytes { get; }
}
