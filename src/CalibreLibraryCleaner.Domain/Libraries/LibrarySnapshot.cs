using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record LibrarySnapshot
{
    public LibrarySnapshot(
        LibraryIdentity identity,
        DateTimeOffset scannedAt,
        IEnumerable<CalibreBook> books,
        IEnumerable<LibraryFinding> findings,
        IEnumerable<ExactBinaryDuplicateGroup>? exactBinaryDuplicateGroups = null,
        IEnumerable<ExactMetadataDuplicateGroup>? exactMetadataDuplicateGroups = null,
        IEnumerable<FormatAssessment>? epubAssessments = null,
        IEnumerable<ConsolidationRecommendation>? consolidationRecommendations = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(books);
        ArgumentNullException.ThrowIfNull(findings);

        Identity = identity;
        ScannedAt = scannedAt;
        Books = new ReadOnlyCollection<CalibreBook>(books.ToArray());
        Findings = new ReadOnlyCollection<LibraryFinding>(findings.ToArray());
        ExactBinaryDuplicateGroups = new ReadOnlyCollection<ExactBinaryDuplicateGroup>(
            (exactBinaryDuplicateGroups ?? []).ToArray());
        ExactMetadataDuplicateGroups = new ReadOnlyCollection<ExactMetadataDuplicateGroup>(
            (exactMetadataDuplicateGroups ?? []).ToArray());
        FormatAssessment[] orderedAssessments = (epubAssessments ?? [])
            .OrderBy(assessment => assessment.CalibreBookId.Value)
            .ThenBy(assessment => assessment.Format, StringComparer.Ordinal)
            .ThenBy(assessment => assessment.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray();
        if (orderedAssessments.Select(assessment => (assessment.CalibreBookId, assessment.Format, assessment.ExpectedRelativePath))
            .Distinct().Count() != orderedAssessments.Length)
        {
            throw new ArgumentException("EPUB assessment associations must be unique.", nameof(epubAssessments));
        }

        EpubAssessments = new ReadOnlyCollection<FormatAssessment>(orderedAssessments);
        ConsolidationRecommendation[] providedRecommendations = (consolidationRecommendations ?? []).ToArray();
        if (providedRecommendations.Select(value => value.GroupId).Distinct().Count() != providedRecommendations.Length)
        {
            throw new ArgumentException("Recommendation group associations must be unique.", nameof(consolidationRecommendations));
        }

        ConsolidationRecommendation[] orderedRecommendations;
        if (providedRecommendations.Length == 0)
        {
            orderedRecommendations = [];
        }
        else
        {
            Dictionary<ExactMetadataDuplicateGroupId, ConsolidationRecommendation> recommendationsByGroup = providedRecommendations
                .ToDictionary(value => value.GroupId);
            if (recommendationsByGroup.Count != ExactMetadataDuplicateGroups.Count
                || ExactMetadataDuplicateGroups.Any(group => !recommendationsByGroup.ContainsKey(group.Id)))
            {
                throw new ArgumentException("A populated recommendation collection requires exactly one recommendation per metadata group.", nameof(consolidationRecommendations));
            }

            orderedRecommendations = ExactMetadataDuplicateGroups.Select(group => recommendationsByGroup[group.Id]).ToArray();
        }

        ConsolidationRecommendations = new ReadOnlyCollection<ConsolidationRecommendation>(orderedRecommendations);
    }

    public LibraryIdentity Identity { get; }

    public DateTimeOffset ScannedAt { get; }

    public IReadOnlyList<CalibreBook> Books { get; }

    public IReadOnlyList<LibraryFinding> Findings { get; }

    public IReadOnlyList<ExactBinaryDuplicateGroup> ExactBinaryDuplicateGroups { get; }

    public IReadOnlyList<ExactMetadataDuplicateGroup> ExactMetadataDuplicateGroups { get; }

    public IReadOnlyList<FormatAssessment> EpubAssessments { get; }

    public IReadOnlyList<ConsolidationRecommendation> ConsolidationRecommendations { get; }
}
