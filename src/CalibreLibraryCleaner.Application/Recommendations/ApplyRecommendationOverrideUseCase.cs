using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Application.Recommendations;

public static class ApplyRecommendationOverrideUseCase
{
    public static RecommendationOverrideOutcome Execute(
        ConsolidationRecommendation generated,
        UserRecommendationOverride proposedOverride)
    {
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(proposedOverride);
        List<RecommendationOverrideValidationError> errors = [];
        if (!Enum.IsDefined(proposedOverride.RequestedStatus))
        {
            errors.Add(new("REVIEW_STATUS_INVALID", "The requested review status is not supported."));
        }

        if (proposedOverride.ModelVersion != generated.ModelVersion
            || proposedOverride.InputVersion != generated.InputVersion)
        {
            errors.Add(new("STALE_OVERRIDE", "The override was created for different recommendation inputs or policy version."));
            return RecommendationOverrideOutcome.Failure(errors);
        }

        HashSet<CalibreBookId> members = generated.MemberIds.ToHashSet();
        if (proposedOverride.MetadataSourceBookId is not null && !members.Contains(proposedOverride.MetadataSourceBookId.Value))
        {
            errors.Add(new("METADATA_SOURCE_NOT_MEMBER", "The selected metadata source is not a current group member."));
        }

        foreach (CalibreBookId retained in proposedOverride.RetainedSeparateBookIds.Where(id => !members.Contains(id)))
        {
            errors.Add(new("RETAINED_RECORD_NOT_MEMBER", $"Record {retained.Value} is not a current group member."));
        }

        foreach (FormatRecommendationOverride formatOverride in proposedOverride.FormatOverrides)
        {
            if (string.IsNullOrWhiteSpace(formatOverride.Format))
            {
                errors.Add(new("FORMAT_REQUIRED", "Every format override requires a format."));
                continue;
            }

            if (!Enum.IsDefined(formatOverride.Action))
            {
                errors.Add(new("FORMAT_ACTION_INVALID", $"The {formatOverride.Format} override action is not supported."));
                continue;
            }

            FormatSourceSelection? generatedFormat = generated.FormatSelections.FirstOrDefault(value => value.Format == formatOverride.Format);
            if (generatedFormat is null)
            {
                errors.Add(new("FORMAT_NOT_REPRESENTED", $"Format {formatOverride.Format} is not represented in this recommendation."));
                continue;
            }

            if (formatOverride.Action == FormatOverrideAction.SelectSource)
            {
                RecommendationFormatCandidate? candidate = generatedFormat.Candidates.FirstOrDefault(value => value.BookId == formatOverride.SourceBookId);
                if (candidate is null || candidate.FileStatus != FormatFileStatus.Present)
                {
                    errors.Add(new("FORMAT_SOURCE_UNAVAILABLE", $"The selected {formatOverride.Format} source is not a present candidate."));
                }
            }
            else if (formatOverride.SourceBookId is not null)
            {
                errors.Add(new("FORMAT_ACTION_SOURCE_CONFLICT", $"The {formatOverride.Action} action cannot also specify a source record."));
            }
        }

        HashSet<CalibreBookId> generatedSeparate = generated.RetainedSeparateRecords.Select(value => value.BookId).ToHashSet();
        HashSet<CalibreBookId> retainedSeparate = generatedSeparate
            .Concat(proposedOverride.RetainedSeparateBookIds)
            .ToHashSet();
        CalibreBookId? metadataSource = proposedOverride.MetadataSourceBookId ?? generated.MetadataSource?.SelectedBookId;
        bool disablesConsolidation = proposedOverride.RequestedStatus is RecommendationReviewStatus.KeepSeparate or RecommendationReviewStatus.NotDuplicates;
        if (!disablesConsolidation && metadataSource is not null && retainedSeparate.Contains(metadataSource.Value))
        {
            errors.Add(new("RETAINED_METADATA_CONFLICT", "A retained-separate record cannot supply consolidated metadata."));
        }

        if (errors.Count > 0)
        {
            return RecommendationOverrideOutcome.Failure(errors);
        }

        List<FormatSourceSelection> formats = generated.FormatSelections.ToList();
        foreach (FormatRecommendationOverride formatOverride in proposedOverride.FormatOverrides)
        {
            int index = formats.FindIndex(value => value.Format == formatOverride.Format);
            FormatSourceSelection original = formats[index];
            formats[index] = formatOverride.Action switch
            {
                FormatOverrideAction.SelectSource => CreateSelectedOverride(original, formatOverride.SourceBookId!.Value),
                FormatOverrideAction.MarkUnresolved => new(
                    original.Format,
                    original.Candidates,
                    null,
                    FormatResolutionStatus.UnresolvedConflict,
                    [],
                    RecommendationDecisionStrength.Ambiguous,
                    [],
                    ["USER.MARKED_UNRESOLVED"]),
                FormatOverrideAction.ExcludeFinalFormat => new(
                    original.Format,
                    original.Candidates,
                    null,
                    FormatResolutionStatus.ExplicitlyExcludedByUser,
                    [],
                    RecommendationDecisionStrength.Ambiguous,
                    [],
                    ["USER.EXPLICIT_FORMAT_EXCLUSION"]),
                _ => throw new ArgumentOutOfRangeException(nameof(proposedOverride)),
            };
        }

        foreach (FormatSourceSelection format in formats.Where(value => !disablesConsolidation && value.ProposedSource is not null
                     && retainedSeparate.Contains(value.ProposedSource.BookId)))
        {
            errors.Add(new("RETAINED_FORMAT_CONFLICT", $"Retained-separate record {format.ProposedSource!.BookId.Value} cannot supply {format.Format}."));
        }

        if (errors.Count > 0)
        {
            return RecommendationOverrideOutcome.Failure(errors);
        }

        bool changed = metadataSource != generated.MetadataSource?.SelectedBookId
            || !retainedSeparate.SetEquals(generatedSeparate)
            || proposedOverride.FormatOverrides.Count > 0;
        if (proposedOverride.RequestedStatus == RecommendationReviewStatus.Accepted && changed)
        {
            return RecommendationOverrideOutcome.Failure([
                new("ACCEPTED_REQUIRES_GENERATED_SELECTION", "Accepted status requires the unchanged generated recommendation."),
            ]);
        }

        RecommendationReviewStatus status = proposedOverride.RequestedStatus switch
        {
            RecommendationReviewStatus.KeepSeparate or RecommendationReviewStatus.NotDuplicates or RecommendationReviewStatus.Deferred => proposedOverride.RequestedStatus,
            _ when changed => RecommendationReviewStatus.ManuallyAdjusted,
            RecommendationReviewStatus.Accepted => RecommendationReviewStatus.Accepted,
            _ => RecommendationReviewStatus.Unreviewed,
        };
        EffectiveRecommendationSelection? effective = status is RecommendationReviewStatus.KeepSeparate or RecommendationReviewStatus.NotDuplicates
            || retainedSeparate.SetEquals(members)
            ? null
            : new(
                metadataSource,
                formats,
                retainedSeparate.OrderBy(value => value.Value).ToArray());
        return RecommendationOverrideOutcome.Success(new(
            generated,
            proposedOverride,
            effective,
            status,
            RecommendationFreshness.Current));
    }

    public static ReviewedConsolidationRecommendation Reset(ConsolidationRecommendation generated)
    {
        ArgumentNullException.ThrowIfNull(generated);
        CalibreBookId[] retainedSeparate = generated.RetainedSeparateRecords.Select(value => value.BookId).ToArray();
        EffectiveRecommendationSelection? effective = retainedSeparate.Length == generated.MemberIds.Count
            ? null
            : new(
                generated.MetadataSource?.SelectedBookId,
                generated.FormatSelections,
                retainedSeparate);
        return new(
            generated,
            null,
            effective,
            RecommendationReviewStatus.Unreviewed,
            RecommendationFreshness.Current);
    }

    private static FormatSourceSelection CreateSelectedOverride(FormatSourceSelection original, CalibreBookId sourceBookId)
    {
        RecommendationFormatCandidate source = original.Candidates.Single(value => value.BookId == sourceBookId);
        return new(
            original.Format,
            original.Candidates,
            source,
            FormatResolutionStatus.Selected,
            [],
            RecommendationDecisionStrength.Ambiguous,
            ["USER.SELECTED_FORMAT_SOURCE"]);
    }
}

public sealed record RecommendationOverrideValidationError(string Code, string Message);

public sealed record RecommendationOverrideOutcome(
    ReviewedConsolidationRecommendation? Reviewed,
    IReadOnlyList<RecommendationOverrideValidationError> Errors)
{
    public bool IsSuccess => Reviewed is not null;
    public static RecommendationOverrideOutcome Success(ReviewedConsolidationRecommendation reviewed) => new(reviewed, []);
    public static RecommendationOverrideOutcome Failure(IReadOnlyList<RecommendationOverrideValidationError> errors) => new(null, errors);
}

public static class RecommendationReviewStalenessEvaluator
{
    public static ReviewedConsolidationRecommendation Reconcile(
        ConsolidationRecommendation current,
        ReviewedConsolidationRecommendation previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);
        UserRecommendationOverride? review = previous.CurrentOverride ?? previous.StaleOverride;
        if (review is null)
        {
            return ApplyRecommendationOverrideUseCase.Reset(current);
        }

        if (review.ModelVersion == current.ModelVersion && review.InputVersion == current.InputVersion)
        {
            RecommendationOverrideOutcome outcome = ApplyRecommendationOverrideUseCase.Execute(current, review);
            return outcome.IsSuccess ? outcome.Reviewed! : ApplyRecommendationOverrideUseCase.Reset(current);
        }

        return new(
            current,
            null,
            null,
            previous.ReviewStatus,
            RecommendationFreshness.Stale,
            review);
    }
}
