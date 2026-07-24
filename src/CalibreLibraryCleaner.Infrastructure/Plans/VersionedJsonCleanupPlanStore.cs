using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Infrastructure.Plans;

internal sealed class VersionedJsonCleanupPlanStore : ICleanupPlanStore
{
    private const long MaximumFileBytes = 64L * 1024 * 1024;

    public async Task<CleanupPlanStoreWriteResult> WriteAsync(
        CleanupPlan plan,
        string libraryRoot,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        PathGuardResult guarded = CleanupPlanPathGuard.ValidateExport(libraryRoot, destinationPath);
        if (!guarded.IsSuccess) return CleanupPlanStoreWriteResult.Failure(guarded.ErrorCode!, guarded.ErrorMessage!);
        string destination = guarded.CanonicalPath!;
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] bytes = CleanupPlanJsonSerializer.Serialize(plan);
            if (bytes.LongLength > MaximumFileBytes)
                return CleanupPlanStoreWriteResult.Failure("CLEANUP_PLAN_TOO_LARGE", "The cleanup plan exceeds the 64 MiB artifact limit.");
            await using (FileStream stream = new(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            return CleanupPlanStoreWriteResult.Success();
        }
        catch (OperationCanceledException)
        {
            DeleteOwnTemporary(temporary);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            DeleteOwnTemporary(temporary);
            return CleanupPlanStoreWriteResult.Failure("CLEANUP_PLAN_WRITE_FAILED", "The cleanup plan could not be published safely.");
        }
    }

    public async Task<CleanupPlanStoreReadResult> ReadAsync(
        string libraryRoot,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PathGuardResult guarded = CleanupPlanPathGuard.ValidateImport(libraryRoot, sourcePath);
        if (!guarded.IsSuccess) return CleanupPlanStoreReadResult.Failure(guarded.ErrorCode!, guarded.ErrorMessage!);
        try
        {
            await using FileStream stream = new(
                guarded.CanonicalPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long expectedLength = stream.Length;
            if (expectedLength > MaximumFileBytes)
                return CleanupPlanStoreReadResult.Failure("CLEANUP_PLAN_TOO_LARGE", "The cleanup plan exceeds the 64 MiB import limit.");
            byte[] bytes = new byte[checked((int)expectedLength)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Length != expectedLength)
                return CleanupPlanStoreReadResult.Failure("CLEANUP_PLAN_SOURCE_CHANGED", "The cleanup plan changed while it was being imported.");
            return CleanupPlanJsonSerializer.Deserialize(bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return CleanupPlanStoreReadResult.Failure("CLEANUP_PLAN_READ_FAILED", "The cleanup plan could not be read safely.");
        }
    }

    private static void DeleteOwnTemporary(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
