namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IEbookViewerLauncher
{
    Task<EbookViewerLaunchResult> LaunchAsync(
        EbookViewerLaunchRequest request,
        CancellationToken cancellationToken);
}

public sealed record EbookViewerLaunchRequest(
    string LibraryRoot,
    string ExpectedRelativePath);

public sealed record EbookViewerLaunchResult(
    bool IsSuccess,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static EbookViewerLaunchResult Success() => new(true);

    public static EbookViewerLaunchResult Failure(string code, string message) =>
        new(false, code, message);
}
