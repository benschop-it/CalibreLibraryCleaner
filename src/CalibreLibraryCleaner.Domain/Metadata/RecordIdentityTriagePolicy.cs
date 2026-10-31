using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace CalibreLibraryCleaner.Domain.Metadata;

public sealed record RecordIdentityTriageAssessment(
    IReadOnlyList<string> ReasonCodes,
    bool MaterialIdentityProblem);

public static partial class RecordIdentityTriagePolicy
{
    public const string PolicyVersion = "record-identity-triage/1.0.0";
    public const int MaximumFieldCharacters = 512;

    public static RecordIdentityTriageAssessment Assess(string? title, IEnumerable<string?> authors)
    {
        ArgumentNullException.ThrowIfNull(authors);
        string?[] names = authors.Take(101).ToArray();
        HashSet<string> reasons = new(StringComparer.Ordinal);
        bool material = string.IsNullOrWhiteSpace(title) || names.Length == 0;
        if (string.IsNullOrWhiteSpace(title)) reasons.Add("TITLE.MISSING");
        Inspect(title, "TITLE");
        if (names.Length > 100) reasons.Add("IDENTITY.FIELD_LIMIT");
        foreach (string? author in names.Take(100))
        {
            if (string.IsNullOrWhiteSpace(author))
            {
                reasons.Add(AuthorSuspicionReasonCodes.Missing);
                material = true;
                continue;
            }
            Inspect(author, "AUTHOR");
            if (author.Length <= MaximumFieldCharacters)
            {
                AuthorSuspicionAssessment suspicion = AuthorSuspicionPolicy.Assess(title, [author]);
                reasons.UnionWith(suspicion.ReasonCodes);
                material |= suspicion.Status == AuthorResolutionStatus.Unresolved;
                if (HasVolumeMarker(author))
                    reasons.Add("AUTHOR.VOLUME_MARKER");
            }
        }
        return new(new ReadOnlyCollection<string>(reasons.Order(StringComparer.Ordinal).ToArray()), material);

        void Inspect(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (value.Length > MaximumFieldCharacters)
            {
                reasons.Add($"{field}.FIELD_LIMIT");
                return;
            }
            string text = value.Trim();
            if (HasUnbalancedDelimiters(text))
            {
                reasons.Add($"{field}.UNBALANCED_DELIMITER");
                material = true;
            }
            if (YearPrefix().IsMatch(text)) reasons.Add($"{field}.YEAR_PREFIX");
            else if (NumberPrefix().IsMatch(text)) reasons.Add($"{field}.NUMBER_PREFIX");
            if (text.Contains("||", StringComparison.Ordinal)
                || text.Contains(";;", StringComparison.Ordinal)
                || text[0] is '|' or ';' || text[^1] is '|' or ';')
            {
                reasons.Add($"{field}.DAMAGED_SEPARATOR");
                material = true;
            }
        }
    }

    internal static bool HasVolumeMarker(string value) =>
        value.Length <= MaximumFieldCharacters && VolumeMarker().IsMatch(value);

    internal static bool HasUnbalancedDelimiters(string value)
    {
        Stack<char> delimiters = new();
        foreach (char character in value)
        {
            if (character is '(' or '[' or '{') delimiters.Push(character);
            else if (character is ')' or ']' or '}'
                && (!delimiters.TryPop(out char open)
                    || (open, character) is not (('(', ')') or ('[', ']') or ('{', '}'))))
                return true;
        }
        return delimiters.Count > 0;
    }

    [GeneratedRegex(@"^\(?[12][0-9]{3}\)?\s+\S", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex YearPrefix();

    [GeneratedRegex(@"^\d{1,4}[.)-]?\s+\S", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex NumberPrefix();

    [GeneratedRegex(@"(?:^|\s)(?:vol(?:ume)?|deel|tome|band|boek|book|part|том|巻)\.?\s*[0-9]{1,4}(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex VolumeMarker();
}
