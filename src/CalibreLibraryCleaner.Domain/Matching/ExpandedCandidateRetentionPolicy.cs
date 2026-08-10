using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Matching;

public sealed record ExpandedCandidateRetentionCandidate(
    CalibreBookId BookId,
    int CompletedAssessmentCount,
    int AssessmentScoreTotal,
    int PresentFormatCount,
    int MetadataCompletenessCount,
    int ValidStrongIdentifierCount,
    bool HasCover);

public sealed record ExpandedCandidateRetentionDecision
{
    public ExpandedCandidateRetentionDecision(
        WorkLanguageCandidateGroupId groupId,
        CalibreBookId keeperBookId,
        IEnumerable<ExpandedCandidateRetentionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ExpandedCandidateRetentionCandidate[] values = candidates.ToArray();
        if (values.Length < 2
            || values.Select(value => value.BookId).Distinct().Count() != values.Length
            || values.All(value => value.BookId != keeperBookId))
            throw new ArgumentException("An expanded retention decision requires distinct candidates and one keeper.");
        GroupId = groupId;
        KeeperBookId = keeperBookId;
        Candidates = new ReadOnlyCollection<ExpandedCandidateRetentionCandidate>(values);
    }

    public WorkLanguageCandidateGroupId GroupId { get; }
    public CalibreBookId KeeperBookId { get; }
    public IReadOnlyList<ExpandedCandidateRetentionCandidate> Candidates { get; }
}

public static class ExpandedCandidateRetentionPolicy
{
    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.Ordinal)
    {
        "-", "?", "N/A", "NONE", "UNKNOWN", "UNKNOWN AUTHOR", "UNTITLED",
    };

    public static IReadOnlyList<ExpandedCandidateRetentionDecision> Select(
        IEnumerable<WorkLanguageCandidateGroup> groups,
        IEnumerable<CalibreBook> books,
        IEnumerable<EpubAssessment>? epubAssessments = null,
        IEnumerable<PdfAssessment>? pdfAssessments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(books);
        Dictionary<CalibreBookId, CalibreBook> booksById = books.ToDictionary(value => value.Id);
        Dictionary<CalibreBookId, int[]> assessmentScores = AssessmentScores(epubAssessments, pdfAssessments);
        List<ExpandedCandidateRetentionDecision> decisions = [];
        foreach (WorkLanguageCandidateGroup group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExpandedCandidateRetentionCandidate[] candidates = Rank(
                group.Members.Select(bookId => booksById[bookId]), assessmentScores);
            decisions.Add(new(group.Id, candidates[0].BookId, candidates));
        }
        return new ReadOnlyCollection<ExpandedCandidateRetentionDecision>(decisions.ToArray());
    }

    public static CalibreBookId SelectKeeper(
        IEnumerable<CalibreBook> books,
        IEnumerable<EpubAssessment>? epubAssessments = null,
        IEnumerable<PdfAssessment>? pdfAssessments = null)
    {
        ArgumentNullException.ThrowIfNull(books);
        ExpandedCandidateRetentionCandidate[] candidates = Rank(
            books, AssessmentScores(epubAssessments, pdfAssessments));
        if (candidates.Length == 0)
            throw new ArgumentException("At least one book is required to select a keeper.", nameof(books));
        return candidates[0].BookId;
    }

    public static IReadOnlyList<ExpandedCandidateRetentionCandidate> RankCandidates(
        IEnumerable<CalibreBook> books,
        IEnumerable<EpubAssessment>? epubAssessments = null,
        IEnumerable<PdfAssessment>? pdfAssessments = null)
    {
        ArgumentNullException.ThrowIfNull(books);
        return new ReadOnlyCollection<ExpandedCandidateRetentionCandidate>(Rank(
            books, AssessmentScores(epubAssessments, pdfAssessments)));
    }

    private static Dictionary<CalibreBookId, int[]> AssessmentScores(
        IEnumerable<EpubAssessment>? epubAssessments,
        IEnumerable<PdfAssessment>? pdfAssessments) => (epubAssessments ?? [])
        .Select(value => (value.CalibreBookId, Score: value.Score?.Value))
        .Concat((pdfAssessments ?? []).Select(value => (value.CalibreBookId, Score: value.Score?.Value)))
        .Where(value => value.Score is not null)
        .GroupBy(value => value.CalibreBookId)
        .ToDictionary(group => group.Key, group => group.Select(value => value.Score!.Value).ToArray());

    private static ExpandedCandidateRetentionCandidate[] Rank(
        IEnumerable<CalibreBook> books,
        IReadOnlyDictionary<CalibreBookId, int[]> assessmentScores) => books
        .Select(book => CreateCandidate(book, assessmentScores.GetValueOrDefault(book.Id) ?? []))
        .OrderByDescending(value => value.CompletedAssessmentCount)
        .ThenByDescending(value => value.AssessmentScoreTotal)
        .ThenByDescending(value => value.PresentFormatCount)
        .ThenByDescending(value => value.MetadataCompletenessCount)
        .ThenByDescending(value => value.ValidStrongIdentifierCount)
        .ThenByDescending(value => value.HasCover)
        .ThenBy(value => value.BookId.Value)
        .ToArray();

    private static ExpandedCandidateRetentionCandidate CreateCandidate(CalibreBook book, int[] scores)
    {
        BookPublicationMetadata publication = book.PublicationMetadata;
        int completeness = 0;
        if (IsUsable(book.Title)) completeness++;
        if (book.Authors.Count > 0 && book.Authors.All(value => IsUsable(value.Name))) completeness++;
        if (IsUsable(book.AuthorSort)) completeness++;
        if (IsUsable(publication.Publisher)) completeness++;
        if (publication.PublicationDate is not null) completeness++;
        if (IsUsable(publication.Series)) completeness++;
        if (publication.SeriesIndex is not null) completeness++;
        if (publication.Languages.Any(IsUsable)) completeness++;
        int identifiers = book.Identifiers.Count(value =>
            CandidateMetadataNormalizer.NormalizeStrongIdentifier(value.Type, value.Value) is not null);
        return new(
            book.Id,
            scores.Length,
            scores.Sum(),
            book.Formats.Count(value => value.FileStatus == FormatFileStatus.Present),
            completeness,
            identifiers,
            publication.HasCover);
    }

    private static bool IsUsable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = string.Join(' ', value.Trim().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        return !PlaceholderValues.Contains(normalized);
    }
}
