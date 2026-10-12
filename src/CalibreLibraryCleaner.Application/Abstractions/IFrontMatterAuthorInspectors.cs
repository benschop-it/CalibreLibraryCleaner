using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Metadata;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IEpubFrontMatterAuthorInspector
{
    Task<FrontMatterAuthorObservation> InspectFrontMatterAuthorsAsync(
        EpubInspectionRequest request,
        FrontMatterInspectionLimits limits,
        CancellationToken cancellationToken);
}

public interface IPdfFrontMatterAuthorInspector
{
    Task<FrontMatterAuthorObservation> InspectFrontMatterAuthorsAsync(
        PdfInspectionRequest request,
        FrontMatterInspectionLimits limits,
        CancellationToken cancellationToken);
}
