using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record FormatHashRequest(
    int Sequence,
    CalibreBookId BookId,
    string Format,
    ResolvedFormatPath Path);

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
        FormatFileObservation observation) =>
        new(sequence, FormatHashResultStatus.Success, fingerprint, observation, null);

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
    string Message);
