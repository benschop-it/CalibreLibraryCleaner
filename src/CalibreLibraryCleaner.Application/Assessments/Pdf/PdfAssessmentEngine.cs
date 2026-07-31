using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Assessments.Pdf;

public sealed class PdfAssessmentEngine(PdfClassificationPolicy classificationPolicy)
{
    public static AnalyzerVersion AnalyzerVersion { get; } = new("pdf-inspector/1.0.0");
    public static ScoringModelVersion ScoringModelVersion { get; } = new("pdf-quality/1.0.0");
    public static PdfPolicyVersion ResourceProfileVersion { get; } = new("pdf-limits/1.0.0");

    public PdfAssessment Assess(
        CalibreBookId bookId,
        string expectedRelativePath,
        FormatFileFingerprint? fingerprint,
        PdfInspectionResult result,
        FormatFileObservation? observation = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.BookId != bookId || !string.Equals(result.ExpectedRelativePath, expectedRelativePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The PDF inspector returned a mismatched association.");
        }

        int? pageCount = result.PageCount;
        PdfSamplingMode? samplingMode = pageCount > 0 && result.SelectedPages.Count > 0
            ? result.SelectedPages.Count == pageCount ? PdfSamplingMode.AllPages : PdfSamplingMode.DeterministicSample
            : null;
        ValidateResult(result, samplingMode);
        PdfClassificationDecision classification = classificationPolicy.Classify(result, samplingMode);
        List<AssessmentFinding> findings = [];
        foreach (PdfInspectionProblem problem in result.Problems
                     .OrderBy(problem => problem.Code)
                     .ThenBy(problem => problem.PageNumber)
                     .ThenBy(problem => problem.EvidenceKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddProblemFinding(findings, problem, result.OpenStatus == PdfOpenStatus.Opened);
        }

        bool hardProblem = findings.Any(finding => finding.Severity == FindingSeverity.Disqualifying);
        PdfPageFacts[] analyzed = result.PageFacts.Where(page => page.Parsed).OrderBy(page => page.PageNumber).ToArray();
        int failedPages = Math.Max(0, result.SelectedPages.Count - analyzed.Length);
        if (!hardProblem)
        {
            Add(findings, "PDF.OPEN.SUCCESS", FindingSeverity.Positive, 50,
                "The PDF opened with the strict V1 parser profile.", PdfAssessment.TechnicalComponentId);
            Add(findings, "PDF.STRUCTURE.PAGE_TREE_VALID", FindingSeverity.Positive, 10,
                "The required page tree is valid and contains at least one page.", PdfAssessment.TechnicalComponentId);

            int coverageBps = Percentage(analyzed.Length, Math.Max(1, result.SelectedPages.Count));
            int coverageAdjustment = coverageBps == 10_000 ? 10 : coverageBps >= 9_900 ? 8 : coverageBps >= 9_500 ? 5 : 0;
            Add(findings, "PDF.PAGE.SAMPLE_PARSE_COVERAGE",
                coverageAdjustment > 0 ? FindingSeverity.Positive : FindingSeverity.Warning,
                coverageAdjustment,
                $"{analyzed.Length} of {result.SelectedPages.Count} requested pages produced bounded facts.",
                PdfAssessment.TechnicalComponentId,
                Evidence(("coverageBasisPoints", coverageBps), ("failedPages", failedPages)));
            if (failedPages > 0)
            {
                int adjustment = Math.Max(-10, checked(-2 * failedPages));
                Add(findings, "PDF.PAGE.UNREADABLE", FindingSeverity.Error, adjustment,
                    "One or more sampled pages could not be inspected; the repeated penalty is capped at -10.",
                    PdfAssessment.TechnicalComponentId,
                    Evidence(("rawCount", failedPages), ("appliedCount", Math.Min(5, failedPages)), ("cap", -10)));
            }

            if (failedPages > 0 && Percentage(failedPages, result.SelectedPages.Count) > 2_000)
            {
                Add(findings, "PDF.PAGE.SAMPLE_INCOMPLETE", FindingSeverity.Disqualifying, 0,
                    "More than 20% of requested pages were unreadable, so the result is not comparable.",
                    PdfAssessment.TechnicalComponentId);
            }

            AddContentFindings(findings, analyzed);
            AddResourceFindings(findings, result, analyzed);
            AddOutlineFindings(findings, result);
            AddMetadataFindings(findings, result);
            AddPageAnomalyFindings(findings, analyzed, pageCount ?? analyzed.Length);
            AddRepeatFindings(findings, analyzed);
            AddInformationalFindings(findings, result, analyzed, samplingMode);

            if (analyzed.Length == 0 && result.SelectedPages.Count > 0)
            {
                Add(findings, "PDF.PAGE.SAMPLE_INCOMPLETE", FindingSeverity.Disqualifying, 0,
                    "No requested page produced enough evidence for a comparable assessment.",
                    PdfAssessment.TechnicalComponentId);
            }
        }

        Add(findings, ClassificationRule(classification.Classification), FindingSeverity.Information, 0,
            classification.Explanation, PdfAssessment.TechnicalComponentId, classification.Evidence);
        if (findings.Count > PdfInspectionLimits.V1.MaximumRetainedFindings)
        {
            throw new InvalidOperationException("The PDF rule catalog exceeded its bounded finding count.");
        }

        bool disqualified = findings.Any(finding => finding.Severity == FindingSeverity.Disqualifying);
        QualityScore? score = disqualified ? null : new(CalculateScore(findings));
        PdfFeatureSummary features = BuildFeatures(result, classification, analyzed, samplingMode);
        FormatAssessment core = new(
            bookId,
            "PDF",
            expectedRelativePath,
            fingerprint,
            disqualified ? AssessmentStatus.Disqualified : AssessmentStatus.Completed,
            score,
            AnalyzerVersion,
            ScoringModelVersion,
            findings,
            PdfAssessment.V1Components,
            observation);
        return new(core, features);
    }

    private static void ValidateResult(PdfInspectionResult result, PdfSamplingMode? samplingMode)
    {
        PdfInspectionLimits limits = PdfInspectionLimits.V1;
        if (result.PageCount > limits.MaximumPages)
        {
            throw new InvalidOperationException("The PDF inspector returned a page count beyond the requested limit.");
        }

        if (result.SelectedPages.Count > limits.MaximumSampledPages
            || !result.SelectedPages.SequenceEqual(result.SelectedPages.Distinct().Order())
            || result.SelectedPages.Any(page => page <= 0 || result.PageCount is int totalPages && page > totalPages)
            || result.PageFacts.Count > limits.MaximumSampledPages
            || result.PageFacts.Select(page => page.PageNumber).Distinct().Count() != result.PageFacts.Count
            || result.PageFacts.Any(page => !result.SelectedPages.Contains(page.PageNumber)))
        {
            throw new InvalidOperationException("The PDF inspector returned an invalid requested-page association.");
        }

        if (result.OpenStatus == PdfOpenStatus.Opened
            && (result.PageCount is not > 0 || result.SelectedPages.Count == 0 || samplingMode is null)
            || result.OpenStatus != PdfOpenStatus.Opened
            && (result.PageCount is not null || result.SelectedPages.Count != 0 || result.PageFacts.Count != 0))
        {
            throw new InvalidOperationException("The PDF inspector returned contradictory open and page facts.");
        }

        if (samplingMode is not null && result.PageCount is null)
        {
            throw new InvalidOperationException("PDF sampling requires a page count.");
        }

        if (result.Identifiers.Count > limits.MaximumRetainedIdentifiers
            || result.Problems.Count > limits.MaximumRetainedFindings
            || result.OutlineEntryCount is < 0 or > 10_000
            || result.ObjectCount is < 0 or > 500_000
            || result.AggregateDecodedBytes is < 0 or > 512L * 1024 * 1024
            || result.InvalidIdentifierCandidateCount < 0
            || MetadataValues(result.Metadata).Any(value => value is null)
            || result.PageFacts.Any(page => !PageFactsWithinBounds(page, limits)))
        {
            throw new InvalidOperationException("The PDF inspector returned facts outside V1 retained bounds.");
        }
    }

    private static bool PageFactsWithinBounds(PdfPageFacts page, PdfInspectionLimits limits) =>
        page.PageNumber > 0
        && page.UsefulCharacterCount >= 0 && page.UsefulCharacterCount <= limits.MaximumUsefulCharactersPerPage
        && page.GlyphAreaCoverageBasisPoints is >= 0 and <= 10_000
        && page.ImageCount >= 0 && page.ImageCount <= limits.MaximumImagesPerPage
        && page.MaximumImageCoverageBasisPoints is >= 0 and <= 10_000
        && page.AggregateImageCoverageBasisPoints is >= 0 and <= 10_000
        && page.NontrivialOperationCount >= 0 && page.NontrivialOperationCount <= limits.MaximumOperationsPerPage
        && page.WidthMilliPoints >= 0 && page.WidthMilliPoints <= limits.MaximumPageDimensionPoints * 1000L
        && page.HeightMilliPoints >= 0 && page.HeightMilliPoints <= limits.MaximumPageDimensionPoints * 1000L
        && IsBounded(page.SharedContentIdentity)
        && IsBounded(page.NormalizedTextFingerprint)
        && IsBounded(page.SharedImageIdentity)
        && IsBounded(page.DominantImageGeometryKey);

    private static bool IsBounded(string? value) => value is null || value.Length <= 128;

    private static PdfFeatureSummary BuildFeatures(
        PdfInspectionResult result,
        PdfClassificationDecision classification,
        PdfPageFacts[] analyzed,
        PdfSamplingMode? samplingMode)
    {
        int usefulPages = analyzed.Count(page => page.HasUsefulText);
        int textBearingPages = analyzed.Count(page => page.UsefulCharacterCount >= 10);
        int usefulCharacters = (int)Math.Min(int.MaxValue, analyzed.Sum(page => (long)page.UsefulCharacterCount));
        int[] characters = analyzed.Select(page => page.UsefulCharacterCount).Order().ToArray();
        int medianCharacters = characters.Length == 0 ? 0 : characters[(characters.Length - 1) / 2];
        PdfTextDensityBand density = medianCharacters switch
        {
            < 10 => PdfTextDensityBand.None,
            < 50 => PdfTextDensityBand.Sparse,
            < 200 => PdfTextDensityBand.Moderate,
            _ => PdfTextDensityBand.Useful,
        };
        PdfTextExtractionStatus extraction = analyzed.Length == 0
            ? PdfTextExtractionStatus.Unavailable
            : result.PageFacts.Any(page => !page.Parsed) ? PdfTextExtractionStatus.Partial : PdfTextExtractionStatus.Available;
        PdfTextSummary text = new(
            extraction,
            analyzed.Length,
            textBearingPages,
            usefulPages,
            usefulCharacters,
            Percentage(usefulPages, Math.Max(1, analyzed.Length)),
            density,
            analyzed.Any(page => page.CountersCapped));
        int imageBearing = analyzed.Count(page => page.ImageCount > 0);
        int imageDominant = analyzed.Count(page => page.IsImageDominant);
        int textDominant = analyzed.Count(page => page.IsTextDominant);
        int nonContentful = analyzed.Count(page => !page.IsContentful);
        PdfContentBalance balance = analyzed.Length == 0
            ? PdfContentBalance.Unknown
            : Percentage(textDominant, analyzed.Length) >= 7_000 ? PdfContentBalance.TextHeavy
            : Percentage(imageDominant, analyzed.Length) >= 7_000 ? PdfContentBalance.ImageHeavy
            : Percentage(nonContentful, analyzed.Length) >= 8_000 ? PdfContentBalance.NoObservableContent
            : textDominant > 0 && imageDominant > 0 ? PdfContentBalance.Balanced
            : PdfContentBalance.Unknown;
        PdfImageSummary images = new(
            analyzed.Length,
            imageBearing,
            imageDominant,
            (int)Math.Min(int.MaxValue, analyzed.Sum(page => (long)page.ImageCount)),
            Percentage(imageDominant, Math.Max(1, analyzed.Length)),
            balance,
            analyzed.Any(page => page.CountersCapped));
        int totalPages = result.PageCount ?? 0;
        int[] blankPages = SuspiciousBlankPages(analyzed, totalPages);
        int[] unusualPages = analyzed.Where(page =>
            Math.Max(page.WidthMilliPoints, page.HeightMilliPoints) > PdfInspectionLimits.V1.PageDimensionWarningPoints * 1000)
            .Select(page => page.PageNumber).ToArray();
        int[] failed = result.SelectedPages.Except(analyzed.Select(page => page.PageNumber)).ToArray();
        int[] resourceHeavy = analyzed.Where(page => page.ResourceHeavy).Select(page => page.PageNumber).ToArray();
        PdfPageAnalysisSummary pageAnalysis = new(
            result.SelectedPages.Count,
            analyzed.Length,
            failed.Length,
            blankPages.Length,
            unusualPages.Length,
            resourceHeavy.Length,
            failed,
            blankPages,
            unusualPages,
            resourceHeavy);
        PdfSamplingSummary? sampling = samplingMode is null || result.PageCount is not > 0 || result.SelectedPages.Count == 0
            ? null
            : new(samplingMode.Value, result.PageCount.Value, result.SelectedPages, analyzed.Length, PdfPageSamplingPolicy.Version);
        return new(
            result.OpenStatus,
            result.EncryptionStatus,
            result.PageCount,
            result.PdfVersion,
            result.Metadata,
            text,
            images,
            result.OutlinePresent,
            result.OutlineEntryCount,
            result.ActiveContent,
            pageAnalysis,
            RepeatedPageClusters(analyzed),
            result.Identifiers,
            classification.Classification,
            classification.Confidence,
            sampling,
            PdfClassificationPolicy.Version,
            ResourceProfileVersion,
            result.FactsIncomplete);
    }

    private static void AddContentFindings(List<AssessmentFinding> findings, PdfPageFacts[] pages)
    {
        int observable = pages.Count(page => page.IsContentful);
        int observableBps = Percentage(observable, Math.Max(1, pages.Length));
        int adjustment = observableBps >= 9_000 ? 8 : observableBps >= 5_000 ? 4 : 0;
        bool notObservable = observableBps <= 2_000;
        Add(findings, adjustment > 0 || !notObservable ? "PDF.CONTENT.OBSERVABLE" : "PDF.CONTENT.NOT_OBSERVABLE",
            adjustment > 0 ? FindingSeverity.Positive : notObservable ? FindingSeverity.Error : FindingSeverity.Information,
            adjustment > 0 ? adjustment : notObservable ? -8 : 0,
            adjustment > 0
                ? "Safely observable text, image, or graphics content is present on the sampled pages."
                : notObservable
                    ? "At least 80% of analyzed pages have no safely observable content."
                    : "Observable content is present on part of the sample, below the positive threshold.",
            PdfAssessment.TechnicalComponentId,
            Evidence(("observableBasisPoints", observableBps)));
    }

    private static void AddResourceFindings(List<AssessmentFinding> findings, PdfInspectionResult result, PdfPageFacts[] pages)
    {
        int heavy = pages.Count(page => page.ResourceHeavy);
        if (result.ResourceCountsWithinSoftLimits && heavy == 0)
        {
            Add(findings, "PDF.RESOURCE.COUNTS_WITHIN_LIMITS", FindingSeverity.Positive, 5,
                "Inspected PDF resources remained below V1 soft thresholds.", PdfAssessment.TechnicalComponentId);
        }
        else
        {
            int adjustment = Math.Max(-15, -5 * Math.Max(1, heavy));
            Add(findings, "PDF.RESOURCE.COUNT_UNUSUAL", FindingSeverity.Warning, adjustment,
                "One or more bounded resource groups exceeded a V1 soft threshold; the penalty is capped at -15.",
                PdfAssessment.TechnicalComponentId,
                Evidence(("rawCount", heavy), ("cap", -15)));
        }

        int unsupported = result.PageFacts.Count(page => page.FontOrFilterUnsupported);
        if (unsupported > 0)
        {
            Add(findings, "PDF.FILTER.UNSUPPORTED", FindingSeverity.Warning, Math.Max(-15, -5 * unsupported),
                "A font, filter, or optional structure was partly unsupported while required analysis remained comparable.",
                PdfAssessment.TechnicalComponentId,
                Evidence(("rawCount", unsupported), ("cap", -15)));
        }

        int nonEmbeddedFontPages = result.PageFacts.Count(page => page.Parsed && !page.FontEvidenceReliable);
        if (nonEmbeddedFontPages > 0)
        {
            Add(findings, "PDF.FONT.NON_EMBEDDED", FindingSeverity.Information, 0,
                "One or more sampled pages use non-embedded or unresolved fonts; host-derived text geometry is excluded from strong classification confidence.",
                PdfAssessment.TechnicalComponentId,
                Evidence(("pageCount", nonEmbeddedFontPages)));
        }
    }

    private static void AddOutlineFindings(List<AssessmentFinding> findings, PdfInspectionResult result) =>
        Add(findings, result.OutlinePresent ? "PDF.OUTLINE.PRESENT" : "PDF.OUTLINE.ABSENT",
            result.OutlinePresent ? FindingSeverity.Positive : FindingSeverity.Information,
            result.OutlinePresent ? 2 : 0,
            result.OutlinePresent ? "A bounded document outline is available." : "No document outline is available; absence is neutral.",
            PdfAssessment.TechnicalComponentId,
            Evidence(("entryCount", result.OutlineEntryCount)));

    private static void AddMetadataFindings(List<AssessmentFinding> findings, PdfInspectionResult result)
    {
        AddMetadataPresence(findings, "PDF.METADATA.TITLE", result.Metadata.Title, 4);
        AddMetadataPresence(findings, "PDF.METADATA.AUTHOR", result.Metadata.Author, 4);
        bool hasDate = result.Metadata.CreationDate.Status == PdfMetadataValueStatus.Present;
        Add(findings, hasDate ? "PDF.METADATA.DATE_PRESENT" : "PDF.METADATA.DATE_MISSING",
            hasDate ? FindingSeverity.Positive : FindingSeverity.Information,
            hasDate ? 2 : 0,
            hasDate ? "A parseable creation/publication-semantic date is present." : "No qualifying creation/publication-semantic date is present.",
            PdfAssessment.EmbeddedMetadataComponentId);
        bool hasIdentifier = result.Identifiers.Count > 0;
        Add(findings, "PDF.IDENTIFIER.ISBN_VALID",
            hasIdentifier ? FindingSeverity.Positive : FindingSeverity.Information,
            hasIdentifier ? 3 : 0,
            hasIdentifier ? "At least one bounded checksum-valid ISBN is present." : "No bounded checksum-valid ISBN was detected.",
            PdfAssessment.EmbeddedMetadataComponentId,
            Evidence(("count", result.Identifiers.Count)));
        if (result.InvalidIdentifierCandidateCount > 0)
        {
            Add(findings, "PDF.IDENTIFIER.INVALID_CHECKSUM_IGNORED", FindingSeverity.Information, 0,
                "One or more bounded identifier candidates failed checksum validation and were ignored.",
                PdfAssessment.EmbeddedMetadataComponentId,
                Evidence(("count", result.InvalidIdentifierCandidateCount)));
        }

        bool otherUseful = result.Metadata.Subject.Status == PdfMetadataValueStatus.Present
            || result.Metadata.Keywords.Status == PdfMetadataValueStatus.Present
            || result.Metadata.Creator.Status == PdfMetadataValueStatus.Present
            || result.Metadata.Producer.Status == PdfMetadataValueStatus.Present;
        Add(findings, "PDF.METADATA.OTHER_USEFUL",
            otherUseful ? FindingSeverity.Positive : FindingSeverity.Information,
            otherUseful ? 2 : 0,
            otherUseful ? "At least one other bounded useful metadata field is present." : "No other useful bounded metadata field is present.",
            PdfAssessment.EmbeddedMetadataComponentId);

        int malformed = MetadataValues(result.Metadata).Count(value => value.Status is PdfMetadataValueStatus.Malformed or PdfMetadataValueStatus.Truncated);
        if (malformed > 0)
        {
            Add(findings, "PDF.METADATA.MALFORMED", FindingSeverity.Warning, Math.Max(-3, -malformed),
                "Malformed or over-limit embedded metadata was ignored; the metadata penalty is capped at -3.",
                PdfAssessment.EmbeddedMetadataComponentId,
                Evidence(("rawCount", malformed), ("cap", -3)));
        }
    }

    private static void AddMetadataPresence(List<AssessmentFinding> findings, string prefix, PdfMetadataValue value, int positive)
    {
        bool present = value.Status == PdfMetadataValueStatus.Present;
        Add(findings, $"{prefix}_{(present ? "PRESENT" : "MISSING")}",
            present ? FindingSeverity.Positive : FindingSeverity.Information,
            present ? positive : 0,
            present ? $"A useful embedded {prefix.Split('.').Last().ToLowerInvariant()} is present." : "The optional embedded metadata field is missing.",
            PdfAssessment.EmbeddedMetadataComponentId);
    }

    private static void AddPageAnomalyFindings(List<AssessmentFinding> findings, PdfPageFacts[] pages, int totalPages)
    {
        int[] blanks = SuspiciousBlankPages(pages, totalPages);
        if (blanks.Length > 0)
        {
            Add(findings, "PDF.PAGE.SUSPICIOUS_BLANK", FindingSeverity.Warning, Math.Max(-8, -2 * blanks.Length),
                "Sampled pages meet the conservative suspicious-blank rule beyond the allowed edge pages.",
                PdfAssessment.TechnicalComponentId,
                PageEvidence(blanks, -8));
        }

        int[] unusual = pages.Where(page => Math.Max(page.WidthMilliPoints, page.HeightMilliPoints)
                > PdfInspectionLimits.V1.PageDimensionWarningPoints * 1000)
            .Select(page => page.PageNumber).ToArray();
        if (unusual.Length > 0)
        {
            Add(findings, "PDF.PAGE.DIMENSIONS_UNUSUAL", FindingSeverity.Warning, Math.Max(-9, -3 * unusual.Length),
                "One or more sampled pages have unusual but permitted dimensions.",
                PdfAssessment.TechnicalComponentId,
                PageEvidence(unusual, -9));
        }
    }

    private static void AddRepeatFindings(List<AssessmentFinding> findings, PdfPageFacts[] pages)
    {
        PdfRepeatedPageCluster[] clusters = RepeatedPageClusters(pages);
        int remainingCap = 12;
        foreach (IGrouping<PdfRepeatedPageEvidence, PdfRepeatedPageCluster> family in clusters.GroupBy(cluster => cluster.Evidence).OrderBy(group => group.Key))
        {
            int extras = family.Sum(cluster => cluster.PageNumbers.Count - 1);
            int perItem = family.Key == PdfRepeatedPageEvidence.LikelySharedImageContent ? -2 : -4;
            int applied = -Math.Min(remainingCap, extras * -perItem);
            remainingCap += applied;
            string rule = family.Key switch
            {
                PdfRepeatedPageEvidence.ExactSharedPageContent => "PDF.PAGE.REPEATED_CONTENT_EXACT",
                PdfRepeatedPageEvidence.ExactNormalizedText => "PDF.PAGE.REPEATED_TEXT_EXACT",
                PdfRepeatedPageEvidence.LikelySharedImageContent => "PDF.PAGE.REPEATED_LIKELY",
                _ => "PDF.PAGE.REPEAT_INSUFFICIENT",
            };
            Add(findings, rule, family.Key == PdfRepeatedPageEvidence.LikelySharedImageContent ? FindingSeverity.Warning : FindingSeverity.Error,
                applied,
                "Conservative repeated-page evidence was found; only extra pages and the strongest evidence contribute, with a combined -12 cap.",
                PdfAssessment.TechnicalComponentId,
                Evidence(("rawExtraPages", extras), ("appliedExtraPages", applied == 0 ? 0 : Math.Abs(applied / perItem)), ("combinedCap", -12)));
        }

        if (clusters.Length == 0)
        {
            Add(findings, "PDF.PAGE.REPEAT_INSUFFICIENT", FindingSeverity.Information, 0,
                "No conservative repeated-page evidence was sufficient for a penalty.", PdfAssessment.TechnicalComponentId);
        }
    }

    private static void AddInformationalFindings(
        List<AssessmentFinding> findings,
        PdfInspectionResult result,
        PdfPageFacts[] pages,
        PdfSamplingMode? samplingMode)
    {
        Add(findings, samplingMode == PdfSamplingMode.DeterministicSample ? "PDF.PAGE.SAMPLE_BOUNDED" : "PDF.PAGE.SAMPLE_ALL",
            FindingSeverity.Information, 0,
            samplingMode == PdfSamplingMode.DeterministicSample
                ? "The assessment uses the deterministic bounded page sample and does not imply whole-document certainty."
                : "Every page was requested for inspection.",
            PdfAssessment.TechnicalComponentId,
            Evidence(("sampleCount", result.SelectedPages.Count), ("totalPages", result.PageCount ?? 0)));
        Add(findings, pages.Any(page => page.UsefulCharacterCount >= 10) ? "PDF.TEXT.EXTRACTION_AVAILABLE" : "PDF.TEXT.EXTRACTION_UNAVAILABLE",
            FindingSeverity.Information, 0,
            pages.Any(page => page.UsefulCharacterCount >= 10)
                ? "Bounded extractable text metrics are available; no page text is retained."
                : "No useful extractable text was observed; absence alone does not reduce the score.",
            PdfAssessment.TechnicalComponentId);
        Add(findings, pages.Any(page => page.ImageCount > 0) ? "PDF.IMAGE.PRESENT" : "PDF.IMAGE.ABSENT",
            FindingSeverity.Information, 0,
            pages.Any(page => page.ImageCount > 0)
                ? "Bounded image-placement facts are present; images were not decoded."
                : "No image placement was observed in the inspected pages.",
            PdfAssessment.TechnicalComponentId);
        Add(findings, result.EncryptionStatus == PdfEncryptionStatus.NotEncrypted ? "PDF.ENCRYPTION.NONE" : "PDF.ENCRYPTION.EMPTY_PASSWORD_ACCESSIBLE",
            FindingSeverity.Information, 0,
            result.EncryptionStatus == PdfEncryptionStatus.NotEncrypted
                ? "No encryption dictionary was reported."
                : "The encrypted PDF was accessible only through the parser's empty-password path.",
            PdfAssessment.TechnicalComponentId);
        if (result.ActiveContent.JavaScriptOrActionMarkers > 0)
        {
            Add(findings, "PDF.ACTION.JAVASCRIPT_PRESENT", FindingSeverity.Information, 0,
                "Inert JavaScript or action markers were counted but never executed.", PdfAssessment.TechnicalComponentId,
                Evidence(("count", result.ActiveContent.JavaScriptOrActionMarkers)));
        }

        if (result.ActiveContent.ExternalReferenceMarkers > 0)
        {
            Add(findings, "PDF.ACTION.EXTERNAL_PRESENT", FindingSeverity.Information, 0,
                "External-reference markers were counted but never opened or resolved.", PdfAssessment.TechnicalComponentId,
                Evidence(("count", result.ActiveContent.ExternalReferenceMarkers)));
        }

        if (result.ActiveContent.EmbeddedFileMarkers > 0)
        {
            Add(findings, "PDF.ATTACHMENT.PRESENT", FindingSeverity.Information, 0,
                "Embedded-file markers were counted; no attachment name or bytes were accessed.", PdfAssessment.TechnicalComponentId,
                Evidence(("count", result.ActiveContent.EmbeddedFileMarkers)));
        }
    }

    private static PdfRepeatedPageCluster[] RepeatedPageClusters(PdfPageFacts[] pages)
    {
        HashSet<int> claimed = [];
        List<PdfRepeatedPageCluster> result = [];
        AddRepeatGroups(result, claimed, pages.Where(page => !page.CountersCapped && !string.IsNullOrWhiteSpace(page.SharedContentIdentity)),
            page => page.SharedContentIdentity!, PdfRepeatedPageEvidence.ExactSharedPageContent);
        AddRepeatGroups(result, claimed, pages.Where(page => !page.CountersCapped && page.TextFingerprintComplete
                && page.UsefulCharacterCount >= 200 && !string.IsNullOrWhiteSpace(page.NormalizedTextFingerprint)),
            page => $"{page.NormalizedTextFingerprint}:{page.WidthMilliPoints}:{page.HeightMilliPoints}", PdfRepeatedPageEvidence.ExactNormalizedText);
        AddRepeatGroups(result, claimed, pages.Where(page => !page.CountersCapped && !page.HasUsefulText
                && page.AggregateImageCoverageBasisPoints >= 7_000 && !string.IsNullOrWhiteSpace(page.SharedImageIdentity)),
            page => $"{page.SharedImageIdentity}:{page.WidthMilliPoints}:{page.HeightMilliPoints}", PdfRepeatedPageEvidence.LikelySharedImageContent);
        return result.OrderBy(cluster => cluster.Evidence).ThenBy(cluster => cluster.PageNumbers[0])
            .Take(PdfInspectionLimits.V1.MaximumRetainedExamples).ToArray();
    }

    private static void AddRepeatGroups(
        List<PdfRepeatedPageCluster> result,
        HashSet<int> claimed,
        IEnumerable<PdfPageFacts> candidates,
        Func<PdfPageFacts, string> keySelector,
        PdfRepeatedPageEvidence evidence)
    {
        foreach (IGrouping<string, PdfPageFacts> group in candidates.GroupBy(keySelector, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            int[] pages = group.Select(page => page.PageNumber).Where(page => !claimed.Contains(page)).Distinct().Order().ToArray();
            List<int> nonAdjacent = [];
            for (int index = 0; index < pages.Length; index++)
            {
                if (index == 0 || pages[index] - pages[index - 1] > 1)
                {
                    nonAdjacent.Add(pages[index]);
                }
            }

            if (nonAdjacent.Count < 2)
            {
                continue;
            }

            result.Add(new(evidence, nonAdjacent.Take(PdfInspectionLimits.V1.MaximumRetainedExamples)));
            foreach (int page in pages)
            {
                claimed.Add(page);
            }
        }
    }

    private static int[] SuspiciousBlankPages(PdfPageFacts[] pages, int totalPages)
    {
        int[] candidates = pages.Where(page => page.IsSuspiciousBlank).Select(page => page.PageNumber).Order().ToArray();
        if (pages.Length < 10)
        {
            return candidates.Length == pages.Length ? candidates : [];
        }

        HashSet<int> allowedEdges = totalPages >= 3 && pages.Any(page => page.IsContentful)
            ? [1, totalPages]
            : [];
        int[] beyondEdges = candidates.Where(page => !allowedEdges.Contains(page)).ToArray();
        int threshold = Math.Max(2, (int)Math.Ceiling(pages.Length * 0.05));
        return beyondEdges.Length > threshold ? beyondEdges : [];
    }

    private static void AddProblemFinding(
        List<AssessmentFinding> findings,
        PdfInspectionProblem problem,
        bool documentOpened)
    {
        bool recoverable = documentOpened && (problem.Code is PdfInspectionProblemCode.PageUnreadable
            or PdfInspectionProblemCode.MalformedFont
            or PdfInspectionProblemCode.MalformedMetadata
            or PdfInspectionProblemCode.MalformedOutline
            or PdfInspectionProblemCode.UnsupportedFilter);
        string rule = problem.Code switch
        {
            PdfInspectionProblemCode.MissingFile => "PDF.FILE.MISSING",
            PdfInspectionProblemCode.InaccessibleFile => "PDF.FILE.INACCESSIBLE",
            PdfInspectionProblemCode.UnsafePath => "PDF.FILE.UNSAFE_PATH",
            PdfInspectionProblemCode.ChangedFile => "PDF.FILE.CHANGED_DURING_INSPECTION",
            PdfInspectionProblemCode.ZeroLength => "PDF.FILE.ZERO_LENGTH",
            PdfInspectionProblemCode.InvalidSignature => "PDF.FILE.SIGNATURE_INVALID",
            PdfInspectionProblemCode.TruncatedFile => "PDF.FILE.TRUNCATED",
            PdfInspectionProblemCode.PasswordRequired => "PDF.ENCRYPTION.PASSWORD_REQUIRED",
            PdfInspectionProblemCode.UnsupportedEncryption => "PDF.ENCRYPTION.UNSUPPORTED",
            PdfInspectionProblemCode.ZeroPages => "PDF.PAGE.COUNT_ZERO",
            PdfInspectionProblemCode.PageLimitExceeded => "PDF.PAGE.COUNT_LIMIT",
            PdfInspectionProblemCode.DimensionLimitExceeded => "PDF.PAGE.DIMENSIONS_LIMIT",
            PdfInspectionProblemCode.StreamLimitExceeded => "PDF.STREAM.EXPANSION_LIMIT",
            PdfInspectionProblemCode.WorkerCrashed => "PDF.WORKER.CRASHED",
            PdfInspectionProblemCode.WorkerProtocolInvalid => "PDF.WORKER.PROTOCOL_INVALID",
            PdfInspectionProblemCode.ParserTimeout => "PDF.OPEN.TIMEOUT",
            PdfInspectionProblemCode.MalformedMetadata => "PDF.METADATA.MALFORMED",
            PdfInspectionProblemCode.MalformedOutline => "PDF.OUTLINE.MALFORMED",
            PdfInspectionProblemCode.MalformedFont => "PDF.FONT.MALFORMED",
            PdfInspectionProblemCode.UnsupportedFilter => "PDF.FILTER.UNSUPPORTED",
            _ when problem.Code.ToString().EndsWith("LimitExceeded", StringComparison.Ordinal) => "PDF.OPEN.RESOURCE_LIMIT",
            _ => "PDF.OPEN.MALFORMED",
        };
        Add(findings, rule, recoverable ? FindingSeverity.Warning : FindingSeverity.Disqualifying, 0,
            recoverable
                ? "A bounded optional PDF fact was unavailable; the condition is recorded without raw parser detail."
                : "A required PDF safety, file, parser, or structure condition failed; the PDF is not scored.",
            PdfAssessment.TechnicalComponentId,
            problem.PageNumber is null ? null : Evidence(("page", problem.PageNumber.Value)));
    }

    private static int CalculateScore(IEnumerable<AssessmentFinding> findings)
    {
        int technical = Math.Clamp(findings.Where(finding => finding.ScoreComponentId == PdfAssessment.TechnicalComponentId)
            .Sum(finding => finding.ScoreAdjustment), 0, 85);
        int metadata = Math.Clamp(findings.Where(finding => finding.ScoreComponentId == PdfAssessment.EmbeddedMetadataComponentId)
            .Sum(finding => finding.ScoreAdjustment), 0, 15);
        return technical + metadata;
    }

    private static string ClassificationRule(PdfDocumentClassification classification) => $"PDF.CLASSIFICATION.{classification switch
    {
        PdfDocumentClassification.DigitalText => "DIGITAL_TEXT",
        PdfDocumentClassification.ScannedWithoutOcr => "SCANNED_WITHOUT_OCR",
        PdfDocumentClassification.ScannedWithOcr => "SCANNED_WITH_OCR",
        PdfDocumentClassification.Mixed => "MIXED",
        PdfDocumentClassification.EmptyOrNearEmpty => "EMPTY_OR_NEAR_EMPTY",
        PdfDocumentClassification.Encrypted => "ENCRYPTED",
        PdfDocumentClassification.Unreadable => "UNREADABLE",
        _ => "UNKNOWN",
    }}";

    private static IEnumerable<PdfMetadataValue> MetadataValues(PdfDocumentMetadataSummary metadata)
    {
        yield return metadata.Title;
        yield return metadata.Author;
        yield return metadata.Subject;
        yield return metadata.Keywords;
        yield return metadata.Creator;
        yield return metadata.Producer;
        yield return metadata.CreationDate;
        yield return metadata.ModificationDate;
    }

    private static Dictionary<string, string> PageEvidence(int[] pages, int cap) => new(StringComparer.Ordinal)
    {
        ["rawCount"] = pages.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["pages"] = string.Join(',', pages.Take(PdfInspectionLimits.V1.MaximumRetainedExamples)),
        ["cap"] = cap.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static Dictionary<string, string> Evidence(params (string Key, int Value)[] values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);

    private static void Add(
        List<AssessmentFinding> findings,
        string ruleId,
        FindingSeverity severity,
        int adjustment,
        string explanation,
        AssessmentScoreComponentId component,
        IReadOnlyDictionary<string, string>? evidence = null) =>
        findings.Add(new(ruleId, severity, adjustment, explanation, evidence, component));

    private static int Percentage(int part, int whole) => whole <= 0 ? 0 : (int)((long)part * 10_000 / whole);
}
