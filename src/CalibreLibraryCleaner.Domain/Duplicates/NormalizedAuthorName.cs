namespace CalibreLibraryCleaner.Domain.Duplicates;

public sealed record NormalizedAuthorName
{
    internal NormalizedAuthorName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
