using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Application.Plans;

internal static class CleanupPlanReviewConsistency
{
    public static bool IsReconstructedFromCurrentOverride(ReviewedConsolidationRecommendation reviewed)
    {
        if (reviewed.CurrentOverride is null) return false;
        RecommendationOverrideOutcome outcome = ApplyRecommendationOverrideUseCase.Execute(reviewed.Generated, reviewed.CurrentOverride);
        return outcome.IsSuccess && Equivalent(reviewed, outcome.Reviewed!);
    }

    public static bool MatchesProvenance(Domain.Plans.CleanupPlanProvenance provenance, ReviewedConsolidationRecommendation reviewed)
    {
        if (!IsReconstructedFromCurrentOverride(reviewed)
            || reviewed.Generated.GroupId != provenance.GroupId
            || reviewed.Generated.ModelVersion != provenance.RecommendationModelVersion
            || reviewed.Generated.InputVersion != provenance.RecommendationInputVersion
            || reviewed.ReviewStatus != provenance.ReviewStatus
            || reviewed.Freshness != provenance.Freshness
            || reviewed.EffectiveSelection?.MetadataSourceBookId != provenance.ReviewedMetadataSourceRecordId
            || reviewed.CurrentOverride!.RequestedStatus != provenance.UserOverride.RequestedStatus
            || reviewed.CurrentOverride.ReviewedAtUtc != provenance.UserOverride.ReviewedAtUtc
            || reviewed.CurrentOverride.MetadataSourceBookId != provenance.UserOverride.MetadataSourceRecordId)
            return false;

        string[] actions = reviewed.CurrentOverride.FormatOverrides
            .Select(value => $"{value.Format}:{value.Action}:{value.SourceBookId?.Value}")
            .Order(StringComparer.Ordinal).ToArray();
        if (!actions.SequenceEqual(provenance.UserOverride.FormatActions)
            || !reviewed.CurrentOverride.RetainedSeparateBookIds.SequenceEqual(provenance.UserOverride.RetainedSeparateRecordIds))
            return false;

        FormatSourceSelection[] effective = reviewed.EffectiveSelection?.FormatSelections
            .OrderBy(value => value.Format, StringComparer.Ordinal).ToArray() ?? [];
        Domain.Plans.CleanupPlanFormatSelectionProvenance[] stored = provenance.ReviewedFormatSelections.ToArray();
        return effective.Length == stored.Length && effective.Zip(stored).All(pair =>
            pair.First.Format == pair.Second.Format
            && pair.First.ResolutionStatus == pair.Second.ResolutionStatus
            && pair.First.ProposedSource?.BookId == pair.Second.SelectedRecordId
            && pair.First.Candidates.Select(value => value.BookId).Distinct().OrderBy(value => value.Value)
                .SequenceEqual(pair.Second.CandidateRecordIds)
            && pair.First.ReasonCodes.SequenceEqual(pair.Second.ReasonCodes)
            && pair.First.WarningCodes.SequenceEqual(pair.Second.WarningCodes));
    }

    private static bool Equivalent(ReviewedConsolidationRecommendation left, ReviewedConsolidationRecommendation right) =>
        ReferenceEquals(left.Generated, right.Generated)
        && OverrideEquivalent(left.CurrentOverride, right.CurrentOverride)
        && EffectiveEquivalent(left.EffectiveSelection, right.EffectiveSelection)
        && left.ReviewStatus == right.ReviewStatus
        && left.Freshness == right.Freshness
        && left.StaleOverride is null && right.StaleOverride is null;

    private static bool OverrideEquivalent(UserRecommendationOverride? left, UserRecommendationOverride? right) =>
        left is null ? right is null : right is not null
        && left.ModelVersion == right.ModelVersion && left.InputVersion == right.InputVersion
        && left.RequestedStatus == right.RequestedStatus && left.ReviewedAtUtc == right.ReviewedAtUtc
        && left.MetadataSourceBookId == right.MetadataSourceBookId
        && left.FormatOverrides.SequenceEqual(right.FormatOverrides)
        && left.RetainedSeparateBookIds.SequenceEqual(right.RetainedSeparateBookIds);

    private static bool EffectiveEquivalent(EffectiveRecommendationSelection? left, EffectiveRecommendationSelection? right)
    {
        if (left is null) return right is null;
        if (right is null || left.MetadataSourceBookId != right.MetadataSourceBookId
            || !left.RetainedSeparateBookIds.SequenceEqual(right.RetainedSeparateBookIds)
            || left.FormatSelections.Count != right.FormatSelections.Count)
            return false;
        return left.FormatSelections.Zip(right.FormatSelections).All(pair => SelectionEquivalent(pair.First, pair.Second));
    }

    private static bool SelectionEquivalent(FormatSourceSelection left, FormatSourceSelection right) =>
        left.Format == right.Format && left.ResolutionStatus == right.ResolutionStatus
        && left.Strength == right.Strength && ReferenceEquals(left.ProposedSource, right.ProposedSource)
        && left.Candidates.Count == right.Candidates.Count
        && left.Candidates.Zip(right.Candidates).All(pair => ReferenceEquals(pair.First, pair.Second))
        && left.ProposedExcludedAlternatives.SequenceEqual(right.ProposedExcludedAlternatives)
        && left.ReasonCodes.SequenceEqual(right.ReasonCodes)
        && left.WarningCodes.SequenceEqual(right.WarningCodes);
}
