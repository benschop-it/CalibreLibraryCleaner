using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Infrastructure.Metadata;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Metadata;

public sealed class FileMetadataReviewDecisionStoreTests
{
    [Fact]
    public async Task RoundTripStoresOnlyBoundedDecisionKeysAndApply()
    {
        using TemporaryDirectory directory = new();
        FileMetadataReviewDecisionStore store = new(new(directory.Path));
        string libraryRoot = Path.Combine(directory.Path, "Private Library Path");
        MetadataReviewDecision decision = Decision("subject/1", apply: false);

        await store.WriteAsync(libraryRoot, [decision], CancellationToken.None);
        IReadOnlyList<MetadataReviewDecision> loaded = await store.ReadAsync(
            libraryRoot, CancellationToken.None);

        loaded.Should().Equal(decision);
        string path = Directory.GetFiles(directory.Path, "*.json").Single();
        Path.GetFileNameWithoutExtension(path).Should().MatchRegex("^[0-9a-f]{64}$");
        string json = await File.ReadAllTextAsync(path);
        json.Should().Contain("subject/1");
        json.Should().Contain("edition-metadata-fusion/1.0.0");
        json.Should().Contain("edition-1");
        json.Should().NotContain(libraryRoot);
        json.Should().NotContain("Book Title");
        json.Should().NotContain("Private Author");
        json.Should().NotContain("AIza");
        json.Should().NotContain("ProviderPayload");
    }

    [Fact]
    public async Task ReplacementDropsStaleDecisionsAndCorruptionFailsClosed()
    {
        using TemporaryDirectory directory = new();
        FileMetadataReviewDecisionStore store = new(new(directory.Path));
        const string libraryRoot = "C:\\Library";
        await store.WriteAsync(libraryRoot, [Decision("stale", false)], CancellationToken.None);

        await store.WriteAsync(libraryRoot, [Decision("current", true)], CancellationToken.None);
        IReadOnlyList<MetadataReviewDecision> loaded = await store.ReadAsync(
            libraryRoot, CancellationToken.None);

        loaded.Should().ContainSingle(value => value.Key.SubjectId.Value == "current");
        string path = Directory.GetFiles(directory.Path, "*.json").Single();
        await File.WriteAllTextAsync(path, "{");
        Func<Task> act = async () => await store.ReadAsync(libraryRoot, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    private static MetadataReviewDecision Decision(string subjectId, bool apply) => new(
        new(
            new(subjectId),
            FusedEditionMetadataProposal.CurrentPolicyVersion,
            new(new("open-library", "provider/1.0.0"), "edition-1"),
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
            new(7)),
        apply);
}
