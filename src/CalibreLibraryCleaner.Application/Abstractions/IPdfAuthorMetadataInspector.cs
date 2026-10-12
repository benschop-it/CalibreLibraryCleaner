using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Metadata;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IPdfAuthorMetadataInspector
{
    Task<EmbeddedAuthorMetadataObservation> InspectAuthorMetadataAsync(
        PdfInspectionRequest request,
        CancellationToken cancellationToken);
}
