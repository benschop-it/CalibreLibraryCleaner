using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IEpubContentSignatureCache
{
    Task<EpubContentSignature?> TryReadAsync(
        EpubContentSignatureCacheKey key,
        CancellationToken cancellationToken);

    Task WriteAsync(
        EpubContentSignatureCacheKey key,
        EpubContentSignature signature,
        CancellationToken cancellationToken);

    Task PruneAsync(CancellationToken cancellationToken);
}
