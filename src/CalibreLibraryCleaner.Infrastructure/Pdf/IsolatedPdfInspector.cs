using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Domain.Assessments;

namespace CalibreLibraryCleaner.Infrastructure.Pdf;

public sealed class IsolatedPdfInspector(PdfWorkerOptions options) : IPdfInspector
{
    public async Task<PdfInspectionResult> InspectAsync(
        PdfInspectionRequest request,
        Func<PdfDocumentHeaderFacts, CancellationToken, ValueTask<IReadOnlyList<int>>> selectPages,
        IProgress<PdfInspectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selectPages);
        request.Limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        string executable = Path.GetFullPath(options.ExecutablePath);
        if (!File.Exists(executable))
        {
            return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.WorkerCrashed, PdfOpenStatus.WorkerFailed);
        }

        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--stdio");
        startInfo.Environment.Clear();
        startInfo.Environment["DOTNET_GCHeapHardLimit"] = request.Limits.ManagedHeapBytes.ToString("X", CultureInfo.InvariantCulture);
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        startInfo.Environment["COMPlus_EnableDiagnostics"] = "0";

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return Failed(PdfInspectionProblemCode.WorkerCrashed, PdfOpenStatus.WorkerFailed);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return Failed(PdfInspectionProblemCode.WorkerCrashed, PdfOpenStatus.WorkerFailed);
        }

        IDisposable? assignedJob = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                assignedJob = options.JobObjectFactory(process, request.Limits.WorkingSetBytes);
            }
            catch (Exception)
            {
            }

            if (assignedJob is null)
            {
                await TryTerminateAsync(process).ConfigureAwait(false);
                return Failed(PdfInspectionProblemCode.WorkerCrashed, PdfOpenStatus.WorkerFailed);
            }
        }

        using IDisposable? job = assignedJob;
        Stopwatch elapsed = Stopwatch.StartNew();
        bool parentResourceWarning = false;
        PdfWorkerMessageBudget responseBudget = new(request.Limits.MaximumWorkerMessageBytes);
        Task discardError = DrainDiscardedErrorAsync(process.StandardError, CancellationToken.None);
        using CancellationTokenSource wallTime = new(TimeSpan.FromSeconds(request.Limits.WallTimeSeconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wallTime.Token);
        CancellationToken token = linked.Token;
        try
        {
            await PdfWorkerProtocol.WriteAsync(
                process.StandardInput,
                new PdfWorkerRequestMessage(PdfWorkerProtocol.Version, "Request", request),
                request.Limits.MaximumWorkerMessageBytes,
                token,
                observeByteCount: ObserveMessageSize).ConfigureAwait(false);

            while (true)
            {
                parentResourceWarning |= HasResourceWarning(process, request.Limits, elapsed.Elapsed);
                PdfInspectionProblemCode? resourceFailure = CheckResources(process, request.Limits);
                if (resourceFailure is { } failure)
                {
                    await TryTerminateAsync(process).ConfigureAwait(false);
                    return Failed(failure, PdfOpenStatus.ResourceLimited);
                }

                Task<PdfWorkerOutputMessage> readMessage = PdfWorkerProtocol.ReadAsync<PdfWorkerOutputMessage>(
                    process.StandardOutput,
                    request.Limits.MaximumWorkerMessageBytes,
                    token,
                    ObserveMessageSize,
                    responseBudget);
                while (!readMessage.IsCompleted)
                {
                    await Task.WhenAny(readMessage, Task.Delay(100, token)).ConfigureAwait(false);
                    parentResourceWarning |= HasResourceWarning(process, request.Limits, elapsed.Elapsed);
                    PdfInspectionProblemCode? monitoredFailure = CheckResources(process, request.Limits);
                    if (monitoredFailure is { } code)
                    {
                        await TryTerminateAsync(process).ConfigureAwait(false);
                        return Failed(code, PdfOpenStatus.ResourceLimited);
                    }
                }

                PdfWorkerOutputMessage message = await readMessage.ConfigureAwait(false);
                if (!string.Equals(message.ProtocolVersion, PdfWorkerProtocol.Version, StringComparison.Ordinal))
                {
                    await TryTerminateAsync(process).ConfigureAwait(false);
                    return Failed(PdfInspectionProblemCode.WorkerProtocolInvalid, PdfOpenStatus.WorkerFailed);
                }

                switch (message.Kind)
                {
                    case "Progress" when message.Progress is not null
                        && message.Header is null && message.Result is null && message.FailureCode is null:
                        progress?.Report(message.Progress with
                        {
                            ResourceWarning = message.Progress.ResourceWarning || parentResourceWarning,
                        });
                        break;
                    case "DocumentOpened" when message.Header is not null
                        && message.Progress is null && message.Result is null && message.FailureCode is null:
                        if (message.Header.BookId != request.BookId
                            || message.Header.ExpectedRelativePath != request.ExpectedRelativePath)
                        {
                            await TryTerminateAsync(process).ConfigureAwait(false);
                            return Failed(PdfInspectionProblemCode.WorkerProtocolInvalid, PdfOpenStatus.WorkerFailed);
                        }

                        IReadOnlyList<int> selected = await selectPages(message.Header, token).ConfigureAwait(false);
                        await PdfWorkerProtocol.WriteAsync(
                            process.StandardInput,
                            new PdfWorkerSelectionMessage(PdfWorkerProtocol.Version, "PageSelection", selected),
                            request.Limits.MaximumWorkerMessageBytes,
                            token,
                            observeByteCount: ObserveMessageSize).ConfigureAwait(false);
                        break;
                    case "Completed" when message.Result is not null
                        && message.Header is null && message.Progress is null && message.FailureCode is null:
                        if (message.Result.BookId != request.BookId
                            || message.Result.ExpectedRelativePath != request.ExpectedRelativePath)
                        {
                            await TryTerminateAsync(process).ConfigureAwait(false);
                            return Failed(PdfInspectionProblemCode.WorkerProtocolInvalid, PdfOpenStatus.WorkerFailed);
                        }

                        process.StandardInput.Close();
                        await process.WaitForExitAsync(token).ConfigureAwait(false);
                        await discardError.ConfigureAwait(false);
                        if (process.ExitCode != 0)
                        {
                            return Failed(PdfInspectionProblemCode.WorkerCrashed, PdfOpenStatus.WorkerFailed);
                        }

                        PdfInspectionResult completed = parentResourceWarning
                            ? message.Result with { ResourceCountsWithinSoftLimits = false }
                            : message.Result;
                        return ParentIdentityMatches(request, completed)
                            ? completed
                            : Failed(PdfInspectionProblemCode.ChangedFile, PdfOpenStatus.Unreadable);
                    case "Failed" when message.FailureCode is not null
                        && message.Header is null && message.Progress is null && message.Result is null:
                        await TryTerminateAsync(process).ConfigureAwait(false);
                        return Failed(message.FailureCode.Value, PdfOpenStatus.WorkerFailed);
                    default:
                        await TryTerminateAsync(process).ConfigureAwait(false);
                        return Failed(PdfInspectionProblemCode.WorkerProtocolInvalid, PdfOpenStatus.WorkerFailed);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryTerminateAsync(process).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TryTerminateAsync(process).ConfigureAwait(false);
            return Failed(PdfInspectionProblemCode.ParserTimeout, PdfOpenStatus.TimedOut);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or JsonException)
        {
            await TryTerminateAsync(process).ConfigureAwait(false);
            return Failed(PdfInspectionProblemCode.WorkerProtocolInvalid, PdfOpenStatus.WorkerFailed);
        }

        PdfInspectionResult Failed(PdfInspectionProblemCode code, PdfOpenStatus status) =>
            PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, code, status);

        void ObserveMessageSize(int byteCount) =>
            parentResourceWarning |= byteCount > request.Limits.WorkerMessageWarningBytes;
    }

    internal static PdfInspectionProblemCode? CheckResources(Process process, PdfInspectionLimits limits)
    {
        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                return null;
            }

            if (process.TotalProcessorTime > TimeSpan.FromSeconds(limits.CpuTimeSeconds))
            {
                return PdfInspectionProblemCode.CpuLimitExceeded;
            }

            return process.WorkingSet64 > limits.WorkingSetBytes ? PdfInspectionProblemCode.MemoryLimitExceeded : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool HasResourceWarning(Process process, PdfInspectionLimits limits, TimeSpan elapsed)
    {
        try
        {
            process.Refresh();
            return !process.HasExited
                && (elapsed > TimeSpan.FromSeconds(limits.WallTimeWarningSeconds)
                    || process.TotalProcessorTime > TimeSpan.FromSeconds(limits.CpuTimeWarningSeconds)
                    || process.WorkingSet64 > limits.WorkingSetWarningBytes);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool ParentIdentityMatches(PdfInspectionRequest request, PdfInspectionResult result)
    {
        if (result.Problems.Any(problem => problem.Code is
            PdfInspectionProblemCode.MissingFile or
            PdfInspectionProblemCode.InaccessibleFile or
            PdfInspectionProblemCode.UnsafePath or
            PdfInspectionProblemCode.ChangedFile))
        {
            return true;
        }

        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.LibraryRoot));
            string fullPath = Path.GetFullPath(request.FullPath);
            string expectedPath = Path.GetFullPath(Path.Combine(root, request.ExpectedRelativePath));
            string rootPrefix = root + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(fullPath, expectedPath, comparison)
                || !fullPath.StartsWith(rootPrefix, comparison))
            {
                return false;
            }

            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            string currentPath = root;
            foreach (string segment in Path.GetRelativePath(root, fullPath)
                         .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                currentPath = Path.Combine(currentPath, segment);
                if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }

            FileInfo current = new(fullPath);
            current.Refresh();
            return current.Exists
                && current.Length == request.Observation.Length
                && current.Length == request.Fingerprint.SizeInBytes
                && current.LastWriteTimeUtc == request.Observation.LastWriteTimeUtc.UtcDateTime
                && (int)current.Attributes == request.Observation.Attributes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task DrainDiscardedErrorAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[1024];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            Array.Clear(buffer, 0, read);
        }
    }

    private static async Task TryTerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using CancellationTokenSource termination = new(TimeSpan.FromMilliseconds(750));
                await process.WaitForExitAsync(termination.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                   or System.ComponentModel.Win32Exception
                   or OperationCanceledException
                   or NotSupportedException)
        {
        }
    }
}
