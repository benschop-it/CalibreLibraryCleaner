using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Recoveries;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Recovery;

internal sealed class FileRecoveryHistoryStore(
    ExecutionStorageOptions options) : IRecoveryHistoryStore, IRecoveryResolutionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public async Task<bool> HasUnresolvedRecoveryAsync(
        string libraryUuid,
        string libraryRoot,
        string? excludingSourceExecutionId,
        CancellationToken cancellationToken)
    {
        foreach (RecoveryHistoryEntry entry in await ReadAsync(
                     libraryUuid, libraryRoot, cancellationToken).ConfigureAwait(false))
        {
            if (excludingSourceExecutionId is not null
                && entry.SourceExecutionId.ToString() == excludingSourceExecutionId) continue;
            if (!IsSafelyTerminal(entry)
                || string.IsNullOrWhiteSpace(entry.JournalIdentity)
                || !await JournalIdentityIsSafeAsync(
                    entry.JournalIdentity, libraryRoot).ConfigureAwait(false)
                || !await JsonLinesRecoveryJournalStore.VerifyTerminalAsync(
                    entry, cancellationToken).ConfigureAwait(false))
                return true;
        }
        return false;
    }

    public async Task RecordAsync(
        RecoveryHistoryEntry entry,
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string root = GetRoot(libraryRoot, "recoveries");
        Directory.CreateDirectory(root);
        root = GetRoot(libraryRoot, "recoveries");
        string executionRoot = Path.Combine(root, entry.RecoveryExecutionId.ToString());
        Directory.CreateDirectory(executionRoot);
        if (!ExecutionPathGuard.TryRejectReparsePoints(executionRoot, true, out _))
            throw new IOException("The recovery history execution directory is linked.");
        int next = Directory.EnumerateFiles(executionRoot, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => int.TryParse(Path.GetFileNameWithoutExtension(path),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int value)
                ? value : 0)
            .DefaultIfEmpty().Max() + 1;
        string path = Path.Combine(executionRoot,
            $"{next.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)}.json");
        await WriteAtomicAsync(path, entry, overwrite: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecoveryHistoryEntry>> ReadAsync(
        string libraryUuid,
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        string root = GetRoot(libraryRoot, "recoveries");
        if (!Directory.Exists(root)) return [];
        List<(string Path, RecoveryHistoryEntry Entry)> entries = [];
        foreach (string path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RecoveryHistoryEntry? entry = await ReadAsync<RecoveryHistoryEntry>(
                    path, cancellationToken).ConfigureAwait(false);
                if (entry is not null && entry.LibraryUuid == libraryUuid)
                    entries.Add((path, entry));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                       or JsonException or ArgumentException)
            {
                entries.Add((path, new(new(Guid.NewGuid()), new(Guid.NewGuid()),
                    new(new string('0', 64)), new(Guid.NewGuid()), libraryUuid,
                    RecoveryExecutionState.ManualInterventionRequired,
                    RecoveryFailureClassification.JournalOrStorage,
                    string.Empty, path, null, DateTimeOffset.UnixEpoch,
                    true, false, false, false)));
            }
        }
        return entries.GroupBy(value => value.Entry.RecoveryExecutionId)
            .Select(group => group.OrderBy(value => value.Path, StringComparer.Ordinal).Last().Entry)
            .OrderByDescending(value => value.FinishedAtUtc)
            .ThenBy(value => value.RecoveryExecutionId.ToString(), StringComparer.Ordinal).ToArray();
    }

    public async Task<bool> IsSourceExecutionResolvedAsync(
        string sourceExecutionId,
        string libraryUuid,
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        string path = ResolutionPath(libraryRoot, sourceExecutionId);
        if (!File.Exists(path)) return false;
        try
        {
            RecoveryResolutionEntry? entry = await ReadAsync<RecoveryResolutionEntry>(
                path, cancellationToken).ConfigureAwait(false);
            return entry is not null && entry.SourceExecutionId.ToString() == sourceExecutionId
                && entry.LibraryUuid == libraryUuid
                && entry.State == RecoveryExecutionState.Recovered
                && entry.CanonicalRootIdentityDigest
                    == RecoverySnapshotFingerprintPolicy.ComputeCanonicalRootIdentity(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot))).Value
                && await JournalIdentityIsSafeAsync(
                    entry.RecoveryJournalIdentity, libraryRoot).ConfigureAwait(false)
                && await JsonLinesRecoveryJournalStore.VerifyRecoveredAsync(
                    entry.RecoveryJournalIdentity, entry, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                   or JsonException or ArgumentException)
        {
            return false;
        }
    }

    public async Task RecordResolutionAsync(
        RecoveryResolutionEntry entry,
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.State != RecoveryExecutionState.Recovered
            || entry.RecoveryJournalFinalHash.Length != 64
            || entry.RecoveryJournalFinalHash.Any(character => !Uri.IsHexDigit(character))
            || entry.CurrentStateManifestFileDigest is null
            || entry.CurrentStateManifestInternalDigest is null
            || entry.CanonicalRootIdentityDigest is null
            || entry.SourceJournalFileDigest is null
            || entry.OriginalManifestFileDigest is null
            || entry.CanonicalRootIdentityDigest
                != RecoverySnapshotFingerprintPolicy.ComputeCanonicalRootIdentity(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot))).Value
            || !await JournalIdentityIsSafeAsync(
                entry.RecoveryJournalIdentity, libraryRoot).ConfigureAwait(false)
            || !await JsonLinesRecoveryJournalStore.VerifyRecoveredAsync(
                entry.RecoveryJournalIdentity, entry, cancellationToken)
                .ConfigureAwait(false))
            throw new InvalidOperationException("Only a verified recovered journal can resolve a source execution.");
        string root = GetRoot(libraryRoot, "resolutions");
        Directory.CreateDirectory(root);
        string path = ResolutionPath(libraryRoot, entry.SourceExecutionId.ToString());
        await WriteAtomicAsync(path, entry, overwrite: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private string ResolutionPath(string libraryRoot, string sourceExecutionId)
    {
        string root = GetRoot(libraryRoot, "resolutions");
        string key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(sourceExecutionId))).ToLowerInvariant();
        return Path.Combine(root, $"{key}.json");
    }

    private string GetRoot(string libraryRoot, string child)
    {
        string history = Path.GetFullPath(options.HistoryRoot);
        string root = Path.Combine(history, "recovery", child);
        string existing = Directory.Exists(root) ? root
            : Directory.Exists(Path.GetDirectoryName(root)) ? Path.GetDirectoryName(root)!
            : Directory.Exists(history) ? history : Path.GetDirectoryName(history)!;
        if (!ExecutionPathGuard.TryValidateExternalDirectory(
                libraryRoot, existing, true, out _, out _))
            throw new IOException("The recovery history root is not a physical external directory.");
        return root;
    }

    private static bool IsSafelyTerminal(RecoveryHistoryEntry entry) =>
        entry.State == RecoveryExecutionState.Recovered
        || !entry.MutationStarted && entry.State is RecoveryExecutionState.CancelledBeforeMutation
            or RecoveryExecutionState.RecoveryFailed or RecoveryExecutionState.Revoked
            or RecoveryExecutionState.Stale;

    private static Task<bool> JournalIdentityIsSafeAsync(
        string journalIdentity,
        string libraryRoot)
    {
        try
        {
            string path = Path.GetFullPath(journalIdentity);
            string? directory = Path.GetDirectoryName(path);
            return Task.FromResult(directory is not null
                && string.Equals(Path.GetFileName(path), "recovery.journal.jsonl",
                    StringComparison.Ordinal)
                && File.Exists(path)
                && ExecutionPathGuard.TryRejectReparsePoints(path, true, out _)
                && ExecutionPathGuard.TryValidateExternalDirectory(
                    libraryRoot, directory, true, out _, out _));
        }
        catch (Exception exception) when (exception is IOException
                   or UnauthorizedAccessException or ArgumentException
                   or NotSupportedException)
        {
            return Task.FromResult(false);
        }
    }

    private static async Task<T?> ReadAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
            throw new IOException("The recovery history entry is linked.");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 4L * 1024 * 1024)
            throw new IOException("The recovery history entry violates its bound.");
        return await JsonSerializer.DeserializeAsync<T>(
            stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAtomicAsync<T>(
        string path,
        T value,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        string root = Path.GetDirectoryName(path)!;
        string temporary = Path.Combine(root, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
