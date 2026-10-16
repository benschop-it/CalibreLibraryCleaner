using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Domain.Metadata;

public sealed record EditionMetadataMergePreview(
    string Title,
    IReadOnlyList<string> Authors,
    IReadOnlyList<string> Identifiers,
    string? Publisher,
    DateTimeOffset? PublicationDate,
    IReadOnlyList<string> Languages,
    string? Series,
    decimal? SeriesIndex,
    bool HasCover);

public static class EditionMetadataApplicationPolicy
{
    public static bool CanApplyAutomatically(
        CalibreBook book,
        EditionMetadataCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(candidate);
        HashSet<LibraryMetadataField> fields = ApplicableFields(book, candidate).ToHashSet();
        if (fields.Contains(LibraryMetadataField.Title)
            && !IsCasingImprovement(book.Title, candidate.Title)) return false;
        if (fields.Contains(LibraryMetadataField.Authors)
            && !IsCasingImprovement(
                book.Authors.Select(value => value.Name),
                candidate.Authors)) return false;
        if (fields.Contains(LibraryMetadataField.Identifiers)
            && WritableIdentifiers(candidate).Any(proposed => book.Identifiers.Any(current =>
                string.Equals(current.Type, proposed.Type, StringComparison.OrdinalIgnoreCase)))) return false;
        if (fields.Contains(LibraryMetadataField.Publisher)
            && !string.IsNullOrWhiteSpace(book.PublicationMetadata.Publisher)) return false;
        if (fields.Contains(LibraryMetadataField.PublicationDate)
            && book.PublicationMetadata.PublicationDate is not null) return false;
        if (fields.Contains(LibraryMetadataField.Languages)
            && book.PublicationMetadata.Languages.Count > 0) return false;
        if (fields.Contains(LibraryMetadataField.Series)
            && !string.IsNullOrWhiteSpace(book.PublicationMetadata.Series)) return false;
        if (fields.Contains(LibraryMetadataField.SeriesIndex)
            && book.PublicationMetadata.SeriesIndex is not null) return false;
        return true;
    }

    public static EditionMetadataMergePreview Preview(
        CalibreBook book,
        EditionMetadataCandidate? candidate,
        bool apply)
    {
        ArgumentNullException.ThrowIfNull(book);
        HashSet<LibraryMetadataField> fields = apply && candidate is not null
            ? ApplicableFields(book, candidate).ToHashSet()
            : new HashSet<LibraryMetadataField>();
        IReadOnlyList<string> authors = book.Authors.Select(value => value.Name).ToArray();
        if (candidate is not null && fields.Contains(LibraryMetadataField.Authors)
            && EditionMetadataAuthorWritePolicy.TryPrepare(candidate.Authors, out IReadOnlyList<string> prepared))
            authors = prepared;
        Dictionary<string, string> identifiers = book.Identifiers
            .ToDictionary(value => value.Type, value => value.Value, StringComparer.OrdinalIgnoreCase);
        if (candidate is not null && fields.Contains(LibraryMetadataField.Identifiers))
            foreach (EditionMetadataIdentifier identifier in WritableIdentifiers(candidate))
                identifiers[identifier.Type] = identifier.Value;
        IReadOnlyList<string> languages = candidate is not null && fields.Contains(LibraryMetadataField.Languages)
            ? WritableLanguages(candidate)
            : book.PublicationMetadata.Languages;
        return new(
            candidate is not null && fields.Contains(LibraryMetadataField.Title) ? candidate.Title : book.Title,
            authors,
            identifiers.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                .Select(value => $"{value.Key}:{value.Value}").ToArray(),
            candidate is not null && fields.Contains(LibraryMetadataField.Publisher)
                ? candidate.Publisher : book.PublicationMetadata.Publisher,
            candidate is not null && fields.Contains(LibraryMetadataField.PublicationDate)
                ? Date(candidate.PublicationDate!) : book.PublicationMetadata.PublicationDate,
            languages,
            candidate is not null && fields.Contains(LibraryMetadataField.Series)
                ? candidate.Series : book.PublicationMetadata.Series,
            candidate is not null && fields.Contains(LibraryMetadataField.SeriesIndex)
                ? candidate.SeriesIndex : book.PublicationMetadata.SeriesIndex,
            book.PublicationMetadata.HasCover
                || candidate is not null && fields.Contains(LibraryMetadataField.Cover));
    }

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

    public static bool IsAllCaps(string value)
    {
        bool hasLetter = false;
        bool hasLower = false;
        foreach (char character in value)
        {
            if (!char.IsLetter(character)) continue;
            hasLetter = true;
            hasLower |= char.IsLower(character);
        }
        return hasLetter && !hasLower;
    }

    private static bool IsCasingImprovement(string current, string proposed) =>
        string.Equals(current, proposed, StringComparison.OrdinalIgnoreCase)
        && IsAllCaps(current) && !IsAllCaps(proposed);

    private static bool IsCasingImprovement(IEnumerable<string> current, IEnumerable<string> proposed)
    {
        string[] currentValues = current.ToArray();
        string[] proposedValues = proposed.ToArray();
        return currentValues.Length == proposedValues.Length
            && currentValues.Zip(proposedValues).All(value => IsCasingImprovement(value.First, value.Second));
    }

    private static DateTimeOffset Date(EditionPublicationDate value) => new(
        value.Year, value.Month ?? 1, value.Day ?? 1, 0, 0, 0, TimeSpan.Zero);

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
