using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class QuarantineReviewSubjectRowViewModel : ObservableObject
{
    private readonly Func<CalibreBookId, bool, Task> _persistInclude;
    private QuarantineReviewSubject _subject;
    private bool _include;

    public QuarantineReviewSubjectRowViewModel(
        QuarantineReviewSubject subject,
        Func<CalibreBookId, bool, Task> persistInclude)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _persistInclude = persistInclude ?? throw new ArgumentNullException(nameof(persistInclude));
        _include = subject.Include;
        PersistIncludeCommand = new AsyncRelayCommand(
            () => _persistInclude(_subject.BookId, Include));
    }

    public CalibreBookId BookId => _subject.BookId;
    public long RecordId => BookId.Value;
    public string CurrentTitle => _subject.CurrentTitle;
    public string CurrentAuthors => Join(_subject.CurrentAuthors.Select(value => value.Name));
    public string CurrentSorts => Join(_subject.CurrentAuthors.Select(value => value.SortName));
    public string Formats => Join(_subject.Formats.Select(value => value.Format));
    public string Reasons => Join(_subject.Disposition.ReasonCodes.Concat(
        _subject.MetadataProblemReasonCodes).Distinct(StringComparer.Ordinal));
    public string Provenance => Join(_subject.Disposition.SupportingLabels.Select(value =>
        $"{value.Source}: {value.Value}").Concat(_subject.IdentityCandidates.Select(value =>
        $"Unit {value.UnitNumber}, lines {value.TitleLineNumber}/{value.AuthorLineNumber}: "
        + $"{value.CompleteTitle} / {string.Join(", ", value.Authors)}"
        + (value.ExplicitCredit ? " (explicit title/byline)" : " (uncertain pairing)"))));
    public FinalRecordQualification Qualification => _subject.Qualification;
    public string Coverage => Join(_subject.Disposition.Evidence?.Routes.Select(value =>
        $"{value.Name}: {value.Outcome}") ?? []);
    public string ContentStatus => _subject.Disposition.Evidence?.Content.ToString() ?? "Incomplete";
    public string ManagedLayoutStatus => _subject.Disposition.Evidence?.ManagedLayoutVerified == true
        ? "Verified by the final naming worker" : "Not verified; final managed naming is required";
    public bool IsProposed => _subject.Disposition.ProposedForQuarantine;
    public bool IsManual => _subject.ManuallyIncluded;
    public bool CanAcceptReviewed => _subject.CanAcceptReviewed;
    private bool IsReviewedAndAccepted => Qualification == FinalRecordQualification.Qualified
        && _subject.Disposition.Evidence?.ReviewerAccepted == true;
    public bool HasMetadataProblem => _subject.MetadataProblemReasonCodes.Count > 0;
    public bool HasDetectedProblem => !IsReviewedAndAccepted && (HasMetadataProblem
        || _subject.Disposition.Evidence?.Content == FinalRecordContentStatus.ConfirmedUnusable);
    public bool IsVisible(bool showAll) => showAll || HasDetectedProblem || Include;
    public string ProblemSummary => IsReviewedAndAccepted ? "Accepted by you after inspection."
        : HasDetectedProblem
        ? Join(_subject.MetadataProblemReasonCodes.Select(DescribeProblem)
            .Concat(_subject.Disposition.Evidence?.Content == FinalRecordContentStatus.ConfirmedUnusable
                ? ["No usable retained format was found."] : Array.Empty<string>()).Distinct(StringComparer.Ordinal))
        : "No specific title or author defect detected.";
    public string SuggestedAction => HasDetectedProblem
        ? "Double-click to open the book. Compare its title page and complete author credits with the catalog, "
            + "and check that the retained formats open. A warning can be legitimate. Leave Move unchecked if the book is fine. "
            + "Use Find repairs if the metadata can be fixed; check Move only if you want this book moved out of this library."
        : "No concrete metadata problem was detected. Missing verification is not evidence that the book is bad. "
            + "Leave Move unchecked to keep it. Optional verification tools are available if you want to certify it after inspection.";
    public string VerificationSummary
    {
        get
        {
            FinalRecordQualificationEvidence? evidence = _subject.Disposition.Evidence;
            if (Qualification == FinalRecordQualification.Qualified)
                return evidence?.ReviewerAccepted == true ? "Accepted by you after inspection." : "Automatically verified.";
            if (evidence is null) return "Automatic verification is incomplete; this is not a diagnosis of bad metadata.";
            List<string> missing = [];
            if (!evidence.TitleSupported || !evidence.AuthorsSupported)
                missing.Add("Opening pages did not confirm the catalog title and complete author credits.");
            if (evidence.Content == FinalRecordContentStatus.Incomplete)
                missing.Add("Readability is not fully verified.");
            if (!evidence.ManagedLayoutVerified)
                missing.Add("Managed naming is not verified, for example because naming was skipped.");
            if (evidence.Conflicting)
                missing.Add("Extracted evidence disagrees; extraction itself may be wrong.");
            if (evidence.ConcreteRepairAvailable)
                missing.Add("A possible metadata repair is available for inspection.");
            return missing.Count > 0 ? string.Join(" ", missing)
                : "This book has not been fully verified; that alone does not establish a defect.";
        }
    }

    public string TransferChoice => !Include ? "Keep in this library"
        : IsManual ? "You checked this book for quarantine" : "Suggested for quarantine; uncheck to keep";

    public bool Include
    {
        get => _include;
        set => SetProperty(ref _include, value);
    }

    public IAsyncRelayCommand PersistIncludeCommand { get; }

    public void Update(QuarantineReviewSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.BookId != BookId)
            throw new ArgumentException("The quarantine review row update is incompatible.");
        _subject = subject;
        _include = subject.Include;
        OnPropertyChanged(string.Empty);
    }

    private static string Join(IEnumerable<string> values)
    {
        string[] retained = values.Where(value => !string.IsNullOrWhiteSpace(value)).Take(100).ToArray();
        return retained.Length == 0 ? "None" : string.Join(", ", retained);
    }

    private static string DescribeProblem(string code) => code switch
    {
        RecordMetadataProblemPolicy.TitleContainsAuthor => "The title includes a complete author credit separated from the title text.",
        RecordMetadataProblemPolicy.TitleAuthorSwapped => "The title and author appear swapped compared with an explicit opening-page credit.",
        RecordMetadataProblemPolicy.AuthorBracketedNumber or AuthorSuspicionReasonCodes.ParenthesizedNumber =>
            "An author contains a bracketed number; check whether it belongs in the name.",
        AuthorSuspicionReasonCodes.MatchesTitle => "An author is identical to the book title.",
        AuthorSuspicionReasonCodes.ResemblesTitle => "An author looks like title text rather than a name.",
        AuthorSuspicionReasonCodes.Missing => "The author is missing.",
        AuthorSuspicionReasonCodes.Placeholder => "An author is a placeholder rather than a credited name.",
        AuthorSuspicionReasonCodes.NumberPrefixed or "AUTHOR.NUMBER_PREFIX" or "AUTHOR.YEAR_PREFIX" =>
            "An author starts with a number or year.",
        AuthorSuspicionReasonCodes.MalformedSeparator or "AUTHOR.DAMAGED_SEPARATOR" =>
            "Author credits contain damaged or ambiguous separators.",
        AuthorSuspicionReasonCodes.UnbalancedDelimiter => "An author has an unmatched bracket or parenthesis.",
        AuthorSuspicionReasonCodes.VolumeMarker => "An author contains a volume or part number.",
        AuthorSuspicionReasonCodes.PseudonymAnnotation => "An author contains a pseudonym annotation; inspect the published credit.",
        "TITLE.MISSING" => "The title is missing.",
        "TITLE.UNBALANCED_DELIMITER" => "The title has an unmatched bracket or parenthesis.",
        "TITLE.DAMAGED_SEPARATOR" => "The title contains damaged separators.",
        "TITLE.YEAR_PREFIX" => "The title starts with a year; check whether it is part of the actual title.",
        "TITLE.NUMBER_PREFIX" => "The title starts with a number; check whether it is part of the actual title.",
        "TITLE.FIELD_LIMIT" or "AUTHOR.FIELD_LIMIT" or "IDENTITY.FIELD_LIMIT" =>
            "Metadata exceeds the supported review limits; inspect the complete catalog fields.",
        _ => "Inspect the catalog metadata; see the technical details for the reported check.",
    };
}
