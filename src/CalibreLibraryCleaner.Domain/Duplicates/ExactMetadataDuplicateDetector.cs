using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Duplicates;

public static class ExactMetadataDuplicateDetector
{
    public static IReadOnlyList<ExactMetadataDuplicateGroup> Detect(
        IEnumerable<CalibreBook> books,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(books);
        Dictionary<NormalizedBookIdentity, List<CalibreBookId>> membersByIdentity = [];
        foreach (CalibreBook book in books)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCreateIdentity(book, cancellationToken, out NormalizedBookIdentity? identity))
            {
                continue;
            }

            if (!membersByIdentity.TryGetValue(identity!, out List<CalibreBookId>? members))
            {
                members = [];
                membersByIdentity.Add(identity!, members);
            }

            members.Add(book.Id);
        }

        cancellationToken.ThrowIfCancellationRequested();
        List<ExactMetadataDuplicateGroup> groups = [];
        foreach ((NormalizedBookIdentity identity, List<CalibreBookId> members) in membersByIdentity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (members.Distinct().Take(2).Count() == 2)
            {
                groups.Add(new(identity, members));
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
