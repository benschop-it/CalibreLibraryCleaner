namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record LibraryIdentity
{
    public LibraryIdentity(string calibreLibraryUuid, int schemaVersion, string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calibreLibraryUuid);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);

        CalibreLibraryUuid = calibreLibraryUuid;
        SchemaVersion = schemaVersion;
        LibraryRoot = libraryRoot;
    }

    public string CalibreLibraryUuid { get; }

    public int SchemaVersion { get; }

    public string LibraryRoot { get; }
}
