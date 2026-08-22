using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;

namespace CalibreLibraryCleaner.Application.Metadata;

public sealed record EditionCoverStagingRequest
{
    public EditionCoverStagingRequest(string key, EditionCoverReference cover)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(cover);
        if (key.Length > 256) throw new ArgumentOutOfRangeException(nameof(key));
        Key = key;
        Cover = cover;
    }

    public string Key { get; }
    public EditionCoverReference Cover { get; }
}

public sealed record StagedEditionCover(string FileName, FormatFileFingerprint Fingerprint);

public sealed record EditionCoverStagingProgress(
    int Completed,
    int Total,
    int CacheHits = 0,
    int Downloads = 0,
    int Omitted = 0);

public interface IEditionCoverStagingSession : IAsyncDisposable
{
    string StagingRoot { get; }
    IReadOnlyDictionary<string, StagedEditionCover> Covers { get; }
}

public sealed record EditionCoverStagingResult(
    IEditionCoverStagingSession? Session,
    string? FailureCode,
    int CompletedCount = 0,
    int TotalCount = 0,
    IReadOnlyDictionary<string, string>? SkippedCovers = null)
{
    public bool IsSuccess => Session is not null && FailureCode is null;
}

public interface IEditionCoverStager
{
    Task<EditionCoverStagingResult> StageAsync(
        IReadOnlyList<EditionCoverStagingRequest> requests,
        IProgress<EditionCoverStagingProgress>? progress,
        CancellationToken cancellationToken);
}
