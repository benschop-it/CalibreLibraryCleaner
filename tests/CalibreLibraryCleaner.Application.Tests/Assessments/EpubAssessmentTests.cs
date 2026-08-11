using System.Collections.Concurrent;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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

        EpubAssessment assessment = new EpubAssessmentEngine().Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        assessment.Score!.Value.Value.Should().Be(100);
        assessment.Findings.Sum(finding => finding.ScoreAdjustment).Should().Be(100);
        assessment.AnalyzerVersion.Value.Should().Be("epub-inspector/1.0.5");
        assessment.ScoringModelVersion.Value.Should().Be("epub-quality/1.0.3");
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

        EpubAssessment first = engine.Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);
        EpubAssessment second = engine.Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

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

        EpubAssessment assessment = new EpubAssessmentEngine().Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        assessment.Findings.Should().ContainSingle(finding =>
            finding.RuleId == "EPUB.COVER.DIMENSIONS"
            && finding.ScoreAdjustment == -2
            && finding.Severity == CalibreLibraryCleaner.Domain.Findings.FindingSeverity.Warning);
    }

    [Fact]
    public void RecoverablePackageProblemRemainsScoredAsAWarning()
    {
        EpubInspectionResult result = Healthy(new CalibreBookId(1), "Book.epub") with
        {
            RecoverableProblems = [new(EpubInspectionProblemCode.PackageMalformed, "An EPUB manifest item has no content file path.")],
        };

        EpubAssessment assessment = new EpubAssessmentEngine().Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        assessment.Status.Should().Be(AssessmentStatus.Completed);
        assessment.Score.Should().NotBeNull();
        assessment.Findings.Should().ContainSingle(finding =>
            finding.RuleId == "EPUB.PACKAGE"
            && finding.Severity == CalibreLibraryCleaner.Domain.Findings.FindingSeverity.Warning
            && finding.ScoreAdjustment == 0
            && finding.Explanation == "An EPUB manifest item has no content file path.");
    }

    [Fact]
    public void FallbackIssuesUseStableReasonCodesAndBoundedStructuredEvidence()
    {
        EpubInspectionIssue issue = new(
            EpubInspectionIssueCode.MalformedPackage,
            "Package",
            "OEBPS/content.opf",
            observed: 1,
            limit: EpubInspectionLimits.V1.MaximumXmlBytes,
            omittedCount: 2,
            exceptionType: nameof(System.Xml.XmlException));
        EpubInspectionResult result = Healthy(new CalibreBookId(1), "Book.epub") with
        {
            Coverage = EpubAssessmentCoverage.FallbackReadable,
            AvailableFacets = EpubAssessmentFacet.Archive | EpubAssessmentFacet.Content,
            Issues = [issue],
            FallbackCandidateCount = 3,
            FallbackRenderableCount = 1,
            RenderableEvidence = EpubRenderableEvidence.Text,
        };

        result.Issues.Should().ContainSingle().Which.Code.Should().Be(EpubInspectionIssueCode.MalformedPackage);
        result.Issues[0].Item.Should().Be("OEBPS/content.opf");
        result.Issues[0].ExceptionType.Should().Be(nameof(System.Xml.XmlException));
    }

    [Fact]
    public void FallbackReadableWarningsArePenalizedAndScoreIsCappedAtSeventy()
    {
        EpubInspectionResult result = Healthy(new CalibreBookId(1), "Book.epub") with
        {
            Coverage = EpubAssessmentCoverage.FallbackReadable,
            AvailableFacets = EpubAssessmentFacet.All,
            Issues =
            [
                new(EpubInspectionIssueCode.InvalidManifestItemPath, "Package", "OEBPS/broken.xhtml"),
                new(EpubInspectionIssueCode.UnknownReadingOrder, "Fallback"),
            ],
            FallbackCandidateCount = 2,
            FallbackRenderableCount = 1,
            RenderableEvidence = EpubRenderableEvidence.Text,
        };

        EpubAssessment assessment = new EpubAssessmentEngine().Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        assessment.Status.Should().Be(AssessmentStatus.Completed);
        assessment.Score.Should().Be(new QualityScore(70));
        assessment.ScoreCap.Should().Be(70);
        assessment.UncappedScore.Should().Be(new QualityScore(89));
        assessment.Findings.Should().Contain(finding =>
            finding.RuleId == "EPUB.PACKAGE.INVALID_MANIFEST_ITEM"
            && finding.Severity == CalibreLibraryCleaner.Domain.Findings.FindingSeverity.Warning
            && finding.ScoreAdjustment == -3);
        assessment.Findings.Should().Contain(finding =>
            finding.RuleId == "EPUB.FALLBACK.READING_ORDER_UNKNOWN"
            && finding.ScoreAdjustment == -8);
        assessment.Findings.Should().ContainSingle(finding =>
            finding.RuleId == "EPUB.SCORE.FALLBACK_CAP"
            && finding.ScoreAdjustment == 0);
    }

    [Fact]
    public void FallbackWarningCategoryCapsRetainExcessEvidenceAndDoNotRaiseLowScores()
    {
        EpubInspectionIssue[] issues =
        [
            .. Enumerable.Range(1, 5).Select(index => new EpubInspectionIssue(
                EpubInspectionIssueCode.InvalidManifestItemPath,
                "Package",
                $"OEBPS/broken-{index}.xhtml")),
            new(EpubInspectionIssueCode.EncryptedEntry, "Archive", "OEBPS/locked-1.xhtml"),
            new(EpubInspectionIssueCode.EncryptedEntry, "Archive", "OEBPS/locked-2.xhtml"),
            new(EpubInspectionIssueCode.UnknownReadingOrder, "Fallback"),
        ];
        EpubInspectionResult result = Healthy(new CalibreBookId(1), "Book.epub") with
        {
            Coverage = EpubAssessmentCoverage.FallbackReadable,
            AvailableFacets = EpubAssessmentFacet.All,
            Issues = issues,
            FallbackCandidateCount = 1,
            FallbackRenderableCount = 1,
            RenderableEvidence = EpubRenderableEvidence.Text,
        };

        EpubAssessment assessment = new EpubAssessmentEngine().Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        assessment.UncappedScore.Should().Be(new QualityScore(60));
        assessment.Score.Should().Be(new QualityScore(60));
        assessment.ScoreCap.Should().Be(70);
        assessment.Findings.Where(finding => finding.RuleId == "EPUB.PACKAGE.INVALID_MANIFEST_ITEM")
            .Select(finding => finding.ScoreAdjustment)
            .Should().BeEquivalentTo([-3, -3, -3, -3, 0]);
        assessment.Findings.Should().Contain(finding =>
            finding.RuleId == "EPUB.PACKAGE.INVALID_MANIFEST_ITEM"
            && finding.ScoreAdjustment == 0
            && finding.Explanation.Contains("penalty cap", StringComparison.Ordinal));
    }

    [Fact]
    public void OmittedFallbackIssueOccurrencesStillApplyTheirCategoryCap()
    {
        EpubInspectionResult result = Healthy(new CalibreBookId(1), "Book.epub") with
        {
            Coverage = EpubAssessmentCoverage.FallbackReadable,
            AvailableFacets = EpubAssessmentFacet.Archive | EpubAssessmentFacet.Content,
            Issues =
            [
                new(EpubInspectionIssueCode.EncryptedEntry, "Archive", "locked.xhtml", omittedCount: 4),
                new(EpubInspectionIssueCode.UnknownReadingOrder, "Fallback"),
            ],
            FallbackCandidateCount = 1,
            FallbackRenderableCount = 1,
            RenderableEvidence = EpubRenderableEvidence.Text,
        };

        EpubAssessment assessment = new EpubAssessmentEngine().Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        assessment.Findings.Where(finding => finding.RuleId == "EPUB.ENCRYPTION.ENTRY_SKIPPED")
            .Sum(finding => finding.ScoreAdjustment).Should().Be(-20);
    }

    [Fact]
    public void FullCoverageIssuesDoNotApplyFallbackPenaltiesOrDoubleNavigationPenalty()
    {
        EpubInspectionResult result = Healthy(new CalibreBookId(1), "Book.epub") with
        {
            NavigationPresent = false,
            Issues = [new(EpubInspectionIssueCode.MalformedNavigation, "Preflight")],
        };

        EpubAssessment assessment = new EpubAssessmentEngine().Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        assessment.Findings.Should().ContainSingle(finding =>
            finding.RuleId == "EPUB.NAVIGATION" && finding.ScoreAdjustment == -6);
        assessment.Findings.Should().NotContain(finding => finding.RuleId == "EPUB.NAVIGATION.MALFORMED");
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

        EpubAssessment unsafeAssessment = engine.Assess(new CalibreBookId(1), "Unsafe.epub", Fingerprint, unsafeResult);
        EpubAssessment truncatedAssessment = engine.Assess(new CalibreBookId(2), "Truncated.epub", Fingerprint, truncatedResult);

        unsafeAssessment.Status.Should().Be(AssessmentStatus.Unassessed);
        unsafeAssessment.Score.Should().BeNull();
        unsafeAssessment.Findings.Should().Contain(finding => finding.RuleId == "EPUB.ARCHIVE_SAFETY" && finding.Severity == CalibreLibraryCleaner.Domain.Findings.FindingSeverity.Warning);
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

        EpubAssessment assessment = new EpubAssessmentEngine().Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        assessment.Findings.Where(finding => finding.RuleId == "EPUB.SPINE.RESOURCE_EXISTS")
            .Sum(finding => finding.ScoreAdjustment).Should().Be(-20);
        assessment.Findings.Should().Contain(finding =>
            finding.RuleId == "EPUB.SPINE.RESOURCE_EXISTS"
            && finding.Evidence.Values.Contains("omitted:9", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(EpubInspectionProblemCode.CannotOpen)]
    [InlineData(EpubInspectionProblemCode.Unreadable)]
    public void DefinitiveOpenFailuresDisqualifyWithoutNumericScore(EpubInspectionProblemCode code)
    {
        EpubInspectionResult result = EpubInspectionResult.Failed(new CalibreBookId(1), "Book.epub", code, "Safe explanation");

        EpubAssessment assessment = new EpubAssessmentEngine().Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        assessment.Status.Should().Be(AssessmentStatus.Disqualified);
        assessment.Score.Should().BeNull();
        assessment.Findings.Should().Contain(finding =>
            finding.RuleId == "EPUB.OPEN"
            && finding.Severity == CalibreLibraryCleaner.Domain.Findings.FindingSeverity.Disqualifying);
    }

    [Theory]
    [InlineData(EpubInspectionProblemCode.UnsafeArchive, "EPUB.ARCHIVE_SAFETY")]
    [InlineData(EpubInspectionProblemCode.PackageMalformed, "EPUB.PACKAGE")]
    [InlineData(EpubInspectionProblemCode.Encrypted, "EPUB.ENCRYPTION")]
    [InlineData(EpubInspectionProblemCode.ChangedDuringInspection, "EPUB.FILE_CHANGED")]
    [InlineData(EpubInspectionProblemCode.Unsupported, "EPUB.UNSUPPORTED")]
    [InlineData(EpubInspectionProblemCode.LimitExceeded, "EPUB.ARCHIVE_SAFETY")]
    public void IncompleteTechnicalInspectionIsUnassessedWithoutDisqualification(EpubInspectionProblemCode code, string ruleId)
    {
        EpubInspectionResult result = EpubInspectionResult.Failed(new CalibreBookId(1), "Book.epub", code, "Safe explanation");

        EpubAssessment assessment = new EpubAssessmentEngine().Assess(new CalibreBookId(1), "Book.epub", Fingerprint, result);

        assessment.Status.Should().Be(AssessmentStatus.Unassessed);
        assessment.Score.Should().BeNull();
        assessment.Findings.Should().ContainSingle(finding =>
            finding.RuleId == ruleId
            && finding.Severity == CalibreLibraryCleaner.Domain.Findings.FindingSeverity.Warning);
        assessment.Findings.Should().NotContain(finding => finding.Severity == CalibreLibraryCleaner.Domain.Findings.FindingSeverity.Disqualifying);
    }

    [Fact]
    public async Task MissingEpubIsDisqualifiedWithoutCallingInspector()
    {
        IEpubInspector inspector = A.Fake<IEpubInspector>();
        AssessEpubFormatsUseCase useCase = new(inspector, new());
        EpubAssessmentTarget target = new(new CalibreBookId(2), "EPUB", "Missing.epub", null, null, FormatFileStatus.Missing, null, null);

        IReadOnlyList<EpubAssessment> results = await useCase.ExecuteAsync([target], 2, EpubInspectionLimits.V1, null, CancellationToken.None);

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

        IReadOnlyList<EpubAssessment> results = await useCase.ExecuteAsync(targets, 2, EpubInspectionLimits.V1, null, CancellationToken.None);

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

        Task<IReadOnlyList<EpubAssessment>> operation = useCase.ExecuteAsync(targets, 2, EpubInspectionLimits.V1, null, CancellationToken.None);
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

        IReadOnlyList<EpubAssessment> results = await useCase.ExecuteAsync(
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

        Exception exception = (await act.Should().ThrowAsync<Exception>()).Which;
        exception.GetType().Name.Should().Be("EpubTargetAssessmentException");
        exception.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("Synthetic inspector defect");
        updates.Should().Contain(update => update.Stage == "Package" && update.CurrentRelativePath == "Book.epub");
    }

    [Fact]
    public async Task ChapterProgressIncludesCompletedAndTotalUnits()
    {
        IEpubInspector inspector = A.Fake<IEpubInspector>();
        A.CallTo(() => inspector.InspectAsync(
                A<EpubInspectionRequest>._,
                A<IProgress<EpubInspectionProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                EpubInspectionRequest request = call.GetArgument<EpubInspectionRequest>(0)!;
                call.GetArgument<IProgress<EpubInspectionProgress>?>(1)?.Report(new("Content", 3, 10));
                return Task.FromResult(Healthy(request.BookId, request.ExpectedRelativePath));
            });
        List<EpubAssessmentProgress> updates = [];
        AssessEpubFormatsUseCase useCase = new(inspector, new());
        EpubAssessmentTarget target = new(
            new CalibreBookId(1), "EPUB", "Book.epub", "root", "book",
            FormatFileStatus.Present, Fingerprint, Observation);

        await useCase.ExecuteAsync(
            [target], 1, EpubInspectionLimits.V1, new InlineProgress(updates.Add), CancellationToken.None);

        updates.Should().Contain(update =>
            update.Stage == "Content 3 of 10"
            && update.CurrentRelativePath == "Book.epub");
    }

    [Fact]
    public async Task RepeatedChapterCallbacksAreCoalescedInDiagnosticLogging()
    {
        IEpubInspector inspector = A.Fake<IEpubInspector>();
        A.CallTo(() => inspector.InspectAsync(
                A<EpubInspectionRequest>._,
                A<IProgress<EpubInspectionProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                EpubInspectionRequest request = call.GetArgument<EpubInspectionRequest>(0)!;
                IProgress<EpubInspectionProgress>? progress =
                    call.GetArgument<IProgress<EpubInspectionProgress>?>(1);
                for (int chapter = 1; chapter <= 100; chapter++)
                    progress?.Report(new("Content", chapter, 100));
                return Task.FromResult(Healthy(request.BookId, request.ExpectedRelativePath));
            });
        CapturingLogger<AssessEpubFormatsUseCase> logger = new();
        AssessEpubFormatsUseCase useCase = new(inspector, new(), logger);
        EpubAssessmentTarget target = new(
            new(1), "EPUB", "Book.epub", "root", "book",
            FormatFileStatus.Present, Fingerprint, Observation);

        await useCase.ExecuteAsync(
            [target], 1, EpubInspectionLimits.V1, null, CancellationToken.None);

        logger.Messages.Count(value => value.Contains("EPUB assessment stage", StringComparison.Ordinal))
            .Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentWorkersReportStableAggregateStagesWithoutAlternatingPaths()
    {
        IEpubInspector inspector = A.Fake<IEpubInspector>();
        TaskCompletionSource bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource bothReportedContent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        int reportedContent = 0;
        A.CallTo(() => inspector.InspectAsync(
                A<EpubInspectionRequest>._,
                A<IProgress<EpubInspectionProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                if (Interlocked.Increment(ref entered) == 2) bothEntered.SetResult();
                await bothEntered.Task;
                call.GetArgument<IProgress<EpubInspectionProgress>?>(1)?.Report(new("Content", 3, 10));
                if (Interlocked.Increment(ref reportedContent) == 2) bothReportedContent.SetResult();
                await bothReportedContent.Task;
                EpubInspectionRequest request = call.GetArgument<EpubInspectionRequest>(0)!;
                return Healthy(request.BookId, request.ExpectedRelativePath);
            });
        EpubAssessmentTarget[] targets =
        [
            new(new(1), "EPUB", "German-Dutch.epub", "root", "one", FormatFileStatus.Present, Fingerprint, Observation),
            new(new(2), "EPUB", "Dutch-German.epub", "root", "two", FormatFileStatus.Present, Fingerprint, Observation),
        ];
        ConcurrentQueue<EpubAssessmentProgress> updates = new();

        await new AssessEpubFormatsUseCase(inspector, new()).ExecuteAsync(
            targets, 2, EpubInspectionLimits.V1, new InlineProgress(updates.Enqueue), CancellationToken.None);

        updates.Should().Contain(value =>
            value.ActiveFiles == 2
            && value.ActiveStageSummary == "Content: 2"
            && value.CurrentRelativePath == string.Empty);
    }

    [Fact]
    public void IdenticalFingerprintReusesEpubAssessmentAndRebindsAssociation()
    {
        EpubAssessment previousAssessment = new EpubAssessmentEngine().Assess(
            new(1), "Old.epub", Fingerprint, Healthy(new(1), "Old.epub"));
        LibrarySnapshot previous = new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\Library"),
            DateTimeOffset.UnixEpoch,
            [new(new(1), "Book", "Author", [new(new(1), "Author", "Author")], [], [], "Author/Book")],
            [],
            epubAssessments: [previousAssessment]);
        EpubAssessmentTarget target = new(
            new(2), "EPUB", "New.epub", "C:\\Library", "C:\\Library\\New.epub",
            FormatFileStatus.Present, Fingerprint, Observation);

        EpubAssessmentReuseResult result = AssessmentReusePolicy.PartitionEpub(previous, [target]);

        result.FreshTargets.Should().BeEmpty();
        result.Reused.Should().ContainSingle().Which.Should().Match<EpubAssessment>(value =>
            value.CalibreBookId == new CalibreBookId(2)
            && value.ExpectedRelativePath == "New.epub"
            && value.Features == previousAssessment.Features);
        AssessmentReusePolicy.PartitionEpub(previous, [target with
        {
            Fingerprint = new(10, new(new string('b', 64))),
        }]).FreshTargets.Should().ContainSingle();
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
        Func<Task> invalidHtmlCharacters = async () => await useCase.ExecuteAsync(
            [], 1, EpubInspectionLimits.V1 with { MaximumHtmlCharacters = 0 }, null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await excessiveEvidence.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await invalidHtmlCharacters.Should().ThrowAsync<ArgumentOutOfRangeException>();
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
}
