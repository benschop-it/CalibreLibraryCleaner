using System.Collections.Concurrent;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Assessments;

public sealed class EpubAssessmentTests
{
    private static readonly FormatFileFingerprint Fingerprint = new(10, new Sha256Digest(new string('a', 64)));
    private static readonly FormatFileObservation Observation = new(10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0);

    [Fact]
    public void HealthyFindingsRecomputeToOneHundred()
    {
        EpubInspectionResult result = Healthy(new CalibreBookId(1), "Book.epub");

        FormatAssessment assessment = new EpubAssessmentEngine().Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        assessment.Score!.Value.Value.Should().Be(100);
        assessment.Findings.Sum(finding => finding.ScoreAdjustment).Should().Be(100);
        assessment.AnalyzerVersion.Value.Should().Be("epub-inspector/1.0.1");
        assessment.ScoringModelVersion.Value.Should().Be("epub-quality/1.0.0");
    }

    [Fact]
    public void V1WeightsCapsAndFindingOrderAreDeterministic()
    {
        EpubInspectionResult result = Healthy(new CalibreBookId(1), "Book.epub") with
        {
            EmbeddedTitle = null,
            Authors = [],
            Languages = [],
            Dates = ["not-a-date"],
            StrongIdentifiers = ["9780306406158"],
            CoverPresent = false,
            CoverWidth = null,
            CoverHeight = null,
            NavigationPresent = false,
            SpineItemCount = 0,
            MissingSpineResources = Enumerable.Range(1, 6).Select(index => $"spine-{index}").Reverse().ToArray(),
            BrokenInternalReferences = Enumerable.Range(1, 7).Select(index => $"broken-{index}").Reverse().ToArray(),
            EmptyChapters = Enumerable.Range(1, 7).Select(index => $"empty-{index}").Reverse().ToArray(),
            RepeatedReferences = Enumerable.Range(1, 5).Select(index => $"repeat-{index}").Reverse().ToArray(),
            ReadableCharacterCount = 100,
        };
        EpubAssessmentEngine engine = new();

        FormatAssessment first = engine.Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);
        FormatAssessment second = engine.Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        first.Score!.Value.Value.Should().Be(0);
        first.Findings.Sum(finding => finding.ScoreAdjustment).Should().BeLessThan(0);
        first.Findings.Select(FindingIdentity).Should().Equal(second.Findings.Select(FindingIdentity));
        first.Findings.Where(finding => finding.RuleId == "EPUB.SPINE.RESOURCE_EXISTS").Sum(finding => finding.ScoreAdjustment).Should().Be(-20);
        first.Findings.Where(finding => finding.RuleId == "EPUB.RESOURCE.INTERNAL_EXISTS").Sum(finding => finding.ScoreAdjustment).Should().Be(-10);
        first.Findings.Where(finding => finding.RuleId == "EPUB.CHAPTER.EMPTY").Sum(finding => finding.ScoreAdjustment).Should().Be(-10);
        first.Findings.Where(finding => finding.RuleId == "EPUB.STRUCTURE.REPEATED_REFERENCE").Sum(finding => finding.ScoreAdjustment).Should().Be(-12);
        first.Findings.Should().Contain(finding => finding.RuleId == "EPUB.METADATA.STRONG_IDENTIFIER" && finding.ScoreAdjustment == 0);
    }

    [Fact]
    public void MalformedSupportedCoverHeaderCarriesDocumentedPenalty()
    {
        EpubInspectionResult result = Healthy(new CalibreBookId(1), "Book.epub") with
        {
            CoverWidth = null,
            CoverHeight = null,
            CoverHeaderMalformed = true,
        };

        FormatAssessment assessment = new EpubAssessmentEngine().Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        assessment.Findings.Should().ContainSingle(finding =>
            finding.RuleId == "EPUB.COVER.DIMENSIONS"
            && finding.ScoreAdjustment == -2
            && finding.Severity == CalibreLibraryCleaner.Domain.Findings.FindingSeverity.Warning);
    }

    [Fact]
    public void UnsafeArchiveAndOptionalTruncationAreRepresentedByFindings()
    {
        EpubAssessmentEngine engine = new();
        EpubInspectionResult unsafeResult = Healthy(new CalibreBookId(1), "Unsafe.epub") with { ArchiveSafe = false };
        EpubInspectionResult truncatedResult = Healthy(new CalibreBookId(2), "Truncated.epub") with
        {
            AnalysisTruncated = true,
            OptionalTruncations = ["css:styles/book.css"],
        };

        FormatAssessment unsafeAssessment = engine.Assess(new CalibreBookId(1), "Unsafe.epub", Fingerprint, unsafeResult);
        FormatAssessment truncatedAssessment = engine.Assess(new CalibreBookId(2), "Truncated.epub", Fingerprint, truncatedResult);

        unsafeAssessment.Status.Should().Be(AssessmentStatus.Disqualified);
        unsafeAssessment.Score.Should().BeNull();
        unsafeAssessment.Findings.Should().Contain(finding => finding.RuleId == "EPUB.ARCHIVE_SAFETY" && finding.Severity == CalibreLibraryCleaner.Domain.Findings.FindingSeverity.Disqualifying);
        truncatedAssessment.Status.Should().Be(AssessmentStatus.Completed);
        truncatedAssessment.Findings.Should().Contain(finding => finding.RuleId == "EPUB.ANALYSIS.TRUNCATED" && finding.ScoreAdjustment == 0);
    }

    [Fact]
    public void BoundedRepeatedEvidenceStillAppliesTheFullCappedPenalty()
    {
        EpubInspectionResult result = Healthy(new CalibreBookId(1), "Book.epub") with
        {
            MissingSpineResources = ["retained.xhtml"],
            TotalMissingSpineResources = 10,
        };

        FormatAssessment assessment = new EpubAssessmentEngine().Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        assessment.Findings.Where(finding => finding.RuleId == "EPUB.SPINE.RESOURCE_EXISTS")
            .Sum(finding => finding.ScoreAdjustment).Should().Be(-20);
        assessment.Findings.Should().Contain(finding =>
            finding.RuleId == "EPUB.SPINE.RESOURCE_EXISTS"
            && finding.Evidence.Values.Contains("omitted:9", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(EpubInspectionProblemCode.CannotOpen, "EPUB.OPEN")]
    [InlineData(EpubInspectionProblemCode.Unreadable, "EPUB.OPEN")]
    [InlineData(EpubInspectionProblemCode.UnsafeArchive, "EPUB.ARCHIVE_SAFETY")]
    [InlineData(EpubInspectionProblemCode.PackageMalformed, "EPUB.PACKAGE")]
    [InlineData(EpubInspectionProblemCode.Encrypted, "EPUB.ENCRYPTION")]
    [InlineData(EpubInspectionProblemCode.ChangedDuringInspection, "EPUB.FILE_CHANGED")]
    [InlineData(EpubInspectionProblemCode.Unsupported, "EPUB.UNSUPPORTED")]
    public void InspectionProblemsDisqualifyWithoutNumericScore(EpubInspectionProblemCode code, string ruleId)
    {
        EpubInspectionResult result = EpubInspectionResult.Failed(new CalibreBookId(1), "Book.epub", code, "Safe explanation");

        FormatAssessment assessment = new EpubAssessmentEngine().Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        assessment.Status.Should().Be(AssessmentStatus.Disqualified);
        assessment.Score.Should().BeNull();
        assessment.Findings.Should().Contain(finding =>
            finding.RuleId == ruleId
            && finding.Severity == CalibreLibraryCleaner.Domain.Findings.FindingSeverity.Disqualifying);
    }

    [Fact]
    public async Task MissingEpubIsDisqualifiedWithoutCallingInspector()
    {
        IEpubInspector inspector = A.Fake<IEpubInspector>();
        AssessEpubFormatsUseCase useCase = new(inspector, new());
        EpubAssessmentTarget target = new(new CalibreBookId(2), "EPUB", "Missing.epub", null, null, FormatFileStatus.Missing, null, null);

        IReadOnlyList<FormatAssessment> results = await useCase.ExecuteAsync([target], 2, EpubInspectionLimits.V1, null, CancellationToken.None);

        results.Should().ContainSingle().Which.Status.Should().Be(AssessmentStatus.Disqualified);
        A.CallTo(() => inspector.InspectAsync(A<EpubInspectionRequest>._, A<IProgress<EpubInspectionProgress>?>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ResultsRemainCanonicalWhenInspectorsCompleteOutOfOrder()
    {
        IEpubInspector inspector = A.Fake<IEpubInspector>();
        A.CallTo(() => inspector.InspectAsync(A<EpubInspectionRequest>._, A<IProgress<EpubInspectionProgress>?>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                EpubInspectionRequest request = call.GetArgument<EpubInspectionRequest>(0)!;
                await Task.Yield();
                return Healthy(request.BookId, request.ExpectedRelativePath);
            });
        AssessEpubFormatsUseCase useCase = new(inspector, new());
        EpubAssessmentTarget[] targets =
        [
            new(new CalibreBookId(3), "EPUB", "c.epub", "root", "c", FormatFileStatus.Present, Fingerprint, Observation),
            new(new CalibreBookId(1), "EPUB", "a.epub", "root", "a", FormatFileStatus.Present, Fingerprint, Observation),
            new(new CalibreBookId(2), "PDF", "b.pdf", "root", "b", FormatFileStatus.Present, Fingerprint, Observation),
        ];

        IReadOnlyList<FormatAssessment> results = await useCase.ExecuteAsync(targets, 2, EpubInspectionLimits.V1, null, CancellationToken.None);

        results.Select(result => result.CalibreBookId.Value).Should().Equal(1, 3);
    }

    [Fact]
    public async Task InspectionConcurrencyIsBoundedAndCancellationAware()
    {
        IEpubInspector inspector = A.Fake<IEpubInspector>();
        TaskCompletionSource twoStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximum = 0;
        A.CallTo(() => inspector.InspectAsync(A<EpubInspectionRequest>._, A<IProgress<EpubInspectionProgress>?>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                EpubInspectionRequest request = call.GetArgument<EpubInspectionRequest>(0)!;
                int current = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, current);
                if (current == 2) twoStarted.TrySetResult();
                await release.Task.WaitAsync(call.GetArgument<CancellationToken>(2));
                Interlocked.Decrement(ref active);
                return Healthy(request.BookId, request.ExpectedRelativePath);
            });
        EpubAssessmentTarget[] targets = Enumerable.Range(1, 5)
            .Select(id => new EpubAssessmentTarget(new CalibreBookId(id), "EPUB", $"{id}.epub", "root", id.ToString(System.Globalization.CultureInfo.InvariantCulture), FormatFileStatus.Present, Fingerprint, Observation))
            .ToArray();
        AssessEpubFormatsUseCase useCase = new(inspector, new());

        Task<IReadOnlyList<FormatAssessment>> operation = useCase.ExecuteAsync(targets, 2, EpubInspectionLimits.V1, null, CancellationToken.None);
        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        maximum.Should().Be(2);
        release.SetResult();
        (await operation).Should().HaveCount(5);

        await FluentActions.Awaiting(() => useCase.ExecuteAsync(targets, 2, EpubInspectionLimits.V1, null, new CancellationToken(canceled: true)))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ThousandsOfTargetsProduceCompleteCanonicalOutput()
    {
        IEpubInspector inspector = A.Fake<IEpubInspector>();
        A.CallTo(() => inspector.InspectAsync(A<EpubInspectionRequest>._, A<IProgress<EpubInspectionProgress>?>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                EpubInspectionRequest request = call.GetArgument<EpubInspectionRequest>(0)!;
                return Task.FromResult(Healthy(request.BookId, request.ExpectedRelativePath));
            });
        EpubAssessmentTarget[] targets = Enumerable.Range(1, 2_000)
            .Reverse()
            .Select(id => new EpubAssessmentTarget(
                new CalibreBookId(id), "EPUB", $"{id:D4}.epub", "root", $"book-{id}",
                FormatFileStatus.Present, Fingerprint, Observation))
            .ToArray();
        AssessEpubFormatsUseCase useCase = new(inspector, new());
        ConcurrentQueue<EpubAssessmentProgress> progress = new();

        IReadOnlyList<FormatAssessment> results = await useCase.ExecuteAsync(
            targets, 4, EpubInspectionLimits.V1, new InlineProgress(progress.Enqueue), CancellationToken.None);

        results.Should().HaveCount(2_000);
        results.Select(result => result.CalibreBookId.Value).Should().BeInAscendingOrder();
        A.CallTo(() => inspector.InspectAsync(A<EpubInspectionRequest>._, A<IProgress<EpubInspectionProgress>?>._, A<CancellationToken>._))
            .MustHaveHappened(2_000, Times.Exactly);
        progress.Where(update => update.Stage == "Complete").Select(update => update.CompletedFiles)
            .Should().Equal(Enumerable.Range(1, 2_000));
    }

    [Fact]
    public async Task PresentTargetRequiresAConsistentVerifiedIdentity()
    {
        IEpubInspector inspector = A.Fake<IEpubInspector>();
        AssessEpubFormatsUseCase useCase = new(inspector, new());
        EpubAssessmentTarget target = new(
            new CalibreBookId(1), "EPUB", "Book.epub", "root", "book", FormatFileStatus.Present,
            Fingerprint,
            new FormatFileObservation(Fingerprint.SizeInBytes + 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));

        Func<Task> act = async () => await useCase.ExecuteAsync([target], 1, EpubInspectionLimits.V1, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        A.CallTo(() => inspector.InspectAsync(A<EpubInspectionRequest>._, A<IProgress<EpubInspectionProgress>?>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task UnexpectedInspectorFaultPropagatesAndSubstageProgressIsForwarded()
    {
        IEpubInspector inspector = A.Fake<IEpubInspector>();
        A.CallTo(() => inspector.InspectAsync(A<EpubInspectionRequest>._, A<IProgress<EpubInspectionProgress>?>._, A<CancellationToken>._))
            .Invokes(call => call.GetArgument<IProgress<EpubInspectionProgress>?>(1)?.Report(new("Package", 0, null)))
            .ThrowsAsync(new InvalidOperationException("Synthetic inspector defect"));
        List<EpubAssessmentProgress> updates = [];
        AssessEpubFormatsUseCase useCase = new(inspector, new());
        EpubAssessmentTarget target = new(new CalibreBookId(1), "EPUB", "Book.epub", "root", "book", FormatFileStatus.Present, Fingerprint, Observation);

        Func<Task> act = async () => await useCase.ExecuteAsync(
            [target], 1, EpubInspectionLimits.V1, new InlineProgress(updates.Add), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Synthetic inspector defect");
        updates.Should().Contain(update => update.Stage == "Package" && update.CurrentRelativePath == "Book.epub");
    }

    [Fact]
    public async Task InvalidLimitsAreRejectedBeforeInspection()
    {
        IEpubInspector inspector = A.Fake<IEpubInspector>();
        AssessEpubFormatsUseCase useCase = new(inspector, new());

        Func<Task> act = async () => await useCase.ExecuteAsync(
            [], 1, EpubInspectionLimits.V1 with { MaximumArchiveEntries = 0 }, null, CancellationToken.None);
        Func<Task> excessiveEvidence = async () => await useCase.ExecuteAsync(
            [], 1, EpubInspectionLimits.V1 with { MaximumEvidencePerRule = 101 }, null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await excessiveEvidence.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static EpubInspectionResult Healthy(CalibreBookId bookId, string path) => new(
        bookId, path, true, true, true, "3.0", "Title", ["Author"], ["en"], ["2020-01-01"],
        ["9780306406157"], true, 600, 800, true, 5, 1, 1, 5, [], [], [], [], [], 6_000, "None", false, []);

    private static string FindingIdentity(AssessmentFinding finding) => string.Join(
        "|",
        finding.RuleId,
        finding.Severity,
        finding.ScoreAdjustment,
        finding.Explanation,
        string.Join(";", finding.Evidence.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")));

    private sealed class InlineProgress(Action<EpubAssessmentProgress> report) : IProgress<EpubAssessmentProgress>
    {
        public void Report(EpubAssessmentProgress value) => report(value);
    }
}
