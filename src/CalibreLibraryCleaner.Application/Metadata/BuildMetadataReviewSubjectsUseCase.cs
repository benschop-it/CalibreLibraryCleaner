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
        IReadOnlyList<MetadataReviewKeeperSelection> keeperSelections)
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
            snapshot.Books,
            [],
            new Dictionary<UnifiedCandidateGroupId, CalibreBookId>(),
            proposals);
    }
}
