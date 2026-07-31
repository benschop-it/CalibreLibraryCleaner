using CalibreLibraryCleaner.Application.Assessments.Pdf;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IPdfInspector
{
    Task<PdfInspectionResult> InspectAsync(
        PdfInspectionRequest request,
        Func<PdfDocumentHeaderFacts, CancellationToken, ValueTask<IReadOnlyList<int>>> selectPages,
        IProgress<PdfInspectionProgress>? progress,
        CancellationToken cancellationToken);
}
