using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Domain.Duplicates;

public sealed record MetadataCandidateRetentionCandidate(
    CalibreBookId BookId,
    int AvailableFormatCount,
    int MetadataCompletenessCount,
    int ValidStrongIdentifierCount,
    bool HasCover);

public sealed record MetadataCandidateRetentionDecision
{
    public MetadataCandidateRetentionDecision(
        CalibreBookId keeperBookId,
        IEnumerable<MetadataCandidateRetentionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        MetadataCandidateRetentionCandidate[] values = candidates.ToArray();
        if (values.Length < 2
            || values.Select(value => value.BookId).Distinct().Count() != values.Length
            || values.All(value => value.BookId != keeperBookId))
            throw new ArgumentException("A metadata retention decision requires distinct candidates and one keeper.");
        KeeperBookId = keeperBookId;
        Candidates = new ReadOnlyCollection<MetadataCandidateRetentionCandidate>(values);
    }

    public CalibreBookId KeeperBookId { get; }
    public IReadOnlyList<MetadataCandidateRetentionCandidate> Candidates { get; }
}

public static class MetadataCandidateRetentionPolicy
{
    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.Ordinal)
    {
        "-", "?", "N/A", "NONE", "UNKNOWN", "UNKNOWN AUTHOR", "UNTITLED",
    };

    public static MetadataCandidateRetentionDecision Select(
        IEnumerable<CalibreBook> books)
    {
        ArgumentNullException.ThrowIfNull(books);
        MetadataCandidateRetentionCandidate[] candidates = books
            .Select(CreateCandidate)
            .OrderByDescending(value => value.AvailableFormatCount)
            .ThenByDescending(value => value.MetadataCompletenessCount)
            .ThenByDescending(value => value.ValidStrongIdentifierCount)
            .ThenByDescending(value => value.HasCover)
            .ThenBy(value => value.BookId.Value)
            .ToArray();
        if (candidates.Length < 2)
            throw new ArgumentException("Metadata retention requires at least two records.", nameof(books));
        return new(candidates[0].BookId, candidates);
    }

    private static MetadataCandidateRetentionCandidate CreateCandidate(CalibreBook book)
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
        int formats = book.Formats.Count(value => value.FileStatus is
            FormatFileStatus.Present or FormatFileStatus.ProjectedPresent);
        return new(book.Id, formats, completeness, identifiers, publication.HasCover);
    }

    private static bool IsUsable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = string.Join(' ', value.Trim().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        return !PlaceholderValues.Contains(normalized);
    }
}
