namespace CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;

public sealed record LibrarySnapshotStorageOptions
{
    public string StorageRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CalibreLibraryCleaner",
        "library-snapshots");

    public long MaximumSnapshotBytes { get; init; } = 512L * 1024 * 1024;
}
