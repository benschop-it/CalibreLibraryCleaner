namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record CalibreCatalogReadOutcome
{
    private CalibreCatalogReadOutcome(CalibreCatalogRecord? catalog, LibraryError? error)
    {
        Catalog = catalog;
        Error = error;
    }

    public bool IsSuccess => Catalog is not null;

    public CalibreCatalogRecord? Catalog { get; }

    public LibraryError? Error { get; }

    public static CalibreCatalogReadOutcome Success(CalibreCatalogRecord catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new(catalog, null);
    }

    public static CalibreCatalogReadOutcome Failure(LibraryError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(null, error);
    }
}
