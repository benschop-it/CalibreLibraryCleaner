using CalibreLibraryCleaner.Application.Recommendations;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IRecommendationExporter
{
    Task<RecommendationExportWriteOutcome> ExportAsync(
        RecommendationReviewExportDocument document,
        string libraryRoot,
        string destinationPath,
        CancellationToken cancellationToken);
}
