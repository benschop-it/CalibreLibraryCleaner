using System.Reflection;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Metadata;

public sealed class BuildMetadataReviewSubjectsUseCaseTests
{
    private static readonly JsonSerializerOptions FixtureJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly DateTimeOffset RetrievedAt = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly EditionMetadataProviderIdentity OpenLibrary = new(
        "open-library", "open-library-edition-search/1.0.0");
    private static readonly EditionMetadataProviderIdentity GoogleBooks = new(
        "google-books", "google-books-volume-search/1.0.0");

    [Fact]
    public void CreatesEveryRetainedRecordAsSingletonInDeterministicOrder()
    {
        SubjectScenario scenario = LoadFixture().SubjectScenarios.Single();
        Dictionary<long, CalibreBook> books = scenario.BookIds.ToDictionary(
            value => value,
            value => Book(value, scenario.GroupMemberIds.Contains(value)
                ? "Grouped Work"
                : "Singleton " + value));
        CalibreBook singletonFirst = books[1];
        CalibreBook groupFirst = books[2];
        CalibreBook groupSecond = books[3];
        CalibreBook singletonLast = books[4];
        ExactMetadataDuplicateGroup exact = ExactMetadataDuplicateDetector.Detect(
            [groupFirst, groupSecond]).Single();
        UnifiedCandidateGroup group = UnifiedCandidateMergePolicy.Merge(
            [exact], [], [groupFirst, groupSecond]).Single();
        LibrarySnapshot snapshot = new(
            new("library", 27, "C:\\library"),
            RetrievedAt,
            scenario.BookIds.Select(value => books[value]),
            [],
            unifiedCandidateGroups: [group]);
        EditionMetadataProvidersBatchResult providerResults = new(
        [
            Batch(GoogleBooks,
                Proposal(scenario.ProposalBookIds[0], GoogleBooks, "gb-singleton", "9780140328721"),
                Proposal(scenario.ProposalBookIds[1], GoogleBooks, "gb-group", "9780306406157")),
            Batch(OpenLibrary,
                Proposal(scenario.ProposalBookIds[0], OpenLibrary, "ol-singleton", "9780140328721"),
                Proposal(scenario.ProposalBookIds[2], OpenLibrary, "ol-group", "9780306406157")),
        ]);
        IReadOnlyList<MetadataReviewSubject> subjects = BuildMetadataReviewSubjectsUseCase.Execute(
            snapshot,
            providerResults,
            []);

        subjects.Should().HaveCount(4);
        subjects.Should().OnlyContain(value => value.UnifiedGroupId == null && value.Members.Count == 1);
        subjects.Select(value => value.TargetBookId.Value).Should().Equal(1, 2, 3, 4);
        subjects[0].Id.Value.Should().Be("metadata-review/book/1");
        subjects[0].Proposal.Confidence.Should().Be(FusedEditionMetadataConfidence.High);
        subjects[3].Id.Value.Should().Be("metadata-review/book/4");
        subjects[3].Proposal.Confidence.Should().Be(FusedEditionMetadataConfidence.Unavailable);
    }

    [Fact]
    public void SkippedAnalysisGroupMembersRemainSeparateMetadataSubjects()
    {
        CalibreBook first = Book(1, "Grouped Work");
        CalibreBook second = Book(2, "Grouped Work");
        ExactMetadataDuplicateGroup exact = ExactMetadataDuplicateDetector.Detect([first, second]).Single();
        UnifiedCandidateGroup group = UnifiedCandidateMergePolicy.Merge([exact], [], [first, second]).Single();
        LibrarySnapshot snapshot = new(
            new("library", 27, "C:\\library"),
            RetrievedAt,
            [first, second],
            [],
            unifiedCandidateGroups: [group]);
        EditionMetadataProvidersBatchResult providerResults = new(
        [
            Batch(OpenLibrary, Proposal(1, OpenLibrary, "ol", "9780306406157")),
            Batch(GoogleBooks, Proposal(2, GoogleBooks, "gb", "9780306406157")),
        ]);
        IReadOnlyList<MetadataReviewSubject> subjects = BuildMetadataReviewSubjectsUseCase.Execute(
            snapshot, providerResults, []);

        subjects.Should().HaveCount(2);
        subjects.Should().OnlyContain(value => value.UnifiedGroupId == null && value.Members.Count == 1);
        subjects.Select(value => value.TargetBookId).Should().Equal(first.Id, second.Id);
    }

    [Fact]
    public void ProviderFailureProducesUnavailableSubjectWithoutChangingSnapshot()
    {
        CalibreBook singleton = Book(1, "Current Metadata");
        LibrarySnapshot snapshot = new(
            new("library", 27, "C:\\library"), RetrievedAt, [singleton], []);
        EditionMetadataProposal unavailable = new(
            singleton.Id,
            GoogleBooks,
            EditionMetadataQueryFields.Title | EditionMetadataQueryFields.Authors,
            RetrievedAt,
            EditionMetadataProposalStatus.Unavailable,
            problemCode: "GOOGLE_BOOKS.UNAVAILABLE");

        MetadataReviewSubject subject = BuildMetadataReviewSubjectsUseCase.Execute(
            snapshot,
            new([Batch(GoogleBooks, unavailable)]),
            []).Single();

        subject.Proposal.Confidence.Should().Be(FusedEditionMetadataConfidence.Unavailable);
        subject.Proposal.IsSelectedByDefault.Should().BeFalse();
        snapshot.Books.Single().Title.Should().Be("Current Metadata");
    }

    private static EditionMetadataProposalBatchResult Batch(
        EditionMetadataProviderIdentity provider,
        params EditionMetadataProposal[] proposals) => new(
        provider,
        proposals.ToDictionary(value => value.BookId),
        proposals.Length,
        0,
        0,
        proposals.Length,
        proposals.Count(value => value.Status == EditionMetadataProposalStatus.Proposed),
        proposals.Count(value => value.Status == EditionMetadataProposalStatus.Unavailable),
        false,
        true);

    private static EditionMetadataProposal Proposal(
        long bookId,
        EditionMetadataProviderIdentity provider,
        string editionId,
        string isbn) => new(
        new(bookId),
        provider,
        EditionMetadataQueryFields.Identifier,
        RetrievedAt,
        EditionMetadataProposalStatus.Proposed,
        new(
            provider.Id + "-work",
            editionId,
            "Canonical Work",
            ["Canonical Author"],
            [new("isbn", isbn)],
            "Canonical Publisher",
            new(2005),
            ["en"]),
        10_008,
        ["METADATA.EDITION.ISBN_EXACT"]);

    private static CalibreBook Book(long id, string title) => new(
        new(id),
        title,
        "Author, Canonical",
        [new(new(id), "Canonical Author", "Author, Canonical")],
        [],
        [],
        $"Author/Book ({id})");

    private static Fixture LoadFixture()
    {
        Assembly assembly = typeof(BuildMetadataReviewSubjectsUseCaseTests).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(value =>
            value.EndsWith("edition-metadata-fusion.v1.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Edition metadata fusion fixture is unavailable.");
        return JsonSerializer.Deserialize<Fixture>(stream, FixtureJsonOptions)
            ?? throw new InvalidDataException("Edition metadata fusion fixture is empty.");
    }

    private sealed record Fixture(SubjectScenario[] SubjectScenarios);
    private sealed record SubjectScenario(
        string Id,
        long[] BookIds,
        long[] GroupMemberIds,
        long SelectedKeeperId,
        long[] ProposalBookIds,
        string[] ExpectedSubjectKinds,
        long[] ExpectedTargetIds,
        string[] ExpectedConfidences);
}
