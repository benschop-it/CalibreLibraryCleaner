using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Matching;
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
        IEnumerable<EpubAssessment>? epubAssessments = null,
        IEnumerable<ConsolidationRecommendation>? consolidationRecommendations = null,
        IEnumerable<PdfAssessment>? pdfAssessments = null,
        IEnumerable<WorkLanguageCandidateGroup>? workLanguageCandidateGroups = null,
        BookMatchingRunSummary? matchingRunSummary = null)
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
        EpubAssessment[] orderedAssessments = (epubAssessments ?? [])
            .OrderBy(assessment => assessment.CalibreBookId.Value)
            .ThenBy(assessment => assessment.Format, StringComparer.Ordinal)
            .ThenBy(assessment => assessment.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray();
        if (orderedAssessments.Select(assessment => (assessment.CalibreBookId, assessment.Format, assessment.ExpectedRelativePath))
            .Distinct().Count() != orderedAssessments.Length)
        {
            throw new ArgumentException("EPUB assessment associations must be unique.", nameof(epubAssessments));
        }

        EpubAssessments = new ReadOnlyCollection<EpubAssessment>(orderedAssessments);
        PdfAssessment[] orderedPdfAssessments = (pdfAssessments ?? [])
            .OrderBy(assessment => assessment.CalibreBookId.Value)
            .ThenBy(assessment => assessment.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray();
        if (orderedPdfAssessments.Select(assessment => (assessment.CalibreBookId, assessment.ExpectedRelativePath))
            .Distinct().Count() != orderedPdfAssessments.Length)
        {
            throw new ArgumentException("PDF assessment associations must be unique.", nameof(pdfAssessments));
        }

        PdfAssessments = new ReadOnlyCollection<PdfAssessment>(orderedPdfAssessments);
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
        WorkLanguageCandidateGroup[] orderedCandidateGroups = (workLanguageCandidateGroups ?? [])
            .OrderBy(value => value.Language, StringComparer.Ordinal)
            .ThenBy(value => value.Id.Value, StringComparer.Ordinal)
            .ToArray();
        HashSet<CalibreBookId> currentBookIds = Books.Select(value => value.Id).ToHashSet();
        if (orderedCandidateGroups.Select(value => value.Id).Distinct().Count() != orderedCandidateGroups.Length
            || orderedCandidateGroups.SelectMany(value => value.Members).Any(value => !currentBookIds.Contains(value))
            || orderedCandidateGroups.SelectMany(value => value.Members)
                .GroupBy(value => value).Any(group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Work-language candidate groups must be unique, disjoint, and reference current books.",
                nameof(workLanguageCandidateGroups));
        }

        WorkLanguageCandidateGroups = new ReadOnlyCollection<WorkLanguageCandidateGroup>(orderedCandidateGroups);
        MatchingRunSummary = matchingRunSummary ?? BookMatchingRunSummary.Unavailable(Books.Count);
        if (MatchingRunSummary.RecordCount != Books.Count
            || MatchingRunSummary.InferredGroupCount != WorkLanguageCandidateGroups.Count)
        {
            throw new ArgumentException(
                "The matching run summary does not match the snapshot.", nameof(matchingRunSummary));
        }
    }

    public LibraryIdentity Identity { get; }

    public DateTimeOffset ScannedAt { get; }

    public IReadOnlyList<CalibreBook> Books { get; }

    public IReadOnlyList<LibraryFinding> Findings { get; }

    public IReadOnlyList<ExactBinaryDuplicateGroup> ExactBinaryDuplicateGroups { get; }

    public IReadOnlyList<ExactMetadataDuplicateGroup> ExactMetadataDuplicateGroups { get; }

    public IReadOnlyList<EpubAssessment> EpubAssessments { get; }

    public IReadOnlyList<PdfAssessment> PdfAssessments { get; }

    public IReadOnlyList<ConsolidationRecommendation> ConsolidationRecommendations { get; }

    public IReadOnlyList<WorkLanguageCandidateGroup> WorkLanguageCandidateGroups { get; }

    public BookMatchingRunSummary MatchingRunSummary { get; }
}
