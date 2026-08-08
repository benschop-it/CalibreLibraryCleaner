using System.Globalization;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Matching;

public sealed class EpubContentSignatureComparerTests
{
    [Fact]
    public void IdenticalDistributedLandmarksAreEquivalent()
    {
        EpubContentSignature first = Signature(1_000, "strict", "relaxed");
        EpubContentSignature second = Signature(1_050, "strict", "relaxed");

        CandidateContentComparison result = EpubContentSignatureComparer.Compare(first, second);

        result.Classification.Should().Be(ContentSimilarityClassification.EquivalentText);
        result.ForwardStrictMatches.Should().Be(12);
        result.ReverseStrictMatches.Should().Be(12);
        result.MatchedRegionCount.Should().Be(4);
    }

    [Fact]
    public void DiacriticTolerantLandmarksAreHighSimilarity()
    {
        EpubContentSignature first = Signature(1_000, "first", "shared");
        EpubContentSignature second = Signature(1_000, "second", "shared");

        CandidateContentComparison result = EpubContentSignatureComparer.Compare(first, second);

        result.Classification.Should().Be(ContentSimilarityClassification.HighSimilarity);
        result.ForwardStrictMatches.Should().Be(0);
        result.ForwardRelaxedMatches.Should().Be(12);
    }

    [Fact]
    public void SparseOrLanguageConflictingEvidenceIsDifferent()
    {
        EpubContentSignature first = Signature(1_000, "first", "first", "en");
        EpubContentSignature unrelated = Signature(1_000, "second", "second", "en");
        EpubContentSignature translation = Signature(1_000, "first", "first", "nl");

        EpubContentSignatureComparer.Compare(first, unrelated).Classification.Should()
            .Be(ContentSimilarityClassification.Different);
        EpubContentSignatureComparer.Compare(first, translation).Classification.Should()
            .Be(ContentSimilarityClassification.Different);
    }

    [Fact]
    public void InsufficientDistributionRemainsAmbiguous()
    {
        EpubContentSignature first = Signature(1_000, "shared", "shared", landmarkCount: 7);
        EpubContentSignature second = Signature(1_000, "shared", "shared", landmarkCount: 7);

        CandidateContentComparison result = EpubContentSignatureComparer.Compare(first, second);

        result.Classification.Should().Be(ContentSimilarityClassification.Ambiguous);
    }

    private static EpubContentSignature Signature(
        long tokenCount,
        string strictPrefix,
        string relaxedPrefix,
        string? language = null,
        int landmarkCount = 12) => new(
        new(2_048, new(new string('f', 64))),
        tokenCount,
        4,
        4,
        Enumerable.Range(0, landmarkCount).Select(index => new ContentLandmarkSignature(
            index,
            index * 70,
            64,
            32,
            Digest(strictPrefix, index),
            Digest(relaxedPrefix, index))),
        detectedLanguage: language,
        languageConfidencePermille: language is null ? null : 900);

    private static Sha256Digest Digest(string prefix, int index)
    {
        byte[] value = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(prefix + index.ToString(CultureInfo.InvariantCulture)));
        return new(Convert.ToHexString(value));
    }
}
