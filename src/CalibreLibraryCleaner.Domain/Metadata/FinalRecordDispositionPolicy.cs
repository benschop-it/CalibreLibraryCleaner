using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Duplicates;

namespace CalibreLibraryCleaner.Domain.Metadata;

public enum FinalRecordDispositionLabelSource
{
    Title,
    Author,
    Identifier,
    ManagedLayout,
}

public static class FinalRecordDispositionReasonCodes
{
    public const string AuthorUnresolved = "RECORD.AUTHOR_UNRESOLVED";
    public const string AuthorConflicting = "RECORD.AUTHOR_CONFLICTING";
    public const string AuthorSuspectExhausted = "RECORD.AUTHOR_SUSPECT_EXHAUSTED";
    public const string PlaceholderLabel = "RECORD.PLACEHOLDER_LABEL";
}

public sealed record FinalRecordDispositionLabel
{
    public FinalRecordDispositionLabel(FinalRecordDispositionLabelSource source, string value)
    {
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string retained = value.Trim();
        if (retained.Length > 512 || retained.Any(char.IsControl))
            throw new ArgumentException("Final record disposition label is invalid.", nameof(value));
        Source = source;
        Value = retained;
    }

    public FinalRecordDispositionLabelSource Source { get; }
    public string Value { get; }
}

public sealed record FinalRecordDispositionAssessment
{
    public FinalRecordDispositionAssessment(
        IEnumerable<string> reasonCodes,
        IEnumerable<FinalRecordDispositionLabel> supportingLabels,
        FinalRecordQualification? qualification = null,
        FinalRecordQualificationEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(reasonCodes);
        ArgumentNullException.ThrowIfNull(supportingLabels);
        string[] reasons = reasonCodes.Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        FinalRecordDispositionLabel[] labels = supportingLabels
            .Distinct()
            .OrderBy(value => value.Source)
            .ThenBy(value => value.Value, StringComparer.Ordinal)
            .ToArray();
        if (reasons.Any(value => string.IsNullOrWhiteSpace(value)
                || value.Length > 128
                || value.Any(char.IsControl))
            || labels.Length > 100
            || labels.Length > 0 && !reasons.Contains(
                FinalRecordDispositionReasonCodes.PlaceholderLabel,
                StringComparer.Ordinal))
            throw new ArgumentException("Final record disposition assessment is invalid.");
        ReasonCodes = new ReadOnlyCollection<string>(reasons);
        SupportingLabels = new ReadOnlyCollection<FinalRecordDispositionLabel>(labels);
        Qualification = qualification ?? (reasons.Length > 0
            ? FinalRecordQualification.ProposedUnresolvable
            : FinalRecordQualification.NeedsReview);
        if (!Enum.IsDefined(Qualification))
            throw new ArgumentOutOfRangeException(nameof(qualification));
        Evidence = evidence;
    }

    public bool ProposedForQuarantine => Qualification == FinalRecordQualification.ProposedUnresolvable;
    public FinalRecordQualification Qualification { get; }
    public FinalRecordQualificationEvidence? Evidence { get; }
    public IReadOnlyList<string> ReasonCodes { get; }
    public IReadOnlyList<FinalRecordDispositionLabel> SupportingLabels { get; }
}

public static class FinalRecordDispositionPolicy
{
    public const string PolicyVersion = "final-record-disposition/2.0.0";

    private static readonly HashSet<string> PlaceholderLabels = new(StringComparer.Ordinal)
    {
        "UNKNOWN",
        "UNKOWN",
        "USER",
    };

    private static readonly HashSet<string> StrongAuthorReasons = new(StringComparer.Ordinal)
    {
        AuthorSuspicionReasonCodes.Missing,
        AuthorSuspicionReasonCodes.Placeholder,
        AuthorSuspicionReasonCodes.ParenthesizedNumber,
        AuthorSuspicionReasonCodes.NumberPrefixed,
        AuthorSuspicionReasonCodes.MatchesTitle,
        AuthorSuspicionReasonCodes.ResemblesTitle,
        AuthorSuspicionReasonCodes.MalformedSeparator,
    };

    public static FinalRecordDispositionAssessment Assess(
        AuthorResolutionStatus authorStatus,
        IEnumerable<string> authorReasonCodes,
        bool inspectionExhausted,
        IEnumerable<FinalRecordDispositionLabel> labels)
    {
        if (!Enum.IsDefined(authorStatus)) throw new ArgumentOutOfRangeException(nameof(authorStatus));
        ArgumentNullException.ThrowIfNull(authorReasonCodes);
        ArgumentNullException.ThrowIfNull(labels);
        string[] authorReasons = authorReasonCodes.ToArray();
        FinalRecordDispositionLabel[] retainedLabels = labels.ToArray();
        if (authorReasons.Length > 100
            || authorReasons.Any(value => string.IsNullOrWhiteSpace(value)
                || value.Length > 128
                || value.Any(char.IsControl))
            || retainedLabels.Length > 100)
            throw new ArgumentException("Final record disposition input exceeds its retained bounds.");

        HashSet<string> reasons = new(StringComparer.Ordinal);
        if (authorStatus == AuthorResolutionStatus.Unresolved)
            reasons.Add(FinalRecordDispositionReasonCodes.AuthorUnresolved);
        if (authorStatus == AuthorResolutionStatus.Conflicting)
            reasons.Add(FinalRecordDispositionReasonCodes.AuthorConflicting);
        if (authorStatus == AuthorResolutionStatus.Suspect
            && inspectionExhausted
            && authorReasons.Any(StrongAuthorReasons.Contains))
            reasons.Add(FinalRecordDispositionReasonCodes.AuthorSuspectExhausted);

        FinalRecordDispositionLabel[] placeholderEvidence = retainedLabels
            .Where(IsPlaceholder)
            .ToArray();
        if (placeholderEvidence.Length > 0)
            reasons.Add(FinalRecordDispositionReasonCodes.PlaceholderLabel);
        return new(reasons, placeholderEvidence, FinalRecordQualification.NeedsReview);
    }

    private static bool IsPlaceholder(FinalRecordDispositionLabel label) =>
        MetadataTextNormalizer.TryNormalizeTitle(label.Value, out NormalizedTitle? normalized)
        && PlaceholderLabels.Contains(normalized!.Value);
}
