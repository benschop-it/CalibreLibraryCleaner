namespace CalibreLibraryCleaner.Infrastructure.DependencyInjection;

public sealed class ApplicationStorageRoot
{
    public const string EnvironmentVariable = "CALIBRE_CLEANER_DATA_ROOT";

    public ApplicationStorageRoot(string? root = null)
    {
        string value = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CalibreLibraryCleaner");
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new ArgumentException("Application storage requires an absolute physical directory.", nameof(root));
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        Validate(Root);
    }

    public string Root { get; }

    public string GetPath(params string[] segments)
    {
        string path = Path.GetFullPath(Path.Combine([Root, .. segments]));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Application storage paths must remain inside their configured root.", nameof(segments));
        Validate(path);
        return path;
    }

    private static void Validate(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "metadata.db")))
                throw new InvalidOperationException("Application storage cannot be inside a Calibre library.");
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Application storage cannot use linked directories.");
            if (File.Exists(directory.FullName))
                throw new InvalidOperationException("Application storage requires directories, not files.");
        }
    }
}
