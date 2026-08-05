using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

internal sealed record InfrastructureExecutionFixture(
    LibrarySnapshot Snapshot,
    string LibraryRoot,
    string SourceFormatPath,
    byte[] FormatBytes);

internal static class InfrastructureExecutionTestData
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    public static InfrastructureExecutionFixture Create(string root)
    {
        string library = Path.Combine(root, "library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0x00]);
        string sourceDirectory = Path.Combine(library, "Author", "Shared (2)");
        Directory.CreateDirectory(sourceDirectory);
        string sourcePath = Path.Combine(sourceDirectory, "book.pdf");
        byte[] formatBytes = "hello"u8.ToArray();
        File.WriteAllBytes(sourcePath, formatBytes);
        FileInfo info = new(sourcePath); info.Refresh();
        FormatFileFingerprint fingerprint = new(info.Length,
            new(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(formatBytes)).ToLowerInvariant()));
        FormatFileObservation observation = new(info.Length,
            new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), (int)info.Attributes);
        CalibreBook target = Book(1, []);
        CalibreBook source = Book(2,
        [
            new("PDF", "book", "Author/Shared (2)/book.pdf", FormatFileStatus.Present, fingerprint, observation),
        ]);
        CalibreBook[] books = [target, source];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        LibraryIdentity identity = new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, library);
        LibrarySnapshot snapshot = new(identity, Now, books, [], [], [group]);
        return new(snapshot, library, sourcePath, formatBytes);
    }

    private static CalibreBook Book(long id, IEnumerable<BookFormat> formats) => new(
        new(id), "Shared", "Author", [new(new(id), "Author", "Author")], [new("isbn", "9780306406157")],
        formats, $"Author/Shared ({id})", new(languages: ["eng"]));
}
