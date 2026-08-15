using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Libraries;

public sealed class FormatHashCacheContractsTests
{
    [Fact]
    public void KeyNormalizesAndValidatesOpaqueDigest()
    {
        FormatHashCacheKey key = new(new string('A', 64));

        key.Value.Should().Be(new string('a', 64));
        Action invalid = () => _ = new FormatHashCacheKey("not-a-key");
        invalid.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EntryMatchesRequiresExactObservationAndFingerprintSize()
    {
        string root = Path.GetFullPath("library");
        FormatHashCacheKey key = new(new string('b', 64));
        DateTimeOffset verified = new(2026, 2, 20, 12, 0, 0, TimeSpan.Zero);
        FormatFileObservation observation = new(10, verified.AddHours(-2), verified.AddHours(-1), 32);
        FormatHashCacheEntry entry = new(
            key,
            FormatHashCacheKey.PolicyVersion,
            new(10, new(new string('a', 64))),
            observation,
            verified);

        entry.Matches(key, observation).Should().BeTrue();
        entry.Matches(key, observation with { }).Should().BeTrue();
        entry.Matches(key, new(10, observation.CreationTimeUtc, observation.LastWriteTimeUtc.AddTicks(1), 32))
            .Should().BeFalse();
    }
}
