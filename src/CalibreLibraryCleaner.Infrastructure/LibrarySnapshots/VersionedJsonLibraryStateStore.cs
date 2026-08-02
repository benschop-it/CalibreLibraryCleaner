using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;

internal sealed class VersionedJsonLibraryStateStore(LibrarySnapshotStorageOptions options) : ILibraryStateStore
{
    private const string SchemaVersion = "library-state/1.0";
    private const string ManifestSuffix = ".library-state.json";
    private static readonly string EmptyDigest = new('0', 64);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public async Task WriteBaselineAsync(LibraryState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Revision.Value != 0 || !state.IsAuthoritative)
            throw new ArgumentException("A persisted baseline must be authoritative revision zero.", nameof(state));
        string root = StorageRoot();
        string libraryRoot = Canonicalize(state.Snapshot.Identity.LibraryRoot);
        RejectInsideLibrary(root, libraryRoot);
        Directory.CreateDirectory(root);
        string key = Key(libraryRoot);
        string generation = state.GenerationId.Value.ToString("N");
        string baselineName = $"{key}.{generation}.baseline.json";
        string journalName = $"{key}.{generation}.deltas.jsonl";
        await WriteAtomicAsync(Path.Combine(root, baselineName),
            LibrarySnapshotJsonSerializer.Serialize(state.Snapshot), cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(Path.Combine(root, journalName), [], cancellationToken).ConfigureAwait(false);
        StateManifest manifest = new(SchemaVersion, libraryRoot, state.GenerationId.Value, 0, 0,
            LibraryStateStatus.Authoritative, state.Snapshot.ScannedAt, state.ProjectedAtUtc, state.ProjectedAtUtc,
            baselineName, journalName, EmptyDigest, 0, null);
        await WriteManifestAsync(root, key, manifest, cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendDeltaAsync(
        string libraryRoot,
        LibraryStateDelta delta,
        LibraryState projectedState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(projectedState);
        string canonical = Canonicalize(libraryRoot);
        string root = StorageRoot();
        string key = Key(canonical);
        StateManifest manifest = await ReadManifestAsync(root, key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No persisted library-state baseline exists.");
        if (manifest.Status != LibraryStateStatus.Authoritative
            || manifest.GenerationId != delta.GenerationId.Value
            || manifest.Revision != delta.ExpectedRevision.Value
            || projectedState.GenerationId.Value != manifest.GenerationId
            || projectedState.Revision.Value != checked(manifest.Revision + 1))
            throw new InvalidOperationException("The persisted state manifest does not match the delta transition.");

        DeltaPayload payload = ToPayload(delta);
        string payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        string digest = Hash($"{manifest.HeadDigest}\n{payloadJson}");
        DeltaEvent entry = new(manifest.HeadDigest, digest, payload);
        string journalPath = Path.Combine(root, manifest.JournalFile);
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        await using (FileStream stream = new(journalPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        StateManifest updated = manifest with
        {
            Revision = projectedState.Revision.Value,
            ProjectedAtUtc = projectedState.ProjectedAtUtc,
            HeadDigest = digest,
            DeltaCount = checked(manifest.DeltaCount + 1),
        };
        if (options.StateDeltaCompactionThreshold > 0
            && updated.DeltaCount >= options.StateDeltaCompactionThreshold)
            await CompactAsync(root, key, updated, projectedState, cancellationToken).ConfigureAwait(false);
        else
            await WriteManifestAsync(root, key, updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUncertaintyAsync(
        string libraryRoot,
        LibraryState state,
        CancellationToken cancellationToken)
    {
        if (state.Status != LibraryStateStatus.Uncertain || state.Uncertainty is null)
            throw new ArgumentException("Only uncertain state can persist uncertainty evidence.", nameof(state));
        string root = StorageRoot();
        string key = Key(Canonicalize(libraryRoot));
        StateManifest manifest = await ReadManifestAsync(root, key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No persisted library-state baseline exists.");
        StateManifest updated = manifest with
        {
            Status = LibraryStateStatus.Uncertain,
            ProjectedAtUtc = state.ProjectedAtUtc,
            Uncertainty = state.Uncertainty,
        };
        await WriteManifestAsync(root, key, updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LibraryState?> ReadAsync(string libraryRoot, CancellationToken cancellationToken)
    {
        string canonical = Canonicalize(libraryRoot);
        string root = StorageRoot();
        string key = Key(canonical);
        StateManifest? manifest = await ReadManifestAsync(root, key, cancellationToken).ConfigureAwait(false);
        if (manifest is null) return null;
        if (manifest.SchemaVersion != SchemaVersion || !PathComparer.Equals(manifest.LibraryRoot, canonical))
            throw new InvalidDataException("The persisted library-state manifest is unsupported or belongs to another library.");
        LibrarySnapshot baseline = await ReadBaselineAsync(Path.Combine(root, manifest.BaselineFile), cancellationToken)
            .ConfigureAwait(false);
        LibraryState state = new(new(manifest.GenerationId), new(manifest.BaseRevision),
            LibraryStateStatus.Authoritative, baseline, manifest.CheckpointProjectedAtUtc);
        string head = EmptyDigest;
        try
        {
            string journalPath = Path.Combine(root, manifest.JournalFile);
            foreach (string line in await File.ReadAllLinesAsync(journalPath, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                DeltaEvent entry = JsonSerializer.Deserialize<DeltaEvent>(line, JsonOptions)
                    ?? throw new InvalidDataException("A persisted state delta is empty.");
                string payloadJson = JsonSerializer.Serialize(entry.Payload, JsonOptions);
                string expected = Hash($"{head}\n{payloadJson}");
                if (entry.PreviousDigest != head || entry.Digest != expected)
                    throw new InvalidDataException("The persisted state delta hash chain is invalid.");
                LibraryStateDelta delta = FromPayload(entry.Payload, state.GenerationId);
                state = LibraryStateDeltaPolicy.Apply(state, delta);
                head = entry.Digest;
            }
            if (state.Revision.Value != manifest.Revision || head != manifest.HeadDigest)
                throw new InvalidDataException("The persisted state manifest and delta journal disagree.");
            if (manifest.Status == LibraryStateStatus.Uncertain)
            {
                LibraryStateUncertainty uncertainty = manifest.Uncertainty
                    ?? new("PERSISTED_STATE_UNCERTAIN", "The persisted state manifest is uncertain.", manifest.ProjectedAtUtc);
                return state.MarkUncertain(uncertainty);
            }
            return state;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException
                                           or InvalidOperationException or ArgumentException or OverflowException)
        {
            return state.MarkUncertain(new("STATE_REPLAY_FAILED",
                $"Persisted state replay failed: {exception.Message}",
                manifest.ProjectedAtUtc < state.ProjectedAtUtc ? state.ProjectedAtUtc : manifest.ProjectedAtUtc));
        }
    }

    private static DeltaPayload ToPayload(LibraryStateDelta delta) => delta switch
    {
        RemoveFormatLibraryStateDelta value => new("remove-format", value.ExpectedRevision.Value,
            value.OperationId, value.AppliedAtUtc, value.RecordId.Value, value.Format,
            value.ExpectedFingerprint.SizeInBytes, value.ExpectedFingerprint.Sha256.Value, null, null, null, null),
        RemoveRecordLibraryStateDelta value => new("remove-empty-record", value.ExpectedRevision.Value,
            value.OperationId, value.AppliedAtUtc, value.RecordId.Value, null, null, null, null, null, null, null),
        AddOrReplaceFormatLibraryStateDelta value => new("add-or-replace-format", value.ExpectedRevision.Value,
            value.OperationId, value.AppliedAtUtc, value.RecordId.Value, value.Format,
            value.Fingerprint.SizeInBytes, value.Fingerprint.Sha256.Value,
            value.ExpectedPreviousFingerprint?.SizeInBytes,
            value.ExpectedPreviousFingerprint?.Sha256.Value, null, null),
        RemoveRecordWithContentLibraryStateDelta value => new("remove-record-with-content", value.ExpectedRevision.Value,
            value.OperationId, value.AppliedAtUtc, value.RecordId.Value, null, null, null, null, null, null, null),
        CreateRecordLibraryStateDelta value => new("create-record", value.ExpectedRevision.Value,
            value.OperationId, value.AppliedAtUtc, value.RecordId.Value, null, null, null, null, null,
            value.Authors.ToArray(), [value.Title, value.AuthorSort]),
        SetMetadataLibraryStateDelta value => new("set-metadata", value.ExpectedRevision.Value,
            value.OperationId, value.AppliedAtUtc, value.RecordId.Value, value.Field.ToString(),
            null, null, null, null, value.Values.ToArray(), null),
        _ => throw new ArgumentOutOfRangeException(nameof(delta), delta.GetType().Name, "Unsupported persisted state delta."),
    };

    private static LibraryStateDelta FromPayload(DeltaPayload value, LibraryStateGenerationId generation)
    {
        LibraryStateRevision revision = new(value.ExpectedRevision);
        CalibreBookId recordId = new(value.RecordId);
        return value.Kind switch
        {
            "remove-format" => new RemoveFormatLibraryStateDelta(generation, revision, value.OperationId,
                value.AppliedAtUtc, recordId, value.Format!, Fingerprint(value.SizeInBytes, value.Sha256)),
            "remove-empty-record" => new RemoveRecordLibraryStateDelta(generation, revision,
                value.OperationId, value.AppliedAtUtc, recordId),
            "add-or-replace-format" => new AddOrReplaceFormatLibraryStateDelta(generation, revision,
                value.OperationId, value.AppliedAtUtc, recordId, value.Format!,
                Fingerprint(value.SizeInBytes, value.Sha256),
                value.PreviousSizeInBytes is null ? null : Fingerprint(value.PreviousSizeInBytes, value.PreviousSha256)),
            "remove-record-with-content" => new RemoveRecordWithContentLibraryStateDelta(generation, revision,
                value.OperationId, value.AppliedAtUtc, recordId),
            "create-record" => new CreateRecordLibraryStateDelta(generation, revision,
                value.OperationId, value.AppliedAtUtc, recordId,
                value.ExtraValues?[0] ?? throw new InvalidDataException("A projected record has no title."),
                value.Values ?? throw new InvalidDataException("A projected record has no authors."),
                value.ExtraValues?[1] ?? throw new InvalidDataException("A projected record has no author sort.")),
            "set-metadata" => new SetMetadataLibraryStateDelta(generation, revision,
                value.OperationId, value.AppliedAtUtc, recordId,
                Enum.Parse<LibraryMetadataField>(value.Format ?? string.Empty, false),
                value.Values ?? []),
            _ => throw new InvalidDataException("The persisted state delta kind is unsupported."),
        };
    }

    private static FormatFileFingerprint Fingerprint(long? size, string? sha256) =>
        new(size ?? throw new InvalidDataException("A persisted fingerprint has no size."),
            new(sha256 ?? throw new InvalidDataException("A persisted fingerprint has no digest.")));

    private static async Task<LibrarySnapshot> ReadBaselineAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        LibrarySnapshotJsonReadResult result = LibrarySnapshotJsonSerializer.Deserialize(bytes);
        return result.Snapshot ?? throw new InvalidDataException(result.Error);
    }

    private static async Task<StateManifest?> ReadManifestAsync(
        string root,
        string key,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(root, key + ManifestSuffix);
        if (!File.Exists(path)) return null;
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<StateManifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The library-state manifest is empty.");
    }

    private static Task WriteManifestAsync(
        string root,
        string key,
        StateManifest manifest,
        CancellationToken cancellationToken) => WriteAtomicAsync(
        Path.Combine(root, key + ManifestSuffix), JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions), cancellationToken);

    private static async Task CompactAsync(
        string root,
        string key,
        StateManifest previous,
        LibraryState state,
        CancellationToken cancellationToken)
    {
        string checkpoint = $"{key}.{state.GenerationId.Value:N}.{state.Revision.Value}.checkpoint.json";
        string journal = $"{key}.{state.GenerationId.Value:N}.{state.Revision.Value}.deltas.jsonl";
        await WriteAtomicAsync(Path.Combine(root, checkpoint),
            LibrarySnapshotJsonSerializer.Serialize(state.Snapshot), cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(Path.Combine(root, journal), [], cancellationToken).ConfigureAwait(false);
        StateManifest compacted = previous with
        {
            BaseRevision = state.Revision.Value,
            Revision = state.Revision.Value,
            CheckpointProjectedAtUtc = state.ProjectedAtUtc,
            BaselineFile = checkpoint,
            JournalFile = journal,
            HeadDigest = EmptyDigest,
            DeltaCount = 0,
        };
        await WriteManifestAsync(root, key, compacted, cancellationToken).ConfigureAwait(false);
        DeleteSuperseded(Path.Combine(root, previous.BaselineFile), Path.Combine(root, checkpoint));
        DeleteSuperseded(Path.Combine(root, previous.JournalFile), Path.Combine(root, journal));
    }

    private static void DeleteSuperseded(string path, string replacement)
    {
        if (!string.Equals(path, replacement, StringComparison.Ordinal) && File.Exists(path)) File.Delete(path);
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string StorageRoot()
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StorageRoot));
        if (Directory.Exists(root) && !ExecutionPathGuard.TryRejectReparsePoints(root, true, out _))
            throw new IOException("The library-state storage directory is not physical.");
        return root;
    }

    private static string Canonicalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string Key(string libraryRoot)
    {
        string value = OperatingSystem.IsWindows() ? libraryRoot.ToUpperInvariant() : libraryRoot;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static void RejectInsideLibrary(string storageRoot, string libraryRoot)
    {
        if (PathComparer.Equals(storageRoot, libraryRoot)
            || storageRoot.StartsWith(libraryRoot + Path.DirectorySeparatorChar, PathComparison)
            || libraryRoot.StartsWith(storageRoot + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidOperationException("Library-state artifacts must be outside the Calibre library.");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record StateManifest(
        string SchemaVersion,
        string LibraryRoot,
        Guid GenerationId,
        long BaseRevision,
        long Revision,
        LibraryStateStatus Status,
        DateTimeOffset ScannedAtUtc,
        DateTimeOffset CheckpointProjectedAtUtc,
        DateTimeOffset ProjectedAtUtc,
        string BaselineFile,
        string JournalFile,
        string HeadDigest,
        int DeltaCount,
        LibraryStateUncertainty? Uncertainty);

    private sealed record DeltaEvent(string PreviousDigest, string Digest, DeltaPayload Payload);

    private sealed record DeltaPayload(
        string Kind,
        long ExpectedRevision,
        string OperationId,
        DateTimeOffset AppliedAtUtc,
        long RecordId,
        string? Format,
        long? SizeInBytes,
        string? Sha256,
        long? PreviousSizeInBytes,
        string? PreviousSha256,
        string[]? Values,
        string[]? ExtraValues);
}
