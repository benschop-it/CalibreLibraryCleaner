using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Duplicates;

namespace CalibreLibraryCleaner.Domain.Metadata;

public static class RecordMetadataProblemPolicy
{
    public const string TitleContainsAuthor = "TITLE.CONTAINS_AUTHOR_CREDIT";
    public const string AuthorBracketedNumber = "AUTHOR.BRACKETED_NUMBER";
    public const string TitleAuthorSwapped = "IDENTITY.TITLE_AUTHOR_SWAPPED";
    private static readonly string[] CreditSeparators = [" - ", " \u2013 ", " \u2014 ", " | ", " / ", ": "];

    public static IReadOnlyList<string> Assess(string title, IEnumerable<string> authors)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(authors);
        string[] names = authors.Take(101).ToArray();
        HashSet<string> reasons = new(
            RecordIdentityTriagePolicy.Assess(title, names).ReasonCodes, StringComparer.Ordinal);
        if (names.Length == 0) reasons.Add(AuthorSuspicionReasonCodes.Missing);
        if (title.Length <= RecordIdentityTriagePolicy.MaximumFieldCharacters)
        {
            string[] segments = title.Split(CreditSeparators,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length > 1)
            {
                foreach (string name in names.Take(100).Where(value =>
                             value.Length <= RecordIdentityTriagePolicy.MaximumFieldCharacters))
                {
                    if (MetadataTextNormalizer.TryNormalizeTitle(name, out NormalizedTitle? credit)
                        && credit!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2
                        && new[] { segments[0], segments[^1] }.Any(segment =>
                            MetadataTextNormalizer.TryNormalizeTitle(segment, out NormalizedTitle? label)
                            && label == credit))
                    {
                        reasons.Add(TitleContainsAuthor);
                        break;
                    }
                }
            }
        }
        foreach (string name in names.Take(100).Where(value =>
                     value.Length <= RecordIdentityTriagePolicy.MaximumFieldCharacters))
        {
            for (int index = 0; index < name.Length; index++)
            {
                if (name[index] is not ('[' or '(')) continue;
                int close = name.IndexOf(name[index] == '[' ? ']' : ')', index + 1);
                if (close < 0) continue;
                ReadOnlySpan<char> content = name.AsSpan(index + 1, close - index - 1).Trim();
                if (content.Length is >= 1 and <= 8 && content.ToString().All(char.IsDigit))
                {
                    reasons.Add(AuthorBracketedNumber);
                    break;
                }
            }
        }
        return new ReadOnlyCollection<string>(reasons.Order(StringComparer.Ordinal).ToArray());
    }
}
