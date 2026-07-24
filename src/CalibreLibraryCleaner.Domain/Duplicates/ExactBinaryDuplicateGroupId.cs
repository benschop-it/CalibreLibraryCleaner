using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Duplicates;

public readonly record struct ExactBinaryDuplicateGroupId
{
    public ExactBinaryDuplicateGroupId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public static ExactBinaryDuplicateGroupId From(FormatFileFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return new(FormattableString.Invariant(
            $"sha256:{fingerprint.Sha256.Value}:{fingerprint.SizeInBytes}"));
    }

    public override string ToString() => Value;
}
