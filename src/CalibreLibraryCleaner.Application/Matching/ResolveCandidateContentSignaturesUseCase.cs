using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Application.Matching;

public sealed record CandidateContentSignatureProgress(
    int CompletedFingerprints,
    int TotalFingerprints,
    int CacheHits,
    int Inspections,
    string Detail = "");

public sealed record CandidateContentSignatureBatchResult
{
    public CandidateContentSignatureBatchResult(
        IDictionary<CalibreBookId, EpubContentSignature> signatures,
        IDictionary<CalibreBookId, EpubContentSignatureProblemCode> unavailable,
        int requestedFingerprintCount,
        int cacheHits,
        int inspections)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        ArgumentNullException.ThrowIfNull(unavailable);
        ArgumentOutOfRangeException.ThrowIfNegative(requestedFingerprintCount);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheHits);
        ArgumentOutOfRangeException.ThrowIfNegative(inspections);
        if (cacheHits > requestedFingerprintCount || inspections < 0)
            throw new ArgumentException("Content signature metrics are invalid.");
        Signatures = new ReadOnlyDictionary<CalibreBookId, EpubContentSignature>(
            signatures.OrderBy(value => value.Key.Value).ToDictionary());
        Unavailable = new ReadOnlyDictionary<CalibreBookId, EpubContentSignatureProblemCode>(
            unavailable.OrderBy(value => value.Key.Value).ToDictionary());
        RequestedFingerprintCount = requestedFingerprintCount;
        CacheHits = cacheHits;
        Inspections = inspections;
    }

    public IReadOnlyDictionary<CalibreBookId, EpubContentSignature> Signatures { get; }
    public IReadOnlyDictionary<CalibreBookId, EpubContentSignatureProblemCode> Unavailable { get; }
    public int RequestedFingerprintCount { get; }
    public int CacheHits { get; }
    public int Inspections { get; }
}

public sealed partial class ResolveCandidateContentSignaturesUseCase(
    IEpubContentSignatureInspector inspector,
    IEpubContentSignatureCache cache,
    ILogger<ResolveCandidateContentSignaturesUseCase>? logger = null)
{
    private readonly ILogger<ResolveCandidateContentSignaturesUseCase> _logger = logger
        ?? NullLogger<ResolveCandidateContentSignaturesUseCase>.Instance;

    public async Task<CandidateContentSignatureBatchResult> ExecuteAsync(
        IReadOnlyList<BookCandidatePair> candidatePairs,
        IReadOnlyList<EpubAssessmentTarget> allTargets,
        int maximumConcurrency,
        EpubInspectionLimits inspectionLimits,
        EpubContentSignatureLimits signatureLimits,
        IProgress<CandidateContentSignatureProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidatePairs);
        ArgumentNullException.ThrowIfNull(allTargets);
        ArgumentNullException.ThrowIfNull(inspectionLimits);
        ArgumentNullException.ThrowIfNull(signatureLimits);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumConcurrency, 4);
        cancellationToken.ThrowIfCancellationRequested();
        long runStarted = Stopwatch.GetTimestamp();
        if (candidatePairs.Select(value => value.Id).Distinct().Count() != candidatePairs.Count)
            throw new ArgumentException("Candidate pair IDs must be unique.", nameof(candidatePairs));

        BookCandidatePair[] ambiguousPairs = candidatePairs
            .Where(value => value.NeedsContentEvidence).ToArray();
        HashSet<CalibreBookId> ambiguousBookIds = ambiguousPairs
            .SelectMany(value => new[] { value.Id.First, value.Id.Second }).ToHashSet();
        Dictionary<CalibreBookId, EpubAssessmentTarget> comparableTargets = allTargets
            .Where(value => ambiguousBookIds.Contains(value.BookId)
                && string.Equals(value.Format, "EPUB", StringComparison.OrdinalIgnoreCase)
                && value.FileStatus == FormatFileStatus.Present
                && value.Fingerprint is not null
                && value.Observation is not null
                && value.Fingerprint.SizeInBytes == value.Observation.Length
                && !string.IsNullOrWhiteSpace(value.LibraryRoot)
                && !string.IsNullOrWhiteSpace(value.FullPath)
                && !string.IsNullOrWhiteSpace(value.ExpectedRelativePath))
            .GroupBy(value => value.BookId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(value => value.ExpectedRelativePath, StringComparer.Ordinal).First());
        BookCandidatePair[] comparablePairs = ambiguousPairs.Where(value =>
            comparableTargets.ContainsKey(value.Id.First)
            && comparableTargets.ContainsKey(value.Id.Second)).ToArray();
        HashSet<CalibreBookId> demandedBookIds = comparablePairs
            .SelectMany(value => new[] { value.Id.First, value.Id.Second }).ToHashSet();
        Dictionary<CalibreBookId, EpubAssessmentTarget> selectedTargets = comparableTargets
            .Where(value => demandedBookIds.Contains(value.Key))
            .ToDictionary();
        ConcurrentDictionary<CalibreBookId, EpubContentSignature> signatures = [];
        ConcurrentDictionary<CalibreBookId, EpubContentSignatureProblemCode> unavailable = [];
        foreach (CalibreBookId bookId in ambiguousBookIds.Where(value => !comparableTargets.ContainsKey(value)))
            unavailable[bookId] = EpubContentSignatureProblemCode.CannotOpen;

        WorkItem[] work = selectedTargets.Values
            .Select(target => CreateRequest(target, inspectionLimits, signatureLimits))
            .GroupBy(value => EpubContentSignatureCacheKey.Create(value).Value, StringComparer.Ordinal)
            .Select(group => new WorkItem(
                EpubContentSignatureCacheKey.Create(group.First()),
                group.OrderBy(value => value.Source.BookId.Value)
                    .ThenBy(value => value.Source.ExpectedRelativePath, StringComparer.Ordinal).ToArray()))
            .OrderBy(value => value.Key.Value, StringComparer.Ordinal)
            .Select((value, index) => value with { Index = index + 1 })
            .ToArray();
        long planningCompleted = Stopwatch.GetTimestamp();
        LogResolutionStarted(
            _logger,
            candidatePairs.Count,
            ambiguousPairs.Length,
            comparablePairs.Length,
            ambiguousPairs.Length - comparablePairs.Length,
            demandedBookIds.Count,
            selectedTargets.Count,
            unavailable.Count,
            work.Length,
            maximumConcurrency,
            ElapsedMilliseconds(runStarted, planningCompleted));
        int completed = 0;
        int cacheHits = 0;
        int inspections = 0;
        long cacheHitBytes = 0;
        long inspectionAttemptBytes = 0;
        long cacheReadMilliseconds = 0;
        long inspectionMilliseconds = 0;
        long cacheWriteMilliseconds = 0;
        progress?.Report(new(0, work.Length, 0, 0,
            $"Planned {work.Length:N0} unique EPUB fingerprints."));
        ParallelOptions parallel = new()
        {
            MaxDegreeOfParallelism = maximumConcurrency,
            CancellationToken = cancellationToken,
        };
        await Parallel.ForEachAsync(work, parallel, async (item, token) =>
        {
            long itemStarted = Stopwatch.GetTimestamp();
            long cacheReadStarted = Stopwatch.GetTimestamp();
            EpubContentSignature? signature = await TryReadCacheAsync(item.Key, token).ConfigureAwait(false);
            long cacheReadCompleted = Stopwatch.GetTimestamp();
            long itemCacheReadMilliseconds = ElapsedMilliseconds(cacheReadStarted, cacheReadCompleted);
            Interlocked.Add(ref cacheReadMilliseconds, itemCacheReadMilliseconds);
            string outcome;
            EpubContentSignatureProblemCode? finalProblem = null;
            long itemInspectionMilliseconds = 0;
            long itemCacheWriteMilliseconds = 0;
            int itemInspections = 0;
            if (signature is not null && signature.Fingerprint == item.Key.Fingerprint)
            {
                Interlocked.Increment(ref cacheHits);
                Interlocked.Add(ref cacheHitBytes, item.Key.Fingerprint.SizeInBytes);
                outcome = "CacheHit";
            }
            else
            {
                outcome = "Unavailable";
                EpubContentSignatureProblemCode problem = EpubContentSignatureProblemCode.Unreadable;
                foreach (EpubContentSignatureRequest request in item.Requests)
                {
                    token.ThrowIfCancellationRequested();
                    EpubContentSignatureResult result;
                    try
                    {
                        Interlocked.Increment(ref inspections);
                        Interlocked.Add(ref inspectionAttemptBytes, item.Key.Fingerprint.SizeInBytes);
                        itemInspections++;
                        long inspectionStarted = Stopwatch.GetTimestamp();
                        try
                        {
                            IProgress<EpubContentSignatureProgress>? inspectionProgress = progress is null
                                ? null
                                : new ThrottledContentProgress(value => progress.Report(new(
                                    Volatile.Read(ref completed),
                                    work.Length,
                                    Volatile.Read(ref cacheHits),
                                    Volatile.Read(ref inspections),
                                    FormatInspectionDetail(item.Index, work.Length, value))));
                            result = await inspector.InspectContentSignatureAsync(
                                request, inspectionProgress, token).ConfigureAwait(false);
                        }
                        finally
                        {
                            itemInspectionMilliseconds += ElapsedMilliseconds(
                                inspectionStarted, Stopwatch.GetTimestamp());
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        result = EpubContentSignatureResult.Failure(EpubContentSignatureProblemCode.Unreadable);
                    }
                    if (result.Signature is not null)
                    {
                        signature = result.Signature;
                        long cacheWriteStarted = Stopwatch.GetTimestamp();
                        await TryWriteCacheAsync(item.Key, signature, token).ConfigureAwait(false);
                        itemCacheWriteMilliseconds += ElapsedMilliseconds(
                            cacheWriteStarted, Stopwatch.GetTimestamp());
                        outcome = "Extracted";
                        break;
                    }
                    problem = result.ProblemCode ?? EpubContentSignatureProblemCode.Unreadable;
                }
                if (signature is null)
                {
                    finalProblem = problem;
                    foreach (EpubContentSignatureRequest request in item.Requests)
                        unavailable[request.Source.BookId] = problem;
                }
            }
            Interlocked.Add(ref inspectionMilliseconds, itemInspectionMilliseconds);
            Interlocked.Add(ref cacheWriteMilliseconds, itemCacheWriteMilliseconds);

            if (signature is not null)
                foreach (EpubContentSignatureRequest request in item.Requests)
                    signatures[request.Source.BookId] = signature;
            int current = Interlocked.Increment(ref completed);
            progress?.Report(new(current, work.Length,
                Volatile.Read(ref cacheHits), Volatile.Read(ref inspections),
                $"Completed fingerprint {current:N0} of {work.Length:N0}."));
            LogFingerprintCompleted(
                _logger,
                item.Index,
                work.Length,
                item.Key.Fingerprint.SizeInBytes,
                item.Requests.Length,
                outcome,
                finalProblem?.ToString() ?? "None",
                itemCacheReadMilliseconds,
                itemInspectionMilliseconds,
                itemCacheWriteMilliseconds,
                itemInspections,
                ElapsedMilliseconds(itemStarted, Stopwatch.GetTimestamp()));
            if (current == work.Length || current % 100 == 0)
                LogResolutionProgress(
                    _logger,
                    current,
                    work.Length,
                    Volatile.Read(ref cacheHits),
                    Volatile.Read(ref inspections),
                    unavailable.Count,
                    ElapsedMilliseconds(runStarted, Stopwatch.GetTimestamp()));
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        long pruneStarted = Stopwatch.GetTimestamp();
        if (work.Length > 0)
            await TryPruneCacheAsync(cancellationToken).ConfigureAwait(false);
        long completedAt = Stopwatch.GetTimestamp();
        long pruneMilliseconds = ElapsedMilliseconds(pruneStarted, completedAt);
        LogResolutionCompleted(
            _logger,
            work.Length,
            work.Sum(value => value.Key.Fingerprint.SizeInBytes),
            cacheHits,
            Volatile.Read(ref cacheHitBytes),
            inspections,
            Volatile.Read(ref inspectionAttemptBytes),
            signatures.Count,
            unavailable.Count,
            Volatile.Read(ref cacheReadMilliseconds),
            Volatile.Read(ref inspectionMilliseconds),
            Volatile.Read(ref cacheWriteMilliseconds),
            pruneMilliseconds,
            ElapsedMilliseconds(runStarted, completedAt));
        return new(signatures, unavailable, work.Length, cacheHits, inspections);
    }

    private async Task<EpubContentSignature?> TryReadCacheAsync(
        EpubContentSignatureCacheKey key,
        CancellationToken cancellationToken)
    {
        try
        {
            return await cache.TryReadAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogCacheReadFailure(_logger, exception.GetType().Name);
            return null;
        }
    }

    private async Task TryWriteCacheAsync(
        EpubContentSignatureCacheKey key,
        EpubContentSignature signature,
        CancellationToken cancellationToken)
    {
        try
        {
            await cache.WriteAsync(key, signature, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogCacheWriteFailure(_logger, exception.GetType().Name);
        }
    }

    private async Task TryPruneCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            await cache.PruneAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogCachePruneFailure(_logger, exception.GetType().Name);
        }
    }

    private static EpubContentSignatureRequest CreateRequest(
        EpubAssessmentTarget target,
        EpubInspectionLimits inspectionLimits,
        EpubContentSignatureLimits signatureLimits) => new(
        new(
            target.BookId,
            target.LibraryRoot!,
            target.FullPath!,
            target.ExpectedRelativePath.Replace('\\', '/'),
            target.Fingerprint!,
            target.Observation!,
            inspectionLimits),
        signatureLimits);

    private static string FormatInspectionDetail(
        int workIndex,
        int workTotal,
        EpubContentSignatureProgress progress)
    {
        string units = progress.TotalUnits is > 0
            ? $" {progress.CompletedUnits:N0} of {progress.TotalUnits.Value:N0}"
            : string.Empty;
        return $"Fingerprint {workIndex:N0} of {workTotal:N0}: {progress.Stage}{units}.";
    }

    private static long ElapsedMilliseconds(long started, long completed) =>
        (long)Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds;

    [LoggerMessage(200, LogLevel.Information,
        "Candidate EPUB signature resolution started. CandidatePairs={CandidatePairs}, AmbiguousPairs={AmbiguousPairs}, ComparablePairs={ComparablePairs}, SkippedPairsWithoutTwoEpubs={SkippedPairsWithoutTwoEpubs}, DemandedBooks={DemandedBooks}, ValidTargets={ValidTargets}, MissingTargets={MissingTargets}, UniqueFingerprints={UniqueFingerprints}, MaximumConcurrency={MaximumConcurrency}, PlanningMilliseconds={PlanningMilliseconds}.")]
    private static partial void LogResolutionStarted(
        ILogger logger,
        int candidatePairs,
        int ambiguousPairs,
        int comparablePairs,
        int skippedPairsWithoutTwoEpubs,
        int demandedBooks,
        int validTargets,
        int missingTargets,
        int uniqueFingerprints,
        int maximumConcurrency,
        long planningMilliseconds);

    [LoggerMessage(201, LogLevel.Debug,
        "Candidate EPUB fingerprint {WorkIndex}/{WorkTotal} completed. FileBytes={FileBytes}, AssociatedRecords={AssociatedRecords}, Outcome={Outcome}, ProblemCode={ProblemCode}, CacheReadMilliseconds={CacheReadMilliseconds}, InspectionMilliseconds={InspectionMilliseconds}, CacheWriteMilliseconds={CacheWriteMilliseconds}, InspectionAttempts={InspectionAttempts}, TotalMilliseconds={TotalMilliseconds}.")]
    private static partial void LogFingerprintCompleted(
        ILogger logger,
        int workIndex,
        int workTotal,
        long fileBytes,
        int associatedRecords,
        string outcome,
        string problemCode,
        long cacheReadMilliseconds,
        long inspectionMilliseconds,
        long cacheWriteMilliseconds,
        int inspectionAttempts,
        long totalMilliseconds);

    [LoggerMessage(202, LogLevel.Information,
        "Candidate EPUB signature progress. CompletedFingerprints={CompletedFingerprints}, TotalFingerprints={TotalFingerprints}, CacheHits={CacheHits}, Inspections={Inspections}, UnavailableRecords={UnavailableRecords}, ElapsedMilliseconds={ElapsedMilliseconds}.")]
    private static partial void LogResolutionProgress(
        ILogger logger,
        int completedFingerprints,
        int totalFingerprints,
        int cacheHits,
        int inspections,
        int unavailableRecords,
        long elapsedMilliseconds);

    [LoggerMessage(203, LogLevel.Information,
        "Candidate EPUB signature resolution completed. UniqueFingerprints={UniqueFingerprints}, UniqueFingerprintBytes={UniqueFingerprintBytes}, CacheHits={CacheHits}, CacheHitBytes={CacheHitBytes}, Inspections={Inspections}, InspectionAttemptBytes={InspectionAttemptBytes}, SignatureRecords={SignatureRecords}, UnavailableRecords={UnavailableRecords}, AggregateCacheReadMilliseconds={AggregateCacheReadMilliseconds}, AggregateInspectionMilliseconds={AggregateInspectionMilliseconds}, AggregateCacheWriteMilliseconds={AggregateCacheWriteMilliseconds}, PruneMilliseconds={PruneMilliseconds}, TotalMilliseconds={TotalMilliseconds}.")]
    private static partial void LogResolutionCompleted(
        ILogger logger,
        int uniqueFingerprints,
        long uniqueFingerprintBytes,
        int cacheHits,
        long cacheHitBytes,
        int inspections,
        long inspectionAttemptBytes,
        int signatureRecords,
        int unavailableRecords,
        long aggregateCacheReadMilliseconds,
        long aggregateInspectionMilliseconds,
        long aggregateCacheWriteMilliseconds,
        long pruneMilliseconds,
        long totalMilliseconds);

    [LoggerMessage(204, LogLevel.Warning,
        "Candidate EPUB signature cache read failed with {FailureType}; extraction will continue as a cache miss.")]
    private static partial void LogCacheReadFailure(ILogger logger, string failureType);

    [LoggerMessage(205, LogLevel.Warning,
        "Candidate EPUB signature cache write failed with {FailureType}; matching will continue without this cache entry.")]
    private static partial void LogCacheWriteFailure(ILogger logger, string failureType);

    [LoggerMessage(206, LogLevel.Warning,
        "Candidate EPUB signature cache prune failed with {FailureType}; matching results remain valid.")]
    private static partial void LogCachePruneFailure(ILogger logger, string failureType);

    private sealed record WorkItem(
        EpubContentSignatureCacheKey Key,
        EpubContentSignatureRequest[] Requests,
        int Index = 0);

    private sealed class ThrottledContentProgress(Action<EpubContentSignatureProgress> report) :
        IProgress<EpubContentSignatureProgress>
    {
        private readonly object _gate = new();
        private string _lastStage = string.Empty;
        private long _lastReport = Stopwatch.GetTimestamp();

        public void Report(EpubContentSignatureProgress value)
        {
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                bool stageChanged = !string.Equals(value.Stage, _lastStage, StringComparison.Ordinal);
                bool completed = value.TotalUnits is not null && value.CompletedUnits == value.TotalUnits;
                if (!stageChanged && !completed
                    && Stopwatch.GetElapsedTime(_lastReport, now) < TimeSpan.FromMilliseconds(500))
                    return;
                _lastStage = value.Stage;
                _lastReport = now;
                report(value);
            }
        }
    }
}
