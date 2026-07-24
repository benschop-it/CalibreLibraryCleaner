using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recommendations;
using FakeItEasy;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

internal sealed record InfrastructureExecutionFixture(
    CleanupPlan Plan,
    LibrarySnapshot Snapshot,
    string LibraryRoot,
    string SourceFormatPath,
    byte[] FormatBytes);

internal static class InfrastructureExecutionTestData
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    public static InfrastructureExecutionFixture Create(string root, bool targetHasCover = false)
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
        CalibreBook target = Book(1, [], targetHasCover);
        CalibreBook source = Book(2,
        [
            new("PDF", "book", "Author/Shared (2)/book.pdf", FormatFileStatus.Present, fingerprint, observation),
        ], false);
        CalibreBook[] books = [target, source];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        LibraryIdentity identity = new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, library);
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(
            identity, group, books, [], [], [], CancellationToken.None);
        ReviewedConsolidationRecommendation reviewed = ApplyRecommendationOverrideUseCase.Execute(generated, new(
            generated.ModelVersion, generated.InputVersion, RecommendationReviewStatus.Accepted, Now)).Reviewed!;
        LibrarySnapshot snapshot = new(identity, Now, books, [], [], [group], [], [generated]);
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => ids.Create()).Returns(new CleanupPlanId(Guid.Parse("11111111-2222-3333-4444-555555555555")));
        A.CallTo(() => clock.GetUtcNow()).ReturnsNextFromSequence(Now, Now.AddMinutes(1));
        CleanupPlan valid = new GenerateCleanupPlanUseCase(ids, clock).Execute(snapshot, reviewed).Plan!;
        CleanupPlan approved = new ApproveCleanupPlanUseCase(clock).Execute(valid, snapshot, reviewed).Plan!;
        return new(approved, snapshot, library, sourcePath, formatBytes);
    }

    public static void CreateExports(ExecutionBackupInputs inputs, InfrastructureExecutionFixture fixture, bool includeExtra = false)
    {
        foreach ((CalibreBookId recordId, string directory) in inputs.ExportDirectories)
        {
            File.WriteAllText(Path.Combine(directory, "metadata.opf"),
                "<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\"><metadata/></package>");
            if (recordId == new CalibreBookId(2)) File.WriteAllBytes(Path.Combine(directory, "Shared.pdf"), fixture.FormatBytes);
            if (recordId == new CalibreBookId(1) && fixture.Plan.Definition.ExpectedLibraryState.Records
                    .Single(value => value.RecordId == recordId).HasCover)
                File.WriteAllBytes(Path.Combine(directory, "cover.jpg"), [0xff, 0xd8, 0xff, 0xd9]);
        }
        if (includeExtra) File.WriteAllText(Path.Combine(inputs.ExportDirectories[new CalibreBookId(2)], "notes.txt"), "extra");
    }

    private static CalibreBook Book(long id, IEnumerable<BookFormat> formats, bool cover) => new(
        new(id), "Shared", "Author", [new(new(id), "Author", "Author")], [new("isbn", "9780306406157")],
        formats, $"Author/Shared ({id})", new(languages: ["eng"], hasCover: cover));
}
