using CalibreLibraryCleaner.Application.Libraries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IFormatFileHasher
{
    Task<IReadOnlyList<FormatHashResult>> HashAsync(
        IReadOnlyList<FormatHashRequest> requests,
        int maxDegreeOfParallelism,
        IProgress<FormatHashProgress>? progress,
        CancellationToken cancellationToken);
}
