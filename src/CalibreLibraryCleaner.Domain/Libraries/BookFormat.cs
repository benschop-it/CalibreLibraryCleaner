namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record BookFormat
{
    public BookFormat(
        string format,
        string storedFileName,
        string expectedRelativePath,
        FormatFileStatus fileStatus,
        FormatFileFingerprint? fingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentNullException.ThrowIfNull(storedFileName);
        ArgumentNullException.ThrowIfNull(expectedRelativePath);
        if ((fileStatus == FormatFileStatus.Present) != (fingerprint is not null))
        {
            throw new ArgumentException("Only a present format must have a fingerprint.", nameof(fingerprint));
        }

        Format = format;
        StoredFileName = storedFileName;
        ExpectedRelativePath = expectedRelativePath;
        FileStatus = fileStatus;
        Fingerprint = fingerprint;
    }

    public string Format { get; }

    public string StoredFileName { get; }

    public string ExpectedRelativePath { get; }

    public FormatFileStatus FileStatus { get; }

    public FormatFileFingerprint? Fingerprint { get; }
}
