using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Duplicates;

public sealed record ExactBinaryDuplicateMember
{
    public ExactBinaryDuplicateMember(CalibreBookId bookId, string format, string expectedRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRelativePath);
        BookId = bookId;
        Format = format;
        ExpectedRelativePath = expectedRelativePath;
    }

    public CalibreBookId BookId { get; }

    public string Format { get; }

    public string ExpectedRelativePath { get; }
}
