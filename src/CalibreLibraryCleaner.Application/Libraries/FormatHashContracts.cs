using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record FormatHashRequest(
    int Sequence,
    CalibreBookId BookId,
    string Format,
    ResolvedFormatPath Path,
    bool ForceVerification = false);

public enum FormatHashResultStatus
{
    Success,
    Missing,
    Inaccessible,
    ChangedDuringHashing,
}

public sealed record FormatHashResult(
    int Sequence,
    FormatHashResultStatus Status,
    FormatFileFingerprint? Fingerprint,
    FormatFileObservation? Observation,
    string? ReasonCode)
{
    public bool WasReused { get; init; }

    public FormatHashResult(
        int sequence,
        FormatHashResultStatus status,
        FormatFileFingerprint? fingerprint,
        string? reasonCode)
        : this(sequence, status, fingerprint, null, reasonCode)
    {
    }

    public static FormatHashResult Success(
        int sequence,
        FormatFileFingerprint fingerprint,
        FormatFileObservation observation,
        bool wasReused = false) =>
        new(sequence, FormatHashResultStatus.Success, fingerprint, observation, null)
        {
            WasReused = wasReused,
        };

    public static FormatHashResult Failure(
        int sequence,
        FormatHashResultStatus status,
        string reasonCode) => new(sequence, status, null, null, reasonCode);
}

public sealed record FormatHashProgress(
    long CompletedBytes,
    long TotalBytes,
    int CompletedFiles,
    int TotalFiles,
    int ActiveFiles,
    string Message,
    long FreshBytes = 0,
    long ReusedBytes = 0,
    int FreshFiles = 0,
    int ReusedFiles = 0);
