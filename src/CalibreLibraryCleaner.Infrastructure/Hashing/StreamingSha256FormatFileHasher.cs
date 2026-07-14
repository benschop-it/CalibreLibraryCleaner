using System.Buffers;
using System.Security;
using System.Security.Cryptography;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using Microsoft.Extensions.Logging;

namespace CalibreLibraryCleaner.Infrastructure.Hashing;

internal sealed class StreamingSha256FormatFileHasher(
    ILogger<StreamingSha256FormatFileHasher> logger) : IFormatFileHasher
{
    private const int BufferSize = 128 * 1024;
    private const long ProgressInterval = 4L * 1024 * 1024;
    private const int CompletedFileProgressInterval = 100;

    private static readonly Action<ILogger, string, string, Exception?> FileHashFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1, nameof(FileHashFailed)),
            "Format hashing failed with reason {ReasonCode} and exception type {ExceptionType}");

    public async Task<IReadOnlyList<FormatHashResult>> HashAsync(
        IReadOnlyList<FormatHashRequest> requests,
        int maxDegreeOfParallelism,
        IProgress<FormatHashProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            return await HashCoreAsync(
                    requests,
                    maxDegreeOfParallelism,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailure("UnexpectedBatchFailure", exception);
            throw;
        }
    }

    private async Task<IReadOnlyList<FormatHashResult>> HashCoreAsync(
        IReadOnlyList<FormatHashRequest> requests,
        int maxDegreeOfParallelism,
        IProgress<FormatHashProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDegreeOfParallelism);
        cancellationToken.ThrowIfCancellationRequested();

        FormatHashResult?[] results = new FormatHashResult?[requests.Count];
        List<PreparedRequest> prepared = [];
        HashSet<int> sequences = [];
        long totalBytes = 0;
        foreach (FormatHashRequest request in requests.OrderBy(request => request.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Sequence < 0 || request.Sequence >= requests.Count || !sequences.Add(request.Sequence))
            {
                throw new InvalidOperationException("Format hash request sequences must be unique and contiguous.");
            }

            PreflightOutcome preflight = Preflight(request);
            if (preflight.State is null)
            {
                results[request.Sequence] = preflight.Failure;
            }
            else
            {
                prepared.Add(new(request, preflight.State));
                totalBytes = checked(totalBytes + preflight.State.Length);
            }
        }

        ProgressCoordinator coordinator = new(
            progress,
            totalBytes,
            requests.Count,
            results.Count(result => result is not null));
        coordinator.ReportInitial();
        ParallelOptions parallelOptions = new()
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
        };
        await Parallel.ForEachAsync(prepared, parallelOptions, async (item, token) =>
        {
            coordinator.StartFile();
            try
            {
                results[item.Request.Sequence] = await HashOneAsync(item, coordinator, token).ConfigureAwait(false);
            }
            finally
            {
                coordinator.CompleteFile();
            }
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        coordinator.ReportCompleted();
        return results.Select(result => result ?? throw new InvalidOperationException("A format hash result is missing.")).ToArray();
    }

    private static PreflightOutcome Preflight(FormatHashRequest request)
    {
        try
        {
            EnsureSafeManagedPath(request.Path);
            FileState state = CaptureState(request.Path.FullPath);
            if ((state.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return PreflightOutcome.FromFailure(FormatHashResult.Failure(
                    request.Sequence,
                    FormatHashResultStatus.Inaccessible,
                    "NotRegularFile"));
            }

            return PreflightOutcome.Success(state);
        }
        catch (FileNotFoundException)
        {
            return PreflightOutcome.FromFailure(FormatHashResult.Failure(
                request.Sequence,
                FormatHashResultStatus.Missing,
                "FileNotFound"));
        }
        catch (DirectoryNotFoundException)
        {
            return PreflightOutcome.FromFailure(FormatHashResult.Failure(
                request.Sequence,
                FormatHashResultStatus.Missing,
                "FileNotFound"));
        }
        catch (UnauthorizedAccessException)
        {
            return PreflightOutcome.FromFailure(FormatHashResult.Failure(
                request.Sequence,
                FormatHashResultStatus.Inaccessible,
                "AccessDenied"));
        }
        catch (SecurityException)
        {
            return PreflightOutcome.FromFailure(FormatHashResult.Failure(
                request.Sequence,
                FormatHashResultStatus.Inaccessible,
                "SecurityDenied"));
        }
        catch (UnsafeManagedPathException)
        {
            return PreflightOutcome.FromFailure(FormatHashResult.Failure(
                request.Sequence,
                FormatHashResultStatus.Inaccessible,
                "UnsafeManagedPath"));
        }
        catch (IOException)
        {
            return PreflightOutcome.FromFailure(FormatHashResult.Failure(
                request.Sequence,
                FormatHashResultStatus.Inaccessible,
                "MetadataReadFailed"));
        }
    }

    private async Task<FormatHashResult> HashOneAsync(
        PreparedRequest prepared,
        ProgressCoordinator progress,
        CancellationToken cancellationToken)
    {
        byte[]? buffer = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryEnsureSafeManagedPath(prepared.Request.Path)
                || !TryCaptureState(prepared.Request.Path.FullPath, out FileState? beforeOpen)
                || beforeOpen != prepared.State)
            {
                return Changed(prepared.Request.Sequence, "ChangedBeforeOpen");
            }

            FileStreamOptions options = new()
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            };
            await using FileStream stream = new(prepared.Request.Path.FullPath, options);
            if (!TryEnsureSafeManagedPath(prepared.Request.Path)
                || stream.Length != prepared.State.Length)
            {
                return Changed(prepared.Request.Sequence, "LengthChangedBeforeRead");
            }

            buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytesRead = 0;
            while (true)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, count));
                bytesRead += count;
                progress.AddBytes(count);
            }

            if (bytesRead != prepared.State.Length
                || stream.Length != prepared.State.Length
                || !TryEnsureSafeManagedPath(prepared.Request.Path)
                || !TryCaptureState(prepared.Request.Path.FullPath, out FileState? afterRead)
                || afterRead != prepared.State)
            {
                return Changed(prepared.Request.Sequence, "ChangedDuringRead");
            }

            string digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return FormatHashResult.Success(
                prepared.Request.Sequence,
                new FormatFileFingerprint(prepared.State.Length, new Sha256Digest(digest)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return Changed(prepared.Request.Sequence, "RemovedDuringHashing");
        }
        catch (DirectoryNotFoundException)
        {
            return Changed(prepared.Request.Sequence, "RemovedDuringHashing");
        }
        catch (UnauthorizedAccessException exception)
        {
            LogFailure("AccessDenied", exception);
            return Inaccessible(prepared.Request.Sequence, "AccessDenied");
        }
        catch (SecurityException exception)
        {
            LogFailure("SecurityDenied", exception);
            return Inaccessible(prepared.Request.Sequence, "SecurityDenied");
        }
        catch (IOException exception)
        {
            LogFailure("ReadFailed", exception);
            return Inaccessible(prepared.Request.Sequence, "ReadFailed");
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static FileState CaptureState(string path)
    {
        FileInfo info = new(path);
        info.Refresh();
        return new(info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, info.Attributes);
    }

    private static void EnsureSafeManagedPath(ResolvedFormatPath path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.LibraryRoot));
        string fullPath = Path.GetFullPath(path.FullPath);
        string expectedPath = Path.GetFullPath(Path.Combine(root, path.RelativePath));
        string rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!string.Equals(fullPath, expectedPath, StringComparison.OrdinalIgnoreCase)
            || !fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafeManagedPathException();
        }

        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null)
        {
            throw new UnsafeManagedPathException();
        }

        FileAttributes rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
        {
            throw new UnsafeManagedPathException();
        }

        string relativeParent = Path.GetRelativePath(root, parent);
        string current = root;
        foreach (string part in relativeParent.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            {
                throw new UnsafeManagedPathException();
            }
        }
    }

    private static bool TryEnsureSafeManagedPath(ResolvedFormatPath path)
    {
        try
        {
            EnsureSafeManagedPath(path);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or SecurityException
                or UnsafeManagedPathException)
        {
            return false;
        }
    }

    private void LogFailure(string reasonCode, Exception exception) =>
        FileHashFailed(logger, reasonCode, exception.GetType().FullName ?? exception.GetType().Name, null);

    private static bool TryCaptureState(string path, out FileState? state)
    {
        try
        {
            state = CaptureState(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            state = null;
            return false;
        }
    }

    private static FormatHashResult Changed(int sequence, string reason) =>
        FormatHashResult.Failure(sequence, FormatHashResultStatus.ChangedDuringHashing, reason);

    private static FormatHashResult Inaccessible(int sequence, string reason) =>
        FormatHashResult.Failure(sequence, FormatHashResultStatus.Inaccessible, reason);

    private sealed record FileState(
        long Length,
        DateTime CreationTimeUtc,
        DateTime LastWriteTimeUtc,
        FileAttributes Attributes);

    private sealed record PreparedRequest(FormatHashRequest Request, FileState State);

    private sealed record PreflightOutcome(FileState? State, FormatHashResult? Failure)
    {
        public static PreflightOutcome Success(FileState state) => new(state, null);

        public static PreflightOutcome FromFailure(FormatHashResult failure) => new(null, failure);
    }

    private sealed class ProgressCoordinator
    {
        private readonly object _gate = new();
        private readonly IProgress<FormatHashProgress>? _progress;
        private readonly long _totalBytes;
        private readonly int _totalFiles;
        private long _completedBytes;
        private long _lastReportedBytes;
        private int _completedFiles;
        private int _activeFiles;

        public ProgressCoordinator(
            IProgress<FormatHashProgress>? progress,
            long totalBytes,
            int totalFiles,
            int initiallyCompletedFiles)
        {
            _progress = progress;
            _totalBytes = totalBytes;
            _totalFiles = totalFiles;
            _completedFiles = initiallyCompletedFiles;
        }

        public void ReportInitial() => Report(force: true);

        public void StartFile()
        {
            lock (_gate)
            {
                _activeFiles++;
            }
        }

        public void AddBytes(int count)
        {
            lock (_gate)
            {
                _completedBytes += count;
                ReportLocked(force: _completedBytes - _lastReportedBytes >= ProgressInterval);
            }
        }

        public void CompleteFile()
        {
            lock (_gate)
            {
                _activeFiles--;
                _completedFiles++;
                ReportLocked(force: _completedFiles == _totalFiles || _completedFiles % CompletedFileProgressInterval == 0);
            }
        }

        public void ReportCompleted()
        {
            lock (_gate)
            {
                _completedFiles = _totalFiles;
                ReportLocked(force: true);
            }
        }

        private void Report(bool force)
        {
            lock (_gate)
            {
                ReportLocked(force);
            }
        }

        private void ReportLocked(bool force)
        {
            if (!force || _progress is null)
            {
                return;
            }

            _lastReportedBytes = _completedBytes;
            _progress.Report(new(
                Math.Min(_completedBytes, _totalBytes),
                _totalBytes,
                _completedFiles,
                _totalFiles,
                _activeFiles,
                $"Hashing ebook files: {_completedFiles} of {_totalFiles} complete"));
        }
    }

    private sealed class UnsafeManagedPathException : Exception
    {
    }
}
