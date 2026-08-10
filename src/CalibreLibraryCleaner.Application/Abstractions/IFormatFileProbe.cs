using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IFormatFileProbe
{
    Task<FormatFileProbeResult> ProbeAsync(
        ResolvedFormatPath path,
        CancellationToken cancellationToken);
}

public enum FormatFileProbeStatus
{
    Success,
    Missing,
    Inaccessible,
    UnsafePath,
}

public sealed record FormatFileProbeResult(
    FormatFileProbeStatus Status,
    FormatFileObservation? Observation,
    string? ReasonCode = null)
{
    public static FormatFileProbeResult Success(FormatFileObservation observation) =>
        new(FormatFileProbeStatus.Success, observation);

    public static FormatFileProbeResult Failure(FormatFileProbeStatus status, string reasonCode) =>
        new(status, null, reasonCode);
}
