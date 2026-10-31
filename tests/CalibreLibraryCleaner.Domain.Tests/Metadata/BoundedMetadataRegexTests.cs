using System.Reflection;
using System.Text.RegularExpressions;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class BoundedMetadataRegexTests
{
    [Fact]
    public void BoundedMetadataMatchersUseLinearExecutionWithoutWallClockTimeouts()
    {
        foreach (MatcherCase item in Matchers())
        {
            Regex matcher = GetMatcher(item);
            matcher.Options.HasFlag(RegexOptions.NonBacktracking).Should().BeTrue(
                "{0} must have bounded linear execution rather than a short timer", item.Method);
            matcher.MatchTimeout.Should().Be(Regex.InfiniteMatchTimeout,
                "{0} must not interrupt bounded metadata processing under debugger or scheduling delays", item.Method);
        }
    }

    [Fact]
    public void LinearMatchersPreserveLegacyDecisionsAndSuffixSpans()
    {
        string[] inputs = Inputs().Distinct(StringComparer.Ordinal).ToArray();
        foreach (MatcherCase item in Matchers())
        {
            Regex matcher = GetMatcher(item);
            Regex reference = new(item.Pattern, item.Options, Regex.InfiniteMatchTimeout);
            for (int index = 0; index < inputs.Length; index++)
            {
                Match expected = reference.Match(inputs[index]);
                Match actual = matcher.Match(inputs[index]);
                actual.Success.Should().Be(expected.Success, "{0}, generated case {1}", item.Method, index);
                if (expected.Success && item.Method == "BareSuffix")
                {
                    actual.Index.Should().Be(expected.Index, "suffix removal must retain the same name");
                    actual.Length.Should().Be(expected.Length, "suffix removal must consume the same annotation");
                }
            }
        }
    }

    [Fact]
    public void MaximumLengthPseudonymEvidenceStillWithholdsTheEntireAuthorField()
    {
        const string suffix = " pseud. van Other Writer";
        string name = new('x', 512 - suffix.Length);
        string value = name + suffix;

        AuthorPseudonymAnnotationPolicy.ContainsMarker(value).Should().BeTrue();
        AuthorPseudonymAnnotationPolicy.TryRemove(value, out string? cleaned).Should().BeTrue();
        cleaned.Should().Be(name);
        EditionMetadataAuthorWritePolicy.TryPrepare([value], out IReadOnlyList<string> prepared)
            .Should().BeFalse();
        prepared.Should().BeEmpty();
        AuthorPseudonymAnnotationPolicy.TryRemove(value + "x", out _).Should().BeFalse();
    }

    private static Regex GetMatcher(MatcherCase item)
    {
        MethodInfo? method = item.Owner.GetMethod(item.Method, BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();
        return (Regex)method!.Invoke(null, null)!;
    }

    private static IEnumerable<MatcherCase> Matchers()
    {
        const RegexOptions invariant = RegexOptions.CultureInvariant;
        const RegexOptions ignoreCase = invariant | RegexOptions.IgnoreCase;
        yield return new(typeof(AuthorPseudonymAnnotationPolicy), "BracketMarker",
            @"\A(?:is\s+)?(?:pseud\.?|pseudoniem|pseudonym)(?=\s|\z)", ignoreCase);
        yield return new(typeof(AuthorPseudonymAnnotationPolicy), "BareSuffix",
            @"\s+(?:is\s+)?(?:pseud\.?|pseudoniem|pseudonym)(?=\s|\z)(?:\s+van\s+[^()\[\]]+)?\s*\z",
            ignoreCase);
        yield return new(typeof(AuthorPseudonymAnnotationPolicy), "AnyMarker",
            @"(?:\A|[\s(\[])(?:is\s+)?(?:pseud\.?|pseudoniem|pseudonym)(?=\s|[\])]|\z)", ignoreCase);
        yield return new(typeof(EditionMetadataAuthorWritePolicy), "MisspelledPseudonymMarker",
            @"(?:\A|[\s(\[])(?:is\s+)?psued\.?(?=\s|[\])]|\z)", ignoreCase);
        yield return new(typeof(RecordIdentityTriagePolicy), "YearPrefix",
            @"^\(?[12][0-9]{3}\)?\s+\S", invariant);
        yield return new(typeof(RecordIdentityTriagePolicy), "NumberPrefix",
            @"^\d{1,4}[.)-]?\s+\S", invariant);
        yield return new(typeof(RecordIdentityTriagePolicy), "VolumeMarker",
            "(?:^|\\s)(?:vol(?:ume)?|deel|tome|band|boek|book|part|\u0442\u043e\u043c|\u5dfb)\\.?\\s*[0-9]{1,4}(?:\\s|$)",
            ignoreCase);
    }

    private static IEnumerable<string> Inputs()
    {
        string[] seeds =
        [
            "", "Writer", "Writer pseud", "Writer pseud. van Other", "Writer is pseud.",
            "Writer (pseud. van Other)", "Writer [pseud. ]", "Writer [pseud.] Other",
            "Writer pseud. van (Other)", "Writer pseud. van Other]", "pseudonymous",
            "1900 Book", "(1997) Book", "12. Book", "12345 Book", "\u0661\u0662. Book",
            "Writer vol. 12", "volume 1", "Book 12345", "\u0442\u043e\u043c 2", "\u5dfb 3",
            "\ud800 pseud.", "i\u017f pseud.", "p\u017feud.", "IS PSEUDONYM",
        ];
        foreach (string seed in seeds)
        {
            yield return seed;
            yield return seed.PadLeft(512);
            yield return seed.PadRight(512);
            yield return new string('x', 512 - seed.Length) + seed;
        }
        foreach (char whitespace in new[] { ' ', '\t', '\u0085', '\u00a0', '\u2028', '\u202f' })
            foreach (string marker in new[] { "pseud", "pseud.", "pseudoniem", "pseudonym", "PSEUD.", "psued.", "pseudonymous" })
                foreach (string boundary in new[] { "", " ", ")", "]", ".", "x", "(", "[" })
                {
                    yield return $"{marker}{boundary}";
                    yield return $"Writer{whitespace}is{whitespace}{marker}{boundary}van Other";
                    yield return $"Writer{whitespace}{marker}{whitespace}van{whitespace}Other{boundary}";
                }
    }

    private sealed record MatcherCase(Type Owner, string Method, string Pattern, RegexOptions Options);
}
