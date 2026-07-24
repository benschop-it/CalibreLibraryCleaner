using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Recommendations;

public sealed record MetadataQualityVector(
    bool CoreUsable,
    bool CatalogIntegrity,
    int ConflictCount,
    int CompletenessCount,
    int GroupConsistencyCount,
    int ValidStrongIdentifierCount);

public sealed record MetadataCandidateComparison(CalibreBookId BookId, MetadataQualityVector Vector);

public sealed record MetadataSourceSelection
{
    public MetadataSourceSelection(
        CalibreBookId selectedBookId,
        IEnumerable<MetadataCandidateComparison> comparisons,
        RecommendationDecisionStrength strength,
        IEnumerable<string> reasonCodes)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        ArgumentNullException.ThrowIfNull(reasonCodes);
        MetadataCandidateComparison[] ordered = comparisons.OrderBy(value => value.BookId.Value).ToArray();
        string[] reasons = reasonCodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!ordered.Any(value => value.BookId == selectedBookId) || reasons.Length == 0)
        {
            throw new ArgumentException("The selected metadata source requires a candidate and linked reason.");
        }

        SelectedBookId = selectedBookId;
        Comparisons = new ReadOnlyCollection<MetadataCandidateComparison>(ordered);
        Strength = strength;
        ReasonCodes = new ReadOnlyCollection<string>(reasons);
    }

    public CalibreBookId SelectedBookId { get; }
    public IReadOnlyList<MetadataCandidateComparison> Comparisons { get; }
    public RecommendationDecisionStrength Strength { get; }
    public IReadOnlyList<string> ReasonCodes { get; }
}

public sealed record RecommendationFormatCandidate
{
    public RecommendationFormatCandidate(
        CalibreBookId bookId,
        string format,
        string expectedRelativePath,
        FormatFileStatus fileStatus,
        FormatFileFingerprint? fingerprint,
        FormatAssessment? assessment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentNullException.ThrowIfNull(expectedRelativePath);
        string canonicalFormat = format.ToUpperInvariant();
        if ((fileStatus == FormatFileStatus.Present) != (fingerprint is not null))
        {
            throw new ArgumentException("Only a present format candidate has a fingerprint.", nameof(fingerprint));
        }

        if (assessment is not null && (assessment.CalibreBookId != bookId
            || !string.Equals(assessment.Format, canonicalFormat, StringComparison.Ordinal)
            || !string.Equals(NormalizePath(assessment.ExpectedRelativePath), NormalizePath(expectedRelativePath), StringComparison.Ordinal)))
        {
            throw new ArgumentException("The assessment association does not match the format candidate.", nameof(assessment));
        }

        BookId = bookId;
        Format = canonicalFormat;
        ExpectedRelativePath = expectedRelativePath;
        FileStatus = fileStatus;
        Fingerprint = fingerprint;
        Assessment = assessment;
    }

    public CalibreBookId BookId { get; }
    public string Format { get; }
    public string ExpectedRelativePath { get; }
    public FormatFileStatus FileStatus { get; }
    public FormatFileFingerprint? Fingerprint { get; }
    public FormatAssessment? Assessment { get; }

    private static string NormalizePath(string value) => value.Replace('\\', '/');
}

public sealed record ProposedFormatAlternative(
    CalibreBookId BookId,
    string ExpectedRelativePath,
    string ReasonCode);

public sealed record FormatSourceSelection
{
    public FormatSourceSelection(
        string format,
        IEnumerable<RecommendationFormatCandidate> candidates,
        RecommendationFormatCandidate? proposedSource,
        FormatResolutionStatus resolutionStatus,
        IEnumerable<ProposedFormatAlternative>? proposedExcludedAlternatives,
        RecommendationDecisionStrength strength,
        IEnumerable<string> reasonCodes,
        IEnumerable<string>? warningCodes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(reasonCodes);
        string canonicalFormat = format.ToUpperInvariant();
        RecommendationFormatCandidate[] ordered = candidates
            .OrderBy(candidate => candidate.BookId.Value)
            .ThenBy(candidate => candidate.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0 || ordered.Any(candidate => !string.Equals(candidate.Format, canonicalFormat, StringComparison.Ordinal)))
        {
            throw new ArgumentException("A format selection requires candidates for one canonical format.", nameof(candidates));
        }

        if (proposedSource is not null && !ordered.Contains(proposedSource))
        {
            throw new ArgumentException("The proposed source must be one of the format candidates.", nameof(proposedSource));
        }

        string[] reasons = reasonCodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] warnings = (warningCodes ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (resolutionStatus == FormatResolutionStatus.Selected && (proposedSource is null || reasons.Length == 0))
        {
            throw new ArgumentException("A selected format requires a source and linked reason.");
        }

        if (resolutionStatus == FormatResolutionStatus.UnresolvedConflict && warnings.Length == 0)
        {
            throw new ArgumentException("An unresolved format requires a linked warning.");
        }

        if (resolutionStatus != FormatResolutionStatus.Selected && proposedSource is not null)
        {
            throw new ArgumentException("Only a selected format can have a source.");
        }

        ProposedFormatAlternative[] exclusions = (proposedExcludedAlternatives ?? [])
            .OrderBy(value => value.BookId.Value)
            .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray();
        if (exclusions.Distinct().Count() != exclusions.Length
            || exclusions.Any(exclusion => !ordered.Any(candidate => candidate.BookId == exclusion.BookId
                && string.Equals(candidate.ExpectedRelativePath, exclusion.ExpectedRelativePath, StringComparison.Ordinal)))
            || exclusions.Any(exclusion => !reasons.Contains(exclusion.ReasonCode, StringComparer.Ordinal))
            || proposedSource is not null && exclusions.Any(exclusion => exclusion.BookId == proposedSource.BookId
                && string.Equals(exclusion.ExpectedRelativePath, proposedSource.ExpectedRelativePath, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Proposed exclusions must be unique non-source candidates.", nameof(proposedExcludedAlternatives));
        }

        if (exclusions.Any(value => value.ReasonCode == "FORMAT.EXACT_BINARY_EQUIVALENT")
            && (resolutionStatus != FormatResolutionStatus.Selected
                || proposedSource?.Fingerprint is null
                || exclusions.Any(exclusion => ordered.Single(candidate => candidate.BookId == exclusion.BookId
                        && string.Equals(candidate.ExpectedRelativePath, exclusion.ExpectedRelativePath, StringComparison.Ordinal)).Fingerprint
                    != proposedSource.Fingerprint)))
        {
            throw new ArgumentException("Exact-binary exclusions must match the selected source fingerprint.", nameof(proposedExcludedAlternatives));
        }

        Format = canonicalFormat;
        Candidates = new ReadOnlyCollection<RecommendationFormatCandidate>(ordered);
        ProposedSource = proposedSource;
        ResolutionStatus = resolutionStatus;
        ProposedExcludedAlternatives = new ReadOnlyCollection<ProposedFormatAlternative>(exclusions);
        Strength = strength;
        ReasonCodes = new ReadOnlyCollection<string>(reasons);
        WarningCodes = new ReadOnlyCollection<string>(warnings);
    }

    public string Format { get; }
    public IReadOnlyList<RecommendationFormatCandidate> Candidates { get; }
    public RecommendationFormatCandidate? ProposedSource { get; }
    public FormatResolutionStatus ResolutionStatus { get; }
    public IReadOnlyList<ProposedFormatAlternative> ProposedExcludedAlternatives { get; }
    public RecommendationDecisionStrength Strength { get; }
    public IReadOnlyList<string> ReasonCodes { get; }
    public IReadOnlyList<string> WarningCodes { get; }
}

public sealed record RecordRecommendation(
    CalibreBookId BookId,
    RecordRecommendationKind Kind,
    RecommendationDecisionStrength Strength,
    IReadOnlyList<string> ReasonCodes);
