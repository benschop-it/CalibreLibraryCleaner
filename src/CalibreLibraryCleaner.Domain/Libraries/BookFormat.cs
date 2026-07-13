namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record BookFormat
{
    public BookFormat(
        string format,
        string storedFileName,
        string expectedRelativePath,
        FormatFileStatus fileStatus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentNullException.ThrowIfNull(storedFileName);
        ArgumentNullException.ThrowIfNull(expectedRelativePath);

        Format = format;
        StoredFileName = storedFileName;
        ExpectedRelativePath = expectedRelativePath;
        FileStatus = fileStatus;
    }

    public string Format { get; }

    public string StoredFileName { get; }

    public string ExpectedRelativePath { get; }

    public FormatFileStatus FileStatus { get; }
}
