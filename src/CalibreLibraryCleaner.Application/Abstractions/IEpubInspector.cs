using CalibreLibraryCleaner.Application.Assessments;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IEpubInspector
{
    Task<EpubInspectionResult> InspectAsync(
        EpubInspectionRequest request,
        IProgress<EpubInspectionProgress>? progress,
        CancellationToken cancellationToken);
}
