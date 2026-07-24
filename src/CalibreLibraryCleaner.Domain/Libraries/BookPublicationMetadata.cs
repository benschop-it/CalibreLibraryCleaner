using System.Collections.ObjectModel;

namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record BookPublicationMetadata
{
    public BookPublicationMetadata(
        string? publisher = null,
        DateTimeOffset? publicationDate = null,
        string? series = null,
        decimal? seriesIndex = null,
        IEnumerable<string>? languages = null,
        bool hasCover = false)
    {
        if (seriesIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seriesIndex));
        }

        Publisher = publisher;
        PublicationDate = publicationDate;
        Series = series;
        SeriesIndex = seriesIndex;
        Languages = new ReadOnlyCollection<string>((languages ?? [])
            .Select(language => language ?? throw new ArgumentException("Stored language values cannot be null.", nameof(languages)))
            .ToArray());
        HasCover = hasCover;
    }

    public static BookPublicationMetadata Empty { get; } = new();

    public string? Publisher { get; }

    public DateTimeOffset? PublicationDate { get; }

    public int? PublicationYear => PublicationDate?.Year;

    public string? Series { get; }

    public decimal? SeriesIndex { get; }

    public IReadOnlyList<string> Languages { get; }

    public bool HasCover { get; }
}
