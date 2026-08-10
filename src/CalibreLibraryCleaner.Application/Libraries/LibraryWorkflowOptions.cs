namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record LibraryWorkflowOptions
{
    public static LibraryWorkflowOptions Compatibility { get; } = new(
        LibraryAnalysisMode.FullCompatibility);

    public static LibraryWorkflowOptions Staged { get; } = new(
        LibraryAnalysisMode.ExactOnly);

    public LibraryWorkflowOptions(LibraryAnalysisMode initialAnalysisMode)
    {
        if (initialAnalysisMode == LibraryAnalysisMode.CandidateResidual
            || !Enum.IsDefined(initialAnalysisMode))
            throw new ArgumentOutOfRangeException(nameof(initialAnalysisMode));
        InitialAnalysisMode = initialAnalysisMode;
    }

    public LibraryAnalysisMode InitialAnalysisMode { get; }

    public bool IsStaged => InitialAnalysisMode == LibraryAnalysisMode.ExactOnly;
}
