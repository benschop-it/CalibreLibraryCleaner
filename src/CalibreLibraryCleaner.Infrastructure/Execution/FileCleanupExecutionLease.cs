using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Infrastructure.Calibre;

namespace CalibreLibraryCleaner.Infrastructure.Execution;

internal sealed class FileCleanupExecutionLease(ExecutionStorageOptions options) : ICleanupExecutionLease
{
    public async Task<ExecutionLeaseAcquisition> TryAcquireAsync(
        ExecutionLeaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.LibraryRoot)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failed("EXECUTION.LEASE_LIBRARY_INVALID", "The library path cannot be canonicalized for lease ownership.");
        }
        if (!Directory.Exists(root) || !ExecutionPathGuard.TryRejectReparsePoints(root, true, out _))
            return Failed("EXECUTION.LEASE_LIBRARY_INVALID",
                "The library path is missing, linked, or cannot be proven to have one physical lease identity.");

        string leaseRoot = Path.GetFullPath(options.LeaseRoot);
        if (!ExecutionPathGuard.TryValidateExternalDirectory(root, leaseRoot, false, out _, out _))
            return Failed("EXECUTION.LEASE_LOCATION_UNSAFE", "The execution lease location resolves inside the Calibre library.");
        try
        {
            Directory.CreateDirectory(leaseRoot);
            if (!ExecutionPathGuard.TryValidateExternalDirectory(
                    root, leaseRoot, true, out string? verifiedLeaseRoot, out _))
                return Failed("EXECUTION.LEASE_LOCATION_UNSAFE", "The execution lease location uses a symbolic link or junction.");
            leaseRoot = verifiedLeaseRoot!;
            string keyMaterial = $"{root.ToUpperInvariant()}\n{request.LibraryUuid}";
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial))).ToLowerInvariant();
            string leasePath = Path.Combine(leaseRoot, $"{key}.lease");
            FileStream stream = new(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            try
            {
                byte[] owner = JsonSerializer.SerializeToUtf8Bytes(new LeaseOwner(
                    request.ExecutionId.ToString(), Environment.ProcessId,
                    Environment.ProcessPath ?? string.Empty, request.RequestedAtUtc.ToUniversalTime(),
                    request.LibraryUuid, root));
                stream.SetLength(0);
                await stream.WriteAsync(owner, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                return new(new Handle(stream, leasePath), []);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (IOException)
        {
            return Failed("EXECUTION.LEASE_HELD", "Another Calibre Library Cleaner execution already holds this library lease.");
        }
        catch (UnauthorizedAccessException)
        {
            return Failed("EXECUTION.LEASE_INACCESSIBLE", "The execution lease could not be created or verified.");
        }
    }

    private static ExecutionLeaseAcquisition Failed(string code, string explanation) => new(null,
        [new ExecutionIssue(code, ExecutionIssueSeverity.BlockingError, explanation)]);

    private sealed record LeaseOwner(
        string ExecutionId,
        int ProcessId,
        string ProcessPath,
        DateTimeOffset CreatedAtUtc,
        string LibraryUuid,
        string CanonicalLibraryRoot);

    private sealed class Handle(FileStream stream, string identity) : ICleanupExecutionLeaseHandle
    {
        private FileStream? _stream = stream;
        public string LeaseIdentity { get; } = identity;
        public bool IsHeld => _stream is not null;

        public async ValueTask DisposeAsync()
        {
            FileStream? current = Interlocked.Exchange(ref _stream, null);
            if (current is not null) await current.DisposeAsync().ConfigureAwait(false);
        }
    }
}
