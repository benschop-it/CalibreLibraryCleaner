using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Domain.Duplicates;

public sealed record ExactMetadataPolicyVersion
{
    public ExactMetadataPolicyVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim();
        if (normalized.Length > 128)
            throw new ArgumentException("An Exact Metadata policy version exceeds its bound.", nameof(value));
        Value = normalized;
    }

    public static ExactMetadataPolicyVersion V1 { get; } = new("exact-metadata/1.0.0");
    public static ExactMetadataPolicyVersion V2 { get; } = new("exact-metadata/1.1.0");
    public static ExactMetadataPolicyVersion Current => V2;

    public string Value { get; }
}

public static class ExactMetadataDuplicateDetector
{
    private static readonly string[] StrongIdentifierTypes = ["ISBN", "DOI", "ASIN", "OCLC"];

    public static ExactMetadataPolicyVersion PolicyVersion => ExactMetadataPolicyVersion.Current;

    public static IReadOnlyList<ExactMetadataDuplicateGroup> Detect(
        IEnumerable<CalibreBook> books,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(books);
        Dictionary<NormalizedBookIdentity, List<CalibreBook>> membersByIdentity = [];
        foreach (CalibreBook book in books)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCreateIdentity(book, cancellationToken, out NormalizedBookIdentity? identity))
            {
                continue;
            }

            if (!membersByIdentity.TryGetValue(identity!, out List<CalibreBook>? members))
            {
                members = [];
                membersByIdentity.Add(identity!, members);
            }

            members.Add(book);
        }

        cancellationToken.ThrowIfCancellationRequested();
        List<ExactMetadataDuplicateGroup> groups = [];
        foreach ((NormalizedBookIdentity identity, List<CalibreBook> members) in membersByIdentity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CalibreBookId[] compatible = CompatibleMembers(members, cancellationToken);
            if (compatible.Length >= 2)
            {
                groups.Add(new(identity, compatible));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ExactMetadataDuplicateGroup[] ordered = groups
            .OrderBy(group => group.Identity.Title.Value, StringComparer.Ordinal)
            .ThenBy(group => group.Identity.Authors, NormalizedAuthorSetComparer.Instance)
            .ThenBy(group => group.Id.Value, StringComparer.Ordinal)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return ordered;
    }

    private static CalibreBookId[] CompatibleMembers(
        IEnumerable<CalibreBook> members,
        CancellationToken cancellationToken)
    {
        CalibreBook[] ordered = members.GroupBy(value => value.Id).Select(value => value.First())
            .OrderBy(value => value.Id.Value).ToArray();
        if (ordered.Length < 2) return [];
        HashSet<CalibreBookId> outliers = [];
        if (!ApplySignal(ordered, Languages, outliers, cancellationToken)) return [];
        foreach (string type in StrongIdentifierTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ApplySignal(ordered, book => StrongIdentifiers(book, type), outliers, cancellationToken))
                return [];
        }

        CalibreBook[] retained = ordered.Where(value => !outliers.Contains(value.Id)).ToArray();
        if (retained.Length < 2 || HasDisjointPair(retained, Languages, cancellationToken)) return [];
        foreach (string type in StrongIdentifierTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasDisjointPair(retained, book => StrongIdentifiers(book, type), cancellationToken)) return [];
        }
        return retained.Select(value => value.Id).ToArray();
    }

    private static bool ApplySignal(
        IReadOnlyList<CalibreBook> members,
        Func<CalibreBook, HashSet<string>> selector,
        HashSet<CalibreBookId> outliers,
        CancellationToken cancellationToken)
    {
        (CalibreBook Book, HashSet<string> Values)[] known = members
            .Where(value => !outliers.Contains(value.Id))
            .Select(value => (value, selector(value)))
            .Where(value => value.Item2.Count > 0)
            .ToArray();
        if (!HasDisjointPair(known.Select(value => value.Values).ToArray(), cancellationToken)) return true;

        IGrouping<string, (CalibreBook Book, HashSet<string> Values)>[] consensus = known
            .GroupBy(value => Signature(value.Values), StringComparer.Ordinal)
            .Where(value => value.Count() >= 2).ToArray();
        if (consensus.Length != 1) return false;
        HashSet<string> consensusValues = consensus[0].First().Values;
        foreach ((CalibreBook book, HashSet<string> values) in known)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!values.Overlaps(consensusValues)) outliers.Add(book.Id);
        }
        return true;
    }

    private static bool HasDisjointPair(
        IEnumerable<CalibreBook> members,
        Func<CalibreBook, HashSet<string>> selector,
        CancellationToken cancellationToken) => HasDisjointPair(
        members.Select(selector).Where(value => value.Count > 0).ToArray(), cancellationToken);

    private static bool HasDisjointPair(
        HashSet<string>[] values,
        CancellationToken cancellationToken)
    {
        if (values.Length < 2) return false;
        HashSet<string> shared = new(values[0], StringComparer.Ordinal);
        for (int index = 1; index < values.Length && shared.Count > 0; index++)
            shared.IntersectWith(values[index]);
        if (shared.Count > 0) return false;
        for (int first = 0; first < values.Length; first++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int second = first + 1; second < values.Length; second++)
                if (!values[first].Overlaps(values[second])) return true;
        }
        return false;
    }

    private static HashSet<string> Languages(CalibreBook book) => book.PublicationMetadata.Languages
        .Select(CandidateMetadataNormalizer.NormalizeLanguage)
        .Where(value => value is not null).Select(value => value!)
        .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> StrongIdentifiers(CalibreBook book, string type) => book.Identifiers
        .Select(value => CandidateMetadataNormalizer.NormalizeStrongIdentifier(value.Type, value.Value))
        .Where(value => value is not null && value.StartsWith(type + ':', StringComparison.Ordinal))
        .Select(value => value!).ToHashSet(StringComparer.Ordinal);

    private static string Signature(IEnumerable<string> values) =>
        string.Join('|', values.Order(StringComparer.Ordinal));

    private static bool TryCreateIdentity(
        CalibreBook book,
        CancellationToken cancellationToken,
        out NormalizedBookIdentity? identity)
    {
        if (!MetadataTextNormalizer.TryNormalizeTitle(book.Title, out NormalizedTitle? title) ||
            !MetadataTextNormalizer.TryCreateAuthorSet(
                book.Authors.Select(author => author.Name),
                out NormalizedAuthorSet? authors,
                cancellationToken))
        {
            identity = null;
            return false;
        }

        identity = new(title!, authors!);
        return true;
    }

    private sealed class NormalizedAuthorSetComparer : IComparer<NormalizedAuthorSet>
    {
        public static NormalizedAuthorSetComparer Instance { get; } = new();

        public int Compare(NormalizedAuthorSet? first, NormalizedAuthorSet? second)
        {
            if (ReferenceEquals(first, second))
            {
                return 0;
            }

            if (first is null)
            {
                return -1;
            }

            if (second is null)
            {
                return 1;
            }

            int sharedCount = Math.Min(first.Names.Count, second.Names.Count);
            for (int index = 0; index < sharedCount; index++)
            {
                int comparison = StringComparer.Ordinal.Compare(
                    first.Names[index].Value,
                    second.Names[index].Value);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return first.Names.Count.CompareTo(second.Names.Count);
        }
    }
}
