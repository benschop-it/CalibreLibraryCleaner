using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Libraries;

public sealed record FormatHashCacheKey
{
    public const string PolicyVersion = "format-sha256/1.0.0";

    public FormatHashCacheKey(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A format hash cache key must contain 64 hexadecimal characters.", nameof(value));
        Value = value.ToLowerInvariant();
    }

    public string Value { get; }
}

public sealed record FormatHashCacheEntry(
    FormatHashCacheKey Key,
    string PolicyVersion,
    FormatFileFingerprint Fingerprint,
    FormatFileObservation Observation,
    DateTimeOffset VerifiedAtUtc)
{
    public bool Matches(FormatHashCacheKey key, FormatFileObservation observation) =>
        Key == key
        && string.Equals(PolicyVersion, FormatHashCacheKey.PolicyVersion, StringComparison.Ordinal)
        && Observation == observation
        && Fingerprint.SizeInBytes == observation.Length;
}
