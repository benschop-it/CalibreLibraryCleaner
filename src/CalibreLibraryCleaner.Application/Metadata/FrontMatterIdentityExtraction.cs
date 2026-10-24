using CalibreLibraryCleaner.Domain.Metadata;

namespace CalibreLibraryCleaner.Application.Metadata;

public sealed record FrontMatterIdentityCandidate
{
    public FrontMatterIdentityCandidate(
        string title,
        IEnumerable<string> authors,
        int unitNumber,
        int titleLineNumber,
        int authorLineNumber,
        bool explicitCredit,
        string? subtitle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(authors);
        string[] names = authors.Take(5).ToArray();
        if (title.Length > 512 || title.Any(char.IsControl)
            || names.Length is < 1 or > 4
            || names.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128
                || value.Any(char.IsControl))
            || unitNumber <= 0 || titleLineNumber <= 0 || authorLineNumber <= 0
            || titleLineNumber > AuthorBylinePolicy.MaximumLines
            || authorLineNumber > AuthorBylinePolicy.MaximumLines)
            throw new ArgumentException("Front-matter identity candidate is invalid.");
        if (subtitle is not null && (subtitle.Length > 512 || subtitle.Any(char.IsControl)))
            throw new ArgumentException("Front-matter subtitle exceeds its bounds.", nameof(subtitle));
        Title = title.Trim();
        Authors = Array.AsReadOnly(names);
        UnitNumber = unitNumber;
        TitleLineNumber = titleLineNumber;
        AuthorLineNumber = authorLineNumber;
        ExplicitCredit = explicitCredit;
        Subtitle = subtitle;
    }

    public string Title { get; }
    public IReadOnlyList<string> Authors { get; }
    public int UnitNumber { get; }
    public int TitleLineNumber { get; }
    public int AuthorLineNumber { get; }
    public bool ExplicitCredit { get; }
    public string? Subtitle { get; }
    public string CompleteTitle => string.IsNullOrWhiteSpace(Subtitle) ? Title : $"{Title}: {Subtitle}";
    public string BibliographicTitle => CompleteTitle.Length <= 512 ? CompleteTitle : Title;
}

public static class FrontMatterIdentityExtraction
{
    public const string PolicyVersion = "front-matter-identity/1.2.0";

    public static IReadOnlyList<FrontMatterIdentityCandidate> Extract(
        IEnumerable<string> lines,
        int unitNumber,
        IReadOnlyCollection<int>? headingLineNumbers = null,
        bool declaredTitlePage = false)
    {
        ArgumentNullException.ThrowIfNull(lines);
        string[] values = lines.Take(AuthorBylinePolicy.MaximumLines)
            .Select(value => string.Join(' ', value.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries))).ToArray();
        if (values.Any(value => value.Equals("References", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Bibliography", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Also by ", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Other books by ", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Also available", StringComparison.OrdinalIgnoreCase)))
            return [];
        AuthorBylineCandidate[] credits = AuthorBylinePolicy.Extract(values).ToArray();
        List<FrontMatterIdentityCandidate> candidates = [];
        foreach (AuthorBylineCandidate author in credits.Where(value => value.Role == AuthorCreditRole.Author))
        {
            int titleIndex = author.LineNumber - 2;
            if (titleIndex < 0 || !Usable(values[titleIndex])) continue;
            string? subtitle = null;
            if (titleIndex > 0 && (values[titleIndex].StartsWith("Subtitle: ", StringComparison.OrdinalIgnoreCase)
                    && values[titleIndex - 1].StartsWith("Title: ", StringComparison.OrdinalIgnoreCase)
                || headingLineNumbers?.Contains(titleIndex + 1) == true
                    && headingLineNumbers.Contains(titleIndex)))
            {
                subtitle = values[titleIndex].StartsWith("Subtitle: ", StringComparison.OrdinalIgnoreCase)
                    ? values[titleIndex][10..].Trim() : values[titleIndex];
                titleIndex--;
            }
            string title = values[titleIndex];
            bool labelled = title.StartsWith("Title: ", StringComparison.OrdinalIgnoreCase);
            if (labelled) title = title[7..].Trim();
            if (!Usable(title) || credits.Any(value => value.LineNumber == titleIndex + 1)) continue;
            candidates.Add(new(
                title, author.Names, unitNumber, titleIndex + 1, author.LineNumber,
                labelled || declaredTitlePage && headingLineNumbers?.Contains(titleIndex + 1) == true, subtitle));
        }
        if (candidates.Count == 0 && credits.Length == 0 && values.Length == 2
            && Usable(values[0]) && Usable(values[1]) && values[1].Length <= 128)
            candidates.Add(new(values[0], [values[1]], unitNumber, 1, 2, explicitCredit: false));
        return Array.AsReadOnly(candidates.Take(16).ToArray());
    }

    private static bool Usable(string value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 512 && !value.Any(char.IsControl)
        && value.Any(char.IsLetter)
        && !value.Contains("copyright", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("translated", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("edited", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("ISBN", StringComparison.OrdinalIgnoreCase);
}
