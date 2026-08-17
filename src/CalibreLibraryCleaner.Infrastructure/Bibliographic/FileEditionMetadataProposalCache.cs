using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Bibliographic;

internal sealed class FileEditionMetadataProposalCache(EditionMetadataProposalCacheOptions options) :
    IEditionMetadataProposalCache
{
    private const string SchemaVersion = "edition-metadata-proposal-cache/1.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
    };

    public async Task<EditionMetadataProposal?> TryReadAsync(
        EditionMetadataLookupQuery query,
        DateTimeOffset minimumRetrievedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string identity = CreateIdentity(query);
            string root = GetStorageRoot();
            string path = Path.Combine(root, identity + ".json");
            if (!File.Exists(path) || !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _)) return null;
            FileInfo info = new(path);
            if (info.Length is <= 0 || info.Length > options.MaximumEntryBytes) return null;
            byte[] bytes = new byte[checked((int)info.Length)];
            await using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            CacheDocument? document = JsonSerializer.Deserialize<CacheDocument>(bytes, JsonOptions);
            if (document is null
                || document.SchemaVersion != SchemaVersion
                || document.ProviderId != query.Provider.Id
                || document.ProviderVersion != query.Provider.Version
                || document.QueryPolicyVersion != EditionMetadataLookupQuery.PolicyVersion
                || document.ProposalPolicyVersion != EditionMetadataProposal.PolicyVersion
                || document.QueryIdentity != identity
                || document.QueryFields != query.Fields
                || document.RetrievedAtUtc < minimumRetrievedAtUtc)
                return null;
            return new(
                query.BookId,
                query.Provider,
                document.QueryFields,
                document.RetrievedAtUtc,
                document.Status,
                Rehydrate(document.Candidate),
                document.MatchScore,
                document.ReasonCodes,
                document.ProblemCode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public async Task WriteAsync(
        EditionMetadataLookupQuery query,
        EditionMetadataProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.BookId != proposal.BookId || query.Provider != proposal.Provider
            || query.Fields != proposal.QueryFields)
            throw new ArgumentException("The edition metadata cache query and proposal do not agree.");
        try
        {
            string root = GetStorageRoot();
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
                root = GetStorageRoot();
            }
            string identity = CreateIdentity(query);
            string path = Path.Combine(root, identity + ".json");
            if (File.Exists(path) && !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _)) return;
            CacheDocument document = new(
                SchemaVersion,
                query.Provider.Id,
                query.Provider.Version,
                EditionMetadataLookupQuery.PolicyVersion,
                EditionMetadataProposal.PolicyVersion,
                identity,
                query.Fields,
                proposal.RetrievedAtUtc,
                proposal.Status,
                CreateDocument(proposal.Candidate),
                proposal.MatchScore,
                proposal.ReasonCodes.ToArray(),
                proposal.ProblemCode);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (bytes.LongLength > options.MaximumEntryBytes) return;
            string temporary = Path.Combine(root, $".{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException or OverflowException)
        { }
    }

    public Task PruneAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string root = GetStorageRoot();
            if (!Directory.Exists(root)) return Task.CompletedTask;
            FileInfo[] entries = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                .Select(value => new FileInfo(value))
                .Where(value => ExecutionPathGuard.TryRejectReparsePointLeaf(value.FullName, true, out _))
                .OrderBy(value => value.LastWriteTimeUtc).ThenBy(value => value.Name, StringComparer.Ordinal)
                .ToArray();
            long total = entries.Sum(value => value.Length);
            foreach (FileInfo entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (total <= options.MaximumTotalBytes) break;
                long length = entry.Length;
                entry.Delete();
                total -= length;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or OverflowException)
        { }
        return Task.CompletedTask;
    }

    private string GetStorageRoot()
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StorageRoot));
        bool exists = Directory.Exists(root);
        if (!ExecutionPathGuard.TryRejectReparsePoints(root, exists, out _))
            throw new IOException("The edition metadata proposal cache directory is not physical.");
        return root;
    }

    private static string CreateIdentity(EditionMetadataLookupQuery query)
    {
        StringBuilder canonical = new();
        Append(canonical, SchemaVersion);
        Append(canonical, EditionMetadataLookupQuery.PolicyVersion);
        Append(canonical, EditionMetadataProposal.PolicyVersion);
        Append(canonical, query.Provider.Id);
        Append(canonical, query.Provider.Version);
        Append(canonical, ((int)query.Fields).ToString(CultureInfo.InvariantCulture));
        Append(canonical, query.Identifier ?? string.Empty);
        Append(canonical, query.Title ?? string.Empty);
        foreach (string author in query.Authors) Append(canonical, author);
        Append(canonical, query.Language ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder target, string value) => target
        .Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
        .Append(':').Append(value).Append('|');

    private static CandidateDocument? CreateDocument(EditionMetadataCandidate? value) => value is null
        ? null
        : new(
            value.WorkId,
            value.EditionId,
            value.Title,
            value.Authors.ToArray(),
            value.Identifiers.Select(identifier => new IdentifierDocument(
                identifier.Type, identifier.Value)).ToArray(),
            value.Publisher,
            value.PublicationDate?.Year,
            value.PublicationDate?.Month,
            value.PublicationDate?.Day,
            value.Languages.ToArray(),
            value.Series,
            value.SeriesIndex,
            value.Cover?.SourceId,
            value.Cover?.Url);

    private static EditionMetadataCandidate? Rehydrate(CandidateDocument? value) => value is null
        ? null
        : new(
            value.WorkId,
            value.EditionId,
            value.Title,
            value.Authors,
            value.Identifiers.Select(identifier => new EditionMetadataIdentifier(
                identifier.Type, identifier.Value)),
            value.Publisher,
            value.PublicationYear is null ? null : new(
                value.PublicationYear.Value, value.PublicationMonth, value.PublicationDay),
            value.Languages,
            value.Series,
            value.SeriesIndex,
            value.CoverSourceId is null || value.CoverUrl is null
                ? null
                : new(value.CoverSourceId, value.CoverUrl));

    private sealed record CacheDocument(
        string SchemaVersion,
        string ProviderId,
        string ProviderVersion,
        string QueryPolicyVersion,
        string ProposalPolicyVersion,
        string QueryIdentity,
        EditionMetadataQueryFields QueryFields,
        DateTimeOffset RetrievedAtUtc,
        EditionMetadataProposalStatus Status,
        CandidateDocument? Candidate,
        int MatchScore,
        string[] ReasonCodes,
        string? ProblemCode);

    private sealed record CandidateDocument(
        string WorkId,
        string EditionId,
        string Title,
        string[] Authors,
        IdentifierDocument[] Identifiers,
        string? Publisher,
        int? PublicationYear,
        int? PublicationMonth,
        int? PublicationDay,
        string[] Languages,
        string? Series,
        decimal? SeriesIndex,
        string? CoverSourceId,
        string? CoverUrl);

    private sealed record IdentifierDocument(string Type, string Value);
}
