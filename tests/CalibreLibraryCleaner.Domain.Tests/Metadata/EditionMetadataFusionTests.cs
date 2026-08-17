using System.Reflection;
using System.Text.Json;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class EditionMetadataFusionTests
{
    private static readonly JsonSerializerOptions FixtureJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly DateTimeOffset RetrievedAt = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LabeledFixtureProducesDeterministicConfidenceSelectionAndMissingFieldFill()
    {
        Fixture fixture = LoadFixture();

        foreach (Scenario scenario in fixture.Scenarios)
        {
            EditionMetadataProposal[] proposals = scenario.Proposals.Select(Proposal).Reverse().ToArray();

            FusedEditionMetadataProposal first = EditionMetadataFusionPolicy.Fuse(proposals);
            FusedEditionMetadataProposal second = EditionMetadataFusionPolicy.Fuse(proposals.Reverse());

            first.Should().BeEquivalentTo(second, scenario.Id);
            first.Confidence.ToString().Should().Be(scenario.ExpectedConfidence, scenario.Id);
            first.PolicyVersion.Should().Be(FusedEditionMetadataProposal.CurrentPolicyVersion);
            first.IsSelectedByDefault.Should().Be(scenario.ExpectedSelected, scenario.Id);
            first.Candidate?.Publisher.Should().Be(scenario.ExpectedPublisher, scenario.Id);
            first.ProviderProposals.Select(value => value.Provider.Id).Should().BeInAscendingOrder();
        }
    }

    [Fact]
    public void MissingFieldIsNeverFilledAcrossConflictingIsbns()
    {
        EditionMetadataProposal primary = Proposal(new(
            "open-library", 1, "ol", "Work", ["Author"], "9780306406157",
            null, 2001, 10_020, ["METADATA.EDITION.ISBN_EXACT"]));
        EditionMetadataProposal conflicting = Proposal(new(
            "google-books", 1, "gb", "Work", ["Author"], "9780140328721",
            "Conflicting Publisher", 2001, 10_008, ["METADATA.EDITION.ISBN_EXACT"]));

        FusedEditionMetadataProposal result = EditionMetadataFusionPolicy.Fuse([primary, conflicting]);

        result.Confidence.Should().Be(FusedEditionMetadataConfidence.Low);
        result.Candidate!.Publisher.Should().BeNull();
        result.ReasonCodes.Should().Contain("METADATA.FUSION.PROVIDER_DISAGREEMENT");
        result.ReasonCodes.Should().NotContain("METADATA.FUSION.MISSING_FIELDS_FILLED");
    }

    [Fact]
    public void IdenticalSameProviderEditionIdMayFillMissingField()
    {
        EditionMetadataProposal primary = Proposal(new(
            "open-library", 1, "same-edition", "Work", ["Author"], null,
            null, 2001, 4_020,
            ["METADATA.EDITION.TITLE_EXACT", "METADATA.EDITION.AUTHOR_EXACT"]));
        EditionMetadataProposal secondary = Proposal(new(
            "open-library", 2, "same-edition", "Work", ["Author"], null,
            "Provider Publisher", 2001, 4_008,
            ["METADATA.EDITION.TITLE_EXACT", "METADATA.EDITION.AUTHOR_EXACT"]));

        FusedEditionMetadataProposal result = EditionMetadataFusionPolicy.Fuse([secondary, primary]);

        result.Confidence.Should().Be(FusedEditionMetadataConfidence.Medium);
        result.Candidate!.Publisher.Should().Be("Provider Publisher");
        result.ReasonCodes.Should().Contain("METADATA.FUSION.MISSING_FIELDS_FILLED");
        result.FieldSources.Should().Contain(value =>
            value.Field == EditionMetadataField.Publisher
            && value.Provider.Id == "open-library"
            && value.EditionId == "same-edition");
    }

    [Fact]
    public void SharedIsbnFieldCompletionRetainsProviderVersionRetrievalAndFieldSource()
    {
        EditionMetadataProposal openLibrary = Proposal(new(
            "open-library", 1, "ol-edition", "Work", ["Author"], "9780306406157",
            null, 2005, 10_020, ["METADATA.EDITION.ISBN_EXACT"]));
        EditionMetadataProposal googleBooks = Proposal(new(
            "google-books", 1, "gb-edition", "Work", ["Author"], "9780306406157",
            "Google Publisher", 2005, 10_008, ["METADATA.EDITION.ISBN_EXACT"]));

        FusedEditionMetadataProposal result = EditionMetadataFusionPolicy.Fuse(
            [googleBooks, openLibrary]);

        result.Confidence.Should().Be(FusedEditionMetadataConfidence.High);
        result.ReasonCodes.Should().Contain([
            "METADATA.FUSION.PROVIDER_AGREEMENT",
            "METADATA.FUSION.SHARED_ISBN",
            "METADATA.FUSION.MISSING_FIELDS_FILLED",
        ]);
        result.ProviderProposals.Should().OnlyContain(value => value.RetrievedAtUtc == RetrievedAt);
        result.ProviderProposals.Select(value => value.Provider.Version).Should().BeEquivalentTo([
            "google-books/1.0.0",
            "open-library/1.0.0",
        ]);
        result.FieldSources.Should().ContainSingle(value =>
            value.Field == EditionMetadataField.Publisher
            && value.Provider.Id == "google-books"
            && value.Provider.Version == "google-books/1.0.0"
            && value.EditionId == "gb-edition");
    }

    private static EditionMetadataProposal Proposal(ProposalDocument value)
    {
        EditionMetadataProviderIdentity provider = new(value.Provider, value.Provider + "/1.0.0");
        EditionMetadataIdentifier[] identifiers = value.Isbn is null
            ? []
            : [new("isbn", value.Isbn)];
        return new(
            new(value.BookId),
            provider,
            EditionMetadataQueryFields.Title | EditionMetadataQueryFields.Authors,
            RetrievedAt,
            EditionMetadataProposalStatus.Proposed,
            new(
                value.Provider + "-work",
                value.EditionId,
                value.Title,
                value.Authors,
                identifiers,
                value.Publisher,
                value.Year is null ? null : new(value.Year.Value),
                ["en"]),
            value.Score,
            value.Reasons);
    }

    private static Fixture LoadFixture()
    {
        Assembly assembly = typeof(EditionMetadataFusionTests).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(value =>
            value.EndsWith("edition-metadata-fusion.v1.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Edition metadata fusion fixture is unavailable.");
        return JsonSerializer.Deserialize<Fixture>(stream, FixtureJsonOptions)
            ?? throw new InvalidDataException("Edition metadata fusion fixture is empty.");
    }

    private sealed record Fixture(string SchemaVersion, Scenario[] Scenarios);
    private sealed record Scenario(
        string Id,
        ProposalDocument[] Proposals,
        string ExpectedConfidence,
        bool ExpectedSelected,
        string? ExpectedPublisher);
    private sealed record ProposalDocument(
        string Provider,
        long BookId,
        string EditionId,
        string Title,
        string[] Authors,
        string? Isbn,
        string? Publisher,
        int? Year,
        int Score,
        string[] Reasons);
}
