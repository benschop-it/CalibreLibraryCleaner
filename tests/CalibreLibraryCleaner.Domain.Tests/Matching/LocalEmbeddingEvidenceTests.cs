using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Matching;

public sealed class LocalEmbeddingEvidenceTests
{
    private static readonly LocalEmbeddingModelIdentity Model = new(
        "ollama",
        "0.32.13",
        "embeddinggemma:latest",
        "sha256-0800cbac9c2064dde519420e75e512a83cb360de3ad5df176185dc69652fc515",
        128);

    [Fact]
    public void InputIdentityIsStableAndContainsNoRawMetadata()
    {
        CalibreBook book = Book(1, "Clockwork Harbor", "Clara Maker");
        BookMatchingProfile profile = BookMatchingProfileFactory.Create([book]).Single();

        LocalEmbeddingInput first = LocalEmbeddingInput.Create(book, profile);
        LocalEmbeddingInput second = LocalEmbeddingInput.Create(book, profile);

        first.InputIdentity.Should().Be(second.InputIdentity).And.HaveLength(64);
        first.InputIdentity.Should().NotContain("CLOCKWORK");
        first.Text.Should().Contain("title:").And.Contain("authors:").And.Contain("language:");
    }

    [Fact]
    public void CosineComparisonIsSymmetricAndDeterministicallyRounded()
    {
        LocalEmbeddingVector first = Vector(1, [1f, 0f]);
        LocalEmbeddingVector second = Vector(2, [0.8f, 0.6f]);

        LocalEmbeddingComparison forward = LocalEmbeddingComparisonPolicy.Compare(first, second);
        LocalEmbeddingComparison reverse = LocalEmbeddingComparisonPolicy.Compare(second, first);

        forward.Should().Be(reverse);
        forward.SimilarityPermille.Should().Be(800);
        forward.PairId.Should().Be(new BookCandidatePairId(new(1), new(2)));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void VectorRejectsNonFiniteValues(float invalid)
    {
        float[] values = Enumerable.Repeat(0.1f, Model.Dimensions).ToArray();
        values[0] = invalid;

        Action action = () => _ = new LocalEmbeddingVector(
            new(1), new string('a', 64), Model, values);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ComparisonRejectsDifferentModels()
    {
        LocalEmbeddingVector first = Vector(1, [1f, 0f]);
        LocalEmbeddingModelIdentity changed = new(
            Model.RuntimeId, Model.RuntimeVersion, Model.ModelId, "different-digest", Model.Dimensions);
        LocalEmbeddingVector second = Vector(2, [1f, 0f], changed);

        Action action = () => LocalEmbeddingComparisonPolicy.Compare(first, second);

        action.Should().Throw<ArgumentException>();
    }

    private static LocalEmbeddingVector Vector(
        long id,
        float[] prefix,
        LocalEmbeddingModelIdentity? model = null)
    {
        LocalEmbeddingModelIdentity identity = model ?? Model;
        float[] values = new float[identity.Dimensions];
        Array.Copy(prefix, values, prefix.Length);
        return new(new(id), new string((char)('a' + id), 64), identity, values);
    }

    private static CalibreBook Book(long id, string title, string author) => new(
        new(id),
        title,
        author,
        [new(null, author, author)],
        [],
        [],
        $"Book {id}",
        new(languages: ["eng"]));
}
