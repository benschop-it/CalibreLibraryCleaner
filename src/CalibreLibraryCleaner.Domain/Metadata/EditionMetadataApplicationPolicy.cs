using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Domain.Metadata;

public static class EditionMetadataApplicationPolicy
{
    public static IReadOnlyList<LibraryMetadataField> ApplicableFields(
        CalibreBook book,
        EditionMetadataCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(candidate);
        List<LibraryMetadataField> fields = [];
        if (!string.Equals(book.Title, candidate.Title, StringComparison.Ordinal))
            fields.Add(LibraryMetadataField.Title);
        if (EditionMetadataAuthorWritePolicy.TryPrepare(candidate.Authors, out IReadOnlyList<string> authors)
            && !SameTextValues(book.Authors.Select(value => value.Name), authors))
            fields.Add(LibraryMetadataField.Authors);
        if (!IdentifiersApplied(book, WritableIdentifiers(candidate)))
            fields.Add(LibraryMetadataField.Identifiers);
        if (candidate.Publisher is not null
            && !string.Equals(book.PublicationMetadata.Publisher, candidate.Publisher, StringComparison.Ordinal))
            fields.Add(LibraryMetadataField.Publisher);
        if (candidate.PublicationDate is not null
            && !SameDate(book.PublicationMetadata.PublicationDate, candidate.PublicationDate))
            fields.Add(LibraryMetadataField.PublicationDate);
        IReadOnlyList<string> languages = WritableLanguages(candidate);
        if (languages.Count > 0 && !SameLanguages(book.PublicationMetadata.Languages, languages))
            fields.Add(LibraryMetadataField.Languages);
        if (candidate.Series is not null
            && !string.Equals(book.PublicationMetadata.Series, candidate.Series, StringComparison.Ordinal))
            fields.Add(LibraryMetadataField.Series);
        if (candidate.SeriesIndex is not null
            && book.PublicationMetadata.SeriesIndex != candidate.SeriesIndex)
            fields.Add(LibraryMetadataField.SeriesIndex);
        if (candidate.Cover is not null && !book.PublicationMetadata.HasCover)
            fields.Add(LibraryMetadataField.Cover);
        return new ReadOnlyCollection<LibraryMetadataField>(fields.ToArray());
    }

    public static IReadOnlyList<EditionMetadataIdentifier> WritableIdentifiers(
        EditionMetadataCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new ReadOnlyCollection<EditionMetadataIdentifier>(candidate.Identifiers
            .GroupBy(value => value.Type, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(value => value.Type == "isbn" && value.Value.Length == 13)
                .ThenBy(value => value.Value, StringComparer.Ordinal)
                .First())
            .OrderBy(value => value.Type, StringComparer.Ordinal)
            .ToArray());
    }

    public static IReadOnlyList<string> WritableLanguages(EditionMetadataCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new ReadOnlyCollection<string>(candidate.Languages
            .Where(value => value.Length == 2 && value.All(char.IsAsciiLetter))
            .ToArray());
    }

    private static bool IdentifiersApplied(
        CalibreBook book,
        IReadOnlyList<EditionMetadataIdentifier> identifiers)
    {
        if (identifiers.Count == 0) return true;
        HashSet<string> current = book.Identifiers
            .Select(value => CandidateMetadataNormalizer.NormalizeStrongIdentifier(value.Type, value.Value))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
        return identifiers.All(value => current.Contains(value.CanonicalValue));
    }

    private static bool SameTextValues(IEnumerable<string> current, IEnumerable<string> proposed) =>
        current.Select(NormalizeText).SequenceEqual(proposed.Select(NormalizeText), StringComparer.Ordinal);

    private static string NormalizeText(string value) => string.Join(' ',
        value.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool SameDate(DateTimeOffset? current, EditionPublicationDate proposed) =>
        current is not null
        && current.Value.Year == proposed.Year
        && current.Value.Month == (proposed.Month ?? 1)
        && current.Value.Day == (proposed.Day ?? 1);

    private static bool SameLanguages(IEnumerable<string> current, IEnumerable<string> proposed)
    {
        string[] currentValues = current.Select(CandidateMetadataNormalizer.NormalizeLanguage)
            .Where(value => value is not null).Select(value => value!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] proposedValues = proposed.Select(CandidateMetadataNormalizer.NormalizeLanguage)
            .Where(value => value is not null).Select(value => value!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return currentValues.SequenceEqual(proposedValues, StringComparer.Ordinal);
    }
}
