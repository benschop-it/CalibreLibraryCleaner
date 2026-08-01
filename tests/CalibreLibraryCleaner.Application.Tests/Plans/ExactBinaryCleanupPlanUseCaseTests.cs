using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Plans;

public sealed class ExactBinaryCleanupPlanUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DifferentMetadataStillProducesConsolidationPlanWithUniqueFormatsBackedUp()
    {
        LibrarySnapshot snapshot = Snapshot();
        ExactBinaryDuplicateGroup group = snapshot.ExactBinaryDuplicateGroups.Single();
        ExactBinaryDuplicateMember keeper = group.Members.Single(value => value.BookId == new CalibreBookId(1));
        GenerateExactBinaryCleanupPlanUseCase useCase = CreateGenerator();

        ExactBinaryCleanupPlanGenerationOutcome outcome = useCase.Execute(
            snapshot, group.Id, keeper);

        outcome.IsSuccess.Should().BeTrue();
        ExactBinaryCleanupPlan plan = outcome.Plan!;
        plan.State.Should().Be(CleanupPlanState.Valid);
        plan.Definition.RetainedFormat.RecordId.Should().Be(new CalibreBookId(1));
        plan.Definition.RecordIdsToRemove.Should().Equal(new CalibreBookId(2));
        plan.Definition.DuplicateEvidence.Should().ContainSingle()
            .Which.RecordId.Should().Be(new CalibreBookId(2));
        plan.Definition.ExpectedRecords.Should().HaveCount(2);
        plan.Definition.ExpectedRecords.Single(value => value.RecordId == new CalibreBookId(1))
            .Formats.Should().Contain(value => value.Format == "PDF");
        plan.Definition.BackupRequirements.Should().Contain(value =>
            value.Kind == BackupRequirementKind.FormatFile
            && value.RecordId == new CalibreBookId(1)
            && value.Format == "PDF");
        plan.Validation.Issues.Should().Contain(value => value.Code == "BINARY_PLAN.SINGLE_KEEPER_CONSOLIDATION");
        plan.ContentDigest.Should().Be(ExactBinaryCleanupPlanContentDigestPolicy.Compute(plan.Definition));
    }

    [Fact]
    public void ExplicitApprovalBindsToCurrentExactPlanBody()
    {
        LibrarySnapshot snapshot = Snapshot();
        ExactBinaryDuplicateGroup group = snapshot.ExactBinaryDuplicateGroups.Single();
        ExactBinaryCleanupPlan valid = CreateGenerator().Execute(
            snapshot, group.Id, group.Members[0]).Plan!;
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddMinutes(1));

        ExactBinaryCleanupPlanOperationOutcome outcome = new ApproveExactBinaryCleanupPlanUseCase(clock)
            .Execute(valid, snapshot);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Plan!.State.Should().Be(CleanupPlanState.Approved);
        outcome.Plan.Approval!.ContentDigest.Should().Be(valid.ContentDigest);
        outcome.Plan.Definition.Should().BeSameAs(valid.Definition);
    }

    [Fact]
    public void ChangedUniqueFormatOnAffectedRecordBlocksApproval()
    {
        LibrarySnapshot snapshot = Snapshot();
        ExactBinaryDuplicateGroup group = snapshot.ExactBinaryDuplicateGroups.Single();
        ExactBinaryCleanupPlan valid = CreateGenerator().Execute(
            snapshot, group.Id, group.Members[0]).Plan!;
        CalibreBook original = snapshot.Books.Single(value => value.Id == new CalibreBookId(1));
        BookFormat epub = original.Formats.Single(value => value.Format == "EPUB");
        BookFormat oldPdf = original.Formats.Single(value => value.Format == "PDF");
        FormatFileFingerprint changedFingerprint = new(99, new Sha256Digest(new string('f', 64)));
        BookFormat changedPdf = new("PDF", oldPdf.StoredFileName, oldPdf.ExpectedRelativePath,
            FormatFileStatus.Present, changedFingerprint,
            new(changedFingerprint.SizeInBytes, Now, Now.AddMinutes(1), 0));
        CalibreBook changedBook = new(original.Id, original.Title, original.AuthorSort, original.Authors,
            original.Identifiers, [epub, changedPdf], original.RelativeDirectory, original.PublicationMetadata);
        CalibreBook[] changedBooks = [changedBook, snapshot.Books.Single(value => value.Id == new CalibreBookId(2))];
        LibrarySnapshot changed = new(snapshot.Identity, Now.AddMinutes(1), changedBooks, [],
            ExactBinaryDuplicateDetector.Detect(changedBooks));
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddMinutes(2));

        ExactBinaryCleanupPlanOperationOutcome outcome = new ApproveExactBinaryCleanupPlanUseCase(clock)
            .Execute(valid, changed);

        outcome.Plan.Should().BeNull();
        outcome.Validation.BlockingErrors.Should().Contain(value => value.Code == "BINARY_PLAN.STALE");
    }

    [Fact]
    public void KeeperOutsideCurrentGroupIsRejectedWithoutAllocatingPlanId()
    {
        LibrarySnapshot snapshot = Snapshot();
        ExactBinaryDuplicateGroup group = snapshot.ExactBinaryDuplicateGroups.Single();
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        ExactBinaryDuplicateMember outside = new(new CalibreBookId(99), "EPUB", "Outside/Book.epub");

        ExactBinaryCleanupPlanGenerationOutcome outcome = new GenerateExactBinaryCleanupPlanUseCase(ids, clock)
            .Execute(snapshot, group.Id, outside);

        outcome.Plan.Should().BeNull();
        outcome.Validation.BlockingErrors.Should().Contain(value => value.Code == "BINARY_PLAN.KEEPER_NOT_MEMBER");
        A.CallTo(() => ids.Create()).MustNotHaveHappened();
    }

    [Fact]
    public void ThreeRecordGroupAlwaysDeletesEveryRecordExceptKeeper()
    {
        LibrarySnapshot original = Snapshot();
        FormatFileFingerprint duplicate = original.Books[0].Formats.Single(value => value.Format == "EPUB").Fingerprint!;
        CalibreBook third = new(
            new(3),
            "Third metadata title",
            "Carol",
            [new(new(3), "Carol", "Carol")],
            [],
            [new("EPUB", "third", "Carol/Third (3)/third.epub", FormatFileStatus.Present,
                duplicate, new(duplicate.SizeInBytes, Now, Now, 0))],
            "Carol/Third (3)");
        CalibreBook[] books = [.. original.Books, third];
        LibrarySnapshot snapshot = new(original.Identity, Now, books, [], ExactBinaryDuplicateDetector.Detect(books));
        ExactBinaryDuplicateGroup group = snapshot.ExactBinaryDuplicateGroups.Single();
        ExactBinaryDuplicateMember keeper = group.Members.Single(value => value.BookId == new CalibreBookId(2));

        ExactBinaryCleanupPlanGenerationOutcome outcome = CreateGenerator().Execute(
            snapshot, group.Id, keeper);

        outcome.Plan!.Definition.RetainedFormat.RecordId.Should().Be(new CalibreBookId(2));
        outcome.Plan.Definition.RecordIdsToRemove.Should().Equal(new CalibreBookId(1), new CalibreBookId(3));
        outcome.Plan.Definition.RecordIdsToRemove.Should().NotContain(new CalibreBookId(2));
    }

    private static GenerateExactBinaryCleanupPlanUseCase CreateGenerator()
    {
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => ids.Create()).Returns(new CleanupPlanId(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        return new(ids, clock);
    }

    private static LibrarySnapshot Snapshot()
    {
        FormatFileFingerprint duplicate = new(10, new Sha256Digest(new string('a', 64)));
        FormatFileFingerprint unique = new(20, new Sha256Digest(new string('b', 64)));
        CalibreBook[] books =
        [
            new(
                new(1),
                "First catalog record",
                "Alice",
                [new(new(1), "Alice", "Alice")],
                [],
                [Format(1, "EPUB", "first", duplicate), Format(1, "PDF", "first", unique)],
                "Alice/First (1)"),
            new(
                new(2),
                "Unrelated metadata title",
                "Bob",
                [new(new(2), "Bob", "Bob")],
                [],
                [Format(2, "EPUB", "second", duplicate)],
                "Bob/Second (2)"),
        ];
        return new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\synthetic\\library"),
            Now,
            books,
            [],
            ExactBinaryDuplicateDetector.Detect(books));
    }

    private static BookFormat Format(long recordId, string format, string name, FormatFileFingerprint fingerprint)
    {
        string directory = recordId == 1 ? "Alice/First (1)" : "Bob/Second (2)";
        return new(
            format,
            name,
            $"{directory}/{name}.{format.ToLowerInvariant()}",
            FormatFileStatus.Present,
            fingerprint,
            new(fingerprint.SizeInBytes, Now, Now, 0));
    }
}
