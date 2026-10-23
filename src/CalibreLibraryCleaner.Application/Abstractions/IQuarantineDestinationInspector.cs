using CalibreLibraryCleaner.Application.Metadata;

namespace CalibreLibraryCleaner.Application.Abstractions;

public sealed record QuarantineDestinationInspection(
    QuarantineDestinationIdentity? Identity,
    string? FailureCode = null)
{
    public bool IsSuccess => Identity is not null && FailureCode is null;
}

public interface IQuarantineDestinationInspector
{
    Task<QuarantineDestinationInspection> InspectAsync(
        string sourceLibraryRoot,
        string destinationLibraryRoot,
        CancellationToken cancellationToken);
}
