using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Libraries;

public sealed class ScanLibraryUseCaseTests
{
    [Theory]
    [InlineData(FormatHashResultStatus.Missing, "FORMAT_FILE_MISSING", FormatFileStatus.Missing)]
    [InlineData(FormatHashResultStatus.Inaccessible, "FORMAT_FILE_INACCESSIBLE", FormatFileStatus.Inaccessible)]
    [InlineData(FormatHashResultStatus.ChangedDuringHashing, "FORMAT_FILE_CHANGED_DURING_HASHING", FormatFileStatus.ChangedDuringHashing)]
    public async Task HashFailureProducesFindingAndSuccessfulSnapshot(
        FormatHashResultStatus resultStatus,
        string findingCode,
        FormatFileStatus fileStatus)
    {
        TestContext context = CreateContext();
        A.CallTo(() => context.Hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                4,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<IReadOnlyList<FormatHashResult>>(
                [FormatHashResult.Failure(0, resultStatus, "TestReason")]));

        LibraryScanOutcome outcome = await context.UseCase.ExecuteAsync("C:/Library", null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Snapshot!.Books[0].Formats[0].FileStatus.Should().Be(fileStatus);
        outcome.Snapshot.Findings.Should().ContainSingle(finding => finding.Code == findingCode);
        outcome.Snapshot.ExactBinaryDuplicateGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task SuccessfulHashesProduceExactFileGroup()
    {
        TestContext context = CreateContext(bookCount: 2);
        FormatFileFingerprint fingerprint = new(4, new Sha256Digest(new string('a', 64)));
        A.CallTo(() => context.Hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                4,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                IReadOnlyList<FormatHashRequest> requests = call.GetArgument<IReadOnlyList<FormatHashRequest>>(0)!;
                return Task.FromResult<IReadOnlyList<FormatHashResult>>(
                    requests.Select(request => Successful(request.Sequence, fingerprint)).ToArray());
            });

        LibraryScanOutcome outcome = await context.UseCase.ExecuteAsync("C:/Library", null, CancellationToken.None);

        outcome.Snapshot!.ExactBinaryDuplicateGroups.Should().ContainSingle();
        outcome.Snapshot.ExactBinaryDuplicateGroups[0].DistinctBookCount.Should().Be(2);
        outcome.Snapshot.ExactMetadataDuplicateGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task SameMetadataWithDifferentHashesProducesOnlyMetadataGroup()
    {
        TestContext context = CreateContext(CreateCatalog(bookCount: 2, sameMetadata: true));
        A.CallTo(() => context.Hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                4,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<IReadOnlyList<FormatHashResult>>(
                call.GetArgument<IReadOnlyList<FormatHashRequest>>(0)!
                    .Select(request => Successful(
                        request.Sequence,
                        new FormatFileFingerprint(
                            request.Sequence + 1,
                            new Sha256Digest(new string((char)('a' + request.Sequence), 64)))))
                    .ToArray()));

        LibraryScanOutcome outcome = await context.UseCase.ExecuteAsync("C:/Library", null, CancellationToken.None);

        outcome.Snapshot!.ExactBinaryDuplicateGroups.Should().BeEmpty();
        outcome.Snapshot.ExactMetadataDuplicateGroups.Should().ContainSingle();
        outcome.Snapshot.ExactMetadataDuplicateGroups[0].Members.Select(member => member.Value).Should().Equal(1, 2);
    }

    [Fact]
    public async Task IncompleteAuthorReferenceExcludesRecordFromMetadataGrouping()
    {
        CalibreCatalogRecord catalog = CreateCatalog(bookCount: 2, sameMetadata: true);
        catalog = new(
            catalog.LibraryUuid,
            catalog.SchemaVersion,
            catalog.Books,
            [new CalibreCatalogIssueRecord(
                "AUTHOR_REFERENCE_MISSING",
                "An author link references a missing or invalid author.",
                "Inspect the book in Calibre.",
                1)]);
        TestContext context = CreateContext(catalog);
        A.CallTo(() => context.Hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                4,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<IReadOnlyList<FormatHashResult>>(
                call.GetArgument<IReadOnlyList<FormatHashRequest>>(0)!
                    .Select(request => Successful(
                        request.Sequence,
                        new FormatFileFingerprint(
                            request.Sequence + 1,
                            new Sha256Digest(new string((char)('a' + request.Sequence), 64)))))
                    .ToArray()));

        LibraryScanOutcome outcome = await context.UseCase.ExecuteAsync("C:/Library", null, CancellationToken.None);

        outcome.Snapshot!.ExactMetadataDuplicateGroups.Should().BeEmpty();
        outcome.Snapshot.Findings.Should().ContainSingle(finding => finding.Code == "AUTHOR_REFERENCE_MISSING");
    }

    [Fact]
    public async Task SameMetadataAndHashesRemainSeparateGroupsInBothCollections()
    {
        TestContext context = CreateContext(CreateCatalog(bookCount: 2, sameMetadata: true));
        FormatFileFingerprint fingerprint = new(4, new Sha256Digest(new string('a', 64)));
        A.CallTo(() => context.Hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                4,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<IReadOnlyList<FormatHashResult>>(
                call.GetArgument<IReadOnlyList<FormatHashRequest>>(0)!
                    .Select(request => Successful(request.Sequence, fingerprint))
                    .ToArray()));

        LibraryScanOutcome outcome = await context.UseCase.ExecuteAsync("C:/Library", null, CancellationToken.None);

        outcome.Snapshot!.ExactBinaryDuplicateGroups.Should().ContainSingle();
        outcome.Snapshot.ExactMetadataDuplicateGroups.Should().ContainSingle();
    }

    [Fact]
    public async Task InvalidPathIsNotSentToHasher()
    {
        TestContext context = CreateContext();
        A.CallTo(() => context.Resolver.ResolveFormat(
                A<ValidatedLibraryLocation>._,
                A<string>._,
                A<string>._,
                A<string>._))
            .Returns(ResolvedFormatPathOutcome.Failure("Path traversal."));
        A.CallTo(() => context.Hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<FormatHashResult>>([]));

        LibraryScanOutcome outcome = await context.UseCase.ExecuteAsync("C:/Library", null, CancellationToken.None);

        outcome.Snapshot!.Findings.Should().ContainSingle(finding => finding.Code == "MANAGED_PATH_INVALID");
        A.CallTo(() => context.Hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>.That.Matches(requests => requests.Count != 0),
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task CancellationDuringValidationPropagates()
    {
        TestContext context = CreateContext();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Func<Task> act = async () => await context.UseCase.ExecuteAsync("C:/Library", null, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        A.CallTo(() => context.Hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task CancellationAtGroupingPhasePropagatesWithoutCompletedProgress()
    {
        TestContext context = CreateContext(bookCount: 2);
        FormatFileFingerprint fingerprint = new(4, new Sha256Digest(new string('a', 64)));
        A.CallTo(() => context.Hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<IReadOnlyList<FormatHashResult>>(
                call.GetArgument<IReadOnlyList<FormatHashRequest>>(0)!
                    .Select(request => Successful(request.Sequence, fingerprint))
                    .ToArray()));
        using CancellationTokenSource cancellation = new();
        List<LibraryScanProgress> updates = [];
        InlineProgress progress = new(update =>
        {
            updates.Add(update);
            if (update.Phase == LibraryScanPhase.GroupingExactDuplicates)
            {
                cancellation.Cancel();
            }
        });

        Func<Task> act = async () => await context.UseCase.ExecuteAsync("C:/Library", progress, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        updates.Should().NotContain(update => update.Phase == LibraryScanPhase.Completed);
    }

    [Fact]
    public async Task CancellationAtMetadataGroupingPropagatesWithoutCompletedProgress()
    {
        TestContext context = CreateContext(CreateCatalog(bookCount: 2, sameMetadata: true));
        FormatFileFingerprint fingerprint = new(4, new Sha256Digest(new string('a', 64)));
        A.CallTo(() => context.Hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<IReadOnlyList<FormatHashResult>>(
                call.GetArgument<IReadOnlyList<FormatHashRequest>>(0)!
                    .Select(request => Successful(request.Sequence, fingerprint))
                    .ToArray()));
        using CancellationTokenSource cancellation = new();
        List<LibraryScanProgress> updates = [];
        InlineProgress progress = new(update =>
        {
            updates.Add(update);
            if (update.Phase == LibraryScanPhase.GroupingExactMetadataDuplicates)
            {
                cancellation.Cancel();
            }
        });

        Func<Task> act = async () => await context.UseCase.ExecuteAsync("C:/Library", progress, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        updates.Should().NotContain(update => update.Phase == LibraryScanPhase.Completed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task InvalidHashResultSequenceBecomesStructuredFailure(int sequence)
    {
        TestContext context = CreateContext();
        FormatFileFingerprint fingerprint = new(4, new Sha256Digest(new string('a', 64)));
        A.CallTo(() => context.Hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<FormatHashResult>>(
                [Successful(sequence, fingerprint)]));

        LibraryScanOutcome outcome = await context.UseCase.ExecuteAsync("C:/Library", null, CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Error!.Code.Should().Be(LibraryErrorCode.HashingFailed);
    }

    [Fact]
    public async Task InvalidHashResultFingerprintCombinationBecomesStructuredFailure()
    {
        TestContext context = CreateContext();
        A.CallTo(() => context.Hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<FormatHashResult>>(
                [new FormatHashResult(0, FormatHashResultStatus.Success, null, null)]));

        LibraryScanOutcome outcome = await context.UseCase.ExecuteAsync("C:/Library", null, CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Error!.Code.Should().Be(LibraryErrorCode.HashingFailed);
    }

    private static TestContext CreateContext(int bookCount = 1) => CreateContext(CreateCatalog(bookCount));

    private static FormatHashResult Successful(int sequence, FormatFileFingerprint fingerprint) => FormatHashResult.Success(
        sequence,
        fingerprint,
        new FormatFileObservation(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));

    private static TestContext CreateContext(CalibreCatalogRecord catalog)
    {
        ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
        ICalibreMetadataReader reader = A.Fake<ICalibreMetadataReader>();
        IFormatFileHasher hasher = A.Fake<IFormatFileHasher>();
        IClock clock = A.Fake<IClock>();
        ValidatedLibraryLocation location = new("C:/Library", "C:/Library/metadata.db");
        A.CallTo(() => resolver.ValidateAsync("C:/Library", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(catalog));
        A.CallTo(() => resolver.ResolveFormat(location, A<string>._, A<string>._, "EPUB"))
            .ReturnsLazily(call =>
            {
                string directory = call.GetArgument<string>(1)!;
                return ResolvedFormatPathOutcome.Success(new(
                    "C:/Library",
                    $"C:/Library/{directory}/Book.epub",
                    $"{directory}/Book.epub"));
            });
        A.CallTo(() => clock.GetUtcNow()).Returns(DateTimeOffset.UnixEpoch);
        return new(
            resolver,
            hasher,
            new ScanLibraryUseCase(resolver, reader, hasher, clock, new()));
    }

    private static CalibreCatalogRecord CreateCatalog(int bookCount, bool sameMetadata = false) => new(
        "87f7ed1f-59a8-45a6-975a-7e06fd84780d",
        27,
        Enumerable.Range(1, bookCount).Select(id => new CalibreBookRecord(
            id,
            sameMetadata ? id == 1 ? "Book : One" : "book:one" : $"Book {id}",
            "Author",
            $"Author/Book ({id})",
            [new CalibreAuthorRecord(id, "Author", "Author")],
            [],
            [new CalibreFormatRecord("EPUB", "Book")])));

    private sealed record TestContext(
        ILibraryPathResolver Resolver,
        IFormatFileHasher Hasher,
        ScanLibraryUseCase UseCase);

    private sealed class InlineProgress(Action<LibraryScanProgress> report) : IProgress<LibraryScanProgress>
    {
        public void Report(LibraryScanProgress value) => report(value);
    }
}
