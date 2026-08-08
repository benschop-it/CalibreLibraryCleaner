namespace CalibreLibraryCleaner.Infrastructure.Epub;

public sealed record EpubContentSignatureCacheOptions
{
    public EpubContentSignatureCacheOptions(
        string? storageRoot = null,
        long maximumEntryBytes = 64 * 1024,
        long maximumTotalBytes = 256L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntryBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEntryBytes, 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTotalBytes, maximumEntryBytes);
        StorageRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CalibreLibraryCleaner",
            "epub-content-signatures");
        MaximumEntryBytes = maximumEntryBytes;
        MaximumTotalBytes = maximumTotalBytes;
    }

    public string StorageRoot { get; }
    public long MaximumEntryBytes { get; }
    public long MaximumTotalBytes { get; }
}
