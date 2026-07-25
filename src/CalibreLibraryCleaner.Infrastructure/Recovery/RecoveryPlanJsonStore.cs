using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Recoveries;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Recovery;

internal sealed class RecoveryPlanJsonStore : IRecoveryPlanStore
{
    private const long MaximumArtifactBytes = 64L * 1024 * 1024;
    public async Task<RecoveryPlanStoreResult> WriteCreateNewAsync(
        RecoveryPlan plan,
        string destinationPath,
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!TryValidatePath(libraryRoot, destinationPath, false, out string? path, out RecoveryIssue? issue))
            return Failure(issue!);
        string temporary = Path.Combine(Path.GetDirectoryName(path!)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] bytes = RecoveryPlanArtifactCodec.Serialize(plan);
            if (bytes.LongLength > MaximumArtifactBytes)
                return Failure(Block("RECOVERY.PLAN_TOO_LARGE", "The recovery plan exceeds 64 MiB."));
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path!, overwrite: false);
            RecoveryPlanStoreResult restored = await ReadAsync(
                path!, libraryRoot, cancellationToken).ConfigureAwait(false);
            if (!restored.IsSuccess || restored.Plan!.ContentDigest != plan.ContentDigest
                || restored.Plan.Id != plan.Id || restored.Plan.ArtifactRevision != plan.ArtifactRevision)
                return Failure(Block("RECOVERY.PLAN_REOPEN_FAILED",
                    "The create-new recovery plan could not be reconstructed exactly."));
            return new(restored.Plan, path, restored.Issues);
        }
        catch (OperationCanceledException)
        {
            DeleteOwnedTemporary(temporary);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                   or JsonException or ArgumentException or NotSupportedException)
        {
            DeleteOwnedTemporary(temporary);
            return Failure(Block("RECOVERY.PLAN_WRITE_FAILED",
                "The recovery plan could not be written create-new outside the library."));
        }
    }

    public async Task<RecoveryPlanStoreResult> ReadAsync(
        string sourcePath,
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        if (!TryValidatePath(libraryRoot, sourcePath, true, out string? path, out RecoveryIssue? issue))
            return Failure(issue!);
        try
        {
            await using FileStream stream = new(path!, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long length = stream.Length;
            if (length is <= 0 or > MaximumArtifactBytes)
                return Failure(Block("RECOVERY.PLAN_SIZE_INVALID",
                    "The recovery plan is empty or exceeds the import bound."));
            byte[] bytes = new byte[checked((int)length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (stream.Length != length)
                return Failure(Block("RECOVERY.PLAN_CHANGED",
                    "The recovery plan changed while it was being read."));
            RecoveryPlan plan = RecoveryPlanArtifactCodec.Deserialize(bytes);
            if (plan.SchemaVersion != RecoveryPlanSchemaVersion.V1
                || plan.ModelVersion != RecoveryModelVersion.V1
                || plan.PolicyVersion != RecoveryPolicyVersion.V1)
                return Failure(Block("RECOVERY.PLAN_VERSION_UNSUPPORTED",
                    "The recovery plan schema, model, or policy version is unsupported."));
            if (RecoveryPlanContentDigestPolicy.Compute(plan.Definition) != plan.ContentDigest)
                return Failure(Block("RECOVERY.PLAN_TAMPERED",
                    "The immutable recovery-plan body hash does not match."));
            return new(plan, path, []);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                   or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Failure(Block("RECOVERY.PLAN_READ_FAILED",
                "The recovery plan is malformed, stale, tampered, or unsupported."));
        }
    }

    private static bool TryValidatePath(
        string libraryRoot,
        string artifactPath,
        bool requireExisting,
        out string? path,
        out RecoveryIssue? issue)
    {
        path = null;
        issue = null;
        try
        {
            string library = Path.GetFullPath(libraryRoot);
            string candidate = Path.GetFullPath(artifactPath);
            string? directory = Path.GetDirectoryName(candidate);
            if (!candidate.EndsWith(".recovery-plan.json", StringComparison.OrdinalIgnoreCase)
                || directory is null || !Directory.Exists(directory)
                || requireExisting && !File.Exists(candidate)
                || !ExecutionPathGuard.TryRejectReparsePoints(directory, true, out _)
                || requireExisting && !ExecutionPathGuard.TryRejectReparsePoints(candidate, true, out _)
                || ExecutionPathGuard.IsContained(library, candidate))
            {
                issue = Block("RECOVERY.PLAN_PATH_UNSAFE",
                    "Recovery plans must be regular .recovery-plan.json files outside the Calibre library.");
                return false;
            }
            path = candidate;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                   or ArgumentException or NotSupportedException)
        {
            issue = Block("RECOVERY.PLAN_PATH_UNSAFE",
                "The recovery-plan path could not be canonicalized safely.");
            return false;
        }
    }

    private static RecoveryPlanStoreResult Failure(RecoveryIssue issue) => new(null, null, [issue]);

    private static RecoveryIssue Block(string code, string explanation) =>
        new(code, RecoveryIssueSeverity.Blocking, "Recovery plan artifact", explanation);

    private static void DeleteOwnedTemporary(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
