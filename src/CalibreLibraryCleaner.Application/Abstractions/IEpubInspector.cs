using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Metadata;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IEpubInspector
{
    Task<EpubInspectionResult> InspectAsync(
        EpubInspectionRequest request,
        IProgress<EpubInspectionProgress>? progress,
        CancellationToken cancellationToken);

    Task<EmbeddedAuthorMetadataObservation> InspectAuthorMetadataAsync(
        EpubInspectionRequest request,
        CancellationToken cancellationToken);
}
