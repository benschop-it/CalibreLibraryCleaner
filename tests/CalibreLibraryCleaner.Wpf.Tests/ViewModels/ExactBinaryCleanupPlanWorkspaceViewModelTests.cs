using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Wpf.Services;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class ExactBinaryCleanupPlanWorkspaceViewModelTests
{
    [Fact]
    public async Task GeneratedRetainedCopyCanBePlannedValidatedAndApprovedWithoutMetadataMatch()
    {
        DateTimeOffset now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        LibrarySnapshot snapshot = Snapshot(now);
        ExactBinaryDuplicateGroup group = snapshot.ExactBinaryDuplicateGroups.Single();
        Dictionary<CalibreBookId, CalibreBook> books = snapshot.Books.ToDictionary(value => value.Id);
        ExactDuplicateGroupRowViewModel groupRow = new(group, books);
        ExactDuplicateMemberRowViewModel retained = groupRow.RetainedMember!;
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        IExactBinaryCleanupPlanConfirmationService confirmation = A.Fake<IExactBinaryCleanupPlanConfirmationService>();
        LibraryStateSession stateSession = new();
        await stateSession.StartFromScanAsync(snapshot, CancellationToken.None);
        ICalibreToolDiscovery tools = A.Fake<ICalibreToolDiscovery>();
        IExecutionBackupStore workspaceStore = A.Fake<IExecutionBackupStore>();
        ICalibreCommandGateway commands = A.Fake<ICalibreCommandGateway>();
        ILibraryMutationLease lease = A.Fake<ILibraryMutationLease>();
        IExactBinaryRecordBackupStore recordBackup = A.Fake<IExactBinaryRecordBackupStore>();
        ICleanupExecutionIdGenerator executionIds = A.Fake<ICleanupExecutionIdGenerator>();
        IExactBinaryRecordDeletionConfirmation deletionConfirmation = A.Fake<IExactBinaryRecordDeletionConfirmation>();
        IExecutionBackupFolderPicker backupPicker = A.Fake<IExecutionBackupFolderPicker>();
        A.CallTo(() => ids.Create()).Returns(new CleanupPlanId(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        A.CallTo(() => clock.GetUtcNow()).Returns(now);
        A.CallTo(() => confirmation.ConfirmApproval(A<ExactBinaryCleanupPlan>._)).Returns(true);
        ExactBinaryCleanupPlanWorkspaceViewModel viewModel = new(
            new(ids, clock), new(clock), new(clock),
            new(stateSession, tools, workspaceStore),
            new(stateSession, tools, commands, lease, workspaceStore, recordBackup, executionIds,
                deletionConfirmation, clock),
            backupPicker,
            confirmation);

        viewModel.UpdateContext(snapshot, groupRow, retained);
        viewModel.GenerateCommand.Execute(null);
        viewModel.ValidateCommand.Execute(null);
        viewModel.ApproveCommand.Execute(null);

        viewModel.Plan!.State.Should().Be(CleanupPlanState.Approved);
        viewModel.Plan.Definition.RetainedFormat.RecordId.Should().Be(retained.Member.BookId);
        viewModel.Plan.Definition.FormatRemovals.Should().ContainSingle();
        viewModel.Plan.Definition.RecordIdsToRemove.Should().Equal(new CalibreBookId(2));
        viewModel.PlanSummary.Should().Contain("remove 1 duplicate format");
    }

    private static LibrarySnapshot Snapshot(DateTimeOffset now)
    {
        FormatFileFingerprint fingerprint = new(4, new Sha256Digest(new string('d', 64)));
        CalibreBook[] books =
        [
            Book(1, "First title", "Alice", fingerprint, now),
            Book(2, "Different title", "Bob", fingerprint, now),
        ];
        return new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\synthetic\\library"),
            now,
            books,
            [],
            ExactBinaryDuplicateDetector.Detect(books));
    }

    private static CalibreBook Book(
        long id,
        string title,
        string author,
        FormatFileFingerprint fingerprint,
        DateTimeOffset now)
    {
        string directory = $"{author}/{title} ({id})";
        BookFormat format = new("EPUB", "book", $"{directory}/book.epub", FormatFileStatus.Present,
            fingerprint, new(fingerprint.SizeInBytes, now, now, 0));
        return new(new(id), title, author, [new(new(id), author, author)], [], [format], directory);
    }
}
