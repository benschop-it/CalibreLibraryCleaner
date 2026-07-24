using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Recommendations;

namespace CalibreLibraryCleaner.Infrastructure.Recommendations;

internal sealed class VersionedJsonRecommendationExporter : IRecommendationExporter
{
    public async Task<RecommendationExportWriteOutcome> ExportAsync(
        RecommendationReviewExportDocument document,
        string libraryRoot,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        RecommendationExportPathOutcome path = RecommendationExportPathGuard.Validate(libraryRoot, destinationPath);
        if (!path.IsSuccess)
        {
            return RecommendationExportWriteOutcome.Failure(path.Error!.Code, path.Error.Message);
        }

        string destination = path.CanonicalDestination!;
        string directory = Path.GetDirectoryName(destination)!;
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] bytes = RecommendationJsonSerializer.Serialize(document);
            cancellationToken.ThrowIfCancellationRequested();
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
            return RecommendationExportWriteOutcome.Success(destination);
        }
        catch (OperationCanceledException)
        {
            DeleteOwnTemporaryFile(temporaryPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            DeleteOwnTemporaryFile(temporaryPath);
            return RecommendationExportWriteOutcome.Failure("EXPORT_WRITE_FAILED", "The recommendation review artifact could not be written safely.");
        }
    }

    private static void DeleteOwnTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
