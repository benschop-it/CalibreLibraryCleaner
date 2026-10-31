using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Domain.Metadata;

namespace CalibreLibraryCleaner.Application.Metadata;

public sealed record MetadataReviewKeeperSelection(
    UnifiedCandidateGroupId GroupId,
    CalibreBookId KeeperBookId);

public static class BuildMetadataReviewSubjectsUseCase
{
    public static IReadOnlyList<MetadataReviewSubject> Execute(
        LibrarySnapshot snapshot,
        EditionMetadataProvidersBatchResult providerResults,
        IReadOnlyList<MetadataReviewKeeperSelection> keeperSelections,
        IReadOnlySet<CalibreBookId>? subjectBookIds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(providerResults);
        ArgumentNullException.ThrowIfNull(keeperSelections);
        if (keeperSelections.Count != 0)
            throw new ArgumentException(
                "Post-cleanup metadata review does not accept Candidate keeper selections.",
                nameof(keeperSelections));
        EditionMetadataProposal[] proposals = providerResults.Providers
            .OrderBy(value => value.Provider.Id, StringComparer.Ordinal)
            .ThenBy(value => value.Provider.Version, StringComparer.Ordinal)
            .SelectMany(value => value.Proposals.OrderBy(proposal => proposal.Key.Value)
                .Select(proposal => proposal.Value))
            .ToArray();
        return MetadataReviewSubjectPolicy.Create(
            subjectBookIds is null ? snapshot.Books : snapshot.Books.Where(book => subjectBookIds.Contains(book.Id)).ToArray(),
            [],
            new Dictionary<UnifiedCandidateGroupId, CalibreBookId>(),
            proposals);
    }
}
