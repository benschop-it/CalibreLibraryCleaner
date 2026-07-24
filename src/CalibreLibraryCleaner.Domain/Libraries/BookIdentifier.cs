namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record BookIdentifier
{
    public BookIdentifier(string type, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Type = type;
        Value = value;
    }

    public string Type { get; }

    public string Value { get; }
}
