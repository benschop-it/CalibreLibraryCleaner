namespace CalibreLibraryCleaner.Infrastructure.Bibliographic;

public sealed record GoogleBooksApiKeyStoreOptions
{
    public GoogleBooksApiKeyStoreOptions(string? storageRoot = null)
    {
        StorageRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CalibreLibraryCleaner",
            "credentials");
    }

    public string StorageRoot { get; }
}
