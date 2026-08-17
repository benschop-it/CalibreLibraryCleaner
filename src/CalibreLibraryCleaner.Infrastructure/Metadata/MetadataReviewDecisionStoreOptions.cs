namespace CalibreLibraryCleaner.Infrastructure.Metadata;

public sealed record MetadataReviewDecisionStoreOptions
{
    public MetadataReviewDecisionStoreOptions(
        string? storageRoot = null,
        long maximumFileBytes = 16L * 1024 * 1024,
        int maximumDecisions = 50_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 64 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumFileBytes, 64L * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDecisions, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumDecisions, 100_000);
        StorageRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CalibreLibraryCleaner",
            "metadata-review-decisions");
        MaximumFileBytes = maximumFileBytes;
        MaximumDecisions = maximumDecisions;
    }

    public string StorageRoot { get; }
    public long MaximumFileBytes { get; }
    public int MaximumDecisions { get; }
}
