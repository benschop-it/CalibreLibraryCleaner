using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public enum ManagedBookLayoutProblem
{
    UnregisteredFormatFile,
    DuplicateRecordDirectory,
    InspectionUnavailable,
}

public static class ManagedBookLayoutFindingCodes
{
    public const string UnregisteredFormatFile = "LIBRARY.UNREGISTERED_FORMAT_FILE";
    public const string DuplicateRecordDirectory = "LIBRARY.DUPLICATE_RECORD_DIRECTORY";
    public const string InspectionUnavailable = "LIBRARY.MANAGED_LAYOUT_INSPECTION_UNAVAILABLE";

    public static bool BlocksManagedNameRefresh(string code) => code is
        UnregisteredFormatFile or DuplicateRecordDirectory or InspectionUnavailable;
}

public sealed record ManagedBookLayoutInspectionTarget(
    CalibreBookId BookId,
    string RelativeDirectory,
    IReadOnlyList<string> RegisteredFormatRelativePaths);

public sealed record ManagedBookLayoutIssue(
    CalibreBookId BookId,
    ManagedBookLayoutProblem Problem);

public interface IManagedBookLayoutInspector
{
    Task<IReadOnlyList<ManagedBookLayoutIssue>> InspectAsync(
        ValidatedLibraryLocation location,
        IReadOnlyList<ManagedBookLayoutInspectionTarget> targets,
        CancellationToken cancellationToken);
}
