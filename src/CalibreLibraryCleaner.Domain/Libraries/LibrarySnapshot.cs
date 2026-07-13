using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Findings;

namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record LibrarySnapshot
{
    public LibrarySnapshot(
        LibraryIdentity identity,
        DateTimeOffset scannedAt,
        IEnumerable<CalibreBook> books,
        IEnumerable<LibraryFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(books);
        ArgumentNullException.ThrowIfNull(findings);

        Identity = identity;
        ScannedAt = scannedAt;
        Books = new ReadOnlyCollection<CalibreBook>(books.ToArray());
        Findings = new ReadOnlyCollection<LibraryFinding>(findings.ToArray());
    }

    public LibraryIdentity Identity { get; }

    public DateTimeOffset ScannedAt { get; }

    public IReadOnlyList<CalibreBook> Books { get; }

    public IReadOnlyList<LibraryFinding> Findings { get; }
}
