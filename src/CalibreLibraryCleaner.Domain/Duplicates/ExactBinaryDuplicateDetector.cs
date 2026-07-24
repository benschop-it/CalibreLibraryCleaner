using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Duplicates;

public static class ExactBinaryDuplicateDetector
{
    public static IReadOnlyList<ExactBinaryDuplicateGroup> Detect(
        IEnumerable<CalibreBook> books,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(books);
        cancellationToken.ThrowIfCancellationRequested();
        return EnumerateCandidates(books, cancellationToken)
            .GroupBy(candidate => new
            {
                candidate.Format.Fingerprint!.SizeInBytes,
                Digest = candidate.Format.Fingerprint.Sha256.Value,
            })
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Candidate first = group.First();
                ExactBinaryDuplicateMember[] members = group
                    .Select(candidate =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return new ExactBinaryDuplicateMember(
                            candidate.BookId,
                            candidate.Format.Format,
                            candidate.Format.ExpectedRelativePath);
                    })
                    .Distinct()
                    .OrderBy(member => member.BookId.Value)
                    .ThenBy(member => member.Format, StringComparer.Ordinal)
                    .ThenBy(member => member.ExpectedRelativePath, StringComparer.Ordinal)
                    .ToArray();
                return members.Length < 2
                    ? null
                    : new ExactBinaryDuplicateGroup(
                        ExactBinaryDuplicateGroupId.From(first.Format.Fingerprint!),
                        first.Format.Fingerprint!,
                        members);
            })
            .Where(group => group is not null)
            .Select(group => group!)
            .OrderByDescending(group => group.Fingerprint.SizeInBytes)
            .ThenBy(group => group.Fingerprint.Sha256.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<Candidate> EnumerateCandidates(
        IEnumerable<CalibreBook> books,
        CancellationToken cancellationToken)
    {
        foreach (CalibreBook book in books)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (BookFormat format in book.Formats)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (format.Fingerprint is not null)
                {
                    yield return new(book.Id, format);
                }
            }
        }
    }

    private sealed record Candidate(CalibreBookId BookId, BookFormat Format);
}
