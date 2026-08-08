using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Matching;

public sealed class ResolveCandidateContentSignaturesUseCaseTests
{
    private readonly IEpubContentSignatureInspector _inspector = A.Fake<IEpubContentSignatureInspector>();
    private readonly IEpubContentSignatureCache _cache = A.Fake<IEpubContentSignatureCache>();

    [Fact]
    public async Task DecisivePairsCauseNoCacheOrInspectionWork()
    {
        ResolveCandidateContentSignaturesUseCase useCase = new(_inspector, _cache);

        CandidateContentSignatureBatchResult result = await useCase.ExecuteAsync(
            [Pair(1, 2, needsContent: false)],
            [Target(1, 'a'), Target(2, 'b')],
            2,
            EpubInspectionLimits.V1,
            EpubContentSignatureLimits.V1,
            null,
            CancellationToken.None);

        result.RequestedFingerprintCount.Should().Be(0);
        result.Signatures.Should().BeEmpty();
        A.CallTo(() => _cache.TryReadAsync(A<EpubContentSignatureCacheKey>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _inspector.InspectContentSignatureAsync(
            A<EpubContentSignatureRequest>._,
            A<IProgress<EpubContentSignatureProgress>?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _cache.PruneAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ContradictoryPairsCauseNoCacheOrInspectionWork()
    {
        BookCandidatePair contradictory = new(
            new(new(1), new(2)),
            900,
            [new("MATCH.AUTHOR.EXACT_VARIANT", CandidateEvidenceStrength.Strong)],
            [new("MATCH.LANGUAGE.CONFLICT")],
            needsContentEvidence: false);
        ResolveCandidateContentSignaturesUseCase useCase = new(_inspector, _cache);

        CandidateContentSignatureBatchResult result = await useCase.ExecuteAsync(
            [contradictory],
            [Target(1, 'a'), Target(2, 'b')],
            2,
            EpubInspectionLimits.V1,
            EpubContentSignatureLimits.V1,
            null,
            CancellationToken.None);

        result.RequestedFingerprintCount.Should().Be(0);
        A.CallTo(() => _cache.TryReadAsync(A<EpubContentSignatureCacheKey>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _inspector.InspectContentSignatureAsync(
            A<EpubContentSignatureRequest>._,
            A<IProgress<EpubContentSignatureProgress>?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AmbiguousPairsInspectEachUniqueFingerprintOnce()
    {
        A.CallTo(() => _cache.TryReadAsync(A<EpubContentSignatureCacheKey>._, A<CancellationToken>._))
            .Returns(Task.FromResult<EpubContentSignature?>(null));
        A.CallTo(() => _inspector.InspectContentSignatureAsync(
                A<EpubContentSignatureRequest>._,
                A<IProgress<EpubContentSignatureProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult(EpubContentSignatureResult.Success(
                Signature(call.GetArgument<EpubContentSignatureRequest>(0)!.Source.Fingerprint))));
        ResolveCandidateContentSignaturesUseCase useCase = new(_inspector, _cache);

        CandidateContentSignatureBatchResult result = await useCase.ExecuteAsync(
            [Pair(1, 2, true), Pair(1, 3, true)],
            [Target(1, 'a'), Target(2, 'b'), Target(3, 'b'), Target(99, 'c')],
            2,
            EpubInspectionLimits.V1,
            EpubContentSignatureLimits.V1,
            null,
            CancellationToken.None);

        result.RequestedFingerprintCount.Should().Be(2);
        result.Inspections.Should().Be(2);
        result.Signatures.Keys.Should().BeEquivalentTo([new CalibreBookId(1), new(2), new(3)]);
        result.Signatures.Should().NotContainKey(new(99));
        A.CallTo(() => _inspector.InspectContentSignatureAsync(
            A<EpubContentSignatureRequest>._,
            A<IProgress<EpubContentSignatureProgress>?>._,
            A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        A.CallTo(() => _cache.WriteAsync(
            A<EpubContentSignatureCacheKey>._,
            A<EpubContentSignature>._,
            A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        A.CallTo(() => _cache.PruneAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CacheHitsAvoidInspectionForComparablePairs()
    {
        A.CallTo(() => _cache.TryReadAsync(A<EpubContentSignatureCacheKey>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<EpubContentSignature?>(
                Signature(call.GetArgument<EpubContentSignatureCacheKey>(0)!.Fingerprint)));
        ResolveCandidateContentSignaturesUseCase useCase = new(_inspector, _cache);

        CandidateContentSignatureBatchResult result = await useCase.ExecuteAsync(
            [Pair(1, 2, true)],
            [Target(1, 'a'), Target(2, 'b')],
            2,
            EpubInspectionLimits.V1,
            EpubContentSignatureLimits.V1,
            null,
            CancellationToken.None);

        result.CacheHits.Should().Be(2);
        result.Inspections.Should().Be(0);
        result.Signatures.Keys.Should().BeEquivalentTo([new CalibreBookId(1), new(2)]);
        result.Unavailable.Should().BeEmpty();
        A.CallTo(() => _inspector.InspectContentSignatureAsync(
            A<EpubContentSignatureRequest>._,
            A<IProgress<EpubContentSignatureProgress>?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task MissingCounterpartSkipsAvailableEpubAndRemainsUnavailable()
    {
        ResolveCandidateContentSignaturesUseCase useCase = new(_inspector, _cache);

        CandidateContentSignatureBatchResult result = await useCase.ExecuteAsync(
            [Pair(1, 2, true)],
            [Target(1, 'a')],
            2,
            EpubInspectionLimits.V1,
            EpubContentSignatureLimits.V1,
            null,
            CancellationToken.None);

        result.RequestedFingerprintCount.Should().Be(0);
        result.Signatures.Should().BeEmpty();
        result.Unavailable.Should().Contain(new KeyValuePair<CalibreBookId, EpubContentSignatureProblemCode>(
            new(2), EpubContentSignatureProblemCode.CannotOpen));
        A.CallTo(() => _cache.TryReadAsync(A<EpubContentSignatureCacheKey>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _inspector.InspectContentSignatureAsync(
            A<EpubContentSignatureRequest>._,
            A<IProgress<EpubContentSignatureProgress>?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task CancellationIsObservedBeforeDemandPlanning()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        ResolveCandidateContentSignaturesUseCase useCase = new(_inspector, _cache);

        Func<Task> act = () => useCase.ExecuteAsync(
            [Pair(1, 2, true)],
            [Target(1, 'a'), Target(2, 'b')],
            2,
            EpubInspectionLimits.V1,
            EpubContentSignatureLimits.V1,
            null,
            source.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DiagnosticsExposeDemandAndTimingWithoutBookMetadata()
    {
        CapturingLogger<ResolveCandidateContentSignaturesUseCase> logger = new();
        A.CallTo(() => _cache.TryReadAsync(A<EpubContentSignatureCacheKey>._, A<CancellationToken>._))
            .Returns(Task.FromResult<EpubContentSignature?>(null));
        A.CallTo(() => _inspector.InspectContentSignatureAsync(
                A<EpubContentSignatureRequest>._,
                A<IProgress<EpubContentSignatureProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult(EpubContentSignatureResult.Success(
                Signature(call.GetArgument<EpubContentSignatureRequest>(0)!.Source.Fingerprint))));
        ResolveCandidateContentSignaturesUseCase useCase = new(_inspector, _cache, logger);

        await useCase.ExecuteAsync(
            [Pair(1, 2, true)],
            [Target(1, 'a'), Target(2, 'b')],
            2,
            EpubInspectionLimits.V1,
            EpubContentSignatureLimits.V1,
            null,
            CancellationToken.None);

        logger.Messages.Should().Contain(value =>
            value.Contains("CandidatePairs=1", StringComparison.Ordinal)
            && value.Contains("UniqueFingerprints=2", StringComparison.Ordinal));
        logger.Messages.Should().Contain(value =>
            value.Contains("AggregateInspectionMilliseconds", StringComparison.Ordinal)
            && value.Contains("PruneMilliseconds", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(value =>
            value.Contains("Author/", StringComparison.Ordinal)
            || value.Contains("Book1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongInspectionReportsFingerprintStageAndChapterProgress()
    {
        A.CallTo(() => _cache.TryReadAsync(A<EpubContentSignatureCacheKey>._, A<CancellationToken>._))
            .Returns(Task.FromResult<EpubContentSignature?>(null));
        A.CallTo(() => _inspector.InspectContentSignatureAsync(
                A<EpubContentSignatureRequest>._,
                A<IProgress<EpubContentSignatureProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                call.GetArgument<IProgress<EpubContentSignatureProgress>?>(1)?
                    .Report(new("Counting", 3, 10));
                return Task.FromResult(EpubContentSignatureResult.Success(
                    Signature(call.GetArgument<EpubContentSignatureRequest>(0)!.Source.Fingerprint)));
            });
        List<CandidateContentSignatureProgress> progress = [];
        ResolveCandidateContentSignaturesUseCase useCase = new(_inspector, _cache);

        await useCase.ExecuteAsync(
            [Pair(1, 2, true)],
            [Target(1, 'a'), Target(2, 'b')],
            2,
            EpubInspectionLimits.V1,
            EpubContentSignatureLimits.V1,
            new InlineProgress(progress.Add),
            CancellationToken.None);

        progress.Should().Contain(value =>
            value.Detail.Contains("Fingerprint", StringComparison.Ordinal)
            && value.Detail.Contains("Counting 3 of 10", StringComparison.Ordinal));
    }

    private static BookCandidatePair Pair(long first, long second, bool needsContent) => new(
        new(new(first), new(second)),
        500,
        [new("MATCH.TEST", CandidateEvidenceStrength.Supporting)],
        [],
        needsContent);

    private static EpubAssessmentTarget Target(long id, char digest) => new(
        new(id),
        "EPUB",
        $"Author/Book{id}.epub",
        "C:\\Library",
        $"C:\\Library\\Book{id}.epub",
        FormatFileStatus.Present,
        new(1_024, new(new string(digest, 64))),
        new(1_024, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));

    private static EpubContentSignature Signature(FormatFileFingerprint fingerprint) => new(
        fingerprint,
        1_000,
        1,
        1,
        Enumerable.Range(0, 12).Select(index => new ContentLandmarkSignature(
            index,
            index * 70,
            64,
            32,
            new(new string('d', 64)),
            new(new string('e', 64)))));

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class InlineProgress(Action<CandidateContentSignatureProgress> report) :
        IProgress<CandidateContentSignatureProgress>
    {
        public void Report(CandidateContentSignatureProgress value) => report(value);
    }
}
