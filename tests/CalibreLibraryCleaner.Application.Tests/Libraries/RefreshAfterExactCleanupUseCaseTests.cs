using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Libraries;

public sealed class RefreshAfterExactCleanupUseCaseTests
{
    private const string Root = "C:\\library";
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly LibraryStateGenerationId ExactGeneration = new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    private static readonly FormatFileFingerprint Epub = Fingerprint('a', 10);
    private static readonly FormatFileFingerprint Pdf = Fingerprint('b', 20);

    [Fact]
    public async Task UnchangedAssociationsReuseFingerprintAndObservationWithoutHashing()
    {
        LibraryState pre = PreState([Book(1, Format(1, "EPUB", Epub))]);
        LibraryState post = CompleteExact(pre, []);
        TestContext context = Context(pre, post, [], Catalog(BookRecord(1, ("EPUB", "book"))));

        PostExactRefreshResult result = await context.UseCase.ExecuteAsync(
            Root, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Metrics.ReusedFingerprintCount.Should().Be(1);
        result.Metrics.TargetedHashCount.Should().Be(0);
        result.State!.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.CandidatePreparationReady);
        result.State.WorkflowCheckpoint.Source.Should().Be(new LibraryWorkflowSource(
            post.GenerationId, post.Revision));
        BookFormat format = result.State.Snapshot.Books.Single().Formats.Single();
        format.FileStatus.Should().Be(FormatFileStatus.Present);
        format.Fingerprint.Should().Be(Epub);
        format.Observation.Should().Be(pre.Snapshot.Books.Single().Formats.Single().Observation);
        A.CallTo(() => context.Hasher.HashAsync(
            A<IReadOnlyList<FormatHashRequest>>._,
            A<int>._,
            A<IProgress<FormatHashProgress>?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task TransferTargetIsHashedAndReboundAsPhysicalPresent()
    {
        LibraryState pre = PreState([
            Book(1, Format(1, "EPUB", Epub)),
            Book(2, Format(2, "PDF", Pdf)),
        ]);
        LibraryStateDelta[] deltas =
        [
            new AddOrReplaceFormatLibraryStateDelta(ExactGeneration, new(0), "add", Now.AddSeconds(1),
                new(1), "PDF", Pdf, null),
            new RemoveFormatLibraryStateDelta(ExactGeneration, new(1), "remove", Now.AddSeconds(1),
                new(2), "PDF", Pdf),
            new RemoveRecordLibraryStateDelta(ExactGeneration, new(2), "remove-record", Now.AddSeconds(1), new(2)),
        ];
        LibraryState post = CompleteExact(pre, deltas);
        TestContext context = Context(
            pre,
            post,
            deltas,
            Catalog(BookRecord(1, ("EPUB", "book"), ("PDF", "book"))));
        A.CallTo(() => context.Hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                FormatHashRequest request = call.GetArgument<IReadOnlyList<FormatHashRequest>>(0)!.Single();
                return Task.FromResult<IReadOnlyList<FormatHashResult>>([
                    FormatHashResult.Success(request.Sequence, Pdf, Observation(Pdf)),
                ]);
            });

        PostExactRefreshResult result = await context.UseCase.ExecuteAsync(
            Root, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Metrics.TargetedHashCount.Should().Be(1);
        result.Metrics.ReboundTransferCount.Should().Be(1);
        result.State!.Snapshot.Books.Should().ContainSingle();
        result.State.Snapshot.Books.Single().Formats.Should().Contain(value =>
            value.Format == "PDF"
            && value.FileStatus == FormatFileStatus.Present
            && value.Fingerprint == Pdf
            && !string.IsNullOrWhiteSpace(value.ExpectedRelativePath));
        A.CallTo(() => context.Hasher.HashAsync(
            A<IReadOnlyList<FormatHashRequest>>.That.Matches(value =>
                value.Count == 1 && value[0].Format == "PDF"),
            A<int>._,
            A<IProgress<FormatHashProgress>?>._,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task UnexplainedCatalogChangeFailsBeforePathResolutionOrPublication()
    {
        LibraryState pre = PreState([Book(1, Format(1, "EPUB", Epub))]);
        LibraryState post = CompleteExact(pre, []);
        CalibreBookRecord changed = BookRecord(1, ("EPUB", "book")) with { Title = "Changed" };
        TestContext context = Context(pre, post, [], Catalog(changed));

        PostExactRefreshResult result = await context.UseCase.ExecuteAsync(
            Root, null, CancellationToken.None);

        result.ErrorCode.Should().Be("POST_EXACT.UNEXPLAINED_CHANGE");
        result.Metrics.UnexplainedDifferenceCount.Should().BeGreaterThan(0);
        A.CallTo(() => context.Resolver.ResolveFormat(
            A<ValidatedLibraryLocation>._,
            A<string>.That.IsNotNull(),
            A<string>.That.IsNotNull(),
            A<string>.That.IsNotNull())).MustNotHaveHappened();
        A.CallTo(() => context.State.StartFromPostExactRefreshAsync(
            A<LibrarySnapshot>._,
            A<LibraryWorkflowSource>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task TransferFingerprintMismatchFailsWithoutPublishingPartialState()
    {
        LibraryState pre = PreState([
            Book(1, Format(1, "EPUB", Epub)),
            Book(2, Format(2, "PDF", Pdf)),
        ]);
        LibraryStateDelta[] deltas =
        [
            new AddOrReplaceFormatLibraryStateDelta(ExactGeneration, new(0), "add", Now.AddSeconds(1),
                new(1), "PDF", Pdf, null),
            new RemoveFormatLibraryStateDelta(ExactGeneration, new(1), "remove", Now.AddSeconds(1),
                new(2), "PDF", Pdf),
            new RemoveRecordLibraryStateDelta(ExactGeneration, new(2), "remove-record", Now.AddSeconds(1), new(2)),
        ];
        LibraryState post = CompleteExact(pre, deltas);
        TestContext context = Context(
            pre, post, deltas, Catalog(BookRecord(1, ("EPUB", "book"), ("PDF", "book"))));
        FormatFileFingerprint changed = Fingerprint('c', 30);
        A.CallTo(() => context.Hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                FormatHashRequest request = call.GetArgument<IReadOnlyList<FormatHashRequest>>(0)!.Single();
                return Task.FromResult<IReadOnlyList<FormatHashResult>>([
                    FormatHashResult.Success(request.Sequence, changed, Observation(changed)),
                ]);
            });

        PostExactRefreshResult result = await context.UseCase.ExecuteAsync(
            Root, null, CancellationToken.None);

        result.ErrorCode.Should().Be("POST_EXACT.TRANSFER_FINGERPRINT_MISMATCH");
        A.CallTo(() => context.State.StartFromPostExactRefreshAsync(
            A<LibrarySnapshot>._,
            A<LibraryWorkflowSource>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        context.State.GetCurrent(Root).Should().BeSameAs(post);
    }

    [Fact]
    public async Task CancellationDuringFactPreparationLeavesPostExactStateAuthoritative()
    {
        LibraryState pre = PreState([Book(1, Format(1, "EPUB", Epub))]);
        LibraryState post = CompleteExact(pre, []);
        TestContext context = Context(pre, post, [], Catalog(BookRecord(1, ("EPUB", "book"))));
        A.CallTo(() => context.FactsPreparer.PrepareAsync(
                Root,
                A<IReadOnlyList<CalibreLibraryCleaner.Application.Assessments.EpubAssessmentTarget>>._,
                A<IReadOnlyList<CalibreLibraryCleaner.Application.Assessments.Pdf.PdfAssessmentTarget>>._,
                A<CancellationToken>._))
            .ThrowsAsync(new OperationCanceledException());

        Func<Task> act = () => context.UseCase.ExecuteAsync(Root, null, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        A.CallTo(() => context.State.StartFromPostExactRefreshAsync(
            A<LibrarySnapshot>._,
            A<LibraryWorkflowSource>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        context.State.GetCurrent(Root).Should().BeSameAs(post);
    }

    [Theory]
    [InlineData(FormatFileProbeStatus.Missing)]
    [InlineData(FormatFileProbeStatus.Inaccessible)]
    [InlineData(FormatFileProbeStatus.UnsafePath)]
    public async Task InvalidUnchangedPhysicalFileFailsWithoutPublication(FormatFileProbeStatus status)
    {
        LibraryState pre = PreState([Book(1, Format(1, "EPUB", Epub))]);
        LibraryState post = CompleteExact(pre, []);
        TestContext context = Context(pre, post, [], Catalog(BookRecord(1, ("EPUB", "book"))));
        A.CallTo(() => context.Probe.ProbeAsync(A<ResolvedFormatPath>._, A<CancellationToken>._))
            .Returns(FormatFileProbeResult.Failure(status, "Synthetic"));

        PostExactRefreshResult result = await context.UseCase.ExecuteAsync(
            Root, null, CancellationToken.None);

        result.ErrorCode.Should().Be("POST_EXACT.UNCHANGED_FILE_INVALID");
        A.CallTo(() => context.State.StartFromPostExactRefreshAsync(
            A<LibrarySnapshot>._,
            A<LibraryWorkflowSource>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        context.State.GetCurrent(Root).Should().BeSameAs(post);
    }

    [Fact]
    public async Task ChangedUnchangedFileObservationFailsWithoutHashingOrPublication()
    {
        LibraryState pre = PreState([Book(1, Format(1, "EPUB", Epub))]);
        LibraryState post = CompleteExact(pre, []);
        TestContext context = Context(pre, post, [], Catalog(BookRecord(1, ("EPUB", "book"))));
        FormatFileObservation changed = new(Epub.SizeInBytes, Now, Now.AddMinutes(1), 0);
        A.CallTo(() => context.Probe.ProbeAsync(A<ResolvedFormatPath>._, A<CancellationToken>._))
            .Returns(FormatFileProbeResult.Success(changed));

        PostExactRefreshResult result = await context.UseCase.ExecuteAsync(
            Root, null, CancellationToken.None);

        result.ErrorCode.Should().Be("POST_EXACT.UNCHANGED_FILE_INVALID");
        A.CallTo(() => context.Hasher.HashAsync(
            A<IReadOnlyList<FormatHashRequest>>._,
            A<int>._,
            A<IProgress<FormatHashProgress>?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => context.State.StartFromPostExactRefreshAsync(
            A<LibrarySnapshot>._,
            A<LibraryWorkflowSource>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    private static TestContext Context(
        LibraryState pre,
        LibraryState post,
        LibraryStateDelta[] deltas,
        CalibreCatalogRecord catalog)
    {
        ILibraryStateSession state = A.Fake<ILibraryStateSession>();
        ILibraryStateStore store = A.Fake<ILibraryStateStore>();
        ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
        ICalibreMetadataReader reader = A.Fake<ICalibreMetadataReader>();
        IFormatFileHasher hasher = A.Fake<IFormatFileHasher>();
        IFormatFileProbe probe = A.Fake<IFormatFileProbe>();
        IResidualAnalysisFactsPreparer factsPreparer = A.Fake<IResidualAnalysisFactsPreparer>();
        IClock clock = A.Fake<IClock>();
        ValidatedLibraryLocation location = new(Root, $"{Root}\\metadata.db");
        A.CallTo(() => state.GetCurrent(Root)).Returns(post);
        A.CallTo(() => store.ReadPostExactRefreshBasisAsync(Root, A<CancellationToken>._))
            .Returns(new PostExactRefreshBasis(pre, deltas));
        A.CallTo(() => resolver.ValidateAsync(Root, A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(
                location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(catalog));
        A.CallTo(() => resolver.ResolveFormat(
                location,
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull()))
            .ReturnsLazily(call =>
            {
                string directory = call.GetArgument<string>(1)!;
                string storedName = call.GetArgument<string>(2)!;
                string format = call.GetArgument<string>(3)!;
                string relative = $"{directory}/{storedName}.{format.ToLowerInvariant()}";
                return ResolvedFormatPathOutcome.Success(new(
                    Root,
                    $"{Root}\\{relative.Replace('/', '\\')}",
                    relative));
            });
        A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddMinutes(1));
        A.CallTo(() => probe.ProbeAsync(A<ResolvedFormatPath>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                ResolvedFormatPath path = call.GetArgument<ResolvedFormatPath>(0)!;
                FormatFileObservation observation = path.RelativePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                    ? Observation(Pdf)
                    : Observation(Epub);
                return Task.FromResult(FormatFileProbeResult.Success(observation));
            });
        A.CallTo(() => factsPreparer.PrepareAsync(
            Root,
            A<IReadOnlyList<CalibreLibraryCleaner.Application.Assessments.EpubAssessmentTarget>>._,
            A<IReadOnlyList<CalibreLibraryCleaner.Application.Assessments.Pdf.PdfAssessmentTarget>>._,
            A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult(new ResidualAnalysisFacts(
            call.GetArgument<IReadOnlyList<CalibreLibraryCleaner.Application.Assessments.EpubAssessmentTarget>>(1)!,
            call.GetArgument<IReadOnlyList<CalibreLibraryCleaner.Application.Assessments.Pdf.PdfAssessmentTarget>>(2)!,
            [],
            [],
            0,
            0,
            0,
            0)));
        A.CallTo(() => state.StartFromPostExactRefreshAsync(
                A<LibrarySnapshot>._,
                A<LibraryWorkflowSource>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                LibrarySnapshot snapshot = call.GetArgument<LibrarySnapshot>(0)!;
                LibraryWorkflowSource source = call.GetArgument<LibraryWorkflowSource>(1)!;
                LibraryStateGenerationId generation = new(
                    Guid.Parse("11111111-2222-3333-4444-555555555555"));
                LibraryState candidate = new(
                    generation,
                    new(0),
                    LibraryStateStatus.Authoritative,
                    snapshot,
                    snapshot.ScannedAt,
                    workflowCheckpoint: new(
                        LibraryWorkflowPhase.CandidatePreparationReady,
                        generation,
                        new(0),
                        LibraryWorkflowPolicyVersions.Current,
                        snapshot.ScannedAt,
                        source));
                return Task.FromResult(LibraryStateSessionOutcome.Success(candidate));
            });
        return new(
            state,
            resolver,
            hasher,
            probe,
            factsPreparer,
            new(state, store, resolver, reader, hasher, probe, factsPreparer, clock, new()));
    }

    private static LibraryState PreState(IEnumerable<CalibreBook> books)
    {
        LibrarySnapshot snapshot = new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, Root),
            Now,
            books,
            []);
        return LibraryState.FromScan(snapshot, ExactGeneration)
            .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, Now);
    }

    private static LibraryState CompleteExact(LibraryState pre, LibraryStateDelta[] deltas)
    {
        LibraryState projected = deltas.Length == 0 ? pre : LibraryStateDeltaPolicy.ApplyBatch(pre, deltas);
        return projected.AdvanceWorkflow(
            LibraryWorkflowPhase.CandidatePreparationReady,
            projected.ProjectedAtUtc.AddSeconds(1));
    }

    private static CalibreBook Book(long id, params BookFormat[] formats) => new(
        new(id),
        $"Book {id}",
        "Author",
        [new(new(id), "Author", "Author")],
        [new("isbn", $"978000000000{id}")],
        formats,
        $"Author/Book {id} ({id})",
        new("Publisher", Now.AddYears(-1), "Series", 1, ["eng"], true));

    private static BookFormat Format(long id, string format, FormatFileFingerprint fingerprint) => new(
        format,
        "book",
        $"Author/Book {id} ({id})/book.{format.ToLowerInvariant()}",
        FormatFileStatus.Present,
        fingerprint,
        Observation(fingerprint));

    private static CalibreBookRecord BookRecord(long id, params (string Format, string StoredName)[] formats) => new(
        id,
        $"Book {id}",
        "Author",
        $"Author/Book {id} ({id})",
        [new(id, "Author", "Author")],
        [new("isbn", $"978000000000{id}")],
        formats.Select(value => new CalibreFormatRecord(value.Format, value.StoredName)).ToArray(),
        new("Publisher", Now.AddYears(-1), "Series", 1, ["eng"], true));

    private static CalibreCatalogRecord Catalog(params CalibreBookRecord[] books) => new(
        "87f7ed1f-59a8-45a6-975a-7e06fd84780d",
        27,
        books);

    private static FormatFileFingerprint Fingerprint(char value, long size) => new(
        size,
        new(new string(value, 64)));

    private static FormatFileObservation Observation(FormatFileFingerprint fingerprint) => new(
        fingerprint.SizeInBytes,
        Now,
        Now,
        0);

    private sealed record TestContext(
        ILibraryStateSession State,
        ILibraryPathResolver Resolver,
        IFormatFileHasher Hasher,
        IFormatFileProbe Probe,
        IResidualAnalysisFactsPreparer FactsPreparer,
        RefreshAfterExactCleanupUseCase UseCase);
}
