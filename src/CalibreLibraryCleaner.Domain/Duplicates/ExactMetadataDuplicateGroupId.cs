using System.Globalization;
using System.Text;

namespace CalibreLibraryCleaner.Domain.Duplicates;

public readonly record struct ExactMetadataDuplicateGroupId
{
    public ExactMetadataDuplicateGroupId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public static ExactMetadataDuplicateGroupId From(NormalizedBookIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        StringBuilder value = new("exact-metadata:v1|title|");
        AppendComponent(value, identity.Title.Value);
        value.Append("|authors|");
        value.Append(identity.Authors.Names.Count.ToString(CultureInfo.InvariantCulture));
        foreach (NormalizedAuthorName author in identity.Authors.Names)
        {
            value.Append('|');
            AppendComponent(value, author.Value);
        }

        return new(value.ToString());
    }

    public override string ToString() => Value;

    private static void AppendComponent(StringBuilder target, string component)
    {
        target.Append(Encoding.UTF8.GetByteCount(component).ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(component);
    }
}
