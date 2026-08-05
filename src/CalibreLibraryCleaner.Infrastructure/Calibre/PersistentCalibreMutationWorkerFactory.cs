using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Calibre;

internal sealed class PersistentCalibreMutationWorkerFactory(CalibreExecutionOptions options)
    : ICalibreMutationWorkerFactory
{
    private const string WorkerResourceName =
        "CalibreLibraryCleaner.Infrastructure.Calibre.calibre_mutation_worker.py";
    private static readonly string[] RequiredCapabilities = ["transferFormat", "removeFormat", "removeRecord"];

    public async Task<CalibreMutationWorkerOpenResult> TryOpenAsync(
        OpenCalibreMutationWorkerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Process? process = null;
        FileStream? trustedToolLock = null;
        FileStream? executableLock = null;
        FileStream? scriptLock = null;
        try
        {
            string libraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.LibraryRoot));
            string trustedCalibredb = Path.GetFullPath(options.TrustedExecutablePath);
            if (IsKnownCalibreWriterRunning())
                return Failed("CALIBRE_WRITER_RUNNING");
            if (!CalibreCompatibilityPolicy.IsSupportedVersion(request.Tool.Identity.ProductVersion, options)
                || !string.Equals(request.Tool.CanonicalExecutablePath, trustedCalibredb, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(request.Tool.Identity.CapabilityProfile, options.CapabilityProfile, StringComparison.Ordinal)
                || !Directory.Exists(libraryRoot) || !File.Exists(Path.Combine(libraryRoot, "metadata.db")))
                return Failed("CALIBRE_WORKER_BOUNDARY_INVALID");

            trustedToolLock = new FileStream(trustedCalibredb, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (await CalibreToolDiscovery.HashStreamAsync(trustedToolLock, cancellationToken).ConfigureAwait(false)
                != request.Tool.Identity.ExecutableSha256)
                return await FailAndDisposeAsync("CALIBRE_WORKER_TOOL_IDENTITY_CHANGED").ConfigureAwait(false);

            string executable = Path.Combine(Path.GetDirectoryName(trustedCalibredb)!, "calibre-debug.exe");
            if (!File.Exists(executable)
                || !ExecutionPathGuard.TryRejectReparsePoints(executable, true, out _))
                return await FailAndDisposeAsync("CALIBRE_WORKER_NOT_FOUND").ConfigureAwait(false);
            executableLock = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] scriptBytes = ReadWorkerResource();
            string scriptDigest = Convert.ToHexString(SHA256.HashData(scriptBytes)).ToLowerInvariant();
            string workerDirectory = Path.Combine(options.ControlledConfigDirectory, "mutation-worker", scriptDigest);
            if (!ExecutionPathGuard.TryValidateExternalDirectory(
                    libraryRoot, workerDirectory, false, out string? canonicalWorkerDirectory, out _))
                return await FailAndDisposeAsync("CALIBRE_WORKER_LOCATION_UNSAFE").ConfigureAwait(false);
            Directory.CreateDirectory(canonicalWorkerDirectory!);
            if (!ExecutionPathGuard.TryValidateExternalDirectory(
                    libraryRoot, canonicalWorkerDirectory!, true, out canonicalWorkerDirectory, out _))
                return await FailAndDisposeAsync("CALIBRE_WORKER_LOCATION_UNSAFE").ConfigureAwait(false);
            string scriptPath = Path.Combine(canonicalWorkerDirectory!, "calibre_mutation_worker.py");
            await MaterializeScriptAsync(scriptPath, scriptBytes, scriptDigest, cancellationToken).ConfigureAwait(false);
            if (!ExecutionPathGuard.TryRejectReparsePoints(scriptPath, true, out _))
                return await FailAndDisposeAsync("CALIBRE_WORKER_SCRIPT_UNSAFE").ConfigureAwait(false);
            scriptLock = new FileStream(scriptPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

            ProcessStartInfo startInfo = CreateStartInfo(executable, canonicalWorkerDirectory!, scriptPath,
                libraryRoot, request.ExpectedLibraryUuid);
            process = new Process { StartInfo = startInfo };
            if (!process.Start()) return await FailAndDisposeAsync("CALIBRE_WORKER_NOT_STARTED").ConfigureAwait(false);
            Task<string> stderr = DrainBoundedAsync(process.StandardError);
            using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(options.WorkerStartupTimeout);
            CalibreMutationWorkerReadyMessage ready = await CalibreMutationWorkerProtocol.ReadAsync<
                CalibreMutationWorkerReadyMessage>(process.StandardOutput, options.WorkerMaximumMessageBytes,
                startup.Token).ConfigureAwait(false);
            if (ready.ProtocolVersion != CalibreMutationWorkerProtocol.Version
                || ready.Kind != CalibreMutationWorkerProtocol.ReadyKind
                || !string.Equals(ready.LibraryUuid, request.ExpectedLibraryUuid, StringComparison.Ordinal)
                || ready.FailureCode == "library_identity_mismatch")
                return await FailAndDisposeAsync("CALIBRE_WORKER_IDENTITY_MISMATCH").ConfigureAwait(false);
            if (ready.FailureCode is not null
                || !RequiredCapabilities.All(ready.Capabilities.Contains))
                return await FailAndDisposeAsync("CALIBRE_WORKER_HANDSHAKE_FAILED").ConfigureAwait(false);

            PersistentCalibreMutationWorkerSession session = new(
                process, trustedToolLock, executableLock, scriptLock, stderr, options);
            process = null;
            trustedToolLock = null;
            executableLock = null;
            scriptLock = null;
            return new(session, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await FailAndDisposeAsync("CALIBRE_WORKER_STARTUP_TIMEOUT").ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return await FailAndDisposeAsync("CALIBRE_WORKER_TRUST_VALIDATION_FAILED").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or JsonException or InvalidOperationException
                                           or System.ComponentModel.Win32Exception
                                           or ArgumentException or NotSupportedException)
        {
            return await FailAndDisposeAsync("CALIBRE_WORKER_STARTUP_FAILED").ConfigureAwait(false);
        }

        async Task<CalibreMutationWorkerOpenResult> FailAndDisposeAsync(string failureCode)
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException
                                                   or System.ComponentModel.Win32Exception
                                                   or NotSupportedException)
                { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }
            process?.Dispose();
            if (scriptLock is not null) await scriptLock.DisposeAsync().ConfigureAwait(false);
            if (executableLock is not null) await executableLock.DisposeAsync().ConfigureAwait(false);
            if (trustedToolLock is not null) await trustedToolLock.DisposeAsync().ConfigureAwait(false);
            return Failed(failureCode);
        }
    }

    private ProcessStartInfo CreateStartInfo(
        string executable,
        string workingDirectory,
        string scriptPath,
        string libraryRoot,
        string expectedLibraryUuid)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        Dictionary<string, string?> inherited = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
            inherited[name] = startInfo.Environment.TryGetValue(name, out string? value) ? value : null;
        startInfo.Environment.Clear();
        foreach ((string name, string? value) in inherited.Where(value => !string.IsNullOrWhiteSpace(value.Value)))
            startInfo.Environment[name] = value!;
        startInfo.Environment["CALIBRE_CONFIG_DIRECTORY"] = options.ControlledConfigDirectory;
        startInfo.Environment["CLC_WORKER_TEMP_DIRECTORY"] = workingDirectory;
        foreach (string argument in new[] { "-e", scriptPath, "--", libraryRoot, expectedLibraryUuid })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static byte[] ReadWorkerResource()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(WorkerResourceName)
            ?? throw new InvalidOperationException("The Calibre mutation worker resource is missing.");
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static async Task MaterializeScriptAsync(
        string scriptPath,
        byte[] scriptBytes,
        string expectedDigest,
        CancellationToken cancellationToken)
    {
        if (File.Exists(scriptPath))
        {
            if (await CalibreToolDiscovery.HashFileAsync(scriptPath, cancellationToken).ConfigureAwait(false)
                != new CalibreLibraryCleaner.Domain.Libraries.Sha256Digest(expectedDigest))
                throw new InvalidDataException("The deployed Calibre mutation worker changed.");
            return;
        }
        string temporaryPath = scriptPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, scriptBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, scriptPath, overwrite: false);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<string> DrainBoundedAsync(StreamReader reader)
    {
        char[] buffer = new char[4096];
        System.Text.StringBuilder captured = new(Math.Min(options.MaximumCapturedCharacters, 4096));
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            int remaining = options.MaximumCapturedCharacters - captured.Length;
            if (remaining > 0) captured.Append(buffer, 0, Math.Min(read, remaining));
        }
        return captured.ToString();
    }

    internal static bool IsKnownCalibreWriterRunning()
    {
        foreach (string processName in new[] { "calibre", "calibre-server", "calibredb" })
        {
            Process[] processes = Process.GetProcessesByName(processName);
            try
            {
                if (processes.Any(process => !process.HasExited)) return true;
            }
            finally
            {
                foreach (Process process in processes) process.Dispose();
            }
        }
        return false;
    }

    private static CalibreMutationWorkerOpenResult Failed(string failureCode) => new(null, failureCode);
}

internal sealed class PersistentCalibreMutationWorkerSession(
    Process process,
    FileStream trustedToolLock,
    FileStream executableLock,
    FileStream scriptLock,
    Task<string> stderr,
    CalibreExecutionOptions options) : ICalibreMutationWorkerSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public async Task<CalibreMutationChunkResult> ExecuteChunkAsync(
        CalibreMutationChunkRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process.HasExited) return Failed(request.ChunkId, false, "CALIBRE_WORKER_EXITED");
            if (PersistentCalibreMutationWorkerFactory.IsKnownCalibreWriterRunning())
                return Failed(request.ChunkId, true, "CALIBRE_WRITER_STARTED_DURING_CLEANUP");
            CalibreMutationWorkerOperationMessage[] operations = request.Operations.Select(value => new CalibreMutationWorkerOperationMessage(
                value.OperationId, value.Kind, value.RecordId.Value, value.CanonicalFormat,
                value.TargetRecordId?.Value, value.ExpectedFingerprint?.SizeInBytes,
                value.ExpectedFingerprint?.Sha256.Value)).ToArray();
            try
            {
                await CalibreMutationWorkerProtocol.WriteAsync(process.StandardInput,
                    new CalibreMutationWorkerRequestMessage(
                        CalibreMutationWorkerProtocol.Version, CalibreMutationWorkerProtocol.ExecuteChunkKind,
                        request.ChunkId, operations), options.WorkerMaximumMessageBytes,
                    CancellationToken.None).ConfigureAwait(false);
                CalibreMutationWorkerResultMessage response = await CalibreMutationWorkerProtocol.ReadAsync<
                    CalibreMutationWorkerResultMessage>(process.StandardOutput, options.WorkerMaximumMessageBytes,
                    CancellationToken.None).ConfigureAwait(false);
                if (!IsValidResponse(request, response))
                    return Failed(request.ChunkId, true, "CALIBRE_WORKER_PROTOCOL_INVALID");
                return new(response.ChunkId!, response.MutationStarted,
                    response.OperationResults!.Select(value => new CalibreMutationOperationResult(
                        value.OperationId, value.Kind, value.IsSuccess, value.FailureCode)).ToArray(),
                    response.FailureCode);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException
                                               or JsonException or EndOfStreamException)
            {
                return Failed(request.ChunkId, true, "CALIBRE_WORKER_RESPONSE_FAILED");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            if (!process.HasExited)
            {
                try
                {
                    await CalibreMutationWorkerProtocol.WriteAsync(process.StandardInput,
                        new CalibreMutationWorkerRequestMessage(CalibreMutationWorkerProtocol.Version,
                            CalibreMutationWorkerProtocol.ShutdownKind),
                        options.WorkerMaximumMessageBytes, CancellationToken.None).ConfigureAwait(false);
                    using CancellationTokenSource shutdown = new(options.WorkerShutdownTimeout);
                    await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException
                                                   or OperationCanceledException)
                { }
            }
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException
                                                   or System.ComponentModel.Win32Exception
                                                   or NotSupportedException)
                { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }
            await stderr.ConfigureAwait(false);
            process.Dispose();
            await scriptLock.DisposeAsync().ConfigureAwait(false);
            await executableLock.DisposeAsync().ConfigureAwait(false);
            await trustedToolLock.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private static bool IsValidResponse(
        CalibreMutationChunkRequest request,
        CalibreMutationWorkerResultMessage response)
    {
        if (response.ProtocolVersion != CalibreMutationWorkerProtocol.Version
            || response.Kind != CalibreMutationWorkerProtocol.ChunkResultKind
            || response.ChunkId != request.ChunkId
            || response.OperationResults is null
            || response.OperationResults.Count != request.Operations.Count
            || !response.OperationResults.Zip(request.Operations).All(pair =>
                pair.First.OperationId == pair.Second.OperationId && pair.First.Kind == pair.Second.Kind))
            return false;
        bool failureSeen = false;
        foreach (CalibreMutationWorkerOperationResultMessage result in response.OperationResults)
        {
            if (!result.IsSuccess) failureSeen = true;
            else if (failureSeen) return false;
            if (result.IsSuccess == (result.FailureCode is not null)) return false;
        }
        return failureSeen == (response.FailureCode is not null);
    }

    private static CalibreMutationChunkResult Failed(string chunkId, bool mutationStarted, string failureCode) =>
        new(chunkId, mutationStarted, [], failureCode);
}
