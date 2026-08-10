using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;

internal sealed class VersionedJsonLibraryStateStore(
    LibrarySnapshotStorageOptions options,
    ILogger<VersionedJsonLibraryStateStore>? logger = null) : ILibraryStateStore
{
    private const string SchemaVersion = "library-state/1.0";
    private const string ManifestSuffix = ".library-state.json";
    private static readonly string EmptyDigest = new('0', 64);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
    private static readonly Action<ILogger, string, Exception?> LogPruneFailure = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(1, "LibraryStateArtifactPruneFailed"),
        "Could not prune unreferenced library state artifact {ArtifactName}.");
    private readonly ILogger<VersionedJsonLibraryStateStore> _logger = logger
        ?? NullLogger<VersionedJsonLibraryStateStore>.Instance;

    public async Task<IReadOnlyList<PersistedLibraryStateInfo>> ListAsync(CancellationToken cancellationToken)
    {
        string root = StorageRoot();
        if (!Directory.Exists(root)) return [];

        List<PersistedLibraryStateInfo> states = [];
        foreach (string path in Directory.EnumerateFiles(root, $"*{ManifestSuffix}", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                StateManifest manifest = await ReadManifestFileAsync(path, cancellationToken).ConfigureAwait(false);
                string canonical = Canonicalize(manifest.LibraryRoot);
                if (manifest.SchemaVersion != SchemaVersion
                    || !string.Equals(Path.GetFileName(path), Key(canonical) + ManifestSuffix, StringComparison.Ordinal)
                    || manifest.GenerationId == Guid.Empty
                    || manifest.BaseRevision < 0
                    || manifest.Revision < manifest.BaseRevision
                    || manifest.DeltaCount < 0)
                {
                    continue;
                }

                states.Add(new(canonical, manifest.ScannedAtUtc, manifest.ProjectedAtUtc,
                    new(manifest.GenerationId), new(manifest.Revision), manifest.Status));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or JsonException or InvalidDataException or ArgumentException
                                               or NotSupportedException or OverflowException)
            {
            }
        }

        return states.OrderBy(state => state.LibraryRoot, PathComparer).ToArray();
    }

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
            baselineName, journalName, EmptyDigest, 0, null, null, ToPayload(state.WorkflowCheckpoint));
        await WriteManifestAsync(root, key, manifest, cancellationToken).ConfigureAwait(false);
        PruneUnreferencedStateFiles(root, key, manifest);
    }

    public async Task AppendDeltaAsync(
        string libraryRoot,
        LibraryStateDelta delta,
        LibraryState projectedState,
        CancellationToken cancellationToken) => await AppendDeltaBatchAsync(
        libraryRoot, [delta], projectedState, compactIfThresholdReached: true,
        null, completeMutationIntent: false, cancellationToken).ConfigureAwait(false);

    public async Task AppendDeltaBatchAsync(
        string libraryRoot,
        IReadOnlyList<LibraryStateDelta> deltas,
        LibraryState projectedState,
        bool compactIfThresholdReached,
        string? mutationIntentId,
        bool completeMutationIntent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        if (deltas.Count == 0) throw new ArgumentException("At least one state delta is required.", nameof(deltas));
        ArgumentNullException.ThrowIfNull(projectedState);
        string canonical = Canonicalize(libraryRoot);
        string root = StorageRoot();
        string key = Key(canonical);
        StateManifest manifest = await ReadManifestAsync(root, key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No persisted library-state baseline exists.");
        if (manifest.Status != LibraryStateStatus.Authoritative
            || deltas.Any(delta => delta is null || manifest.GenerationId != delta.GenerationId.Value)
            || deltas.Select((delta, index) => delta.ExpectedRevision.Value == checked(manifest.Revision + index))
                .Any(matches => !matches)
            || projectedState.GenerationId.Value != manifest.GenerationId
            || projectedState.Revision.Value != checked(manifest.Revision + deltas.Count))
            throw new InvalidOperationException("The persisted state manifest does not match the delta batch transition.");
        ValidateMutationIntent(manifest, deltas, mutationIntentId, completeMutationIntent);

        string head = manifest.HeadDigest;
        StringBuilder journalAppend = new();
        foreach (LibraryStateDelta delta in deltas)
        {
            DeltaPayload payload = ToPayload(delta);
            string payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
            string digest = Hash($"{head}\n{payloadJson}");
            DeltaEvent entry = new(head, digest, payload);
            journalAppend.Append(JsonSerializer.Serialize(entry, JsonOptions)).Append('\n');
            head = digest;
        }
        string journalPath = Path.Combine(root, manifest.JournalFile);
        byte[] lines = Encoding.UTF8.GetBytes(journalAppend.ToString());
        await using (FileStream stream = new(journalPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(lines, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        StateManifest updated = manifest with
        {
            Revision = projectedState.Revision.Value,
            ProjectedAtUtc = projectedState.ProjectedAtUtc,
            HeadDigest = head,
            DeltaCount = checked(manifest.DeltaCount + deltas.Count),
            PendingMutationIntent = completeMutationIntent ? null : manifest.PendingMutationIntent,
        };
        if (compactIfThresholdReached
            && options.StateDeltaCompactionThreshold > 0
            && updated.DeltaCount >= options.StateDeltaCompactionThreshold)
            await CompactStateAsync(root, key, updated, projectedState, cancellationToken).ConfigureAwait(false);
        else
            await WriteManifestAsync(root, key, updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteMutationIntentAsync(
        string libraryRoot,
        LibraryStateMutationIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        string canonical = Canonicalize(libraryRoot);
        string root = StorageRoot();
        string key = Key(canonical);
        StateManifest manifest = await ReadManifestAsync(root, key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No persisted library-state baseline exists.");
        if (manifest.Status != LibraryStateStatus.Authoritative
            || manifest.PendingMutationIntent is not null
            || manifest.GenerationId != intent.GenerationId.Value
            || manifest.Revision != intent.ExpectedRevision.Value)
            throw new InvalidOperationException("The persisted state manifest cannot accept the mutation intent.");
        await WriteManifestAsync(root, key, manifest with { PendingMutationIntent = ToPayload(intent) }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WriteWorkflowCheckpointAsync(
        string libraryRoot,
        LibraryState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        string canonical = Canonicalize(libraryRoot);
        string root = StorageRoot();
        string key = Key(canonical);
        StateManifest manifest = await ReadManifestAsync(root, key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No persisted library-state baseline exists.");
        if (manifest.Status != LibraryStateStatus.Authoritative
            || !state.IsWorkflowCheckpointCurrent
            || manifest.PendingMutationIntent is not null
            || manifest.GenerationId != state.GenerationId.Value
            || manifest.Revision != state.Revision.Value
            || !PathComparer.Equals(manifest.LibraryRoot, canonical))
            throw new InvalidOperationException(
                "The persisted state manifest cannot accept the workflow checkpoint.");
        await WriteManifestAsync(root, key, manifest with
        {
            WorkflowCheckpoint = ToPayload(state.WorkflowCheckpoint),
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompactAsync(
        string libraryRoot,
        LibraryState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        string canonical = Canonicalize(libraryRoot);
        string root = StorageRoot();
        string key = Key(canonical);
        StateManifest manifest = await ReadManifestAsync(root, key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No persisted library-state baseline exists.");
        if (manifest.Status != LibraryStateStatus.Authoritative
            || state.Status != LibraryStateStatus.Authoritative
            || manifest.PendingMutationIntent is not null
            || manifest.GenerationId != state.GenerationId.Value
            || manifest.Revision != state.Revision.Value)
            throw new InvalidOperationException("The persisted state manifest does not match the checkpoint state.");
        if (manifest.DeltaCount > 0)
            await CompactStateAsync(root, key, manifest, state, cancellationToken).ConfigureAwait(false);
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
            state = ApplyWorkflowCheckpoint(state, manifest.WorkflowCheckpoint);
            if (manifest.Status == LibraryStateStatus.Uncertain)
            {
                LibraryStateUncertainty uncertainty = manifest.Uncertainty
                    ?? new("PERSISTED_STATE_UNCERTAIN", "The persisted state manifest is uncertain.", manifest.ProjectedAtUtc);
                return state.MarkUncertain(uncertainty);
            }
            if (manifest.PendingMutationIntent is not null)
            {
                LibraryStateMutationIntent intent = FromPayload(manifest.PendingMutationIntent);
                return state.MarkUncertain(new("MUTATION_INTENT_INCOMPLETE",
                    "A persisted mutation intent has no unambiguous committed outcome.",
                    intent.CreatedAtUtc, intent.IntentId));
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

    private static void ValidateMutationIntent(
        StateManifest manifest,
        IReadOnlyList<LibraryStateDelta> deltas,
        string? mutationIntentId,
        bool completeMutationIntent)
    {
        if (mutationIntentId is null)
        {
            if (completeMutationIntent || manifest.PendingMutationIntent is not null)
                throw new InvalidOperationException("A pending mutation intent blocks an unrelated delta batch.");
            return;
        }
        MutationIntentPayload intent = manifest.PendingMutationIntent
            ?? throw new InvalidOperationException("The committed mutation batch has no persisted intent.");
        long committedCount = checked(manifest.Revision - intent.ExpectedRevision);
        if (!string.Equals(intent.IntentId, mutationIntentId, StringComparison.Ordinal)
            || intent.GenerationId != manifest.GenerationId
            || committedCount < 0
            || committedCount > intent.OperationCount
            || deltas.Count > intent.OperationCount
            || completeMutationIntent && committedCount + deltas.Count != intent.OperationCount
            || !completeMutationIntent && committedCount + deltas.Count >= intent.OperationCount)
            throw new InvalidOperationException("The committed mutation batch does not match its persisted intent.");
    }

    private static MutationIntentPayload ToPayload(LibraryStateMutationIntent intent) => new(
        intent.IntentId, intent.GenerationId.Value, intent.ExpectedRevision.Value,
        intent.OperationCount, intent.CreatedAtUtc);

    private static LibraryStateMutationIntent FromPayload(MutationIntentPayload intent) => new(
        intent.IntentId, new(intent.GenerationId), new(intent.ExpectedRevision),
        intent.OperationCount, intent.CreatedAtUtc);

    private static WorkflowCheckpointPayload ToPayload(LibraryWorkflowCheckpoint checkpoint) => new(
        checkpoint.Phase,
        checkpoint.GenerationId.Value,
        checkpoint.Revision.Value,
        checkpoint.PolicyVersions.Workflow,
        checkpoint.PolicyVersions.ExactAnalysis,
        checkpoint.PolicyVersions.ExactCleanup,
        checkpoint.PolicyVersions.CandidateAnalysis,
        checkpoint.PublishedAtUtc);

    private static LibraryState ApplyWorkflowCheckpoint(
        LibraryState state,
        WorkflowCheckpointPayload? payload)
    {
        if (payload is null)
            return WithConservativeWorkflowCheckpoint(state);
        LibraryWorkflowPolicyVersions versions = new(
            payload.WorkflowPolicyVersion,
            payload.ExactAnalysisPolicyVersion,
            payload.ExactCleanupPolicyVersion,
            payload.CandidateAnalysisPolicyVersion);
        if (versions != LibraryWorkflowPolicyVersions.Current)
            return WithConservativeWorkflowCheckpoint(state);
        LibraryWorkflowCheckpoint checkpoint = new(
            payload.Phase,
            new(payload.GenerationId),
            new(payload.Revision),
            versions,
            payload.PublishedAtUtc);
        return new(state.GenerationId, state.Revision, state.Status, state.Snapshot,
            state.ProjectedAtUtc, state.Uncertainty, checkpoint);
    }

    private static LibraryState WithConservativeWorkflowCheckpoint(LibraryState state) => new(
        state.GenerationId,
        state.Revision,
        state.Status,
        state.Snapshot,
        state.ProjectedAtUtc,
        state.Uncertainty,
        new(
            LibraryWorkflowPhase.RequiresExactAnalysis,
            state.GenerationId,
            state.Revision,
            LibraryWorkflowPolicyVersions.Current,
            state.ProjectedAtUtc));

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
        return await ReadManifestFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StateManifest> ReadManifestFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
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

    private async Task CompactStateAsync(
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
        PruneUnreferencedStateFiles(root, key, compacted);
    }

    private void PruneUnreferencedStateFiles(string root, string key, StateManifest active)
    {
        HashSet<string> retained = new(StringComparer.Ordinal)
        {
            active.BaselineFile,
            active.JournalFile,
        };
        IEnumerable<string> candidates = Directory.EnumerateFiles(root, $"{key}.*.baseline.json", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(root, $"{key}.*.checkpoint.json", SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(root, $"{key}.*.deltas.jsonl", SearchOption.TopDirectoryOnly));
        foreach (string path in candidates)
        {
            string name = Path.GetFileName(path);
            if (retained.Contains(name)) continue;
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogPruneFailure(_logger, name, exception);
            }
        }
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
        LibraryStateUncertainty? Uncertainty,
        MutationIntentPayload? PendingMutationIntent = null,
        WorkflowCheckpointPayload? WorkflowCheckpoint = null);

    private sealed record MutationIntentPayload(
        string IntentId,
        Guid GenerationId,
        long ExpectedRevision,
        int OperationCount,
        DateTimeOffset CreatedAtUtc);

    private sealed record WorkflowCheckpointPayload(
        LibraryWorkflowPhase Phase,
        Guid GenerationId,
        long Revision,
        string WorkflowPolicyVersion,
        string ExactAnalysisPolicyVersion,
        string ExactCleanupPolicyVersion,
        string CandidateAnalysisPolicyVersion,
        DateTimeOffset PublishedAtUtc);

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
