using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Matching;

[Flags]
public enum BibliographicQueryFields
{
    None = 0,
    Identifier = 1 << 0,
    Title = 1 << 1,
    Authors = 1 << 2,
    Language = 1 << 3,
}

public sealed record BibliographicProviderIdentity
{
    public BibliographicProviderIdentity(string id, string version)
    {
        Id = Bound(id, 64, nameof(id));
        Version = Bound(version, 128, nameof(version));
    }

    public string Id { get; }
    public string Version { get; }

    private static string Bound(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}

public sealed record BibliographicLookupQuery
{
    public const string PolicyVersion = "bibliographic-query/1.0.0";
    private const BibliographicQueryFields AllFields = BibliographicQueryFields.Identifier
        | BibliographicQueryFields.Title | BibliographicQueryFields.Authors | BibliographicQueryFields.Language;

    public BibliographicLookupQuery(
        CalibreBookId bookId,
        BibliographicProviderIdentity provider,
        BibliographicQueryFields fields,
        string? identifier,
        string? title,
        IEnumerable<string>? authors,
        string? language)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (fields == BibliographicQueryFields.None || (fields & ~AllFields) != 0)
            throw new ArgumentException("Bibliographic query fields are invalid.", nameof(fields));
        string? boundedIdentifier = Optional(identifier, 256, nameof(identifier));
        string? boundedTitle = Optional(title, 512, nameof(title));
        string[] boundedAuthors = (authors ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Take(9).ToArray();
        if (boundedAuthors.Length > 8 || boundedAuthors.Any(value => value.Length > 256))
            throw new ArgumentException("Bibliographic query authors exceed their bounds.", nameof(authors));
        string? normalizedLanguage = CandidateMetadataNormalizer.NormalizeLanguage(language);
        if (boundedIdentifier is not null
            && (!boundedIdentifier.StartsWith("ISBN:", StringComparison.Ordinal)
                || CandidateMetadataNormalizer.NormalizeStrongIdentifier(
                    "isbn", boundedIdentifier[5..]) != boundedIdentifier))
            throw new ArgumentException("Bibliographic identifier query is not a canonical ISBN.", nameof(identifier));
        if (fields.HasFlag(BibliographicQueryFields.Identifier) != (boundedIdentifier is not null)
            || fields.HasFlag(BibliographicQueryFields.Title) != (boundedTitle is not null)
            || fields.HasFlag(BibliographicQueryFields.Authors) != (boundedAuthors.Length > 0)
            || fields.HasFlag(BibliographicQueryFields.Language) != (normalizedLanguage is not null)
            || !fields.HasFlag(BibliographicQueryFields.Identifier)
                && (!fields.HasFlag(BibliographicQueryFields.Title)
                    || !fields.HasFlag(BibliographicQueryFields.Authors)))
            throw new ArgumentException("Bibliographic query values do not match disclosed fields.");

        BookId = bookId;
        Provider = provider;
        Fields = fields;
        Identifier = boundedIdentifier;
        Title = boundedTitle;
        Authors = new ReadOnlyCollection<string>(boundedAuthors);
        Language = normalizedLanguage;
        QueryIdentity = CreateIdentity();
    }

    public CalibreBookId BookId { get; }
    public BibliographicProviderIdentity Provider { get; }
    public BibliographicQueryFields Fields { get; }
    public string? Identifier { get; }
    public string? Title { get; }
    public IReadOnlyList<string> Authors { get; }
    public string? Language { get; }
    public string QueryIdentity { get; }

    public static BibliographicLookupQuery Create(
        CalibreBook book,
        BookMatchingProfile profile,
        BibliographicProviderIdentity provider)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(profile);
        if (book.Id != profile.BookId)
            throw new ArgumentException("The bibliographic query book and profile must agree.");
        string? isbn = profile.StrongIdentifiers.FirstOrDefault(value =>
            value.StartsWith("ISBN:", StringComparison.Ordinal));
        if (isbn is not null)
            return new(book.Id, provider, BibliographicQueryFields.Identifier,
                isbn, null, null, null);
        string[] authors = book.Authors.Select(value => value.Name).ToArray();
        string? language = profile.Languages.Count == 1 ? profile.Languages[0] : null;
        BibliographicQueryFields fields = BibliographicQueryFields.Title | BibliographicQueryFields.Authors;
        if (language is not null) fields |= BibliographicQueryFields.Language;
        return new(book.Id, provider, fields, null, book.Title, authors, language);
    }

    private string CreateIdentity()
    {
        StringBuilder canonical = new();
        Append(canonical, PolicyVersion);
        Append(canonical, BibliographicWorkResolution.PolicyVersion);
        Append(canonical, Provider.Id);
        Append(canonical, Provider.Version);
        Append(canonical, ((int)Fields).ToString(CultureInfo.InvariantCulture));
        Append(canonical, Identifier ?? string.Empty);
        Append(canonical, Title ?? string.Empty);
        foreach (string author in Authors) Append(canonical, author);
        Append(canonical, Language ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder target, string value) => target
        .Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
        .Append(':').Append(value).Append('|');

    private static string? Optional(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}

public sealed record BibliographicWorkCandidate
{
    public BibliographicWorkCandidate(
        string workId,
        string title,
        IEnumerable<string> authors,
        IEnumerable<string>? languages,
        int editionCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(authors);
        if (workId.Length > 160 || title.Length > 512)
            throw new ArgumentOutOfRangeException(nameof(workId));
        string[] authorValues = authors.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Take(16).ToArray();
        string[] languageValues = (languages ?? [])
            .Select(CandidateMetadataNormalizer.NormalizeLanguage).Where(value => value is not null)
            .Select(value => value!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Take(32).ToArray();
        if (authorValues.Length == 0 || authorValues.Any(value => value.Length > 256) || editionCount < 0)
            throw new ArgumentException("Bibliographic candidate facts are invalid.");
        WorkId = workId.Trim();
        Title = title.Trim();
        Authors = new ReadOnlyCollection<string>(authorValues);
        Languages = new ReadOnlyCollection<string>(languageValues);
        EditionCount = editionCount;
    }

    public string WorkId { get; }
    public string Title { get; }
    public IReadOnlyList<string> Authors { get; }
    public IReadOnlyList<string> Languages { get; }
    public int EditionCount { get; }
}

public enum BibliographicSearchStatus
{
    Success,
    NotFound,
    Unavailable,
}

public sealed record BibliographicSearchResult
{
    public BibliographicSearchResult(
        BibliographicProviderIdentity provider,
        BibliographicSearchStatus status,
        DateTimeOffset retrievedAtUtc,
        IEnumerable<BibliographicWorkCandidate>? candidates = null,
        string? problemCode = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        BibliographicWorkCandidate[] values = (candidates ?? []).Take(6).ToArray();
        if (!Enum.IsDefined(status) || values.Length > 5
            || status != BibliographicSearchStatus.Success && values.Length > 0
            || status == BibliographicSearchStatus.Success && values.Length == 0
            || problemCode is { Length: > 128 })
            throw new ArgumentException("Bibliographic search result is invalid.");
        Provider = provider;
        Status = status;
        RetrievedAtUtc = retrievedAtUtc.ToUniversalTime();
        Candidates = new ReadOnlyCollection<BibliographicWorkCandidate>(values);
        ProblemCode = string.IsNullOrWhiteSpace(problemCode) ? null : problemCode.Trim();
    }

    public BibliographicProviderIdentity Provider { get; }
    public BibliographicSearchStatus Status { get; }
    public DateTimeOffset RetrievedAtUtc { get; }
    public IReadOnlyList<BibliographicWorkCandidate> Candidates { get; }
    public string? ProblemCode { get; }
}

public enum BibliographicResolutionStatus
{
    Matched,
    Ambiguous,
    NotFound,
    Unavailable,
}

public sealed record BibliographicWorkResolution
{
    public const string PolicyVersion = "bibliographic-resolution/1.0.0";
    private const BibliographicQueryFields AllFields = BibliographicQueryFields.Identifier
        | BibliographicQueryFields.Title | BibliographicQueryFields.Authors | BibliographicQueryFields.Language;

    public BibliographicWorkResolution(
        CalibreBookId bookId,
        BibliographicProviderIdentity provider,
        string queryIdentity,
        BibliographicQueryFields queryFields,
        DateTimeOffset retrievedAtUtc,
        BibliographicResolutionStatus status,
        string? workId = null,
        string? problemCode = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (queryIdentity.Length != 64 || !queryIdentity.All(Uri.IsHexDigit)
            || queryFields == BibliographicQueryFields.None || (queryFields & ~AllFields) != 0
            || !Enum.IsDefined(status)
            || (status == BibliographicResolutionStatus.Matched) != !string.IsNullOrWhiteSpace(workId)
            || workId is { Length: > 160 } || problemCode is { Length: > 128 })
            throw new ArgumentException("Bibliographic work resolution is invalid.");
        BookId = bookId;
        Provider = provider;
        QueryIdentity = queryIdentity.ToLowerInvariant();
        QueryFields = queryFields;
        RetrievedAtUtc = retrievedAtUtc.ToUniversalTime();
        Status = status;
        WorkId = workId?.Trim();
        ProblemCode = string.IsNullOrWhiteSpace(problemCode) ? null : problemCode.Trim();
    }

    public CalibreBookId BookId { get; }
    public BibliographicProviderIdentity Provider { get; }
    public string QueryIdentity { get; }
    public BibliographicQueryFields QueryFields { get; }
    public DateTimeOffset RetrievedAtUtc { get; }
    public BibliographicResolutionStatus Status { get; }
    public string? WorkId { get; }
    public string? ProblemCode { get; }
}

public static class BibliographicResolutionPolicy
{
    public static BibliographicWorkResolution Resolve(
        BibliographicLookupQuery query,
        BibliographicSearchResult search)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(search);
        if (search.Provider != query.Provider)
            throw new ArgumentException("Bibliographic resolution inputs do not agree.");
        if (search.Status == BibliographicSearchStatus.Unavailable)
            return Resolution(BibliographicResolutionStatus.Unavailable, search.ProblemCode);
        if (search.Status == BibliographicSearchStatus.NotFound)
            return Resolution(BibliographicResolutionStatus.NotFound, search.ProblemCode);

        BibliographicWorkCandidate[] qualifying = search.Candidates
            .Where(value => Compatible(query, value))
            .GroupBy(value => value.WorkId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(value => value.EditionCount).First())
            .OrderByDescending(value => value.EditionCount)
            .ThenBy(value => value.WorkId, StringComparer.Ordinal).ToArray();
        if (qualifying.Length == 1)
            return Resolution(BibliographicResolutionStatus.Matched, workId: qualifying[0].WorkId);
        if (qualifying.Length > 1
            && ExactTitle(query, qualifying[0])
            && qualifying[0].EditionCount >= 5
            && qualifying[0].EditionCount >= 4L * qualifying[1].EditionCount)
            return Resolution(BibliographicResolutionStatus.Matched, workId: qualifying[0].WorkId);
        return Resolution(qualifying.Length == 0
            ? BibliographicResolutionStatus.NotFound
            : BibliographicResolutionStatus.Ambiguous);

        BibliographicWorkResolution Resolution(
            BibliographicResolutionStatus status,
            string? problemCode = null,
            string? workId = null) => new(
            query.BookId,
            query.Provider,
            query.QueryIdentity,
            query.Fields,
            search.RetrievedAtUtc,
            status,
            workId,
            problemCode);
    }

    private static bool Compatible(BibliographicLookupQuery query, BibliographicWorkCandidate candidate)
    {
        if (query.Identifier is not null) return true;
        string[] queryAuthorKeys = CandidateMetadataNormalizer.AuthorKeys(query.Authors);
        string[] candidateAuthorKeys = CandidateMetadataNormalizer.AuthorKeys(candidate.Authors);
        if (!CandidateMetadataNormalizer.HaveCompatibleAuthorIdentity(queryAuthorKeys, candidateAuthorKeys)) return false;
        string[] titleTokens = CandidateMetadataNormalizer.TitleTokens(candidate.Title);
        string[] queryTitleTokens = CandidateMetadataNormalizer.TitleTokens(query.Title!);
        if (CandidateMetadataNormalizer.TitleSimilarityPermille(queryTitleTokens, titleTokens) < 500
            && !CandidateMetadataNormalizer.TitleKeys(query.Title!).Intersect(
                CandidateMetadataNormalizer.TitleKeys(candidate.Title), StringComparer.Ordinal).Any())
            return false;
        return query.Language is null || candidate.Languages.Count == 0
            || candidate.Languages.Contains(query.Language, StringComparer.Ordinal);
    }

    private static bool ExactTitle(BibliographicLookupQuery query, BibliographicWorkCandidate candidate) =>
        query.Title is not null && CandidateMetadataNormalizer.TitleKeys(query.Title).Intersect(
            CandidateMetadataNormalizer.TitleKeys(candidate.Title), StringComparer.Ordinal).Any();
}

public static class BibliographicPairEvidencePolicy
{
    public const string EvidenceCode = "MATCH.BIBLIOGRAPHIC.SAME_WORK";

    public static IReadOnlyList<BookCandidatePair> Enrich(
        IEnumerable<BookCandidatePair> pairs,
        IReadOnlyDictionary<CalibreBookId, BibliographicWorkResolution> resolutions)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(resolutions);
        return pairs.OrderBy(value => value.Id.First.Value).ThenBy(value => value.Id.Second.Value)
            .Select(pair => Enrich(pair, resolutions)).ToArray();
    }

    private static BookCandidatePair Enrich(
        BookCandidatePair pair,
        IReadOnlyDictionary<CalibreBookId, BibliographicWorkResolution> resolutions)
    {
        if (!resolutions.TryGetValue(pair.Id.First, out BibliographicWorkResolution? first)
            || !resolutions.TryGetValue(pair.Id.Second, out BibliographicWorkResolution? second)
            || first.Status != BibliographicResolutionStatus.Matched
            || second.Status != BibliographicResolutionStatus.Matched
            || first.Provider != second.Provider
            || !string.Equals(first.WorkId, second.WorkId, StringComparison.Ordinal))
            return pair;
        CandidateEvidence evidence = new(
            EvidenceCode,
            CandidateEvidenceStrength.Anchor,
            new(first.Provider.Id, first.Provider.Version, first.WorkId!));
        return new(
            pair.Id,
            pair.CheapScore,
            pair.Evidence.Append(evidence),
            pair.Contradictions,
            pair.NeedsContentEvidence);
    }
}
