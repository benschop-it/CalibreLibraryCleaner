namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record FormatFileFingerprint
{
    public FormatFileFingerprint(long sizeInBytes, Sha256Digest sha256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeInBytes);
        SizeInBytes = sizeInBytes;
        Sha256 = sha256;
    }

    public long SizeInBytes { get; }

    public Sha256Digest Sha256 { get; }
}
