using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Metadata;

public enum AuthorEvidenceSource
{
    EpubPackageMetadata,
    PdfDocumentMetadata,
    EpubFrontMatter,
    PdfFrontMatter,
}

public enum AuthorProposalSource
{
    None,
    ConservativeNameOrder,
    CachedEmbeddedMetadata,
    ContentDerivedFrontMatter,
}

public enum CurrentAuthorProtection
{
    Unverified,
    CredibleCurrentMetadata,
    VerifiedAppliedOnlineMetadata,
}

public sealed record AuthorEvidenceObservation
{
    public AuthorEvidenceObservation(
        AuthorEvidenceSource source,
        string identity,
        IEnumerable<string> authors)
    {
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(authors);
        string[] values = authors.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Take(100)
            .ToArray();
        if (identity.Length > 256 || identity.Any(char.IsControl)
            || values.Any(value => value.Length > 512 || value.Any(char.IsControl)))
            throw new ArgumentException("Author evidence exceeds its retained bounds.");
        Source = source;
        Identity = identity;
        Authors = new ReadOnlyCollection<string>(values);
    }

    public AuthorEvidenceSource Source { get; }
    public string Identity { get; }
    public IReadOnlyList<string> Authors { get; }
}

public sealed record AuthorResolution
{
    public AuthorResolution(
        AuthorResolutionStatus status,
        CurrentAuthorProtection currentProtection,
        IEnumerable<string> reasonCodes,
        IEnumerable<AuthorEvidenceObservation> evidence,
        AuthorProposalSource proposalSource = AuthorProposalSource.None,
        IEnumerable<string>? proposedDisplayNames = null,
        IEnumerable<string>? proposedSortNames = null,
        bool selectedByDefault = false)
    {
        ArgumentNullException.ThrowIfNull(reasonCodes);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!Enum.IsDefined(status)
            || !Enum.IsDefined(currentProtection)
            || !Enum.IsDefined(proposalSource))
            throw new ArgumentOutOfRangeException(nameof(status));
        string[] reasons = reasonCodes.Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        AuthorEvidenceObservation[] observations = evidence
            .OrderBy(value => value.Source)
            .ThenBy(value => value.Identity, StringComparer.Ordinal)
            .ToArray();
        string[] displays = proposedDisplayNames?.ToArray() ?? [];
        string[] sorts = proposedSortNames?.ToArray() ?? [];
        bool hasProposal = proposalSource != AuthorProposalSource.None;
        if (hasProposal != (displays.Length > 0)
            || sorts.Length != 0 && sorts.Length != displays.Length
            || selectedByDefault && proposalSource != AuthorProposalSource.ConservativeNameOrder
            || reasons.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128)
            || displays.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512)
            || sorts.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512))
            throw new ArgumentException("Author resolution is invalid.");
        Status = status;
        CurrentProtection = currentProtection;
        ReasonCodes = new ReadOnlyCollection<string>(reasons);
        Evidence = new ReadOnlyCollection<AuthorEvidenceObservation>(observations);
        ProposalSource = proposalSource;
        ProposedDisplayNames = new ReadOnlyCollection<string>(displays);
        ProposedSortNames = new ReadOnlyCollection<string>(sorts);
        SelectedByDefault = selectedByDefault;
    }

    public AuthorResolutionStatus Status { get; }
    public CurrentAuthorProtection CurrentProtection { get; }
    public IReadOnlyList<string> ReasonCodes { get; }
    public IReadOnlyList<AuthorEvidenceObservation> Evidence { get; }
    public AuthorProposalSource ProposalSource { get; }
    public IReadOnlyList<string> ProposedDisplayNames { get; }
    public IReadOnlyList<string> ProposedSortNames { get; }
    public bool SelectedByDefault { get; }
}

public static class AuthorSourcePreservationPolicy
{
    public const string PolicyVersion = "author-source-preservation/1.0.0";

    public static AuthorResolution Resolve(
        string title,
        IReadOnlyList<BookAuthor> currentAuthors,
        bool verifiedAppliedOnlineAuthors,
        IEnumerable<AuthorEvidenceObservation> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(currentAuthors);
        ArgumentNullException.ThrowIfNull(evidence);
        AuthorEvidenceObservation[] observations = evidence.ToArray();
        AuthorSuspicionAssessment current = AuthorSuspicionPolicy.Assess(
            title, currentAuthors.Select(value => value.Name));
        CurrentAuthorProtection protection = verifiedAppliedOnlineAuthors
            ? CurrentAuthorProtection.VerifiedAppliedOnlineMetadata
            : current.Status == AuthorResolutionStatus.Known
                ? CurrentAuthorProtection.CredibleCurrentMetadata
                : CurrentAuthorProtection.Unverified;

        if (verifiedAppliedOnlineAuthors && current.Status != AuthorResolutionStatus.Unresolved)
            return Current(AuthorResolutionStatus.Known);

        if (PipeAuthorNameNormalizationPolicy.TryNormalizeBook(
                currentAuthors,
                out IReadOnlyList<string> displays,
                out IReadOnlyList<string> sorts))
        {
            return new(
                AuthorResolutionStatus.Suspect,
                protection,
                current.ReasonCodes.Append(AuthorSuspicionReasonCodes.ConservativeNameOrder),
                observations,
                AuthorProposalSource.ConservativeNameOrder,
                displays,
                sorts,
                selectedByDefault: true);
        }

        if (current.Status == AuthorResolutionStatus.Known)
            return Current(AuthorResolutionStatus.Known);

        AuthorEvidenceObservation[] usable = observations.Where(value =>
            value.Authors.Count > 0
            && AuthorSuspicionPolicy.Assess(title, value.Authors).Status == AuthorResolutionStatus.Known)
            .ToArray();
        IGrouping<string, AuthorEvidenceObservation>[] alternatives = usable
            .Select(value => (Value: value, Key: NormalizedKey(value.Authors)))
            .Where(value => value.Key is not null)
            .GroupBy(value => value.Key!, value => value.Value, StringComparer.Ordinal)
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .ToArray();
        if (alternatives.Length > 1)
            return Current(AuthorResolutionStatus.Conflicting);
        if (alternatives.Length == 0)
            return Current(current.Status);

        AuthorEvidenceObservation selected = alternatives[0]
            .OrderBy(value => value.Source)
            .ThenBy(value => value.Identity, StringComparer.Ordinal)
            .First();
        string? currentKey = NormalizedKey(currentAuthors.Select(value => value.Name));
        if (string.Equals(currentKey, alternatives[0].Key, StringComparison.Ordinal))
            return Current(current.Status);
        return new(
            AuthorResolutionStatus.Suspect,
            protection,
            current.ReasonCodes,
            observations,
            selected.Source is AuthorEvidenceSource.EpubFrontMatter
                or AuthorEvidenceSource.PdfFrontMatter
                ? AuthorProposalSource.ContentDerivedFrontMatter
                : AuthorProposalSource.CachedEmbeddedMetadata,
            selected.Authors,
            selectedByDefault: false);

        AuthorResolution Current(AuthorResolutionStatus status) => new(
            status,
            protection,
            current.ReasonCodes,
            observations);
    }

    private static string? NormalizedKey(IEnumerable<string> authors)
    {
        List<string> values = [];
        foreach (string author in authors)
        {
            if (!MetadataTextNormalizer.TryNormalizeAuthorName(
                    author, out NormalizedAuthorName? normalized))
                return null;
            values.Add(normalized!.Value);
        }
        return values.Count == 0 ? null : string.Join('\u001F', values);
    }
}
