using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IExactDuplicateFormatStaging
{
    Task<StagedExactDuplicateFormat> StageAsync(
        CleanupExecutionId executionId,
        string libraryRoot,
        CalibreBookId recordId,
        BookFormat format,
        CancellationToken cancellationToken);

    Task CleanupAsync(
        CleanupExecutionId executionId,
        CancellationToken cancellationToken);
}

public sealed record StagedExactDuplicateFormat(
    CalibreBookId SourceRecordId,
    string Format,
    string PhysicalPath,
    FormatFileFingerprint Fingerprint);
