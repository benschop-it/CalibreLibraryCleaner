using System.Globalization;
using System.Text;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;

namespace CalibreLibraryCleaner.Application.Metadata;

public sealed record FinalRecordInputIdentity
{
    private readonly string _canonical;

    private FinalRecordInputIdentity(string canonical) => _canonical = canonical;

    public static FinalRecordInputIdentity Create(CalibreBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        StringBuilder canonical = new();
        Add(FinalRecordDispositionPolicy.PolicyVersion);
        Add(FinalRecordQualificationPolicy.PolicyVersion);
        Add(RecordIdentityTriagePolicy.PolicyVersion);
        Add(AuthorSuspicionPolicy.PolicyVersion);
        Add(AuthorSourcePreservationPolicy.PolicyVersion);
        Add(FrontMatterAuthorVersions.EpubExtractor);
        Add(FrontMatterAuthorVersions.PdfExtractor);
        Add(FrontMatterAuthorVersions.Limits);
        Add(EditionMetadataLookupQuery.PolicyVersion);
        Add(FusedEditionMetadataProposal.CurrentPolicyVersion);
        Add(book.Id.Value.ToString(CultureInfo.InvariantCulture));
        Add(book.Title);
        Add(book.AuthorSort);
        Add(book.RelativeDirectory);
        Add(book.AppliedAuthorProvenance?.ProviderId);
        Add(book.AppliedAuthorProvenance?.ProviderVersion);
        Add(book.AppliedAuthorProvenance?.EditionId);
        Add(book.AppliedAuthorProvenance?.ProposalPolicyVersion);
        Add(book.AppliedAuthorProvenance?.ValuesIdentity);
        Add(book.Authors.Count.ToString(CultureInfo.InvariantCulture));
        foreach (BookAuthor author in book.Authors)
        {
            Add(author.Id?.Value.ToString(CultureInfo.InvariantCulture));
            Add(author.Name);
            Add(author.SortName);
        }
        Add("identifiers");
        Add(book.Identifiers.Count.ToString(CultureInfo.InvariantCulture));
        foreach (BookIdentifier identifier in book.Identifiers.OrderBy(value => value.Type, StringComparer.Ordinal)
                     .ThenBy(value => value.Value, StringComparer.Ordinal))
        {
            Add(identifier.Type);
            Add(identifier.Value);
        }
        Add("publication");
        Add(book.PublicationMetadata.Publisher);
        Add(book.PublicationMetadata.PublicationDate?.ToString("O", CultureInfo.InvariantCulture));
        Add(book.PublicationMetadata.Series);
        Add(book.PublicationMetadata.SeriesIndex?.ToString(CultureInfo.InvariantCulture));
        Add(book.PublicationMetadata.HasCover ? "cover" : "no-cover");
        Add(book.PublicationMetadata.Languages.Count.ToString(CultureInfo.InvariantCulture));
        foreach (string language in book.PublicationMetadata.Languages) Add(language);
        Add("formats");
        Add(book.Formats.Count.ToString(CultureInfo.InvariantCulture));
        foreach (BookFormat format in book.Formats.OrderBy(value => value.Format, StringComparer.Ordinal))
        {
            Add(format.Format);
            Add(format.StoredFileName);
            Add(format.ExpectedRelativePath);
            Add(format.FileStatus.ToString());
            Add(format.Fingerprint?.Sha256.Value);
            Add(format.Fingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture));
            Add(format.Observation?.Length.ToString(CultureInfo.InvariantCulture));
            Add(format.Observation?.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture));
            Add(format.Observation?.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture));
            Add(format.Observation?.Attributes.ToString(CultureInfo.InvariantCulture));
        }
        return new(canonical.ToString());

        void Add(string? value)
        {
            string text = value ?? string.Empty;
            canonical.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text);
        }
    }
}
