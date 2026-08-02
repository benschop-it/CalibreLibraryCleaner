namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record BookFormat
{
    public BookFormat(
        string format,
        string storedFileName,
        string expectedRelativePath,
        FormatFileStatus fileStatus,
        FormatFileFingerprint? fingerprint = null,
        FormatFileObservation? observation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentNullException.ThrowIfNull(storedFileName);
        ArgumentNullException.ThrowIfNull(expectedRelativePath);
        bool scannedPresent = fileStatus == FormatFileStatus.Present;
        bool projectedPresent = fileStatus == FormatFileStatus.ProjectedPresent;
        if (scannedPresent != (fingerprint is not null && observation is not null)
            || projectedPresent && (fingerprint is null || observation is not null)
            || !scannedPresent && !projectedPresent && (fingerprint is not null || observation is not null))
        {
            throw new ArgumentException("Scanned formats require fingerprint and observation; projected formats require only a fingerprint.", nameof(fingerprint));
        }

        if (observation is not null && fingerprint!.SizeInBytes != observation.Length)
        {
            throw new ArgumentException("The fingerprint and verified observation lengths must match.", nameof(observation));
        }

        Format = format;
        StoredFileName = storedFileName;
        ExpectedRelativePath = expectedRelativePath;
        FileStatus = fileStatus;
        Fingerprint = fingerprint;
        Observation = observation;
    }

    public string Format { get; }

    public string StoredFileName { get; }

    public string ExpectedRelativePath { get; }

    public FormatFileStatus FileStatus { get; }

    public FormatFileFingerprint? Fingerprint { get; }

    public FormatFileObservation? Observation { get; }

    public bool IsProjected => FileStatus == FormatFileStatus.ProjectedPresent;
}
