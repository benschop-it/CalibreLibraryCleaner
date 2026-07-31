using CalibreLibraryCleaner.Domain.Assessments;

namespace CalibreLibraryCleaner.Application.Assessments.Pdf;

public sealed record PdfClassificationDecision(
    PdfDocumentClassification Classification,
    PdfClassificationConfidence Confidence,
    string Explanation,
    IReadOnlyDictionary<string, string> Evidence);

public sealed class PdfClassificationPolicy
{
    private readonly PdfPolicyVersion _version = Version;

    public static PdfPolicyVersion Version { get; } = new("pdf-classification/1.0.0");

    public PdfClassificationDecision Classify(PdfInspectionResult result, PdfSamplingMode? samplingMode)
    {
        ArgumentNullException.ThrowIfNull(result);
        PdfPageFacts[] analyzed = result.PageFacts.Where(page => page.Parsed).OrderBy(page => page.PageNumber).ToArray();
        int requested = result.SelectedPages.Count;
        int failed = Math.Max(0, requested - analyzed.Length);

        if (result.OpenStatus != PdfOpenStatus.Opened
            && result.EncryptionStatus is not (PdfEncryptionStatus.PasswordRequired or PdfEncryptionStatus.UnsupportedEncryption))
        {
            return Decision(PdfDocumentClassification.Unreadable, PdfClassificationConfidence.Insufficient,
                "Required PDF open or page-tree facts were unavailable.", analyzed, requested, 0);
        }

        if (result.EncryptionStatus is not (PdfEncryptionStatus.NotEncrypted or PdfEncryptionStatus.Unknown))
        {
            PdfClassificationConfidence encryptionConfidence = result.EncryptionStatus == PdfEncryptionStatus.EncryptedAccessibleWithEmptyPassword
                ? PdfClassificationConfidence.High
                : PdfClassificationConfidence.Insufficient;
            return Decision(PdfDocumentClassification.Encrypted, encryptionConfidence,
                "An encryption dictionary is present; classification is separate from score eligibility.", analyzed, requested, 10_000);
        }

        if (analyzed.Length == 0)
        {
            return Decision(PdfDocumentClassification.Unknown, PdfClassificationConfidence.Insufficient,
                "No sampled page produced sufficient bounded content facts.", analyzed, requested, 0);
        }

        int useful = analyzed.Count(page => page.HasUsefulText);
        int imageDominant = analyzed.Count(page => page.IsImageDominant);
        int imageDominantWithoutUseful = analyzed.Count(page => page.IsImageDominant && !page.HasUsefulText);
        int textDominant = analyzed.Count(page => page.IsTextDominant);
        int nearEmpty = analyzed.Count(page => page.UsefulCharacterCount < 10
            && page.AggregateImageCoverageBasisPoints < 500
            && page.NontrivialOperationCount < 10);
        int usefulInImageDominant = analyzed.Count(page => page.IsImageDominant && page.HasUsefulText);
        int noUsefulInImageDominant = analyzed.Count(page => page.IsImageDominant && !page.HasUsefulText);
        int usefulBps = Percentage(useful, analyzed.Length);
        int imageBps = Percentage(imageDominant, analyzed.Length);
        int imageWithoutTextBps = Percentage(imageDominantWithoutUseful, analyzed.Length);
        int textBps = Percentage(textDominant, analyzed.Length);
        int emptyBps = Percentage(nearEmpty, analyzed.Length);
        int usefulWithinImagesBps = Percentage(usefulInImageDominant, Math.Max(1, imageDominant));
        int noUsefulWithinImagesBps = Percentage(noUsefulInImageDominant, Math.Max(1, imageDominant));
        int medianImageCoverage = LowerMedian(analyzed.Where(page => page.IsImageDominant).Select(page => page.AggregateImageCoverageBasisPoints));
        int medianUsefulCharacters = LowerMedian(analyzed.Select(page => page.UsefulCharacterCount));
        bool geometryConsistent = HasConsistentImageGeometry(analyzed);

        PdfDocumentClassification classification;
        string explanation;
        int margin;
        if (emptyBps >= 8_000)
        {
            classification = PdfDocumentClassification.EmptyOrNearEmpty;
            explanation = "At least 80% of analyzed pages had no safely observable text, image, or graphics content.";
            margin = emptyBps - 8_000;
        }
        else if (analyzed.Length >= 5
                 && (textBps >= 2_000 && imageWithoutTextBps >= 2_000
                     || usefulWithinImagesBps >= 2_000 && noUsefulWithinImagesBps >= 2_000))
        {
            classification = PdfDocumentClassification.Mixed;
            explanation = "The sample contains material text-dominant and image-dominant/no-useful-text cohorts.";
            margin = Math.Min(
                Math.Max(Math.Min(textBps, imageWithoutTextBps) - 2_000, 0),
                Math.Max(Math.Min(usefulWithinImagesBps, noUsefulWithinImagesBps) - 2_000, 0));
        }
        else if (analyzed.Length >= 5 && imageBps >= 8_000 && usefulBps >= 8_000
                 && medianImageCoverage >= 8_500 && geometryConsistent)
        {
            classification = PdfDocumentClassification.ScannedWithOcr;
            explanation = "Pages are scan-like and contain an existing text layer; this does not prove OCR provenance and no OCR was run.";
            margin = Math.Min(Math.Min(imageBps - 8_000, usefulBps - 8_000), medianImageCoverage - 8_500);
        }
        else if (analyzed.Length >= 5 && imageBps >= 8_000 && usefulBps < 1_000
                 && medianUsefulCharacters < 20 && geometryConsistent)
        {
            classification = PdfDocumentClassification.ScannedWithoutOcr;
            explanation = "Pages are consistently scan-like and contain little safely extractable text; no OCR was attempted.";
            margin = Math.Min(imageBps - 8_000, 1_000 - usefulBps);
        }
        else if (usefulBps >= 8_000 && imageBps < 5_000)
        {
            classification = PdfDocumentClassification.DigitalText;
            explanation = "Useful extractable text is present on at least 80% of analyzed pages without scan-like image dominance.";
            margin = Math.Min(usefulBps - 8_000, 5_000 - imageBps);
        }
        else
        {
            classification = PdfDocumentClassification.Unknown;
            explanation = "Bounded evidence is insufficient or ambiguous; image-heavy and illustrated content is not assumed to be a defective scan.";
            margin = 0;
        }

        PdfClassificationConfidence confidence = Confidence(
            classification,
            analyzed,
            requested,
            failed,
            result.FactsIncomplete,
            samplingMode,
            margin);
        PdfClassificationDecision decision = Decision(classification, confidence, explanation, analyzed, requested, margin);
        Dictionary<string, string> evidence = new(decision.Evidence, StringComparer.Ordinal)
        {
            ["classificationPolicy"] = _version.Value,
        };
        return decision with { Evidence = evidence };
    }

    private static PdfClassificationConfidence Confidence(
        PdfDocumentClassification classification,
        PdfPageFacts[] analyzed,
        int requested,
        int failed,
        bool incomplete,
        PdfSamplingMode? samplingMode,
        int marginBasisPoints)
    {
        if ((analyzed.Length < 3 && requested >= 3)
            || Percentage(failed, Math.Max(1, requested)) > 2_000
            || incomplete
            || analyzed.Any(page => page.CountersCapped))
        {
            return PdfClassificationConfidence.Insufficient;
        }

        if (analyzed.Any(page => !page.FontEvidenceReliable || page.HiddenTextPresent))
        {
            return PdfClassificationConfidence.Low;
        }

        if (classification == PdfDocumentClassification.Unknown || analyzed.Length < 5 || marginBasisPoints < 500)
        {
            return PdfClassificationConfidence.Low;
        }

        bool sampled = samplingMode == PdfSamplingMode.DeterministicSample;
        if (!sampled && analyzed.Length >= 20 && marginBasisPoints >= 1_500
            && classification != PdfDocumentClassification.ScannedWithOcr)
        {
            return PdfClassificationConfidence.High;
        }

        if ((!sampled && analyzed.Length >= 5 && marginBasisPoints >= 1_000)
            || sampled && marginBasisPoints >= 1_500)
        {
            return PdfClassificationConfidence.Medium;
        }

        return PdfClassificationConfidence.Low;
    }

    private static PdfClassificationDecision Decision(
        PdfDocumentClassification classification,
        PdfClassificationConfidence confidence,
        string explanation,
        PdfPageFacts[] analyzed,
        int requested,
        int margin) => new(
        classification,
        confidence,
        explanation,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["analyzedPages"] = analyzed.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["requestedPages"] = requested.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["thresholdMarginBasisPoints"] = margin.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["fontEvidenceReliable"] = analyzed.All(page => page.FontEvidenceReliable).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["hiddenTextPresent"] = analyzed.Any(page => page.HiddenTextPresent).ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

    private static bool HasConsistentImageGeometry(PdfPageFacts[] analyzed)
    {
        PdfPageFacts[] pages = analyzed.Where(page => page.IsImageDominant
            && page.WidthMilliPoints > 0
            && page.HeightMilliPoints > 0
            && !string.IsNullOrWhiteSpace(page.DominantImageGeometryKey)).ToArray();
        if (pages.Length == 0)
        {
            return false;
        }

        int[] aspectRatios = pages.Select(page => (int)Math.Min(int.MaxValue,
            (long)Math.Max(page.WidthMilliPoints, page.HeightMilliPoints) * 10_000
            / Math.Min(page.WidthMilliPoints, page.HeightMilliPoints))).Order().ToArray();
        int median = aspectRatios[(aspectRatios.Length - 1) / 2];
        string geometry = pages.GroupBy(page => page.DominantImageGeometryKey, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .First().Key!;
        int consistent = pages.Count(page =>
        {
            int ratio = (int)Math.Min(int.MaxValue,
                (long)Math.Max(page.WidthMilliPoints, page.HeightMilliPoints) * 10_000
                / Math.Min(page.WidthMilliPoints, page.HeightMilliPoints));
            return Math.Abs((long)ratio - median) * 100 <= median
                && string.Equals(page.DominantImageGeometryKey, geometry, StringComparison.Ordinal);
        });
        return Percentage(consistent, pages.Length) >= 8_000;
    }

    private static int LowerMedian(IEnumerable<int> values)
    {
        int[] ordered = values.Order().ToArray();
        return ordered.Length == 0 ? 0 : ordered[(ordered.Length - 1) / 2];
    }

    private static int Percentage(int part, int whole) => whole <= 0 ? 0 : (int)((long)part * 10_000 / whole);
}
