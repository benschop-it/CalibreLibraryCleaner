using System.Security.Cryptography;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;

internal static class LibraryStateCapture
{
    public static IReadOnlyList<LibraryEntryState> Capture(string root) => Directory
        .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Select(path => CaptureEntry(root, path))
        .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
        .ToArray();

    private static LibraryEntryState CaptureEntry(string root, string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
        byte[]? hash;
        if (isDirectory)
        {
            hash = null;
        }
        else
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            hash = SHA256.HashData(stream);
        }
        long length = isDirectory ? 0 : new FileInfo(path).Length;
        return new(
            Path.GetRelativePath(root, path),
            isDirectory,
            attributes,
            length,
            File.GetCreationTimeUtc(path),
            File.GetLastWriteTimeUtc(path),
            hash);
    }
}

internal sealed record LibraryEntryState(
    string RelativePath,
    bool IsDirectory,
    FileAttributes Attributes,
    long Length,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc,
    byte[]? Sha256);
