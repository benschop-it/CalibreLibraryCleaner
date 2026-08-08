using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Matching;

public static class BookMatchingProfileFactory
{
    public static IReadOnlyList<BookMatchingProfile> Create(
        IEnumerable<CalibreBook> books,
        IEnumerable<EpubAssessment>? epubAssessments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(books);
        Dictionary<CalibreBookId, EpubAssessment[]> epubByBook = (epubAssessments ?? [])
            .GroupBy(value => value.CalibreBookId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(value => value.ExpectedRelativePath, StringComparer.Ordinal).ToArray());
        List<BookMatchingProfile> profiles = [];

        foreach (CalibreBook book in books.OrderBy(value => value.Id.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EpubAssessment[] assessments = epubByBook.GetValueOrDefault(book.Id) ?? [];
            string[] titleVariants = new[] { book.Title }
                .Concat(assessments.Select(value => value.Features.EmbeddedTitle))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(4)
                .ToArray();
            string[] titleKeys = titleVariants.SelectMany(CandidateMetadataNormalizer.TitleKeys)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(8).ToArray();
            string[] titleTokens = titleVariants.SelectMany(CandidateMetadataNormalizer.TitleTokens)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(128).ToArray();
            if (titleKeys.Length == 0 || titleTokens.Length == 0) continue;

            string[] authorVariants = book.Authors
                .SelectMany(value => new[] { value.Name, value.SortName })
                .Append(book.AuthorSort)
                .Concat(assessments.SelectMany(value => value.Features.Authors))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(16)
                .ToArray();
            string[] languages = book.PublicationMetadata.Languages
                .Concat(assessments.SelectMany(value => value.Features.Languages))
                .Select(CandidateMetadataNormalizer.NormalizeLanguage)
                .Where(value => value is not null)
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(16)
                .ToArray();
            string[] strongIdentifiers = book.Identifiers
                .Select(value => CandidateMetadataNormalizer.NormalizeStrongIdentifier(value.Type, value.Value))
                .Where(value => value is not null)
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(32)
                .ToArray();
            string[] embeddedIdentifiers = assessments
                .SelectMany(value => value.Features.StrongIdentifiers)
                .Select(CandidateMetadataNormalizer.NormalizeEmbeddedIdentifier)
                .Where(value => value is not null)
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(64)
                .ToArray();
            string[] exactBinaryKeys = book.Formats
                .Where(value => value.Fingerprint is not null)
                .Select(value => BinaryKey(value.Fingerprint!))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(64)
                .ToArray();

            profiles.Add(new(
                book.Id,
                titleKeys,
                titleTokens,
                CandidateMetadataNormalizer.AuthorKeys(authorVariants).Take(64),
                CandidateMetadataNormalizer.AuthorTokens(authorVariants).Take(64),
                languages,
                strongIdentifiers,
                embeddedIdentifiers,
                exactBinaryKeys,
                CandidateMetadataNormalizer.NormalizeSeries(book.PublicationMetadata.Series),
                book.PublicationMetadata.SeriesIndex,
                book.PublicationMetadata.PublicationYear));
        }

        return profiles;
    }

    private static string BinaryKey(FormatFileFingerprint fingerprint) =>
        $"SHA256:{fingerprint.Sha256.Value}:{fingerprint.SizeInBytes}";
}
