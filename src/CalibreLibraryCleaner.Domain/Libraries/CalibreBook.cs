using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record CalibreBook
{
    public CalibreBook(
        CalibreBookId id,
        string title,
        string authorSort,
        IEnumerable<BookAuthor> authors,
        IEnumerable<BookIdentifier> identifiers,
        IEnumerable<BookFormat> formats,
        string relativeDirectory,
        BookPublicationMetadata? publicationMetadata = null,
        AppliedAuthorProvenance? appliedAuthorProvenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(authorSort);
        ArgumentNullException.ThrowIfNull(authors);
        ArgumentNullException.ThrowIfNull(identifiers);
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentNullException.ThrowIfNull(relativeDirectory);

        Id = id;
        Title = title;
        AuthorSort = authorSort;
        Authors = new ReadOnlyCollection<BookAuthor>(authors.ToArray());
        Identifiers = new ReadOnlyCollection<BookIdentifier>(identifiers.ToArray());
        Formats = new ReadOnlyCollection<BookFormat>(formats.ToArray());
        RelativeDirectory = relativeDirectory;
        PublicationMetadata = publicationMetadata ?? BookPublicationMetadata.Empty;
        if (appliedAuthorProvenance is not null
            && !appliedAuthorProvenance.Matches(Authors.Select(value => value.Name)))
            throw new ArgumentException(
                "Applied author provenance must bind to the exact current author values.",
                nameof(appliedAuthorProvenance));
        AppliedAuthorProvenance = appliedAuthorProvenance;
    }

    public CalibreBookId Id { get; }

    public string Title { get; }

    public string AuthorSort { get; }

    public IReadOnlyList<BookAuthor> Authors { get; }

    public IReadOnlyList<BookIdentifier> Identifiers { get; }

    public IReadOnlyList<BookFormat> Formats { get; }

    public string RelativeDirectory { get; }

    public BookPublicationMetadata PublicationMetadata { get; }

    public AppliedAuthorProvenance? AppliedAuthorProvenance { get; }
}

public sealed record AppliedAuthorProvenance
{
    public AppliedAuthorProvenance(
        string providerId,
        string providerVersion,
        string editionId,
        string proposalPolicyVersion,
        string valuesIdentity)
    {
        ProviderId = Bound(providerId, 64, nameof(providerId));
        ProviderVersion = Bound(providerVersion, 128, nameof(providerVersion));
        EditionId = Bound(editionId, 160, nameof(editionId));
        ProposalPolicyVersion = Bound(proposalPolicyVersion, 128, nameof(proposalPolicyVersion));
        ValuesIdentity = valuesIdentity is { Length: 64 }
            && valuesIdentity.All(char.IsAsciiHexDigit)
            ? valuesIdentity.ToLowerInvariant()
            : throw new ArgumentException("Applied author provenance has an invalid value identity.", nameof(valuesIdentity));
    }

    public string ProviderId { get; }
    public string ProviderVersion { get; }
    public string EditionId { get; }
    public string ProposalPolicyVersion { get; }
    public string ValuesIdentity { get; }

    public bool Matches(IEnumerable<string> authors) =>
        string.Equals(ValuesIdentity, CreateValuesIdentity(authors), StringComparison.Ordinal);

    public static string CreateValuesIdentity(IEnumerable<string> authors)
    {
        ArgumentNullException.ThrowIfNull(authors);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count = 0;
        foreach (string author in authors)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(author);
            byte[] value = Encoding.UTF8.GetBytes(author.Normalize(NormalizationForm.FormC));
            hash.AppendData(BitConverter.GetBytes(value.Length));
            hash.AppendData(value);
            count++;
        }
        if (count == 0) throw new ArgumentException("Applied author provenance requires authors.", nameof(authors));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Bound(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        return normalized.Length <= maximumLength && normalized.All(character => !char.IsControl(character))
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}
