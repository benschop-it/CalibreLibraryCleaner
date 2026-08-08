using System.Diagnostics;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace CalibreLibraryCleaner.Infrastructure.Tests.LibrarySnapshots;

public sealed class MatchingScaleBaselineTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TwentyThousandRecordExactGroupingAndSnapshotSerializationBaseline()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CALIBRE_RUN_MATCHING_BENCHMARK"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine("Set CALIBRE_RUN_MATCHING_BENCHMARK=1 to run the 20,000-record baseline.");
            return;
        }

        const int recordCount = 20_000;
        CalibreBook[] books = Enumerable.Range(1, recordCount)
            .Select(index => CreateBook(index))
            .OrderBy(value => (value.Id.Value * 7919) % recordCount)
            .ToArray();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch groupingElapsed = Stopwatch.StartNew();

        IReadOnlyList<ExactMetadataDuplicateGroup> groups =
            ExactMetadataDuplicateDetector.Detect(books);

        groupingElapsed.Stop();
        long groupingAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long matchingAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long matchingRetainedBefore = GC.GetTotalMemory(forceFullCollection: true);
        Stopwatch profileElapsed = Stopwatch.StartNew();
        IReadOnlyList<BookMatchingProfile> profiles = BookMatchingProfileFactory.Create(books);
        profileElapsed.Stop();
        long profileAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - matchingAllocatedBefore;
        long candidateAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch candidateElapsed = Stopwatch.StartNew();
        BookCandidateGenerationResult candidates = BookCandidateGenerator.Generate(profiles);
        candidateElapsed.Stop();
        long candidateAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - candidateAllocatedBefore;
        long matchingAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - matchingAllocatedBefore;
        long matchingRetainedBytes = GC.GetTotalMemory(forceFullCollection: true) - matchingRetainedBefore;
        LibrarySnapshot snapshot = new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\synthetic\\matching-benchmark"),
            DateTimeOffset.UnixEpoch,
            books,
            [],
            exactMetadataDuplicateGroups: groups);
        Stopwatch serializationElapsed = Stopwatch.StartNew();
        byte[] serialized = LibrarySnapshotJsonSerializer.Serialize(snapshot);
        serializationElapsed.Stop();

        groups.Should().HaveCount(recordCount / 2);
        groups.SelectMany(value => value.Members).Should().HaveCount(recordCount);
        profiles.Should().HaveCount(recordCount);
        candidates.LimitExceeded.Should().BeFalse();
        candidates.Pairs.Should().HaveCount(recordCount / 2);
        snapshot.MatchingRunSummary.Status.Should().Be(Domain.Matching.MatchingEvidenceStatus.Unavailable);
        output.WriteLine("records={0}", recordCount);
        output.WriteLine("exactGroups={0}", groups.Count);
        output.WriteLine("groupingMilliseconds={0}", groupingElapsed.ElapsedMilliseconds);
        output.WriteLine("groupingAllocatedBytes={0}", groupingAllocatedBytes);
        output.WriteLine("profileMilliseconds={0}", profileElapsed.ElapsedMilliseconds);
        output.WriteLine("profileAllocatedBytes={0}", profileAllocatedBytes);
        output.WriteLine("candidateMilliseconds={0}", candidateElapsed.ElapsedMilliseconds);
        output.WriteLine("candidateAllocatedBytes={0}", candidateAllocatedBytes);
        output.WriteLine("matchingAllocatedBytes={0}", matchingAllocatedBytes);
        output.WriteLine("matchingRetainedBytes={0}", matchingRetainedBytes);
        output.WriteLine("candidatePairs={0}", candidates.Pairs.Count);
        output.WriteLine("proposedDirectedPairs={0}", candidates.ProposedDirectedPairCount);
        output.WriteLine("recordsCapped={0}", candidates.RecordsCapped);
        output.WriteLine("maximumObservedBucketSize={0}", candidates.MaximumObservedBucketSize);
        output.WriteLine("serializationMilliseconds={0}", serializationElapsed.ElapsedMilliseconds);
        output.WriteLine("snapshotBytes={0}", serialized.LongLength);
    }

    private static CalibreBook CreateBook(int index)
    {
        int work = (index - 1) / 2;
        string title = $"Synthetic Work {work:D5}";
        string[] authors = index % 2 == 0
            ? ["First Author", "Second Author"]
            : ["Second Author", "First Author"];
        return new(
            new(index),
            title,
            "Author, First",
            authors.Select((author, authorIndex) => new BookAuthor(
                new((index * 10L) + authorIndex + 1), author, author)),
            [],
            [],
            $"Synthetic/{index}",
            new(languages: [index % 4 < 2 ? "eng" : "nld"]));
    }
}
