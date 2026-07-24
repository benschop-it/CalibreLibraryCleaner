using CalibreLibraryCleaner.Application.Executions;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface ICalibreToolDiscovery
{
    Task<CalibreToolDiscoveryResult> DiscoverAndProbeAsync(
        string libraryRoot,
        CancellationToken cancellationToken);
}

public interface ICalibreCommandGateway
{
    Task<CalibreCommandResult> ExportRecordAsync(
        ExportCalibreRecordRequest request,
        CancellationToken cancellationToken);

    Task<CalibreCommandResult> AddOrReplaceFormatAsync(
        AddOrReplaceCalibreFormatRequest request,
        CancellationToken cancellationToken);

    Task<CalibreCommandResult> RemoveRecordAsync(
        RemoveCalibreRecordRequest request,
        CancellationToken cancellationToken);
}
