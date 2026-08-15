using CalibreLibraryCleaner.Application.Libraries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IFormatHashCache
{
    Task<FormatHashCacheEntry?> TryReadAsync(
        FormatHashCacheKey key,
        CancellationToken cancellationToken);

    Task WriteAsync(
        FormatHashCacheEntry entry,
        CancellationToken cancellationToken);

    Task PruneAsync(CancellationToken cancellationToken);
}
