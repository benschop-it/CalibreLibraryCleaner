using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Application.Recommendations;

public sealed class GenerateConsolidationRecommendationsUseCase(
    ConsolidationRecommendationPolicy policy)
{
    public Task<IReadOnlyList<ConsolidationRecommendation>> ExecuteAsync(
        LibrarySnapshot snapshot,
        IProgress<RecommendationGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Task.Run<IReadOnlyList<ConsolidationRecommendation>>(
            () => ExecuteCore(snapshot, progress, cancellationToken),
            cancellationToken);
    }

    private ConsolidationRecommendation[] ExecuteCore(
        LibrarySnapshot snapshot,
        IProgress<RecommendationGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<CalibreBookId, CalibreBook> books = snapshot.Books
            .ToDictionary(book => book.Id);
        int total = snapshot.ExactMetadataDuplicateGroups.Count;
        progress?.Report(new(0, total));
        ConsolidationRecommendation[] results = new ConsolidationRecommendation[total];
        for (int index = 0; index < total; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExactMetadataDuplicateGroup group = snapshot.ExactMetadataDuplicateGroups[index];
            CalibreBook[] members = group.Members
                .Where(books.ContainsKey)
                .Select(id => books[id])
                .ToArray();
            results[index] = policy.Generate(
                snapshot.Identity,
                group,
                members,
                snapshot.ExactBinaryDuplicateGroups,
                snapshot.EpubAssessments,
                snapshot.Findings,
                cancellationToken);
            progress?.Report(new(index + 1, total));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }
}

public sealed record RecommendationGenerationProgress(int CompletedGroups, int TotalGroups);
