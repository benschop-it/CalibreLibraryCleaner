using CalibreLibraryCleaner.Application.Recoveries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IRecoverySourceArtifactReader
{
    Task<RecoverySourceInspection> ReadAndVerifyAsync(
        string sourceBundle,
        CancellationToken cancellationToken);
}
