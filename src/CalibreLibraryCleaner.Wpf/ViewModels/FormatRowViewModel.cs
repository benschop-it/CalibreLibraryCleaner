using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class FormatRowViewModel(BookFormat format)
{
    public string Format { get; } = format.Format;

    public string StoredFileName { get; } = format.StoredFileName;

    public string ExpectedRelativePath { get; } = format.ExpectedRelativePath;

    public string Status { get; } = format.FileStatus switch
    {
        FormatFileStatus.Present => "Present",
        FormatFileStatus.Missing => "Missing",
        FormatFileStatus.InvalidPath => "Invalid path",
        _ => "Unknown",
    };
}
