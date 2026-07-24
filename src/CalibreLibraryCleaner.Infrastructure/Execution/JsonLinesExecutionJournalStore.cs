using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;

namespace CalibreLibraryCleaner.Infrastructure.Execution;

internal sealed class JsonLinesExecutionJournalStore : IExecutionJournalStore
{
    private const string Schema = "cleanup-execution-journal/1.0";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public async Task<IExecutionJournalSession> CreateAsync(
        ExecutionJournalCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.Combine(request.Workspace.BundlePath, "execution.journal.jsonl");
        FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        Session session = new(stream, path, request);
        try
        {
            await session.AppendAsync(new("JournalHeader", CleanupExecutionState.Created,
                request.CreatedAtUtc, "Versioned execution journal created."), cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JournalReconciliationResult> ReconcileAsync(
        string canonicalBackupDestinationIdentity,
        string libraryUuid,
        CancellationToken cancellationToken)
    {
        List<ExecutionIssue> issues = [];
        bool recovery = false;
        if (!Directory.Exists(canonicalBackupDestinationIdentity)) return new(false, issues);
        foreach (string directory in Directory.EnumerateDirectories(canonicalBackupDestinationIdentity, "execution-*", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ExecutionPathGuard.TryRejectReparsePoints(directory, true, out _))
            {
                recovery = true;
                issues.Add(new("EXECUTION.JOURNAL_PATH_UNSAFE", ExecutionIssueSeverity.BlockingError,
                    "An execution journal directory is linked or cannot be inspected safely."));
                continue;
            }
            string path = Path.Combine(directory, "execution.journal.jsonl");
            if (!File.Exists(path)) continue;
            if (!ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
            {
                recovery = true;
                issues.Add(new("EXECUTION.JOURNAL_PATH_UNSAFE", ExecutionIssueSeverity.BlockingError,
                    "An execution journal is linked or cannot be inspected safely."));
                continue;
            }
            ReconciledJournal reconciled = await ReadAndValidateAsync(path, cancellationToken).ConfigureAwait(false);
            if (reconciled.LibraryUuid is not null && !string.Equals(reconciled.LibraryUuid, libraryUuid, StringComparison.Ordinal)) continue;
            if (!reconciled.IsValid)
            {
                recovery = true;
                issues.Add(new("EXECUTION.JOURNAL_CORRUPT", ExecutionIssueSeverity.BlockingError,
                    "An execution journal is corrupt or truncated; the absence of mutation cannot be proven."));
                continue;
            }

            bool validCompletedSummary = reconciled.MutationStarted
                && reconciled.HasTerminalSummary
                && reconciled.LastState == CleanupExecutionState.Completed
                && await HasMatchingTerminalSummaryAsync(directory, reconciled, cancellationToken).ConfigureAwait(false);
            if (reconciled.MutationStarted && !validCompletedSummary)
            {
                recovery = true;
                issues.Add(new("EXECUTION.INCOMPLETE_MUTATION_JOURNAL", ExecutionIssueSeverity.BlockingError,
                    "A prior execution crossed the mutation boundary without verified completion."));
            }
            else if (!reconciled.MutationStarted && !reconciled.HasTerminalSummary)
            {
                issues.Add(new("EXECUTION.INCOMPLETE_PREMUTATION_JOURNAL", ExecutionIssueSeverity.Information,
                    "A prior execution ended before mutation and requires no library recovery."));
            }
        }
        return new(recovery, issues);
    }

    private static async Task<ReconciledJournal> ReadAndValidateAsync(string path, CancellationToken cancellationToken)
    {
        string previousHash = new string('0', 64);
        string? libraryUuid = null;
        string? executionId = null;
        string? planId = null;
        string? planContentDigest = null;
        bool mutation = false;
        bool terminal = false;
        CleanupExecutionState? lastState = null;
        try
        {
            using StreamReader reader = new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan), Encoding.UTF8, false, 16 * 1024);
            int expectedSequence = 1;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0 || line.Length > 1_000_000)
                    return new(false, libraryUuid, executionId, planId, planContentDigest, mutation, terminal, lastState);
                JournalLine? entry = JsonSerializer.Deserialize<JournalLine>(line, Options);
                if (entry is null || entry.Schema != Schema || entry.Sequence != expectedSequence
                    || entry.PreviousHash != previousHash || ComputeHash(entry with { EntryHash = string.Empty }) != entry.EntryHash)
                    return new(false, libraryUuid, executionId, planId, planContentDigest, mutation, terminal, lastState);
                if (terminal)
                    return new(false, libraryUuid, executionId, planId, planContentDigest, mutation, terminal, lastState);
                libraryUuid ??= entry.LibraryUuid;
                executionId ??= entry.ExecutionId;
                planId ??= entry.PlanId;
                planContentDigest ??= entry.PlanContentDigest;
                if (!string.Equals(libraryUuid, entry.LibraryUuid, StringComparison.Ordinal)
                    || !string.Equals(executionId, entry.ExecutionId, StringComparison.Ordinal)
                    || !string.Equals(planId, entry.PlanId, StringComparison.Ordinal)
                    || !string.Equals(planContentDigest, entry.PlanContentDigest, StringComparison.Ordinal))
                    return new(false, libraryUuid, executionId, planId, planContentDigest, mutation, terminal, lastState);
                mutation |= entry.Event.MutationStarted || entry.Event.Kind == "MutationStarting";
                terminal = entry.Event.Kind == "TerminalSummary";
                lastState = entry.Event.State;
                previousHash = entry.EntryHash;
                expectedSequence++;
            }
            return new(true, libraryUuid, executionId, planId, planContentDigest, mutation, terminal, lastState);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new(false, libraryUuid, executionId, planId, planContentDigest, mutation, terminal, lastState);
        }
    }

    private static async Task<bool> HasMatchingTerminalSummaryAsync(
        string directory,
        ReconciledJournal journal,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, "execution-summary.json");
        try
        {
            if (!File.Exists(path) || !ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
                return false;
            FileInfo info = new(path);
            if (info.Length <= 0 || info.Length > 1_000_000) return false;
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            ExecutionHistoryEntry? summary = await JsonSerializer.DeserializeAsync<ExecutionHistoryEntry>(
                stream, Options, cancellationToken).ConfigureAwait(false);
            return summary is not null
                && string.Equals(summary.ExecutionId.ToString(), journal.ExecutionId, StringComparison.Ordinal)
                && string.Equals(summary.PlanId.ToString(), journal.PlanId, StringComparison.Ordinal)
                && string.Equals(summary.PlanContentDigest.Value, journal.PlanContentDigest, StringComparison.Ordinal)
                && string.Equals(summary.LibraryUuid, journal.LibraryUuid, StringComparison.Ordinal)
                && summary.State == CleanupExecutionState.Completed
                && summary.Disposition == CleanupExecutionDisposition.Completed
                && summary.FailureClassification == CleanupExecutionFailureClassification.None
                && summary.MutationStarted;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private sealed class Session : IExecutionJournalSession
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ExecutionJournalCreateRequest _request;
        private FileStream? _stream;
        private int _sequence;
        private string _previousHash = new('0', 64);

        public Session(FileStream stream, string path, ExecutionJournalCreateRequest request)
        {
            _stream = stream;
            JournalIdentity = path;
            _request = request;
        }

        public string JournalIdentity { get; }
        public bool IsAvailable => _stream is { CanWrite: true };

        public async Task AppendAsync(ExecutionJournalEvent entry, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(entry);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                FileStream stream = _stream ?? throw new ObjectDisposedException(nameof(Session));
                JournalLine unsigned = new(Schema, ++_sequence, _previousHash, string.Empty,
                    _request.Workspace.ExecutionId.ToString(), _request.Plan.Id.ToString(),
                    _request.Plan.ContentDigest.Value, _request.Plan.InputIdentity.LibraryUuid,
                    _request.ApplicationVersion, entry);
                JournalLine complete = unsigned with { EntryHash = ComputeHash(unsigned) };
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(complete, Options);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                _previousHash = complete.EntryHash;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or ObjectDisposedException or JsonException)
            {
                throw new ExecutionJournalPersistenceException("The execution journal entry could not be persisted.", exception);
            }
            finally { _gate.Release(); }
        }

        public async Task CompleteAsync(ExecutionHistoryEntry terminalEntry, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(terminalEntry);
            await AppendAsync(new("TerminalSummary", terminalEntry.State, terminalEntry.FinishedAtUtc,
                $"Terminal disposition: {terminalEntry.Disposition}; failure classification: {terminalEntry.FailureClassification}.",
                FailureCode: terminalEntry.FailureClassification == CleanupExecutionFailureClassification.None
                    ? null : terminalEntry.FailureClassification.ToString(), MutationStarted: terminalEntry.MutationStarted), cancellationToken).ConfigureAwait(false);
            try
            {
                string finalPath = Path.Combine(_request.Workspace.BundlePath, "execution-summary.json");
                string temporaryPath = finalPath + $".{_request.Workspace.ExecutionId}.tmp";
                byte[] summary = JsonSerializer.SerializeToUtf8Bytes(terminalEntry, Options);
                await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(summary, cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, finalPath, overwrite: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or JsonException or ArgumentException)
            {
                throw new ExecutionJournalPersistenceException("The terminal execution summary could not be persisted.", exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                FileStream? stream = Interlocked.Exchange(ref _stream, null);
                if (stream is not null)
                {
                    stream.Flush(flushToDisk: true);
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally { _gate.Release(); _gate.Dispose(); }
        }
    }

    private static string ComputeHash(JournalLine line)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(line with { EntryHash = string.Empty }, Options);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private sealed record JournalLine(
        string Schema,
        int Sequence,
        string PreviousHash,
        string EntryHash,
        string ExecutionId,
        string PlanId,
        string PlanContentDigest,
        string LibraryUuid,
        string ApplicationVersion,
        ExecutionJournalEvent Event);

    private sealed record ReconciledJournal(
        bool IsValid,
        string? LibraryUuid,
        string? ExecutionId,
        string? PlanId,
        string? PlanContentDigest,
        bool MutationStarted,
        bool HasTerminalSummary,
        CleanupExecutionState? LastState);
}
