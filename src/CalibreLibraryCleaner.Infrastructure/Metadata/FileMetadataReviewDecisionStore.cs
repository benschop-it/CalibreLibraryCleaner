using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Metadata;

internal sealed class FileMetadataReviewDecisionStore(MetadataReviewDecisionStoreOptions options) :
    IMetadataReviewDecisionStore
{
    private const string SchemaVersion = "metadata-review-decisions/1.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8,
    };

    public async Task<IReadOnlyList<MetadataReviewDecision>> ReadAsync(
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string identity = LibraryIdentity(libraryRoot);
        string root = GetStorageRoot();
        string path = Path.Combine(root, identity + ".json");
        if (!File.Exists(path) || !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _)) return [];
        FileInfo info = new(path);
        if (info.Length is <= 0 || info.Length > options.MaximumFileBytes)
            throw new InvalidDataException("The metadata review decision file exceeds its bound.");
        byte[] bytes = new byte[checked((int)info.Length)];
        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        DecisionDocument document;
        try
        {
            document = JsonSerializer.Deserialize<DecisionDocument>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The metadata review decision file is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The metadata review decision file is malformed.", exception);
        }
        if (document.SchemaVersion != SchemaVersion || document.LibraryIdentity != identity
            || document.Decisions is null || document.Decisions.Length > options.MaximumDecisions)
            throw new InvalidDataException("The metadata review decision file is incompatible.");
        MetadataReviewDecision[] decisions = document.Decisions.Select(Rehydrate)
            .OrderBy(value => value.Key.SubjectId.Value, StringComparer.Ordinal)
            .ThenBy(value => value.Key.ProposalPolicyVersion, StringComparer.Ordinal)
            .ThenBy(value => value.Key.EditionIdentity.Provider.Id, StringComparer.Ordinal)
            .ThenBy(value => value.Key.EditionIdentity.EditionId, StringComparer.Ordinal).ToArray();
        if (decisions.Select(value => value.Key).Distinct().Count() != decisions.Length)
            throw new InvalidDataException("The metadata review decision file contains duplicate keys.");
        return decisions;
    }

    public async Task WriteAsync(
        string libraryRoot,
        IReadOnlyList<MetadataReviewDecision> decisions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        cancellationToken.ThrowIfCancellationRequested();
        if (decisions.Count > options.MaximumDecisions
            || decisions.Select(value => value.Key).Distinct().Count() != decisions.Count)
            throw new ArgumentException("Metadata review decisions exceed their bounds.", nameof(decisions));
        string identity = LibraryIdentity(libraryRoot);
        string root = GetStorageRoot();
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            root = GetStorageRoot();
        }
        string path = Path.Combine(root, identity + ".json");
        if (File.Exists(path) && !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _))
            throw new IOException("The metadata review decision file is not physical.");
        DecisionDocument document = new(
            SchemaVersion,
            identity,
            decisions.OrderBy(value => value.Key.SubjectId.Value, StringComparer.Ordinal)
                .ThenBy(value => value.Key.ProposalPolicyVersion, StringComparer.Ordinal)
                .ThenBy(value => value.Key.EditionIdentity.Provider.Id, StringComparer.Ordinal)
                .ThenBy(value => value.Key.EditionIdentity.EditionId, StringComparer.Ordinal)
                .Select(CreateDocument).ToArray());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.LongLength > options.MaximumFileBytes)
            throw new InvalidDataException("The metadata review decision file exceeds its bound.");
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

    private string GetStorageRoot()
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StorageRoot));
        bool exists = Directory.Exists(root);
        if (!ExecutionPathGuard.TryRejectReparsePoints(root, exists, out _))
            throw new IOException("The metadata review decision directory is not physical.");
        return root;
    }

    private static string LibraryIdentity(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot)).ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static DecisionEntry CreateDocument(MetadataReviewDecision value) => new(
        value.Key.SubjectId.Value,
        value.Key.ProposalPolicyVersion,
        value.Key.EditionIdentity.Provider.Id,
        value.Key.EditionIdentity.Provider.Version,
        value.Key.EditionIdentity.EditionId,
        value.Key.GenerationId.Value,
        value.Key.Revision.Value,
        value.Apply);

    private static MetadataReviewDecision Rehydrate(DecisionEntry value) => new(
        new(
            new(value.SubjectId),
            value.ProposalPolicyVersion,
            new(new(value.ProviderId, value.ProviderVersion), value.EditionId),
            new(value.GenerationId),
            new(value.Revision)),
        value.Apply);

    private sealed record DecisionDocument(
        string SchemaVersion,
        string LibraryIdentity,
        DecisionEntry[] Decisions);

    private sealed record DecisionEntry(
        string SubjectId,
        string ProposalPolicyVersion,
        string ProviderId,
        string ProviderVersion,
        string EditionId,
        Guid GenerationId,
        long Revision,
        bool Apply);
}
