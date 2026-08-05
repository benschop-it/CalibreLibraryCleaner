using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Wpf.Services;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class ExactBinaryCleanupPlanWorkspaceViewModelTests
{
    [Fact]
    public async Task RemoveDuplicatesHonorsUserKeeperOverride()
    {
        DateTimeOffset now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        FormatFileFingerprint fingerprint = new(4, new(new string('d', 64)));
        CalibreBook[] books = [Book(1, fingerprint, now), Book(2, fingerprint, now)];
        LibrarySnapshot snapshot = new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\library"),
            now, books, [], ExactBinaryDuplicateDetector.Detect(books));
        using LibraryStateSession stateSession = new();
        await stateSession.StartFromScanAsync(snapshot, CancellationToken.None);
        ExactBinaryDuplicateGroup group = snapshot.ExactBinaryDuplicateGroups.Single();
        ExactDuplicateGroupRowViewModel row = new(group, books.ToDictionary(value => value.Id));
        row.RetainedMember = row.Members.Single(value => value.BookId == 2);

        ICalibreToolDiscovery tools = A.Fake<ICalibreToolDiscovery>();
        CalibreToolDescriptor tool = new("C:\\Calibre2\\calibredb.exe",
            new("C:\\Calibre2\\calibredb.exe", "9.11.0", new(new string('f', 64)),
                "calibredb/windows/9.11.0"));
        A.CallTo(() => tools.DiscoverAndProbeAsync(A<string>._, A<CancellationToken>._))
            .Returns(new CalibreToolDiscoveryResult(tool, []));
        ILibraryMutationLease lease = A.Fake<ILibraryMutationLease>();
        ILibraryMutationLeaseHandle handle = A.Fake<ILibraryMutationLeaseHandle>();
        A.CallTo(() => handle.IsHeld).Returns(true);
        A.CallTo(() => lease.TryAcquireAsync(A<LibraryMutationLeaseRequest>._, A<CancellationToken>._))
            .Returns(new LibraryMutationLeaseAcquisition(handle, []));
        ICleanupExecutionIdGenerator ids = A.Fake<ICleanupExecutionIdGenerator>();
        A.CallTo(() => ids.Create()).Returns(new CleanupExecutionId(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(now.AddSeconds(1));
        ICalibreMutationWorkerFactory workers = A.Fake<ICalibreMutationWorkerFactory>();
        ICalibreMutationWorkerSession workerSession = A.Fake<ICalibreMutationWorkerSession>();
        A.CallTo(() => workerSession.ExecuteChunkAsync(A<CalibreMutationChunkRequest>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                CalibreMutationChunkRequest chunk = call.GetArgument<CalibreMutationChunkRequest>(0)!;
                return Task.FromResult(new CalibreMutationChunkResult(chunk.ChunkId, true,
                    chunk.Operations.Select(value => new CalibreMutationOperationResult(
                        value.OperationId, value.Kind, true)).ToArray()));
            });
        A.CallTo(() => workers.TryOpenAsync(A<OpenCalibreMutationWorkerRequest>._,
                A<CancellationToken>._))
            .Returns(new CalibreMutationWorkerOpenResult(workerSession, null));
        ExecuteBulkExactDuplicateCleanupUseCase useCase = new(
            stateSession, tools, workers, lease, ids, clock);
        IExactDuplicateCleanupConfirmationService confirmation =
            A.Fake<IExactDuplicateCleanupConfirmationService>();
        A.CallTo(() => confirmation.ConfirmExternalBackup(A<int>._)).Returns(true);
        ExactBinaryCleanupPlanWorkspaceViewModel viewModel = new(useCase, confirmation);
        viewModel.UpdateContext(snapshot, [row]);

        await viewModel.RemoveDuplicatesCommand.ExecuteAsync(null);

        viewModel.ResultSummary.Should().Contain("Removed 1 duplicate format")
            .And.Contain("deleted 1 empty record");
        stateSession.GetCurrent(snapshot.Identity.LibraryRoot)!.Snapshot.Books
            .Select(value => value.Id).Should().Equal(new CalibreBookId(2));
        A.CallTo(() => workerSession.ExecuteChunkAsync(
            A<CalibreMutationChunkRequest>.That.Matches(value => value.Operations.Any(operation =>
                operation.Kind == CalibreMutationOperationKind.RemoveFormat
                && operation.RecordId == new CalibreBookId(1))),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    private static CalibreBook Book(
        long id,
        FormatFileFingerprint fingerprint,
        DateTimeOffset now)
    {
        string directory = $"Author/Book ({id})";
        return new(new(id), "Book", "Author", [new(new(id), "Author", "Author")], [],
            [new("EPUB", "book", $"{directory}/book.epub", FormatFileStatus.Present,
                fingerprint, new(fingerprint.SizeInBytes, now, now, 0))], directory);
    }

}
