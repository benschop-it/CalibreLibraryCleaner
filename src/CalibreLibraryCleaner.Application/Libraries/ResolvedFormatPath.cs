namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record ResolvedFormatPath(string FullPath, string RelativePath);

public sealed record ResolvedFormatPathOutcome
{
    private ResolvedFormatPathOutcome(ResolvedFormatPath? path, string? reason)
    {
        Path = path;
        Reason = reason;
    }

    public bool IsSuccess => Path is not null;

    public ResolvedFormatPath? Path { get; }

    public string? Reason { get; }

    public static ResolvedFormatPathOutcome Success(ResolvedFormatPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new(path, null);
    }

    public static ResolvedFormatPathOutcome Failure(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(null, reason);
    }
}
