namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record FormatFileObservation
{
    public const string SourceVersion = "format-file-observation/1.0";

    public FormatFileObservation(
        long length,
        DateTimeOffset creationTimeUtc,
        DateTimeOffset lastWriteTimeUtc,
        int attributes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        CreationTimeUtc = creationTimeUtc.ToUniversalTime();
        LastWriteTimeUtc = lastWriteTimeUtc.ToUniversalTime();
        Attributes = attributes;
    }

    public long Length { get; }

    public DateTimeOffset CreationTimeUtc { get; }

    public DateTimeOffset LastWriteTimeUtc { get; }

    public int Attributes { get; }
}
