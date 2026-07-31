using System.Collections.Concurrent;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Assessments;

public sealed class PdfAssessmentPolicyTests
{
    private static readonly FormatFileFingerprint Fingerprint = new(100, new Sha256Digest(new string('a', 64)));
    private static readonly FormatFileObservation Observation = new(100, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(199)]
    [InlineData(200)]
    public void SamplingInspectsAllPagesWithinLimit(int pageCount)
    {
        IReadOnlyList<int> pages = new PdfPageSamplingPolicy().Select(pageCount);

        pages.Should().Equal(Enumerable.Range(1, pageCount));
    }

    [Theory]
    [InlineData(201)]
    [InlineData(1_000)]
    [InlineData(100_000)]
    public void LargeSamplingIsStableAndIncludesEdgesAndOutlinePages(int pageCount)
    {
        PdfPageSamplingPolicy policy = new();

        int firstOutline = Math.Min(50, pageCount);
        int lastOutline = Math.Min(700, pageCount);
        IReadOnlyList<int> first = policy.Select(pageCount, [0, firstOutline, firstOutline, pageCount + 1, lastOutline]);
        IReadOnlyList<int> second = policy.Select(pageCount, [0, firstOutline, firstOutline, pageCount + 1, lastOutline]);

        first.Should().Equal(second);
        first.Should().HaveCount(200).And.BeInAscendingOrder();
        first.Should().Contain([1, 2, 3, 4, 5, pageCount - 2, pageCount - 1, pageCount, firstOutline]);
        if (firstOutline < pageCount)
        {
            first.Should().Contain(firstOutline + 1);
        }

        first.Should().Contain(lastOutline);
        if (lastOutline < pageCount)
        {
            first.Should().Contain(lastOutline + 1);
        }

        first.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void LargeSamplingDistributesPagesAcrossTheWholeDocumentAfterMandatoryPages()
    {
        IReadOnlyList<int> pages = new PdfPageSamplingPolicy().Select(100_000, [90_000, 10_000]);

        pages.Should().HaveCount(200).And.BeInAscendingOrder();
        pages.Count(page => page <= 25_000).Should().BeGreaterThan(35);
        pages.Count(page => page is > 25_000 and <= 50_000).Should().BeGreaterThan(35);
        pages.Count(page => page is > 50_000 and <= 75_000).Should().BeGreaterThan(35);
        pages.Count(page => page > 75_000).Should().BeGreaterThan(35);
    }

    [Fact]
    public void ClassificationCoversEncryptedUnreadableAndUnknownOutcomes()
    {
        PdfClassificationPolicy policy = new();
        PdfInspectionResult encrypted = Result(5, Enumerable.Range(1, 5).Select(page => Page(page, "digital")).ToArray()) with
        {
            EncryptionStatus = PdfEncryptionStatus.EncryptedAccessibleWithEmptyPassword,
        };
        PdfInspectionResult unreadable = PdfInspectionResult.Failed(
            new(1), "Book.pdf", PdfInspectionProblemCode.MalformedStructure);
        PdfInspectionResult unknown = Result(4, Enumerable.Range(1, 4).Select(page => Page(page, "scan-no-text")).ToArray());

        policy.Classify(encrypted, PdfSamplingMode.AllPages).Classification.Should().Be(PdfDocumentClassification.Encrypted);
        policy.Classify(unreadable, null).Classification.Should().Be(PdfDocumentClassification.Unreadable);
        policy.Classify(unknown, PdfSamplingMode.AllPages).Classification.Should().Be(PdfDocumentClassification.Unknown);
    }

    [Fact]
    public void ClassificationThresholdsAreExactAndScanClassificationDoesNotChangeScore()
    {
        PdfClassificationPolicy policy = new();
        PdfPageFacts[] eightDigital = Enumerable.Range(1, 10)
            .Select(page => Page(page, page <= 8 ? "digital" : "empty")).ToArray();
        PdfPageFacts[] sevenDigital = Enumerable.Range(1, 10)
            .Select(page => Page(page, page <= 7 ? "digital" : "empty")).ToArray();
        PdfPageFacts[] eightScans = Enumerable.Range(1, 10)
            .Select(page => Page(page, page <= 8 ? "scan-no-text" : "digital")).ToArray();

        policy.Classify(Result(10, eightDigital), PdfSamplingMode.AllPages).Classification
            .Should().Be(PdfDocumentClassification.DigitalText);
        policy.Classify(Result(10, sevenDigital), PdfSamplingMode.AllPages).Classification
            .Should().Be(PdfDocumentClassification.Unknown);
        policy.Classify(Result(10, eightScans), PdfSamplingMode.AllPages).Classification
            .Should().Be(PdfDocumentClassification.Mixed);

        PdfAssessment digital = new PdfAssessmentEngine(new()).Assess(
            new(1), "Book.pdf", Fingerprint,
            Result(10, Enumerable.Range(1, 10).Select(page => Page(page, "digital")).ToArray()), Observation);
        PdfAssessment scan = new PdfAssessmentEngine(new()).Assess(
            new(1), "Book.pdf", Fingerprint,
            Result(10, Enumerable.Range(1, 10).Select(page => Page(page, "scan-no-text")).ToArray()), Observation);

        scan.Features.Classification.Should().Be(PdfDocumentClassification.ScannedWithoutOcr);
        scan.Score.Should().Be(digital.Score);
        scan.Findings.Single(finding => finding.RuleId == "PDF.CLASSIFICATION.SCANNED_WITHOUT_OCR")
            .ScoreAdjustment.Should().Be(0);
    }

    [Fact]
    public void OcrClassificationDisclosesInferenceAndNeverClaimsOcrWasRun()
    {
        PdfInspectionResult result = Result(10, Enumerable.Range(1, 10).Select(page => Page(page, "scan-text")).ToArray());

        PdfClassificationDecision decision = new PdfClassificationPolicy().Classify(result, PdfSamplingMode.AllPages);

        decision.Classification.Should().Be(PdfDocumentClassification.ScannedWithOcr);
        decision.Explanation.Should().Contain("existing text layer").And.Contain("no OCR was run");
    }

    [Theory]
    [InlineData(PdfDocumentClassification.DigitalText, "digital")]
    [InlineData(PdfDocumentClassification.ScannedWithoutOcr, "scan-no-text")]
    [InlineData(PdfDocumentClassification.ScannedWithOcr, "scan-text")]
    [InlineData(PdfDocumentClassification.Mixed, "mixed")]
    [InlineData(PdfDocumentClassification.EmptyOrNearEmpty, "empty")]
    public void ClassificationIsDeterministic(PdfDocumentClassification expected, string scenario)
    {
        PdfInspectionResult result = Result(10, Enumerable.Range(1, 10).Select(page => Page(page, scenario)).ToArray());
        PdfClassificationPolicy policy = new();

        PdfClassificationDecision first = policy.Classify(result, PdfSamplingMode.AllPages);
        PdfClassificationDecision second = policy.Classify(result with { PageFacts = result.PageFacts.Reverse().ToArray() }, PdfSamplingMode.AllPages);

        first.Classification.Should().Be(expected);
        second.Classification.Should().Be(expected);
        first.Evidence.Should().BeEquivalentTo(second.Evidence);
    }

    [Fact]
    public void ImageHeavyIllustratedShortDocumentRemainsUnknownWithoutPenalty()
    {
        PdfInspectionResult result = Result(4, Enumerable.Range(1, 4).Select(page => Page(page, "scan-no-text")).ToArray());
        PdfAssessment assessment = new PdfAssessmentEngine(new()).Assess(new CalibreBookId(1), "Book.pdf", Fingerprint, result, Observation);

        assessment.Features.Classification.Should().Be(PdfDocumentClassification.Unknown);
        assessment.Findings.Single(finding => finding.RuleId.StartsWith("PDF.CLASSIFICATION", StringComparison.Ordinal)).ScoreAdjustment.Should().Be(0);
        assessment.Findings.Should().NotContain(finding => finding.RuleId == "PDF.TEXT.EXTRACTION_UNAVAILABLE" && finding.ScoreAdjustment != 0);
    }

    [Fact]
    public void MaximumCatalogYieldsEightyFiveAndFifteenComponents()
    {
        PdfInspectionResult result = Result(10, Enumerable.Range(1, 10).Select(page => Page(page, "digital")).ToArray()) with
        {
            OutlinePresent = true,
            OutlineEntryCount = 5,
            Metadata = new(
                new(PdfMetadataValueStatus.Present, "Title"),
                new(PdfMetadataValueStatus.Present, "Author"),
                new(PdfMetadataValueStatus.Present, "Subject"),
                new(PdfMetadataValueStatus.Missing),
                new(PdfMetadataValueStatus.Missing),
                new(PdfMetadataValueStatus.Missing),
                new(PdfMetadataValueStatus.Present, "2020-01-01"),
                new(PdfMetadataValueStatus.Missing)),
            Identifiers = [new("9780306406157", PdfIdentifierSource.DocumentInformation)],
        };

        PdfAssessment assessment = new PdfAssessmentEngine(new()).Assess(new CalibreBookId(1), "Book.pdf", Fingerprint, result, Observation);

        assessment.Score!.Value.Value.Should().Be(100);
        assessment.ScoreBreakdown.TechnicalScore.Should().Be(85);
        assessment.ScoreBreakdown.EmbeddedMetadataScore.Should().Be(15);
        assessment.ObservedObservation.Should().Be(Observation);
        assessment.Findings.Where(finding => finding.ScoreAdjustment != 0).Should().OnlyContain(finding =>
            finding.ScoreComponentId == PdfAssessment.TechnicalComponentId
            || finding.ScoreComponentId == PdfAssessment.EmbeddedMetadataComponentId);
    }

    [Fact]
    public void PasswordRequiredIsEncryptedAndDisqualifiedWithoutScore()
    {
        PdfInspectionResult result = PdfInspectionResult.Failed(
            new CalibreBookId(1), "Book.pdf", PdfInspectionProblemCode.PasswordRequired,
            PdfOpenStatus.Unreadable, PdfEncryptionStatus.PasswordRequired);

        PdfAssessment assessment = new PdfAssessmentEngine(new()).Assess(new CalibreBookId(1), "Book.pdf", Fingerprint, result, Observation);

        assessment.Status.Should().Be(AssessmentStatus.Disqualified);
        assessment.Score.Should().BeNull();
        assessment.Features.Classification.Should().Be(PdfDocumentClassification.Encrypted);
        assessment.Findings.Should().Contain(finding => finding.Severity == Domain.Findings.FindingSeverity.Disqualifying);
    }

    [Fact]
    public void EmptyPasswordAccessibleEncryptionIsClassifiedButRemainsScoreEligible()
    {
        PdfInspectionResult result = Result(5, Enumerable.Range(1, 5).Select(page => Page(page, "digital")).ToArray()) with
        {
            EncryptionStatus = PdfEncryptionStatus.EncryptedAccessibleWithEmptyPassword,
        };

        PdfAssessment assessment = new PdfAssessmentEngine(new()).Assess(
            new(1), "Book.pdf", Fingerprint, result, Observation);

        assessment.Status.Should().Be(AssessmentStatus.Completed);
        assessment.Score.Should().NotBeNull();
        assessment.Features.Classification.Should().Be(PdfDocumentClassification.Encrypted);
        assessment.Findings.Should().ContainSingle(finding =>
            finding.RuleId == "PDF.ENCRYPTION.EMPTY_PASSWORD_ACCESSIBLE" && finding.ScoreAdjustment == 0);
    }

    [Fact]
    public void RepeatedUnreadableResourceBlankDimensionAndMetadataPenaltiesAreCapped()
    {
        PdfAssessmentEngine engine = new(new());
        PdfPageFacts[] repeatedPages = Enumerable.Range(1, 20)
            .Select(page => Page(page, "digital") with
            {
                NormalizedTextFingerprint = page % 2 == 1 ? "same-text" : $"unique-{page}",
            })
            .ToArray();
        PdfAssessment repeated = engine.Assess(new(1), "Book.pdf", Fingerprint, Result(20, repeatedPages), Observation);
        repeated.Findings.Single(finding => finding.RuleId == "PDF.PAGE.REPEATED_TEXT_EXACT")
            .ScoreAdjustment.Should().Be(-12);

        PdfPageFacts[] heavyPages = Enumerable.Range(1, 8)
            .Select(page => Page(page, "digital") with { ResourceHeavy = true })
            .ToArray();
        PdfAssessment heavy = engine.Assess(new(1), "Book.pdf", Fingerprint, Result(8, heavyPages) with
        {
            ResourceCountsWithinSoftLimits = false,
        }, Observation);
        heavy.Findings.Single(finding => finding.RuleId == "PDF.RESOURCE.COUNT_UNUSUAL")
            .ScoreAdjustment.Should().Be(-15);

        PdfPageFacts[] blankPages = Enumerable.Range(1, 8).Select(page => Page(page, "empty")).ToArray();
        PdfAssessment blank = engine.Assess(new(1), "Book.pdf", Fingerprint, Result(8, blankPages), Observation);
        blank.Findings.Single(finding => finding.RuleId == "PDF.PAGE.SUSPICIOUS_BLANK")
            .ScoreAdjustment.Should().Be(-8);

        PdfPageFacts[] unusualPages = Enumerable.Range(1, 8)
            .Select(page => Page(page, "digital") with { WidthMilliPoints = 15_000_000 })
            .ToArray();
        PdfAssessment unusual = engine.Assess(new(1), "Book.pdf", Fingerprint, Result(8, unusualPages), Observation);
        unusual.Findings.Single(finding => finding.RuleId == "PDF.PAGE.DIMENSIONS_UNUSUAL")
            .ScoreAdjustment.Should().Be(-9);

        PdfDocumentMetadataSummary malformedMetadata = new(
            new(PdfMetadataValueStatus.Malformed),
            new(PdfMetadataValueStatus.Malformed),
            new(PdfMetadataValueStatus.Malformed),
            new(PdfMetadataValueStatus.Malformed),
            new(PdfMetadataValueStatus.Malformed),
            new(PdfMetadataValueStatus.Malformed),
            new(PdfMetadataValueStatus.Malformed),
            new(PdfMetadataValueStatus.Malformed));
        PdfAssessment malformed = engine.Assess(new(1), "Book.pdf", Fingerprint, Result(8, heavyPages) with
        {
            Metadata = malformedMetadata,
        }, Observation);
        malformed.Findings.Single(finding => finding.RuleId == "PDF.METADATA.MALFORMED")
            .ScoreAdjustment.Should().Be(-3);
    }

    [Fact]
    public void RepeatedPageEvidenceSeparatesExactLikelyAndInsufficientSignals()
    {
        PdfAssessmentEngine engine = new(new());
        PdfPageFacts[] exactPages = Enumerable.Range(1, 5).Select(page => Page(page, "digital")).ToArray();
        exactPages[0] = exactPages[0] with { NormalizedTextFingerprint = "same" };
        exactPages[2] = exactPages[2] with { NormalizedTextFingerprint = "same" };
        PdfAssessment exact = engine.Assess(new(1), "Book.pdf", Fingerprint, Result(5, exactPages), Observation);

        PdfPageFacts[] likelyPages = Enumerable.Range(1, 5).Select(page => Page(page, "scan-no-text")).ToArray();
        likelyPages[0] = likelyPages[0] with { SharedImageIdentity = "same-image" };
        likelyPages[2] = likelyPages[2] with { SharedImageIdentity = "same-image" };
        PdfAssessment likely = engine.Assess(new(1), "Book.pdf", Fingerprint, Result(5, likelyPages), Observation);

        PdfPageFacts[] uniquePages = Enumerable.Range(1, 5).Select(page => Page(page, "digital")).ToArray();
        PdfAssessment insufficient = engine.Assess(new(1), "Book.pdf", Fingerprint, Result(5, uniquePages), Observation);

        exact.Findings.Should().ContainSingle(finding => finding.RuleId == "PDF.PAGE.REPEATED_TEXT_EXACT" && finding.ScoreAdjustment == -4);
        likely.Findings.Should().ContainSingle(finding => finding.RuleId == "PDF.PAGE.REPEATED_LIKELY" && finding.ScoreAdjustment == -2);
        insufficient.Findings.Should().ContainSingle(finding => finding.RuleId == "PDF.PAGE.REPEAT_INSUFFICIENT" && finding.ScoreAdjustment == 0);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(8, true)]
    public void UnreadablePageThresholdAndRepeatedPenaltyAreDeterministic(int failedPages, bool disqualified)
    {
        PdfPageFacts[] pages = Enumerable.Range(1, 10 - failedPages).Select(page => Page(page, "digital")).ToArray();
        PdfAssessment assessment = new PdfAssessmentEngine(new()).Assess(new(1), "Book.pdf", Fingerprint, Result(10, pages), Observation);

        (assessment.Status == AssessmentStatus.Disqualified).Should().Be(disqualified);
        assessment.Findings.Single(finding => finding.RuleId == "PDF.PAGE.UNREADABLE").ScoreAdjustment
            .Should().Be(Math.Max(-10, -2 * failedPages));
        if (disqualified)
        {
            assessment.Score.Should().BeNull();
            assessment.Findings.Should().Contain(finding => finding.RuleId == "PDF.PAGE.SAMPLE_INCOMPLETE"
                && finding.Severity == Domain.Findings.FindingSeverity.Disqualifying);
        }
    }

    [Fact]
    public void FindingOrderingAndScoreAreIndependentOfInspectorPageOrder()
    {
        PdfPageFacts[] pages = Enumerable.Range(1, 10).Select(page => Page(page, "digital")).ToArray();
        PdfInspectionResult forward = Result(10, pages);
        PdfInspectionResult reverse = forward with { PageFacts = pages.Reverse().ToArray() };
        PdfAssessmentEngine engine = new(new());

        PdfAssessment first = engine.Assess(new(1), "Book.pdf", Fingerprint, forward, Observation);
        PdfAssessment second = engine.Assess(new(1), "Book.pdf", Fingerprint, reverse, Observation);

        first.Score.Should().Be(second.Score);
        first.Findings.Select(finding => (finding.Severity, finding.RuleId, finding.ScoreAdjustment, finding.Explanation))
            .Should().Equal(second.Findings.Select(finding => (finding.Severity, finding.RuleId, finding.ScoreAdjustment, finding.Explanation)));
    }

    [Fact]
    public void DeterministicSampleCapsClassificationConfidenceAndDisclosesLimitations()
    {
        PdfPageFacts[] pages = Enumerable.Range(1, 10).Select(page => Page(page, "digital")).ToArray();
        PdfInspectionResult sampled = Result(10, pages) with
        {
            PageCount = 1_000,
            SelectedPages = Enumerable.Range(1, 10).ToArray(),
        };

        PdfAssessment assessment = new PdfAssessmentEngine(new()).Assess(new(1), "Book.pdf", Fingerprint, sampled, Observation);

        assessment.Features.Sampling!.Mode.Should().Be(PdfSamplingMode.DeterministicSample);
        assessment.Features.ClassificationConfidence.Should().NotBe(PdfClassificationConfidence.High);
        assessment.Findings.Should().Contain(finding => finding.RuleId == "PDF.PAGE.SAMPLE_BOUNDED"
            && finding.Explanation.Contains("does not imply whole-document certainty", StringComparison.Ordinal));
    }

    [Fact]
    public void NonEmbeddedFontEvidenceIsDisclosedAndCannotProduceStrongClassificationConfidence()
    {
        PdfPageFacts[] pages = Enumerable.Range(1, 20)
            .Select(page => Page(page, "digital") with { FontEvidenceReliable = false })
            .ToArray();

        PdfAssessment assessment = new PdfAssessmentEngine(new()).Assess(
            new(1), "Book.pdf", Fingerprint, Result(20, pages), Observation);

        assessment.Features.Classification.Should().Be(PdfDocumentClassification.DigitalText);
        assessment.Features.ClassificationConfidence.Should().Be(PdfClassificationConfidence.Low);
        assessment.Findings.Should().ContainSingle(finding =>
            finding.RuleId == "PDF.FONT.NON_EMBEDDED" && finding.ScoreAdjustment == 0);
    }

    [Fact]
    public void HiddenTextEvidenceCannotProduceStrongClassificationConfidence()
    {
        PdfPageFacts[] pages = Enumerable.Range(1, 20)
            .Select(page => Page(page, "digital") with { HiddenTextPresent = true })
            .ToArray();

        PdfAssessment assessment = new PdfAssessmentEngine(new()).Assess(
            new(1), "Book.pdf", Fingerprint, Result(20, pages), Observation);

        assessment.Features.Classification.Should().Be(PdfDocumentClassification.DigitalText);
        assessment.Features.ClassificationConfidence.Should().Be(PdfClassificationConfidence.Low);
        assessment.Findings.Single(finding => finding.RuleId == "PDF.CLASSIFICATION.DIGITAL_TEXT")
            .Evidence.Should().Contain("hiddenTextPresent", "True");
    }

    [Fact]
    public void PartialObservableContentDoesNotReceiveTheEightyPercentAbsentPenalty()
    {
        PdfPageFacts[] pages = Enumerable.Range(1, 10)
            .Select(page => Page(page, page <= 3 ? "digital" : "empty"))
            .ToArray();

        PdfAssessment assessment = new PdfAssessmentEngine(new()).Assess(
            new(1), "Book.pdf", Fingerprint, Result(10, pages), Observation);

        assessment.Findings.Should().ContainSingle(finding =>
            finding.RuleId == "PDF.CONTENT.OBSERVABLE" && finding.ScoreAdjustment == 0);
        assessment.Findings.Should().NotContain(finding => finding.RuleId == "PDF.CONTENT.NOT_OBSERVABLE");
    }

    [Fact]
    public void AdjacentRepeatedPagesAreExcludedFromRepeatPenalties()
    {
        PdfPageFacts[] pages = Enumerable.Range(1, 5).Select(page => Page(page, "digital")).ToArray();
        pages[0] = pages[0] with { NormalizedTextFingerprint = "same" };
        pages[1] = pages[1] with { NormalizedTextFingerprint = "same" };
        pages[2] = pages[2] with { NormalizedTextFingerprint = "same" };

        PdfAssessment assessment = new PdfAssessmentEngine(new()).Assess(
            new(1), "Book.pdf", Fingerprint, Result(5, pages), Observation);

        assessment.Findings.Should().NotContain(finding => finding.RuleId == "PDF.PAGE.REPEATED_TEXT_EXACT");
        assessment.Findings.Should().ContainSingle(finding => finding.RuleId == "PDF.PAGE.REPEAT_INSUFFICIENT");
    }

    [Fact]
    public async Task PublishedAssessmentsRequireTheExactFrozenV1Limits()
    {
        AssessPdfFormatsUseCase useCase = new(A.Fake<IPdfInspector>(), new(), new(new()));
        PdfInspectionLimits lowered = PdfInspectionLimits.V1 with
        {
            MaximumPages = PdfInspectionLimits.V1.MaximumPages - 1,
        };
        PdfInspectionLimits raised = PdfInspectionLimits.V1 with
        {
            MaximumPages = PdfInspectionLimits.V1.MaximumPages + 1,
        };

        await FluentActions.Awaiting(() => useCase.ExecuteAsync([], 1, lowered, null, CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        FluentActions.Invoking(raised.Validate).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task OrchestrationIsBoundedCanonicalAndCancellationAware()
    {
        IPdfInspector inspector = A.Fake<IPdfInspector>();
        int active = 0;
        int maximum = 0;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource twoStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => inspector.InspectAsync(
                A<PdfInspectionRequest>._,
                A<Func<PdfDocumentHeaderFacts, CancellationToken, ValueTask<IReadOnlyList<int>>>>._,
                A<IProgress<PdfInspectionProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                PdfInspectionRequest request = call.GetArgument<PdfInspectionRequest>(0)!;
                Func<PdfDocumentHeaderFacts, CancellationToken, ValueTask<IReadOnlyList<int>>> selector = call.GetArgument<Func<PdfDocumentHeaderFacts, CancellationToken, ValueTask<IReadOnlyList<int>>>>(1)!;
                CancellationToken token = call.GetArgument<CancellationToken>(3);
                int current = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, current);
                if (current == 2) twoStarted.TrySetResult();
                IReadOnlyList<int> selected = await selector(new(request.BookId, request.ExpectedRelativePath, 5, []), token);
                await release.Task.WaitAsync(token);
                Interlocked.Decrement(ref active);
                return Result(5, selected.Select(page => Page(page, "digital")).ToArray(), request.BookId, request.ExpectedRelativePath);
            });
        PdfAssessmentTarget[] targets = Enumerable.Range(1, 5).Reverse().Select(id => new PdfAssessmentTarget(
            new CalibreBookId(id), "PDF", $"{id}.pdf", "root", $"file-{id}",
            FormatFileStatus.Present, Fingerprint, Observation)).ToArray();
        AssessPdfFormatsUseCase useCase = new(inspector, new(), new(new()));
        ConcurrentQueue<PdfAssessmentProgress> progress = new();

        Task<IReadOnlyList<PdfAssessment>> operation = useCase.ExecuteAsync(targets, 2, PdfInspectionLimits.V1,
            new InlineProgress(progress.Enqueue), CancellationToken.None);
        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        maximum.Should().Be(2);
        release.SetResult();
        IReadOnlyList<PdfAssessment> assessments = await operation;
        assessments.Select(value => value.CalibreBookId.Value).Should().BeInAscendingOrder();
        progress.Where(value => value.Stage == "Complete").Select(value => value.CompletedFiles).Should().Equal(1, 2, 3, 4, 5);
        progress.Where(value => value.Stage == "Complete").Select(value => value.CurrentRelativePath)
            .Should().Equal("1.pdf", "2.pdf", "3.pdf", "4.pdf", "5.pdf");

        await FluentActions.Awaiting(() => useCase.ExecuteAsync(targets, 2, PdfInspectionLimits.V1, null, new(canceled: true)))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task OrchestrationCoalescesPageProgressDeterministically()
    {
        IPdfInspector inspector = A.Fake<IPdfInspector>();
        A.CallTo(() => inspector.InspectAsync(
                A<PdfInspectionRequest>._,
                A<Func<PdfDocumentHeaderFacts, CancellationToken, ValueTask<IReadOnlyList<int>>>>._,
                A<IProgress<PdfInspectionProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                PdfInspectionRequest request = call.GetArgument<PdfInspectionRequest>(0)!;
                Func<PdfDocumentHeaderFacts, CancellationToken, ValueTask<IReadOnlyList<int>>> selector = call.GetArgument<Func<PdfDocumentHeaderFacts, CancellationToken, ValueTask<IReadOnlyList<int>>>>(1)!;
                IProgress<PdfInspectionProgress> pageProgress = call.GetArgument<IProgress<PdfInspectionProgress>>(2)!;
                IReadOnlyList<int> selected = await selector(new(request.BookId, request.ExpectedRelativePath, 200, []), CancellationToken.None);
                for (int page = 1; page <= 200; page++)
                {
                    pageProgress.Report(new("Pages", page, 200, false));
                }

                return Result(200, selected.Select(page => Page(page, "digital")).ToArray(), request.BookId, request.ExpectedRelativePath);
            });
        PdfAssessmentTarget target = new(new(1), "PDF", "one.pdf", "root", "file-1", FormatFileStatus.Present, Fingerprint, Observation);
        ConcurrentQueue<PdfAssessmentProgress> progress = new();

        await new AssessPdfFormatsUseCase(inspector, new(), new(new())).ExecuteAsync(
            [target], 1, PdfInspectionLimits.V1, new InlineProgress(progress.Enqueue), CancellationToken.None);

        PdfAssessmentProgress[] pages = progress.Where(value => value.Stage == "Pages").ToArray();
        pages.Should().HaveCount(41);
        pages.Select(value => value.CompletedPages).Should().Equal(
            Enumerable.Range(0, 40).Select(index => 1 + index * 5).Append(200));
        pages.Should().OnlyContain(value => value.CompletedFiles == 0 && value.TotalFiles == 1 && value.CurrentRelativePath == "one.pdf");
    }

    [Fact]
    public async Task OrchestrationFiltersNonPdfAndOrdersTwoThousandIndependentTargets()
    {
        IPdfInspector inspector = A.Fake<IPdfInspector>();
        PdfAssessmentTarget[] pdfTargets = Enumerable.Range(1, 2_000).Reverse().Select(id => new PdfAssessmentTarget(
            new(id), "PDF", $"{id:D4}.pdf", null, null, FormatFileStatus.Missing, null, null)).ToArray();
        PdfAssessmentTarget epub = new(new(9_999), "EPUB", "ignored.epub", null, null, FormatFileStatus.Missing, null, null);
        AssessPdfFormatsUseCase useCase = new(inspector, new(), new(new()));

        IReadOnlyList<PdfAssessment> results = await useCase.ExecuteAsync(
            [epub, .. pdfTargets], 2, PdfInspectionLimits.V1, null, CancellationToken.None);

        results.Should().HaveCount(2_000);
        results.Select(result => result.CalibreBookId.Value).Should().BeInAscendingOrder();
        results.Should().OnlyContain(result => result.Status == AssessmentStatus.Disqualified && result.Score == null);
        A.CallTo(() => inspector.InspectAsync(
            A<PdfInspectionRequest>._,
            A<Func<PdfDocumentHeaderFacts, CancellationToken, ValueTask<IReadOnlyList<int>>>>._,
            A<IProgress<PdfInspectionProgress>?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    private static PdfInspectionResult Result(
        int pageCount,
        IReadOnlyList<PdfPageFacts> pages,
        CalibreBookId? bookId = null,
        string path = "Book.pdf") => new(
        bookId ?? new CalibreBookId(1), path, PdfOpenStatus.Opened, PdfEncryptionStatus.NotEncrypted,
        pageCount, "1.7", PdfDocumentMetadataSummary.Empty, false, 0, new(0, 0, 0),
        Enumerable.Range(1, pageCount).ToArray(), pages, [], 0, 10, 0, true, false, []);

    private static PdfPageFacts Page(int page, string scenario)
    {
        (int characters, int textCoverage, int images, int maxImage, int imageCoverage, int operations, string? geometry) = scenario switch
        {
            "digital" => (1_000, 2_000, 0, 0, 0, 20, null),
            "scan-no-text" => (0, 0, 1, 9_000, 9_000, 20, "full-page"),
            "scan-text" => (1_000, 1_000, 1, 9_000, 9_000, 20, "full-page"),
            "mixed" when page <= 5 => (1_000, 2_000, 0, 0, 0, 20, null),
            "mixed" => (0, 0, 1, 9_000, 9_000, 20, "full-page"),
            _ => (0, 0, 0, 0, 0, 0, null),
        };
        return new(page, true, characters, textCoverage, images, maxImage, imageCoverage, operations,
            612_000, 792_000, null, characters >= 200 ? $"text-{page}" : null, true,
            images > 0 ? $"image-{page}" : null, geometry, false, false);
    }

    private sealed class InlineProgress(Action<PdfAssessmentProgress> report) : IProgress<PdfAssessmentProgress>
    {
        public void Report(PdfAssessmentProgress value) => report(value);
    }
}
