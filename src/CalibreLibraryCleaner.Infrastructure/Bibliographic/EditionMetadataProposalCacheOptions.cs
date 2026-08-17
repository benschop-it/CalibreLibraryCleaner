namespace CalibreLibraryCleaner.Infrastructure.Bibliographic;

public sealed record EditionMetadataProposalCacheOptions
{
    public EditionMetadataProposalCacheOptions(
        string? storageRoot = null,
        long maximumEntryBytes = 64 * 1024,
        long maximumTotalBytes = 256L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntryBytes, 4 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEntryBytes, 256 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTotalBytes, maximumEntryBytes);
        StorageRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CalibreLibraryCleaner",
            "edition-metadata-proposals");
        MaximumEntryBytes = maximumEntryBytes;
        MaximumTotalBytes = maximumTotalBytes;
    }

    public string StorageRoot { get; }
    public long MaximumEntryBytes { get; }
    public long MaximumTotalBytes { get; }
}
