namespace CalibreLibraryCleaner.Domain.Duplicates;

public sealed record ExactMetadataDuplicateMatchReason
{
    private ExactMetadataDuplicateMatchReason(string code, string category, string description)
    {
        Code = code;
        Category = category;
        Description = description;
    }

    public static ExactMetadataDuplicateMatchReason TitleAndAuthorSetEqual { get; } = new(
        "EXACT_NORMALIZED_TITLE_AUTHOR_SET",
        "Exact normalized metadata candidate",
        "Normalized title and order-independent normalized author set are exactly equal.");

    public string Code { get; }

    public string Category { get; }

    public string Description { get; }
}
