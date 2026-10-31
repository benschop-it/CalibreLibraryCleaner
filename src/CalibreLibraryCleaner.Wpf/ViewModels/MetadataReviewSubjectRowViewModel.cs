using System.Globalization;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public enum MetadataReviewFilterMode
{
    All,
    AppliedAutomatically,
    NeedsReview,
    Unavailable,
    AlreadyApplied,
}

public sealed class MetadataReviewSubjectRowViewModel : ObservableObject
{
    private readonly Func<MetadataReviewSubjectId, bool, Task> _persistApply;
    private ReviewedMetadataSubject _reviewed;
    private CalibreBook _currentBook;
    private bool _apply;

    public MetadataReviewSubjectRowViewModel(
        ReviewedMetadataSubject reviewed,
        CalibreBook currentBook,
        Func<MetadataReviewSubjectId, bool, Task> persistApply)
    {
        _reviewed = reviewed ?? throw new ArgumentNullException(nameof(reviewed));
        _currentBook = currentBook ?? throw new ArgumentNullException(nameof(currentBook));
        _persistApply = persistApply ?? throw new ArgumentNullException(nameof(persistApply));
        if (reviewed.Subject.TargetBookId != currentBook.Id)
            throw new ArgumentException("The metadata review current book does not match its target.");
        _apply = reviewed.Apply;
        PersistApplyCommand = new AsyncRelayCommand(
            () => _persistApply(SubjectId, Apply),
            () => CanApply);
    }

    public MetadataReviewSubjectId SubjectId => _reviewed.Subject.Id;
    public string SubjectKind => _reviewed.Subject.UnifiedGroupId is null
        ? "Singleton"
        : $"Unified ({_reviewed.Subject.Members.Count:N0})";
    public long TargetBookId => _reviewed.Subject.TargetBookId.Value;
    public string Confidence => IsAlreadyApplied
        ? "Already applied"
        : _reviewed.Subject.Proposal.Confidence.ToString();
    public bool IsAutomaticConfidence => _reviewed.Subject.Proposal.Confidence
        is FusedEditionMetadataConfidence.High or FusedEditionMetadataConfidence.Medium;
    public bool IsAppliedAutomatically => IsAutomaticConfidence && Apply
        && !IsOverride && !_reviewed.RequiresReview;
    public bool NeedsReview => !IsAlreadyApplied && (_reviewed.RequiresReview
        || _reviewed.Subject.Proposal.Confidence == FusedEditionMetadataConfidence.Low);
    public bool IsUnavailable => _reviewed.Subject.Proposal.Confidence == FusedEditionMetadataConfidence.Unavailable;
    public bool IsAlreadyApplied => _reviewed.IsAlreadyApplied;
    public bool CanApply => !IsUnavailable && !IsAlreadyApplied;
    public bool IsOverride => _reviewed.IsOverride;
    public string Reasons => string.Join(", ", _reviewed.Subject.Proposal.ReasonCodes.Concat(
        HasPseudonymAnnotations ? [EditionMetadataAuthorWritePolicy.PseudonymAnnotationWithheld] : Array.Empty<string>())
        .Concat(Candidate is { AuthorOrderKnown: false }
            ? [EditionMetadataAuthorWritePolicy.LegacyAuthorOrderUnavailable] : Array.Empty<string>()));
    public string ProviderOutcomes => FormatProviderOutcomes(_reviewed.Subject.Proposal);
    public string Provenance => FormatProvenance(_reviewed.Subject.Proposal);

    public bool Apply
    {
        get => _apply;
        set
        {
            if (!SetProperty(ref _apply, value)) return;
            OnPropertyChanged(nameof(IsAppliedAutomatically));
            OnPropertyChanged(nameof(NeedsReview));
            OnPropertyChanged(nameof(FinalTitle));
            OnPropertyChanged(nameof(FinalAuthors));
            OnPropertyChanged(nameof(FinalIdentifiers));
            OnPropertyChanged(nameof(FinalPublisher));
            OnPropertyChanged(nameof(FinalPublicationDate));
            OnPropertyChanged(nameof(FinalLanguages));
            OnPropertyChanged(nameof(FinalSeries));
            OnPropertyChanged(nameof(FinalCover));
        }
    }

    public IAsyncRelayCommand PersistApplyCommand { get; }

    public bool MatchesFilter(MetadataReviewFilterMode mode) => mode switch
    {
        MetadataReviewFilterMode.All => true,
        MetadataReviewFilterMode.AppliedAutomatically => IsAppliedAutomatically,
        MetadataReviewFilterMode.NeedsReview => NeedsReview,
        MetadataReviewFilterMode.Unavailable => IsUnavailable,
        MetadataReviewFilterMode.AlreadyApplied => IsAlreadyApplied,
        _ => false,
    };

    public string CurrentTitle => _currentBook.Title;
    public string CurrentAuthors => Join(_currentBook.Authors.Select(value => value.Name));
    public string CurrentIdentifiers => Join(_currentBook.Identifiers.Select(value => $"{value.Type}:{value.Value}"));
    public string CurrentPublisher => Value(_currentBook.PublicationMetadata.Publisher);
    public string CurrentPublicationDate => _currentBook.PublicationMetadata.PublicationDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        ?? "Unknown";
    public string CurrentLanguages => Join(_currentBook.PublicationMetadata.Languages);
    public string CurrentSeries => Series(
        _currentBook.PublicationMetadata.Series,
        _currentBook.PublicationMetadata.SeriesIndex);
    public string CurrentCover => _currentBook.PublicationMetadata.HasCover ? "Available" : "None";

    public string ProposedTitle => Value(Candidate?.Title);
    public string ProposedAuthors => Candidate is null ? "None"
        : EditionMetadataAuthorWritePolicy.TryPrepare(_currentBook, Candidate, out IReadOnlyList<string> prepared)
            ? Join(prepared) : "Unchanged (provider author field withheld)";
    public string ProviderAuthors => Join(Candidate?.Authors ?? []);
    public string AuthorWriteNotice => HasPseudonymAnnotations
        ? "Provider authors contain pseudonym annotations. The complete current author list will be retained, "
            + "even when Apply is checked; unrelated metadata can still be applied."
        : Candidate is { AuthorOrderKnown: false }
            ? EditionMetadataAuthorWritePolicy.TryPrepare(_currentBook, Candidate, out _)
                ? "Legacy cached coauthor order is unavailable. Author preparation preserves the corresponding current credit order."
                : "Legacy cached coauthor order is unavailable. The complete current author list will be retained; unrelated metadata can still be applied."
        : Candidate is not null && !EditionMetadataAuthorWritePolicy.TryPrepare(_currentBook, Candidate, out _)
            ? "Provider authors cannot be safely prepared. The complete current author list will be retained."
            : "Provider authors can be applied according to the current review choice.";
    public string ProposedIdentifiers => Join(Candidate?.Identifiers.Select(value => value.CanonicalValue) ?? []);
    public string ProposedPublisher => Value(Candidate?.Publisher);
    public string ProposedPublicationDate => Date(Candidate?.PublicationDate);
    public string ProposedLanguages => Join(Candidate?.Languages ?? []);
    public string ProposedSeries => Series(Candidate?.Series, Candidate?.SeriesIndex);
    public string ProposedCover => Candidate?.Cover is null
        ? "None"
        : $"Available ({Candidate.Cover.SourceId})";

    public string FinalTitle => MergePreview.Title;
    public string FinalAuthors => Join(MergePreview.Authors);
    public string FinalIdentifiers => Join(MergePreview.Identifiers);
    public string FinalPublisher => Value(MergePreview.Publisher);
    public string FinalPublicationDate => MergePreview.PublicationDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        ?? "Unknown";
    public string FinalLanguages => Join(MergePreview.Languages);
    public string FinalSeries => Series(MergePreview.Series, MergePreview.SeriesIndex);
    public string FinalCover => MergePreview.HasCover ? "Available" : "None";

    private EditionMetadataCandidate? Candidate => _reviewed.Subject.Proposal.Candidate;
    private bool HasPseudonymAnnotations => Candidate is not null
        && EditionMetadataAuthorWritePolicy.HasPseudonymAnnotations(Candidate.Authors);
    private EditionMetadataMergePreview MergePreview => EditionMetadataApplicationPolicy.Preview(
        _currentBook, Candidate, Apply);

    public void Update(ReviewedMetadataSubject reviewed, CalibreBook currentBook)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        ArgumentNullException.ThrowIfNull(currentBook);
        if (reviewed.Subject.Id != SubjectId || reviewed.Subject.TargetBookId != currentBook.Id)
            throw new ArgumentException("The metadata review row update is incompatible.");
        _reviewed = reviewed;
        _currentBook = currentBook;
        _apply = reviewed.Apply;
        OnPropertyChanged(string.Empty);
        PersistApplyCommand.NotifyCanExecuteChanged();
    }

    private static string FormatProvenance(FusedEditionMetadataProposal proposal)
    {
        if (proposal.Candidate is null || proposal.PrimaryProvider is null) return FormatProviderOutcomes(proposal);
        string primary = $"Primary: {proposal.PrimaryProvider.Id} {proposal.PrimaryProvider.Version} / {proposal.Candidate.EditionId}";
        string providers = string.Join("; ", proposal.ProviderProposals
            .Where(value => value.Candidate is not null)
            .Select(value => $"{value.Provider.Id} {value.Provider.Version} / {value.Candidate!.EditionId} / {value.RetrievedAtUtc:u}"
                + (value.CacheOrigin is { } origin
                    ? $" (cached source: {origin.Provider.Version}; {origin.QueryPolicyVersion}; {origin.ProposalPolicyVersion})"
                    : string.Empty))
            .Distinct(StringComparer.Ordinal)
            .Take(16));
        return providers.Length == 0 ? primary : primary + "; Sources: " + providers;
    }

    private static string FormatProviderOutcomes(FusedEditionMetadataProposal proposal)
    {
        string[] outcomes = proposal.ProviderProposals
            .GroupBy(value => value.Provider)
            .Select(group => group.OrderByDescending(value => value.Status == EditionMetadataProposalStatus.Proposed)
                .ThenByDescending(value => value.RetrievedAtUtc).First())
            .OrderBy(value => value.Provider.Id, StringComparer.Ordinal)
            .Select(value => $"{value.Provider.Id}: {Outcome(value)}")
            .ToArray();
        return outcomes.Length == 0 ? "No provider result" : string.Join("; ", outcomes);

        static string Outcome(EditionMetadataProposal value)
        {
            string category = value.Status switch
            {
                EditionMetadataProposalStatus.Proposed => "proposal available",
                EditionMetadataProposalStatus.Ambiguous => "ambiguous candidates",
                EditionMetadataProposalStatus.NotFound => "no validated match",
                EditionMetadataProposalStatus.Unavailable => "temporarily unavailable",
                _ => value.Status.ToString(),
            };
            if (value.ProblemCode is null) return category;
            string detail = value.ProblemCode switch
            {
                "GOOGLE_BOOKS.HTTP_429" => "HTTP 429 / quota or rate limit",
                "GOOGLE_BOOKS.HTTP_503" => "HTTP 503 / provider backend",
                "GOOGLE_BOOKS.NO_COMPATIBLE_VOLUMES" => "no compatible volume returned",
                "OPEN_LIBRARY.NO_COMPATIBLE_EDITIONS" => "no compatible edition returned",
                _ => value.ProblemCode,
            };
            return $"{category} ({detail}; {value.ProblemCode})";
        }
    }

    private static string Date(EditionPublicationDate? value) => value is null
        ? "Unknown"
        : value.Day is not null
            ? $"{value.Year:D4}-{value.Month!.Value:D2}-{value.Day.Value:D2}"
            : value.Month is not null
                ? $"{value.Year:D4}-{value.Month.Value:D2}"
                : value.Year.ToString("D4", CultureInfo.InvariantCulture);

    private static string Series(string? value, decimal? index) => string.IsNullOrWhiteSpace(value)
        ? "None"
        : index is null
            ? value
            : $"{value} #{index.Value.ToString(CultureInfo.InvariantCulture)}";

    private static string Join(IEnumerable<string> values)
    {
        string[] bounded = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).Take(32).ToArray();
        return bounded.Length == 0 ? "None" : string.Join(", ", bounded);
    }

    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
}
