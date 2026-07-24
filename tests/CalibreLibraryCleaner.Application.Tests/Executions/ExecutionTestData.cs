using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recommendations;
using FakeItEasy;

namespace CalibreLibraryCleaner.Application.Tests.Executions;

internal static class ExecutionTestData
{
    public static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
    public static readonly FormatFileFingerprint SourceFingerprint = new(5, new(new string('a', 64)));
    public static readonly FormatFileObservation SourceObservation = new(5, Now.AddDays(-1), Now.AddHours(-1), 32);

    public static (CleanupPlan Plan, LibrarySnapshot Preflight, LibrarySnapshot Constructive, LibrarySnapshot Final) Approved()
    {
        CalibreBook target = Book(1, []);
        CalibreBook source = Book(2,
        [
            new("PDF", "book", "Author/Shared (2)/book.pdf", FormatFileStatus.Present,
                SourceFingerprint, SourceObservation),
        ]);
        LibraryIdentity identity = new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\synthetic\\library");
        CalibreBook[] originalBooks = [target, source];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(originalBooks).Single();
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(
            identity, group, originalBooks, [], [], [], CancellationToken.None);
        ReviewedConsolidationRecommendation reviewed = ApplyRecommendationOverrideUseCase.Execute(generated, new(
            generated.ModelVersion, generated.InputVersion, RecommendationReviewStatus.Accepted, Now)).Reviewed!;
        LibrarySnapshot preflight = new(identity, Now, originalBooks, [], [], [group], [], [generated]);
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => ids.Create()).Returns(new CleanupPlanId(Guid.Parse("11111111-2222-3333-4444-555555555555")));
        A.CallTo(() => clock.GetUtcNow()).ReturnsNextFromSequence(Now, Now.AddMinutes(1));
        CleanupPlan valid = new GenerateCleanupPlanUseCase(ids, clock).Execute(preflight, reviewed).Plan!;
        CleanupPlan approved = new ApproveCleanupPlanUseCase(clock).Execute(valid, preflight, reviewed).Plan!;
        if (approved.Definition.TargetRecordId != new CalibreBookId(1)
            || approved.Definition.FormatRetentions.Single().SourceState.RecordId != new CalibreBookId(2))
            throw new InvalidOperationException("The execution fixture did not produce the expected off-target retention.");

        CalibreBook targetWithFormat = Book(1,
        [
            new("PDF", "book", "Author/Shared (1)/book.pdf", FormatFileStatus.Present,
                SourceFingerprint, new(5, Now, Now, 32)),
        ]);
        LibrarySnapshot constructive = new(identity, Now.AddMinutes(2), [targetWithFormat, source], []);
        LibrarySnapshot final = new(identity, Now.AddMinutes(3), [targetWithFormat], []);
        return (approved, preflight, constructive, final);
    }

    public static LibrarySnapshot ChangedSource(LibrarySnapshot source)
    {
        CalibreBook target = source.Books.Single(value => value.Id == new CalibreBookId(1));
        CalibreBook original = source.Books.Single(value => value.Id == new CalibreBookId(2));
        BookFormat format = original.Formats.Single();
        CalibreBook changed = Book(2,
        [
            new(format.Format, format.StoredFileName, format.ExpectedRelativePath, format.FileStatus,
                format.Fingerprint, new(format.Observation!.Length, format.Observation.CreationTimeUtc,
                    format.Observation.LastWriteTimeUtc.AddSeconds(1), format.Observation.Attributes)),
        ]);
        return new(source.Identity, source.ScannedAt.AddMinutes(1), [target, changed], source.Findings,
            source.ExactBinaryDuplicateGroups, source.ExactMetadataDuplicateGroups, source.EpubAssessments,
            source.ConsolidationRecommendations);
    }

    private static CalibreBook Book(long id, IEnumerable<BookFormat> formats) => new(
        new(id), "Shared", "Author", [new(new(id), "Author", "Author")],
        [new("isbn", "9780306406157")], formats, $"Author/Shared ({id})",
        new(languages: ["eng"]));
}
