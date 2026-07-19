using System.Text;
using System.Text.Json.Nodes;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recommendations;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Plans;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Plans;

public sealed class CleanupPlanJsonStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DeterministicVersionedJsonRoundTripsCompleteApprovedPlan()
    {
        CleanupPlan valid = Generate();
        CleanupPlan approved = CleanupPlanLifecyclePolicy.Approve(valid, valid.Validation, Now.AddMinutes(1));

        byte[] first = CleanupPlanJsonSerializer.Serialize(approved);
        CleanupPlanStoreReadResult read = CleanupPlanJsonSerializer.Deserialize(first);
        byte[] second = CleanupPlanJsonSerializer.Serialize(read.Plan!);

        read.IsSuccess.Should().BeTrue();
        second.Should().Equal(first);
        first.Take(3).Should().NotEqual([0xEF, 0xBB, 0xBF]);
        string json = Encoding.UTF8.GetString(first);
        json.Should().StartWith("{\n  \"schemaVersion\": \"cleanup-plan/1.0\",\n  \"cleanupPlanPolicyVersion\": \"cleanup-plan-policy/1.0.0\"");
        json.Should().EndWith("\n").And.NotContain("\r");
        json.Should().Contain("\"expectedLibraryState\"").And.Contain("\"backupRequirements\"").And.Contain("\"approval\"");
        json.ToUpperInvariant().Should().NotContain("C:\\SYNTHETIC");
        read.Plan!.Approval!.ContentDigest.Should().Be(approved.ContentDigest);
    }

    [Fact]
    public void UntrustedJsonWithChangedBodyOrFutureSchemaIsRejected()
    {
        string json = Encoding.UTF8.GetString(CleanupPlanJsonSerializer.Serialize(Generate()));
        byte[] changedBody = Encoding.UTF8.GetBytes(json.Replace("\"title\": \"Shared\"", "\"title\": \"Changed\"", StringComparison.Ordinal));
        byte[] future = Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\": \"cleanup-plan/1.0\"", "\"schemaVersion\": \"cleanup-plan/9.0\"", StringComparison.Ordinal));

        CleanupPlanStoreReadResult digestFailure = CleanupPlanJsonSerializer.Deserialize(changedBody);
        CleanupPlanStoreReadResult schemaFailure = CleanupPlanJsonSerializer.Deserialize(future);

        digestFailure.IsSuccess.Should().BeFalse();
        digestFailure.Error!.Code.Should().Be("CLEANUP_PLAN_JSON_INVALID");
        schemaFailure.IsSuccess.Should().BeFalse();
        schemaFailure.Error!.Code.Should().Be("UNSUPPORTED_CLEANUP_PLAN_SCHEMA");
    }

    [Fact]
    public void DuplicateUnknownUnsafeAndExcessivelyDeepJsonAreControlledFailures()
    {
        string json = Encoding.UTF8.GetString(CleanupPlanJsonSerializer.Serialize(Generate()));
        byte[] duplicate = Encoding.UTF8.GetBytes(json.Replace(
            "{\n  \"schemaVersion\"",
            "{\n  \"schemaVersion\": \"cleanup-plan/1.0\",\n  \"schemaVersion\"",
            StringComparison.Ordinal));
        byte[] unknown = Encoding.UTF8.GetBytes(json.Replace(
            "{\n  \"schemaVersion\"",
            "{\n  \"unexpected\": true,\n  \"schemaVersion\"",
            StringComparison.Ordinal));
        byte[] unsafePath = Encoding.UTF8.GetBytes(json.Replace(
            "Author/Shared (1)/book.pdf",
            "../book.pdf",
            StringComparison.Ordinal));
        string nested = string.Concat(Enumerable.Repeat("{\"nested\":", CleanupPlanJsonSerializer.MaximumDepth + 1))
            + "0"
            + new string('}', CleanupPlanJsonSerializer.MaximumDepth + 1);
        byte[] tooDeep = Encoding.UTF8.GetBytes(nested);

        foreach (byte[] input in new[] { duplicate, unknown, unsafePath, tooDeep })
        {
            CleanupPlanStoreReadResult result = CleanupPlanJsonSerializer.Deserialize(input);
            result.IsSuccess.Should().BeFalse();
            result.Error.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task StoreWritesAndReadsOnlyExternalArtifactWithoutLibrarySidecars()
    {
        using TemporaryDirectory root = new();
        string library = Path.Combine(root.Path, "library");
        string external = Path.Combine(root.Path, "external");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(external);
        using ServiceProvider provider = TestServices.CreateProvider();
        ICleanupPlanStore store = provider.GetRequiredService<ICleanupPlanStore>();
        CleanupPlan plan = Generate();
        string destination = Path.Combine(external, "plan.cleanup-plan.json");
        string inside = Path.Combine(library, "plan.cleanup-plan.json");

        CleanupPlanStoreWriteResult write = await store.WriteAsync(plan, library, destination, CancellationToken.None);
        CleanupPlanStoreReadResult read = await store.ReadAsync(library, destination, CancellationToken.None);
        CleanupPlanStoreWriteResult blocked = await store.WriteAsync(plan, library, inside, CancellationToken.None);

        write.IsSuccess.Should().BeTrue();
        read.IsSuccess.Should().BeTrue();
        read.Plan!.ContentDigest.Should().Be(plan.ContentDigest);
        blocked.IsSuccess.Should().BeFalse();
        blocked.Error!.Code.Should().Be("CLEANUP_PLAN_INSIDE_LIBRARY");
        Directory.EnumerateFileSystemEntries(library, "*", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.EnumerateFiles(external).Should().ContainSingle(path => path == destination);
    }

    [Fact]
    public async Task PreCanceledStoreOperationsPublishNothingAndPropagateCancellation()
    {
        using TemporaryDirectory root = new();
        string library = Path.Combine(root.Path, "library");
        string external = Path.Combine(root.Path, "external");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(external);
        using ServiceProvider provider = TestServices.CreateProvider();
        ICleanupPlanStore store = provider.GetRequiredService<ICleanupPlanStore>();
        using CancellationTokenSource source = new();
        source.Cancel();
        string destination = Path.Combine(external, "plan.cleanup-plan.json");

        Func<Task> action = () => store.WriteAsync(Generate(), library, destination, source.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(destination).Should().BeFalse();
        Directory.EnumerateFiles(external).Should().BeEmpty();
        Directory.EnumerateFiles(library).Should().BeEmpty();
    }

    [Fact]
    public async Task ImportFromInsideLibraryAndWrongExtensionAreRejectedWithoutReading()
    {
        using TemporaryDirectory root = new();
        string library = Path.Combine(root.Path, "library");
        string external = Path.Combine(root.Path, "external");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(external);
        byte[] bytes = CleanupPlanJsonSerializer.Serialize(Generate());
        string inside = Path.Combine(library, "hostile.cleanup-plan.json");
        string wrongExtension = Path.Combine(external, "plan.json");
        await File.WriteAllBytesAsync(inside, bytes);
        await File.WriteAllBytesAsync(wrongExtension, bytes);
        using ServiceProvider provider = TestServices.CreateProvider();
        ICleanupPlanStore store = provider.GetRequiredService<ICleanupPlanStore>();

        CleanupPlanStoreReadResult insideResult = await store.ReadAsync(library, inside, CancellationToken.None);
        CleanupPlanStoreReadResult extensionResult = await store.ReadAsync(library, wrongExtension, CancellationToken.None);

        insideResult.IsSuccess.Should().BeFalse();
        insideResult.Error!.Code.Should().Be("CLEANUP_PLAN_INSIDE_LIBRARY");
        extensionResult.IsSuccess.Should().BeFalse();
        extensionResult.Error!.Code.Should().Be("CLEANUP_PLAN_EXTENSION_REQUIRED");
    }

    [Fact]
    public void RemovingRequiredSafetyNoticeOrChangingImmutableProvenanceIsRejected()
    {
        byte[] bytes = CleanupPlanJsonSerializer.Serialize(Generate());
        JsonObject missingNotice = JsonNode.Parse(bytes)!.AsObject();
        JsonArray issues = missingNotice["issues"]!.AsArray();
        JsonNode notice = issues.Single(value => value!["code"]!.GetValue<string>() == "PLAN.NON_EXECUTABLE")!;
        issues.Remove(notice);
        JsonObject changedProvenance = JsonNode.Parse(bytes)!.AsObject();
        changedProvenance["provenance"]!["userOverride"]!["reviewedAtUtc"] = Now.AddDays(1);

        CleanupPlanStoreReadResult missing = CleanupPlanJsonSerializer.Deserialize(Encoding.UTF8.GetBytes(missingNotice.ToJsonString()));
        CleanupPlanStoreReadResult changed = CleanupPlanJsonSerializer.Deserialize(Encoding.UTF8.GetBytes(changedProvenance.ToJsonString()));

        missing.IsSuccess.Should().BeFalse();
        changed.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void NumericEnumMissingScalarAndManufacturedApprovalAreRejected()
    {
        byte[] bytes = CleanupPlanJsonSerializer.Serialize(Generate());
        JsonObject numericEnum = JsonNode.Parse(bytes)!.AsObject();
        numericEnum["formatRetentions"]![0]!["mode"] = 999;
        JsonObject missingScalar = JsonNode.Parse(bytes)!.AsObject();
        missingScalar["expectedLibraryState"]!["records"]![0]!.AsObject().Remove("hasCover");
        JsonObject manufacturedApproval = JsonNode.Parse(bytes)!.AsObject();
        manufacturedApproval["approval"] = new JsonObject
        {
            ["approvedAtUtc"] = Now.AddMinutes(1),
            ["method"] = "ExplicitLocalUser",
            ["approvedRevision"] = 1,
            ["contentDigest"] = manufacturedApproval["contentDigest"]!.GetValue<string>(),
        };

        foreach (JsonObject input in new[] { numericEnum, missingScalar, manufacturedApproval })
            CleanupPlanJsonSerializer.Deserialize(Encoding.UTF8.GetBytes(input.ToJsonString())).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void RehashedInstructionStateThatDisagreesWithExpectedLibraryStateIsRejected()
    {
        CleanupPlan plan = Generate();
        FormatRetentionInstruction retained = plan.Definition.FormatRetentions.Single();
        ExpectedFormatState source = retained.SourceState;
        ExpectedFormatState alteredSource = new(source.RecordId, source.Format, source.StoredFileName, source.RelativePath,
            source.Status, new(source.Fingerprint.SizeInBytes, new(new string('b', 64))), source.Observation);
        FormatRetentionInstruction alteredRetention = new(retained.Id, retained.Format, retained.TargetRecordId,
            alteredSource, retained.Mode, retained.ReviewedSelectionReference);
        CleanupPlanDefinition inconsistent = new(plan.Definition.ExpectedLibraryState, plan.Definition.TargetRecordId,
            plan.Definition.InvolvedRecordIds, plan.Definition.MetadataRetention, [alteredRetention],
            plan.Definition.FormatRemovals, plan.Definition.RecordRemovals, plan.Definition.BackupRequirements,
            plan.Definition.Provenance);
        string changedDigest = CleanupPlanContentDigestPolicy.Compute(inconsistent).Value;
        JsonObject json = JsonNode.Parse(CleanupPlanJsonSerializer.Serialize(plan))!.AsObject();
        json["formatRetentions"]![0]!["sourceState"]!["sha256"] = new string('b', 64);
        json["contentDigest"] = changedDigest;
        json["inputIdentity"]!["definitionDigest"] = changedDigest;

        CleanupPlanStoreReadResult result = CleanupPlanJsonSerializer.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString()));

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void ExcessiveNestedProvenanceCollectionIsRejectedBeforeDomainReconstruction()
    {
        JsonObject json = JsonNode.Parse(CleanupPlanJsonSerializer.Serialize(Generate()))!.AsObject();
        JsonArray candidates = json["provenance"]!["generatedFormatSelections"]![0]!["candidateRecordIds"]!.AsArray();
        for (int index = candidates.Count; index <= CleanupPlanJsonSerializer.MaximumNestedItems; index++)
            candidates.Add(1);

        CleanupPlanStoreReadResult result = CleanupPlanJsonSerializer.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString()));

        result.IsSuccess.Should().BeFalse();
    }

    private static CleanupPlan Generate()
    {
        FormatFileFingerprint fingerprint = new(5, new(new string('a', 64)));
        CalibreBook[] books =
        [
            new(new(1), "Shared", "Author", [new(new(1), "Author", "Author")], [],
                [new("PDF", "book", "Author/Shared (1)/book.pdf", FormatFileStatus.Present, fingerprint,
                    new FormatFileObservation(5, Now.AddDays(-1), Now.AddHours(-1), 32))],
                "Author/Shared (1)", new(languages: ["eng"])),
            new(new(2), "Shared", "Author", [new(new(2), "Author", "Author")], [], [],
                "Author/Shared (2)", new(languages: ["eng"])),
        ];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        LibraryIdentity identity = new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\synthetic\\library");
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(
            identity, group, books, [], [], [], CancellationToken.None);
        ReviewedConsolidationRecommendation reviewed = ApplyRecommendationOverrideUseCase.Execute(generated, new(
            generated.ModelVersion, generated.InputVersion, RecommendationReviewStatus.Accepted, Now)).Reviewed!;
        LibrarySnapshot snapshot = new(identity, Now, books, [], [], [group], [], [generated]);
        return new GenerateCleanupPlanUseCase(new FixedIds(), new FixedClock()).Execute(snapshot, reviewed).Plan!;
    }

    private sealed class FixedIds : ICleanupPlanIdGenerator
    {
        public CleanupPlanId Create() => new(Guid.Parse("11111111-2222-3333-4444-555555555555"));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset GetUtcNow() => Now;
    }
}
