namespace CalibreLibraryCleaner.Infrastructure.Metadata;

public sealed record EmbeddedAuthorMetadataCacheOptions
{
    public EmbeddedAuthorMetadataCacheOptions(
        string? storageRoot = null,
        int maximumEntryBytes = 64 * 1024,
        long maximumTotalBytes = 256L * 1024 * 1024,
        int maximumEntries = 100_000)
    {
        if (maximumEntryBytes < 1_024 || maximumTotalBytes < maximumEntryBytes
            || maximumEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumEntryBytes));
        StorageRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CalibreLibraryCleaner",
            "embedded-author-metadata-cache");
        MaximumEntryBytes = maximumEntryBytes;
        MaximumTotalBytes = maximumTotalBytes;
        MaximumEntries = maximumEntries;
    }

    public string StorageRoot { get; }
    public int MaximumEntryBytes { get; }
    public long MaximumTotalBytes { get; }
    public int MaximumEntries { get; }
}
