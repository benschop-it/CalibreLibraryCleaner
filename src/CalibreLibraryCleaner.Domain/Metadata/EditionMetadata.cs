using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Domain.Metadata;

[Flags]
public enum EditionMetadataQueryFields
{
    None = 0,
    Identifier = 1 << 0,
    Title = 1 << 1,
    Authors = 1 << 2,
    Language = 1 << 3,
}

public sealed record EditionMetadataProviderIdentity
{
    public EditionMetadataProviderIdentity(string id, string version)
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

public sealed record EditionMetadataLookupQuery
{
    public const string PolicyVersion = "edition-metadata-query/1.0.0";
    private const EditionMetadataQueryFields AllFields = EditionMetadataQueryFields.Identifier
        | EditionMetadataQueryFields.Title | EditionMetadataQueryFields.Authors
        | EditionMetadataQueryFields.Language;

    public EditionMetadataLookupQuery(
        CalibreBookId bookId,
        EditionMetadataProviderIdentity provider,
        EditionMetadataQueryFields fields,
        string? identifier,
        string? title,
        IEnumerable<string>? authors,
        string? language)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (fields == EditionMetadataQueryFields.None || (fields & ~AllFields) != 0)
            throw new ArgumentException("Edition metadata query fields are invalid.", nameof(fields));
        string? normalizedIdentifier = NormalizeIdentifier(identifier);
        string? boundedTitle = Optional(title, 512, nameof(title));
        string[] boundedAuthors = (authors ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Take(9).ToArray();
        if (boundedAuthors.Length > 8 || boundedAuthors.Any(value => value.Length > 256))
            throw new ArgumentException("Edition metadata query authors exceed their bounds.", nameof(authors));
        string? normalizedLanguage = CandidateMetadataNormalizer.NormalizeLanguage(language);
        if (fields.HasFlag(EditionMetadataQueryFields.Identifier) != (normalizedIdentifier is not null)
            || fields.HasFlag(EditionMetadataQueryFields.Title) != (boundedTitle is not null)
            || fields.HasFlag(EditionMetadataQueryFields.Authors) != (boundedAuthors.Length > 0)
            || fields.HasFlag(EditionMetadataQueryFields.Language) != (normalizedLanguage is not null)
            || normalizedIdentifier is null
                && (!fields.HasFlag(EditionMetadataQueryFields.Title)
                    || !fields.HasFlag(EditionMetadataQueryFields.Authors)))
            throw new ArgumentException("Edition metadata query values do not match disclosed fields.");

        BookId = bookId;
        Provider = provider;
        Fields = fields;
        Identifier = normalizedIdentifier;
        Title = boundedTitle;
        Authors = new ReadOnlyCollection<string>(boundedAuthors);
        Language = normalizedLanguage;
    }

    public CalibreBookId BookId { get; }
    public EditionMetadataProviderIdentity Provider { get; }
    public EditionMetadataQueryFields Fields { get; }
    public string? Identifier { get; }
    public string? Title { get; }
    public IReadOnlyList<string> Authors { get; }
    public string? Language { get; }

    public static EditionMetadataLookupQuery Create(
        CalibreBook book,
        EditionMetadataProviderIdentity provider)
    {
        ArgumentNullException.ThrowIfNull(book);
        string? isbn = book.Identifiers.Select(value =>
                CandidateMetadataNormalizer.NormalizeStrongIdentifier(value.Type, value.Value))
            .FirstOrDefault(value => value?.StartsWith("ISBN:", StringComparison.Ordinal) == true);
        if (isbn is not null)
            return new(book.Id, provider, EditionMetadataQueryFields.Identifier,
                isbn, null, null, null);
        string[] authors = book.Authors.Select(value => value.Name).ToArray();
        string[] languages = book.PublicationMetadata.Languages
            .Select(CandidateMetadataNormalizer.NormalizeLanguage)
            .Where(value => value is not null).Distinct(StringComparer.Ordinal)
            .Select(value => value!).Take(2).ToArray();
        string? language = languages.Length == 1 ? languages[0] : null;
        EditionMetadataQueryFields fields = EditionMetadataQueryFields.Title
            | EditionMetadataQueryFields.Authors;
        if (language is not null) fields |= EditionMetadataQueryFields.Language;
        return new(book.Id, provider, fields, null, book.Title, authors, language);
    }

    private static string? NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim();
        if (!normalized.StartsWith("ISBN:", StringComparison.Ordinal)
            || CandidateMetadataNormalizer.NormalizeStrongIdentifier("isbn", normalized[5..]) != normalized)
            throw new ArgumentException("Edition metadata identifier must be a canonical ISBN.", nameof(value));
        return normalized;
    }

    private static string? Optional(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}

public sealed record EditionMetadataIdentifier
{
    public EditionMetadataIdentifier(string type, string value)
    {
        string? canonical = CandidateMetadataNormalizer.NormalizeStrongIdentifier(type, value);
        if (canonical is null)
            throw new ArgumentException("Edition metadata identifier is invalid.");
        int separator = canonical.IndexOf(':', StringComparison.Ordinal);
        Type = canonical[..separator].ToLowerInvariant();
        Value = canonical[(separator + 1)..];
    }

    public string Type { get; }
    public string Value { get; }
    public string CanonicalValue => Type.ToUpperInvariant() + ":" + Value;
}

public sealed record EditionPublicationDate
{
    public EditionPublicationDate(int year, int? month = null, int? day = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        if (month is < 1 or > 12 || day is not null && month is null
            || day is < 1 || day is not null && day > DateTime.DaysInMonth(year, month!.Value))
            throw new ArgumentOutOfRangeException(nameof(month));
        Year = year;
        Month = month;
        Day = day;
    }

    public int Year { get; }
    public int? Month { get; }
    public int? Day { get; }
}

public sealed record EditionCoverReference
{
    public EditionCoverReference(string sourceId, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (sourceId.Length > 128 || url.Length > 2048
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Edition cover reference is invalid.");
        SourceId = sourceId.Trim();
        Url = parsed.AbsoluteUri;
    }

    public string SourceId { get; }
    public string Url { get; }
}

public sealed record EditionMetadataCandidate
{
    public EditionMetadataCandidate(
        string workId,
        string editionId,
        string title,
        IEnumerable<string> authors,
        IEnumerable<EditionMetadataIdentifier>? identifiers = null,
        string? publisher = null,
        EditionPublicationDate? publicationDate = null,
        IEnumerable<string>? languages = null,
        string? series = null,
        decimal? seriesIndex = null,
        EditionCoverReference? cover = null)
    {
        ArgumentNullException.ThrowIfNull(authors);
        WorkId = Bound(workId, 160, nameof(workId));
        EditionId = Bound(editionId, 160, nameof(editionId));
        Title = Bound(title, 512, nameof(title));
        string[] authorValues = authors.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Take(17).ToArray();
        EditionMetadataIdentifier[] identifierValues = (identifiers ?? []).Distinct()
            .OrderBy(value => value.Type, StringComparer.Ordinal)
            .ThenBy(value => value.Value, StringComparer.Ordinal).Take(33).ToArray();
        string[] languageValues = (languages ?? [])
            .Select(CandidateMetadataNormalizer.NormalizeLanguage).Where(value => value is not null)
            .Select(value => value!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Take(9).ToArray();
        if (authorValues.Length is 0 or > 16 || authorValues.Any(value => value.Length > 256)
            || identifierValues.Length > 32 || languageValues.Length > 8
            || seriesIndex is < 0 or > 1_000_000)
            throw new ArgumentException("Edition metadata candidate facts are invalid.");
        Authors = new ReadOnlyCollection<string>(authorValues);
        Identifiers = new ReadOnlyCollection<EditionMetadataIdentifier>(identifierValues);
        Publisher = Optional(publisher, 512, nameof(publisher));
        PublicationDate = publicationDate;
        Languages = new ReadOnlyCollection<string>(languageValues);
        Series = Optional(series, 512, nameof(series));
        SeriesIndex = seriesIndex;
        Cover = cover;
    }

    public string WorkId { get; }
    public string EditionId { get; }
    public string Title { get; }
    public IReadOnlyList<string> Authors { get; }
    public IReadOnlyList<EditionMetadataIdentifier> Identifiers { get; }
    public string? Publisher { get; }
    public EditionPublicationDate? PublicationDate { get; }
    public IReadOnlyList<string> Languages { get; }
    public string? Series { get; }
    public decimal? SeriesIndex { get; }
    public EditionCoverReference? Cover { get; }

    internal int Completeness => 1 + (Authors.Count > 0 ? 1 : 0) + (Identifiers.Count > 0 ? 1 : 0)
        + (Publisher is not null ? 1 : 0) + (PublicationDate is not null ? 1 : 0)
        + (Languages.Count > 0 ? 1 : 0) + (Series is not null ? 1 : 0) + (Cover is not null ? 1 : 0);

    private static string Bound(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string? Optional(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}

public enum EditionMetadataSearchStatus
{
    Success,
    NotFound,
    Unavailable,
}

public sealed record EditionMetadataSearchResult
{
    public EditionMetadataSearchResult(
        EditionMetadataProviderIdentity provider,
        EditionMetadataSearchStatus status,
        DateTimeOffset retrievedAtUtc,
        IEnumerable<EditionMetadataCandidate>? candidates = null,
        string? problemCode = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        EditionMetadataCandidate[] values = (candidates ?? []).Take(6).ToArray();
        if (!Enum.IsDefined(status) || values.Length > 5
            || status != EditionMetadataSearchStatus.Success && values.Length > 0
            || status == EditionMetadataSearchStatus.Success && values.Length == 0
            || problemCode is { Length: > 128 })
            throw new ArgumentException("Edition metadata search result is invalid.");
        Provider = provider;
        Status = status;
        RetrievedAtUtc = retrievedAtUtc.ToUniversalTime();
        Candidates = new ReadOnlyCollection<EditionMetadataCandidate>(values);
        ProblemCode = string.IsNullOrWhiteSpace(problemCode) ? null : problemCode.Trim();
    }

    public EditionMetadataProviderIdentity Provider { get; }
    public EditionMetadataSearchStatus Status { get; }
    public DateTimeOffset RetrievedAtUtc { get; }
    public IReadOnlyList<EditionMetadataCandidate> Candidates { get; }
    public string? ProblemCode { get; }
}

public enum EditionMetadataProposalStatus
{
    Proposed,
    Ambiguous,
    NotFound,
    Unavailable,
}

public sealed record EditionMetadataProposal
{
    public const string PolicyVersion = "edition-metadata-proposal/1.0.0";
    private const EditionMetadataQueryFields AllQueryFields = EditionMetadataQueryFields.Identifier
        | EditionMetadataQueryFields.Title | EditionMetadataQueryFields.Authors
        | EditionMetadataQueryFields.Language;

    public EditionMetadataProposal(
        CalibreBookId bookId,
        EditionMetadataProviderIdentity provider,
        EditionMetadataQueryFields queryFields,
        DateTimeOffset retrievedAtUtc,
        EditionMetadataProposalStatus status,
        EditionMetadataCandidate? candidate = null,
        int matchScore = 0,
        IEnumerable<string>? reasonCodes = null,
        string? problemCode = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        string[] reasons = (reasonCodes ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Take(17).ToArray();
        if (queryFields == EditionMetadataQueryFields.None || (queryFields & ~AllQueryFields) != 0
            || !Enum.IsDefined(status) || matchScore is < 0 or > 20_000 || reasons.Length > 16
            || reasons.Any(value => value.Length > 128) || problemCode is { Length: > 128 }
            || (status == EditionMetadataProposalStatus.Proposed) != (candidate is not null)
            || status == EditionMetadataProposalStatus.Proposed && (matchScore == 0 || reasons.Length == 0)
            || status != EditionMetadataProposalStatus.Proposed && (matchScore != 0 || reasons.Length != 0))
            throw new ArgumentException("Edition metadata proposal is invalid.");
        BookId = bookId;
        Provider = provider;
        QueryFields = queryFields;
        RetrievedAtUtc = retrievedAtUtc.ToUniversalTime();
        Status = status;
        Candidate = candidate;
        MatchScore = matchScore;
        ReasonCodes = new ReadOnlyCollection<string>(reasons);
        ProblemCode = string.IsNullOrWhiteSpace(problemCode) ? null : problemCode.Trim();
    }

    public CalibreBookId BookId { get; }
    public EditionMetadataProviderIdentity Provider { get; }
    public EditionMetadataQueryFields QueryFields { get; }
    public DateTimeOffset RetrievedAtUtc { get; }
    public EditionMetadataProposalStatus Status { get; }
    public EditionMetadataCandidate? Candidate { get; }
    public int MatchScore { get; }
    public IReadOnlyList<string> ReasonCodes { get; }
    public string? ProblemCode { get; }
}

public static class EditionMetadataProposalPolicy
{
    public static EditionMetadataProposal Resolve(
        EditionMetadataLookupQuery query,
        EditionMetadataSearchResult search)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(search);
        if (query.Provider != search.Provider)
            throw new ArgumentException("Edition metadata proposal inputs do not agree.");
        if (search.Status == EditionMetadataSearchStatus.Unavailable)
            return Proposal(EditionMetadataProposalStatus.Unavailable, problemCode: search.ProblemCode);
        if (search.Status == EditionMetadataSearchStatus.NotFound)
            return Proposal(EditionMetadataProposalStatus.NotFound, problemCode: search.ProblemCode);

        ScoredCandidate[] scored = search.Candidates.Select(value => Score(query, value))
            .Where(value => value is not null).Select(value => value!)
            .GroupBy(value => value.Candidate.EditionId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(value => value.Score).First())
            .OrderByDescending(value => value.Score)
            .ThenBy(value => value.Candidate.EditionId, StringComparer.Ordinal).ToArray();
        if (scored.Length == 0)
            return Proposal(EditionMetadataProposalStatus.NotFound);
        if (scored.Length > 1 && scored[0].Score == scored[1].Score)
            return Proposal(EditionMetadataProposalStatus.Ambiguous);
        return Proposal(
            EditionMetadataProposalStatus.Proposed,
            scored[0].Candidate,
            scored[0].Score,
            scored[0].Reasons);

        EditionMetadataProposal Proposal(
            EditionMetadataProposalStatus status,
            EditionMetadataCandidate? candidate = null,
            int score = 0,
            IEnumerable<string>? reasons = null,
            string? problemCode = null) => new(
            query.BookId,
            query.Provider,
            query.Fields,
            search.RetrievedAtUtc,
            status,
            candidate,
            score,
            reasons,
            problemCode);
    }

    private static ScoredCandidate? Score(
        EditionMetadataLookupQuery query,
        EditionMetadataCandidate candidate)
    {
        List<string> reasons = [];
        int score = candidate.Completeness;
        if (query.Identifier is not null)
        {
            if (!candidate.Identifiers.Any(value => value.CanonicalValue == query.Identifier)) return null;
            score += 10_000;
            reasons.Add("METADATA.EDITION.ISBN_EXACT");
        }
        else
        {
            string[] queryAuthorKeys = CandidateMetadataNormalizer.AuthorKeys(query.Authors);
            string[] candidateAuthorKeys = CandidateMetadataNormalizer.AuthorKeys(candidate.Authors);
            if (!CandidateMetadataNormalizer.HaveCompatibleAuthorIdentity(queryAuthorKeys, candidateAuthorKeys))
                return null;
            bool exactAuthor = CandidateMetadataNormalizer.HaveExactAuthorIdentity(
                queryAuthorKeys, candidateAuthorKeys);
            score += exactAuthor ? 1_000 : 500;
            reasons.Add(exactAuthor
                ? "METADATA.EDITION.AUTHOR_EXACT"
                : "METADATA.EDITION.AUTHOR_COMPATIBLE");

            bool exactTitle = CandidateMetadataNormalizer.TitleKeys(query.Title!).Intersect(
                CandidateMetadataNormalizer.TitleKeys(candidate.Title), StringComparer.Ordinal).Any();
            int titleSimilarity = CandidateMetadataNormalizer.TitleSimilarityPermille(
                CandidateMetadataNormalizer.TitleTokens(query.Title!),
                CandidateMetadataNormalizer.TitleTokens(candidate.Title));
            if (!exactTitle && titleSimilarity < 500) return null;
            score += exactTitle ? 3_000 : 2 * titleSimilarity;
            reasons.Add(exactTitle
                ? "METADATA.EDITION.TITLE_EXACT"
                : "METADATA.EDITION.TITLE_SIMILAR");
        }

        if (query.Language is not null && candidate.Languages.Count > 0)
        {
            if (!candidate.Languages.Contains(query.Language, StringComparer.Ordinal)) return null;
            score += 200;
            reasons.Add("METADATA.EDITION.LANGUAGE_COMPATIBLE");
        }
        return new(candidate, score, reasons);
    }

    private sealed record ScoredCandidate(
        EditionMetadataCandidate Candidate,
        int Score,
        IReadOnlyList<string> Reasons);
}
