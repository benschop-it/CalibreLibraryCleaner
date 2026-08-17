namespace CalibreLibraryCleaner.Application.Metadata;

public interface IGoogleBooksApiKeyStore
{
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken);

    Task<string?> ReadAsync(CancellationToken cancellationToken);

    Task SaveAsync(string apiKey, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

public sealed class GoogleBooksApiKeyStorageException : Exception
{
    public GoogleBooksApiKeyStorageException()
        : base("The protected Google Books API key store is unavailable.")
    {
    }
}
