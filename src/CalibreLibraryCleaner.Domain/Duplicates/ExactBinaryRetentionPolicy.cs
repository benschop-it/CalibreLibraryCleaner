using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Duplicates;

public sealed record ExactBinaryRetentionCandidate(
    ExactBinaryDuplicateMember Member,
    int RecordFormatCount,
    int MetadataCompletenessCount,
    int ValidStrongIdentifierCount,
    bool HasCover);

public sealed record ExactBinaryRetentionDecision
{
    public ExactBinaryRetentionDecision(
        ExactBinaryDuplicateGroupId groupId,
        ExactBinaryDuplicateMember? retainedMember,
        IEnumerable<ExactBinaryDuplicateMember> formatRemovals,
        IEnumerable<ExactBinaryRetentionCandidate> candidates,
        string? skipReason = null)
    {
        ArgumentNullException.ThrowIfNull(formatRemovals);
        ArgumentNullException.ThrowIfNull(candidates);
        ExactBinaryDuplicateMember[] removals = formatRemovals.ToArray();
        ExactBinaryRetentionCandidate[] values = candidates.ToArray();
        if ((retainedMember is null) != (skipReason is not null)
            || retainedMember is not null && (removals.Contains(retainedMember) || values.All(value => value.Member != retainedMember)))
        {
            throw new ArgumentException("An exact-binary retention decision must be either eligible with one retained member or skipped with a reason.");
        }

        GroupId = groupId;
        RetainedMember = retainedMember;
        FormatRemovals = new ReadOnlyCollection<ExactBinaryDuplicateMember>(removals);
        Candidates = new ReadOnlyCollection<ExactBinaryRetentionCandidate>(values);
        SkipReason = skipReason;
    }

    public ExactBinaryDuplicateGroupId GroupId { get; }
    public ExactBinaryDuplicateMember? RetainedMember { get; }
    public IReadOnlyList<ExactBinaryDuplicateMember> FormatRemovals { get; }
    public IReadOnlyList<ExactBinaryRetentionCandidate> Candidates { get; }
    public string? SkipReason { get; }
    public bool IsEligible => RetainedMember is not null;
}

public static class ExactBinaryRetentionPolicy
{
    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.Ordinal)
    {
        "-", "?", "N/A", "NONE", "UNKNOWN", "UNKNOWN AUTHOR", "UNTITLED",
    };

    public static IReadOnlyList<ExactBinaryRetentionDecision> Select(
        IEnumerable<ExactBinaryDuplicateGroup> groups,
        IEnumerable<CalibreBook> books,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(books);
        Dictionary<CalibreBookId, CalibreBook> booksById = books.ToDictionary(value => value.Id);
        List<ExactBinaryRetentionDecision> decisions = [];
        foreach (ExactBinaryDuplicateGroup group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Members.Select(value => value.Format).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                decisions.Add(new(group.Id, null, [], [],
                    "The byte-identical files have different canonical format labels."));
                continue;
            }

            ExactBinaryRetentionCandidate[] candidates = group.Members
                .Select(member => CreateCandidate(member, booksById[member.BookId]))
                .OrderByDescending(value => value.RecordFormatCount)
                .ThenByDescending(value => value.MetadataCompletenessCount)
                .ThenByDescending(value => value.ValidStrongIdentifierCount)
                .ThenByDescending(value => value.HasCover)
                .ThenBy(value => value.Member.BookId.Value)
                .ThenBy(value => value.Member.ExpectedRelativePath, StringComparer.Ordinal)
                .ToArray();
            ExactBinaryDuplicateMember retained = candidates[0].Member;
            decisions.Add(new(group.Id, retained,
                group.Members.Where(value => value != retained)
                    .OrderBy(value => value.BookId.Value)
                    .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal),
                candidates));
        }
        return new ReadOnlyCollection<ExactBinaryRetentionDecision>(decisions.ToArray());
    }

    private static ExactBinaryRetentionCandidate CreateCandidate(
        ExactBinaryDuplicateMember member,
        CalibreBook book)
    {
        BookPublicationMetadata publication = book.PublicationMetadata;
        int metadataCompleteness = 0;
        if (IsUsable(book.Title)) metadataCompleteness++;
        if (book.Authors.Count > 0 && book.Authors.All(value => IsUsable(value.Name))) metadataCompleteness++;
        if (IsUsable(book.AuthorSort)) metadataCompleteness++;
        if (IsUsable(publication.Publisher)) metadataCompleteness++;
        if (publication.PublicationDate is not null) metadataCompleteness++;
        if (IsUsable(publication.Series)) metadataCompleteness++;
        if (publication.SeriesIndex is not null) metadataCompleteness++;
        if (publication.Languages.Any(IsUsable)) metadataCompleteness++;
        int validIdentifiers = book.Identifiers.Count(value => IsValidStrongIdentifier(value.Type, value.Value));
        return new(member, book.Formats.Count, metadataCompleteness, validIdentifiers, publication.HasCover);
    }

    private static bool IsUsable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = string.Join(' ', value.Trim().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        return !PlaceholderValues.Contains(normalized);
    }

    private static bool IsValidStrongIdentifier(string type, string value)
    {
        string canonicalType = type.Trim().ToUpperInvariant();
        string trimmed = value.Trim();
        return canonicalType switch
        {
            "ISBN" or "ISBN10" or "ISBN13" => IsValidIsbn(trimmed),
            "DOI" => IsValidDoi(trimmed),
            "ASIN" or "AMAZON" => IsValidAsin(trimmed),
            "OCLC" or "WORLD-CAT" or "WORLDCAT" => IsValidOclc(trimmed),
            _ => false,
        };
    }

    private static bool IsValidIsbn(string value)
    {
        string isbn = new(value.Where(character => char.IsDigit(character) || character is 'X' or 'x')
            .Select(char.ToUpperInvariant).ToArray());
        if (isbn.Length == 10)
        {
            int sum = 0;
            for (int index = 0; index < isbn.Length; index++)
            {
                if (index < 9 && !char.IsDigit(isbn[index])) return false;
                sum += (10 - index) * (isbn[index] == 'X' ? 10 : isbn[index] - '0');
            }
            return sum % 11 == 0;
        }
        if (isbn.Length != 13 || !isbn.All(char.IsDigit)) return false;
        int weighted = 0;
        for (int index = 0; index < 12; index++) weighted += (isbn[index] - '0') * (index % 2 == 0 ? 1 : 3);
        return (10 - weighted % 10) % 10 == isbn[12] - '0';
    }

    private static bool IsValidDoi(string value)
    {
        foreach (string prefix in new[] { "https://doi.org/", "http://doi.org/", "doi:" })
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            value = value[prefix.Length..].Trim();
            break;
        }
        return value.StartsWith("10.", StringComparison.Ordinal) && value.Contains('/') && !value.Any(char.IsWhiteSpace);
    }

    private static bool IsValidAsin(string value) => value.Length == 10 && value.All(char.IsLetterOrDigit);

    private static bool IsValidOclc(string value)
    {
        string normalized = value.ToUpperInvariant();
        foreach (string prefix in new[] { "OCLC", "OCM", "OCN", "ON" })
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) continue;
            normalized = normalized[prefix.Length..].TrimStart(':', ' ');
            break;
        }
        return normalized.Length > 0 && normalized.All(char.IsDigit);
    }
}
