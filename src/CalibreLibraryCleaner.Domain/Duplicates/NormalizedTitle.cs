namespace CalibreLibraryCleaner.Domain.Duplicates;

public sealed record NormalizedTitle
{
    internal NormalizedTitle(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
