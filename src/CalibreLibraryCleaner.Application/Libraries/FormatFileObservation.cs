namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record FormatFileObservation
{
    public FormatFileObservation(
        long length,
        DateTimeOffset creationTimeUtc,
        DateTimeOffset lastWriteTimeUtc,
        int attributes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        CreationTimeUtc = creationTimeUtc;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Attributes = attributes;
    }

    public long Length { get; }

    public DateTimeOffset CreationTimeUtc { get; }

    public DateTimeOffset LastWriteTimeUtc { get; }

    public int Attributes { get; }
}
