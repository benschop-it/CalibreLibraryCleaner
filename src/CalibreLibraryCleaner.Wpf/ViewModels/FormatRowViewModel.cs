using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class FormatRowViewModel(BookFormat format)
{
    public string Format { get; } = format.Format;

    public string StoredFileName { get; } = format.StoredFileName;

    public string ExpectedRelativePath { get; } = format.ExpectedRelativePath;

    public string FileSize { get; } = format.Fingerprint is null
        ? string.Empty
        : $"{format.Fingerprint.SizeInBytes:N0} bytes";

    public string Sha256 { get; } = format.Fingerprint?.Sha256.Value ?? string.Empty;

    public string Status { get; } = format.FileStatus switch
    {
        FormatFileStatus.Present => "Present",
        FormatFileStatus.Missing => "Missing",
        FormatFileStatus.InvalidPath => "Invalid path",
        FormatFileStatus.Inaccessible => "Inaccessible",
        FormatFileStatus.ChangedDuringHashing => "Changed during hashing",
        _ => "Unknown",
    };
}
