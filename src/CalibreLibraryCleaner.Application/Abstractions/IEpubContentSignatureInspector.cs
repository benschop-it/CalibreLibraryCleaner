using CalibreLibraryCleaner.Application.Assessments;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IEpubContentSignatureInspector
{
    Task<EpubContentSignatureResult> InspectContentSignatureAsync(
        EpubContentSignatureRequest request,
        IProgress<EpubContentSignatureProgress>? progress,
        CancellationToken cancellationToken);
}
