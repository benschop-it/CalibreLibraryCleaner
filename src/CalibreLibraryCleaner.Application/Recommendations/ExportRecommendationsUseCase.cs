using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Application.Recommendations;

public sealed class ExportRecommendationsUseCase(
    IRecommendationExporter exporter,
    IClock clock)
{
    public async Task<RecommendationExportWriteOutcome> ExecuteAsync(
        LibrarySnapshot snapshot,
        IReadOnlyList<ReviewedConsolidationRecommendation> reviewedGroups,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(reviewedGroups);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return RecommendationExportWriteOutcome.Failure("DESTINATION_REQUIRED", "Choose a JSON destination outside the Calibre library.");
        }

        if (reviewedGroups.Count != snapshot.ConsolidationRecommendations.Count
            || snapshot.ConsolidationRecommendations.Any(generated =>
                reviewedGroups.Count(reviewed => ReferenceEquals(generated, reviewed.Generated)) != 1))
        {
            return RecommendationExportWriteOutcome.Failure("REVIEW_STATE_MISMATCH", "The review state does not match the current generated recommendations.");
        }

        RecommendationReviewExportDocument document;
        try
        {
            document = new(
                snapshot.Identity.CalibreLibraryUuid,
                snapshot.Identity.SchemaVersion,
                clock.GetUtcNow(),
                reviewedGroups.Select(reviewed => new RecommendationReviewExportGroup(
                    reviewed,
                    snapshot.Books.Where(book => reviewed.Generated.MemberIds.Contains(book.Id)).OrderBy(book => book.Id.Value).ToArray())));
        }
        catch (ArgumentException)
        {
            return RecommendationExportWriteOutcome.Failure("REVIEW_STATE_INVALID", "The current review state could not be validated for export.");
        }
        return await exporter.ExportAsync(
            document,
            snapshot.Identity.LibraryRoot,
            destinationPath,
            cancellationToken).ConfigureAwait(false);
    }
}
