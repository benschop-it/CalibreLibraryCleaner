using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class MetadataDuplicateGroupRowViewModel : ObservableObject
{
    private ReviewedConsolidationRecommendation? _reviewed;
    private RecommendationSourceOptionViewModel? _reviewedMetadataSource;

    public MetadataDuplicateGroupRowViewModel(
        ExactMetadataDuplicateGroup group,
        IReadOnlyDictionary<CalibreBookId, CalibreBook> books,
        ConsolidationRecommendation? recommendation = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(books);
        GroupId = group.Id;
        Recommendation = recommendation;
        NormalizedTitle = group.Identity.Title.Value;
        NormalizedAuthors = group.Identity.Authors.ToString();
        RecordCount = group.Members.Count;
        Category = group.MatchReason.Category;
        Reason = group.MatchReason.Description;
        Members = new ReadOnlyCollection<MetadataDuplicateMemberRowViewModel>(group.Members
            .Select(member =>
            {
                CalibreBook book = books[member];
                MetadataCandidateComparison? comparison = recommendation?.MetadataSource?.Comparisons
                    .FirstOrDefault(value => value.BookId == book.Id);
                return new MetadataDuplicateMemberRowViewModel(
                    book.Id.Value,
                    book.Title,
                    string.Join(" & ", book.Authors.Select(author => author.Name)),
                    book.AuthorSort,
                    string.Join(", ", book.Formats.Select(format => $"{format.Format} ({format.FileStatus})")),
                    string.Join(", ", book.Identifiers.Select(identifier => $"{identifier.Type}:{identifier.Value}")),
                    book.PublicationMetadata.Publisher ?? string.Empty,
                    book.PublicationMetadata.PublicationDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    string.Join(", ", book.PublicationMetadata.Languages),
                    book.PublicationMetadata.Series is null ? string.Empty : $"{book.PublicationMetadata.Series} {book.PublicationMetadata.SeriesIndex}",
                    book.PublicationMetadata.HasCover ? "Yes" : "No",
                    comparison is null
                        ? "Not ranked"
                        : $"Core usable: {comparison.Vector.CoreUsable}; catalog integrity: {comparison.Vector.CatalogIntegrity}; conflicts: {comparison.Vector.ConflictCount}; completeness: {comparison.Vector.CompletenessCount}; consistency: {comparison.Vector.GroupConsistencyCount}; valid strong identifiers: {comparison.Vector.ValidStrongIdentifierCount}");
            })
            .ToArray());
        MetadataSourceOptions = new ReadOnlyCollection<RecommendationSourceOptionViewModel>(group.Members
            .Select(id => new RecommendationSourceOptionViewModel(id.Value, $"Record {id.Value}"))
            .ToArray());
        SearchText = string.Join('\n', Members.SelectMany(member => new[] { member.BookId.ToString(System.Globalization.CultureInfo.InvariantCulture), member.Title, member.Authors }).Prepend(NormalizedAuthors).Prepend(NormalizedTitle));
        if (recommendation is not null)
        {
            SetReviewed(ApplyRecommendationOverrideUseCase.Reset(recommendation));
        }
    }

    public ExactMetadataDuplicateGroupId GroupId { get; }
    public ConsolidationRecommendation? Recommendation { get; }
    public string NormalizedTitle { get; }
    public string NormalizedAuthors { get; }
    public int RecordCount { get; }
    public string Category { get; }
    public string Reason { get; }
    public IReadOnlyList<MetadataDuplicateMemberRowViewModel> Members { get; }
    public IReadOnlyList<RecommendationSourceOptionViewModel> MetadataSourceOptions { get; }
    public IReadOnlyList<RecommendationFormatRowViewModel> FormatRows { get; private set; } = [];
    public IReadOnlyList<RecommendationReasonRowViewModel> ReasonRows { get; private set; } = [];
    public IReadOnlyList<RecommendationWarningRowViewModel> WarningRows { get; private set; } = [];
    public string Confidence => Recommendation?.Confidence.ToString() ?? "Not generated";
    public string ReviewStatus => _reviewed?.ReviewStatus.ToString() ?? "Active";
    public string Freshness => _reviewed?.Freshness.ToString() ?? "Current";
    public int WarningCount => Recommendation?.Warnings.Count ?? 0;
    public bool IsOverridden => _reviewed?.CurrentOverride is not null;
    public bool IsDeferred => _reviewed?.ReviewStatus == RecommendationReviewStatus.Deferred;
    public ReviewedConsolidationRecommendation? Reviewed => _reviewed;
    public string StaleOverrideSummary => DescribeStaleOverride(_reviewed?.StaleOverride);

    public RecommendationSourceOptionViewModel? ReviewedMetadataSource
    {
        get => _reviewedMetadataSource;
        set => SetProperty(ref _reviewedMetadataSource, value);
    }

    internal string SearchText { get; }

    public void SetReviewed(ReviewedConsolidationRecommendation reviewed)
    {
        _reviewed = reviewed;
        bool disablesConsolidation = reviewed.ReviewStatus is RecommendationReviewStatus.KeepSeparate or RecommendationReviewStatus.NotDuplicates;
        long? source = reviewed.EffectiveSelection?.MetadataSourceBookId?.Value
            ?? (disablesConsolidation ? null : reviewed.Generated.MetadataSource?.SelectedBookId.Value);
        _reviewedMetadataSource = MetadataSourceOptions.FirstOrDefault(value => value.BookId == source);
        IEnumerable<CalibreBookId> retainedSeparate = reviewed.EffectiveSelection?.RetainedSeparateBookIds
            ?? (reviewed.ReviewStatus == RecommendationReviewStatus.KeepSeparate
                ? reviewed.Generated.MemberIds
                : reviewed.ReviewStatus == RecommendationReviewStatus.NotDuplicates
                    ? []
                    : reviewed.Generated.RetainedSeparateRecords.Select(value => value.BookId));
        HashSet<long> separate = retainedSeparate.Select(value => value.Value).ToHashSet();
        foreach (MetadataDuplicateMemberRowViewModel member in Members) member.IsRetainedSeparate = separate.Contains(member.BookId);
        FormatRows = CreateFormats(reviewed);
        ReasonRows = reviewed.Generated.Reasons.Select(value => new RecommendationReasonRowViewModel(value.Code, Subject(value.SubjectKind, value.BookId, value.Format), value.Explanation, Evidence(value.Evidence))).ToArray();
        List<RecommendationWarningRowViewModel> warningRows = reviewed.Generated.Warnings
            .Select(value => new RecommendationWarningRowViewModel(value.Code, value.Severity.ToString(), Subject(value.SubjectKind, value.BookId, value.Format), value.Explanation, Evidence(value.Evidence)))
            .ToList();
        foreach (FormatSourceSelection format in reviewed.EffectiveSelection?.FormatSelections ?? [])
        {
            foreach (string code in format.WarningCodes.Where(code => code.StartsWith("USER.", StringComparison.Ordinal)))
            {
                warningRows.Add(new(code, "ManualReview", $"User / {format.Format}", code == "USER.EXPLICIT_FORMAT_EXCLUSION" ? "The user explicitly excluded this final format. Every original candidate remains recorded in the generated recommendation." : "The user marked this format unresolved; every candidate remains preserved.", string.Empty));
            }
        }
        if (reviewed.Freshness == RecommendationFreshness.Stale)
        {
            warningRows.Add(new("USER.STALE_OVERRIDE", "Blocking", "User review", "The previous override is stale and has no effective final selection until reset or reapplied.", string.Empty));
        }
        WarningRows = warningRows.ToArray();
        OnPropertyChanged(nameof(ReviewStatus)); OnPropertyChanged(nameof(Freshness)); OnPropertyChanged(nameof(IsOverridden)); OnPropertyChanged(nameof(IsDeferred)); OnPropertyChanged(nameof(StaleOverrideSummary)); OnPropertyChanged(nameof(ReviewedMetadataSource)); OnPropertyChanged(nameof(FormatRows)); OnPropertyChanged(nameof(ReasonRows)); OnPropertyChanged(nameof(WarningRows));
    }

    private static RecommendationFormatRowViewModel[] CreateFormats(ReviewedConsolidationRecommendation reviewed)
    {
        return reviewed.Generated.FormatSelections.Select(generated =>
        {
            FormatSourceSelection? effective = reviewed.EffectiveSelection?.FormatSelections.FirstOrDefault(value => value.Format == generated.Format);
            List<RecommendationSourceOptionViewModel> options = generated.Candidates
                .Where(value => value.FileStatus == FormatFileStatus.Present)
                .Select(value => new RecommendationSourceOptionViewModel(value.BookId.Value, $"Record {value.BookId.Value}: {value.ExpectedRelativePath}"))
                .ToList();
            options.Add(new(null, "Unresolved — preserve all", "MarkUnresolved"));
            options.Add(new(null, "Explicitly exclude final format", "ExcludeFinalFormat"));
            if (generated.ResolutionStatus == FormatResolutionStatus.Unavailable)
            {
                options.Add(new(null, "Unavailable — no present source", "Unavailable"));
            }

            bool disablesConsolidation = reviewed.ReviewStatus is RecommendationReviewStatus.KeepSeparate or RecommendationReviewStatus.NotDuplicates;
            RecommendationSourceOptionViewModel? selected = disablesConsolidation && reviewed.EffectiveSelection is null
                ? null
                : effective?.ResolutionStatus switch
                {
                    FormatResolutionStatus.UnresolvedConflict => options.First(value => value.Action == "MarkUnresolved"),
                    FormatResolutionStatus.ExplicitlyExcludedByUser => options.First(value => value.Action == "ExcludeFinalFormat"),
                    FormatResolutionStatus.Unavailable => options.First(value => value.Action == "Unavailable"),
                    _ => options.FirstOrDefault(value => value.BookId == (effective?.ProposedSource ?? generated.ProposedSource)?.BookId.Value),
                };
            string epub = string.Join("; ", generated.Candidates.Where(value => value.Assessment is not null).Select(value => $"Record {value.BookId.Value}: {value.Assessment!.Status}, score {value.Assessment.Score?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not scored"}, {value.Assessment.AnalyzerVersion}/{value.Assessment.ScoringModelVersion}"));
            return new RecommendationFormatRowViewModel(
                generated.Format,
                generated.ProposedSource is null ? "None — preserve all" : $"Record {generated.ProposedSource.BookId.Value}",
                generated.ResolutionStatus.ToString(),
                generated.Strength.ToString(),
                string.Join("; ", generated.Candidates.Select(value => $"Record {value.BookId.Value}: {value.FileStatus}, {value.ExpectedRelativePath}")),
                generated.ProposedExcludedAlternatives.Count > 0 ? "Byte-identical alternatives" : "No",
                epub,
                string.Join(", ", (effective?.WarningCodes ?? generated.WarningCodes)),
                options.ToArray(),
                selected);
        }).ToArray();
    }

    private static string Subject(RecommendationSubjectKind kind, CalibreBookId? id, string? format) => $"{kind}{(id is null ? string.Empty : $" / record {id.Value.Value}")}{(format is null ? string.Empty : $" / {format}")}";
    private static string Evidence(IReadOnlyDictionary<string, string> evidence) => string.Join("; ", evidence.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => $"{value.Key}={value.Value}"));

    private static string DescribeStaleOverride(UserRecommendationOverride? value)
    {
        if (value is null) return string.Empty;
        List<string> choices = [$"Previous stale review: {value.RequestedStatus}"];
        if (value.MetadataSourceBookId is not null) choices.Add($"metadata record {value.MetadataSourceBookId.Value.Value}");
        choices.AddRange(value.FormatOverrides.Select(format => format.SourceBookId is null
            ? $"{format.Format}: {format.Action}"
            : $"{format.Format}: {format.Action} record {format.SourceBookId.Value.Value}"));
        if (value.RetainedSeparateBookIds.Count > 0)
        {
            choices.Add($"retained separately: {string.Join(", ", value.RetainedSeparateBookIds.Select(id => id.Value))}");
        }

        return string.Join("; ", choices);
    }
}
