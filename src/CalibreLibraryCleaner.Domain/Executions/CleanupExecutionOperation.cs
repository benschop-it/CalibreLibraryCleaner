using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Domain.Executions;

public sealed record ExecutionOperationId
{
    public ExecutionOperationId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 256) throw new ArgumentException("An operation ID is too long.", nameof(value));
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record CleanupExecutionOperation
{
    public CleanupExecutionOperation(
        ExecutionOperationId id,
        ExecutionOperationPhase phase,
        ExecutionOperationKind kind,
        CalibreBookId targetRecordId,
        CalibreBookId? sourceRecordId,
        string? format,
        ExpectedFormatState? selectedSourceState,
        bool replacesExistingTargetFormat,
        IEnumerable<ExecutionOperationId> dependencyIds)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(dependencyIds);
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ExecutionOperationId[] dependencies = dependencyIds.Distinct().OrderBy(value => value.Value, StringComparer.Ordinal).ToArray();
        string? canonicalFormat = string.IsNullOrWhiteSpace(format) ? null : format.ToUpperInvariant();
        bool isFormatOperation = kind is ExecutionOperationKind.VerifyTargetFormatPreserved or ExecutionOperationKind.AddOrReplaceFormat;
        if (isFormatOperation != (canonicalFormat is not null && selectedSourceState is not null))
            throw new ArgumentException("Format operations require a selected format state.");
        if (selectedSourceState is not null && (selectedSourceState.Format != canonicalFormat || selectedSourceState.RecordId != sourceRecordId))
            throw new ArgumentException("The operation source does not match the selected format state.", nameof(selectedSourceState));
        if (kind == ExecutionOperationKind.AddOrReplaceFormat && sourceRecordId == targetRecordId)
            throw new ArgumentException("A constructive add operation requires an off-target source.", nameof(sourceRecordId));
        if (kind == ExecutionOperationKind.RemoveRedundantRecord && (phase != ExecutionOperationPhase.Destructive || sourceRecordId is null || sourceRecordId == targetRecordId))
            throw new ArgumentException("Record removal must be a destructive off-target operation.");
        if (kind != ExecutionOperationKind.AddOrReplaceFormat && replacesExistingTargetFormat)
            throw new ArgumentException("Only an add operation can replace a target format.", nameof(replacesExistingTargetFormat));
        Id = id;
        Phase = phase;
        Kind = kind;
        TargetRecordId = targetRecordId;
        SourceRecordId = sourceRecordId;
        Format = canonicalFormat;
        SelectedSourceState = selectedSourceState;
        ReplacesExistingTargetFormat = replacesExistingTargetFormat;
        DependencyIds = Array.AsReadOnly(dependencies);
    }

    public ExecutionOperationId Id { get; }
    public ExecutionOperationPhase Phase { get; }
    public ExecutionOperationKind Kind { get; }
    public CalibreBookId TargetRecordId { get; }
    public CalibreBookId? SourceRecordId { get; }
    public string? Format { get; }
    public ExpectedFormatState? SelectedSourceState { get; }
    public bool ReplacesExistingTargetFormat { get; }
    public IReadOnlyList<ExecutionOperationId> DependencyIds { get; }
}

public sealed record CleanupExecutionOperationGraph
{
    public CleanupExecutionOperationGraph(IEnumerable<CleanupExecutionOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        CleanupExecutionOperation[] ordered = operations.ToArray();
        if (ordered.Length == 0 || ordered.Select(value => value.Id).Distinct().Count() != ordered.Length)
            throw new ArgumentException("An execution graph requires unique operations.", nameof(operations));
        HashSet<ExecutionOperationId> seen = [];
        foreach (CleanupExecutionOperation operation in ordered)
        {
            if (operation.DependencyIds.Any(dependency => !seen.Contains(dependency)))
                throw new ArgumentException("Operations must be supplied in dependency-safe topological order.", nameof(operations));
            seen.Add(operation.Id);
        }

        if (ordered.SkipWhile(value => value.Phase != ExecutionOperationPhase.Destructive)
            .Any(value => value.Phase != ExecutionOperationPhase.Destructive))
            throw new ArgumentException("All destructive operations must occur last.", nameof(operations));
        Operations = Array.AsReadOnly(ordered);
        Digest = ComputeDigest(ordered);
    }

    public IReadOnlyList<CleanupExecutionOperation> Operations { get; }
    public Sha256Digest Digest { get; }
    public IReadOnlyList<CleanupExecutionOperation> ConstructiveOperations => Operations.Where(value => value.Phase == ExecutionOperationPhase.Constructive).ToArray();
    public IReadOnlyList<CleanupExecutionOperation> DestructiveOperations => Operations.Where(value => value.Phase == ExecutionOperationPhase.Destructive).ToArray();

    private static Sha256Digest ComputeDigest(IEnumerable<CleanupExecutionOperation> operations)
    {
        StringBuilder canonical = new();
        Add(canonical, "cleanup-execution-operation-graph/1.0");
        foreach (CleanupExecutionOperation operation in operations)
        {
            Add(canonical, operation.Id.Value);
            Add(canonical, operation.Phase.ToString());
            Add(canonical, operation.Kind.ToString());
            Add(canonical, operation.TargetRecordId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(canonical, operation.SourceRecordId?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            Add(canonical, operation.Format ?? string.Empty);
            Add(canonical, operation.ReplacesExistingTargetFormat ? "1" : "0");
            ExpectedFormatState? source = operation.SelectedSourceState;
            Add(canonical, source?.RelativePath ?? string.Empty);
            Add(canonical, source?.Fingerprint.SizeInBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            Add(canonical, source?.Fingerprint.Sha256.Value ?? string.Empty);
            foreach (ExecutionOperationId dependency in operation.DependencyIds) Add(canonical, dependency.Value);
            Add(canonical, "|");
        }

        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant());
    }

    private static void Add(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append(';');
}

public sealed record CleanupExecutionCapabilityResult(
    CleanupExecutionOperationGraph? Graph,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsSupported => Graph is not null && Issues.All(value => value.Severity != ExecutionIssueSeverity.BlockingError);
}

public static class CleanupExecutionCapabilityPolicy
{
    public static CleanupExecutionCapabilityResult Evaluate(CleanupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<ExecutionIssue> issues = [];
        if (plan.SchemaVersion != CleanupPlanSchemaVersion.V1 || plan.PolicyVersion != CleanupPlanPolicyVersion.V1)
            Block(issues, "EXECUTION.UNSUPPORTED_PLAN_VERSION", "Only cleanup-plan/1.0 with cleanup-plan-policy/1.0.0 is executable.");
        if (plan.State != CleanupPlanState.Approved || plan.Approval is null || plan.Approval.ContentDigest != plan.ContentDigest)
            Block(issues, "EXECUTION.APPROVAL_INVALID", "The cleanup plan is not currently approved and bound to its canonical body.");
        if (!plan.Validation.IsValid)
            Block(issues, "EXECUTION.PLAN_BLOCKED", "The cleanup plan contains blocking validation issues.");
        if (CleanupPlanContentDigestPolicy.Compute(plan.Definition) != plan.ContentDigest || plan.InputIdentity.DefinitionDigest != plan.ContentDigest)
            Block(issues, "EXECUTION.PLAN_TAMPERED", "The cleanup plan body or identity digest changed.");
        foreach (CleanupPlanIssue issue in CleanupPlanSafetyPolicy.Validate(plan.Definition)
                     .Where(value => value.Severity == CleanupPlanIssueSeverity.BlockingError))
            Block(issues, "EXECUTION.PLAN_UNSAFE", issue.Explanation, issue.RecordId, issue.Format);

        CleanupPlanDefinition definition = plan.Definition;
        if (definition.ExpectedLibraryState.Records.Any(value => value.HasCover))
            Block(issues, "EXECUTION.COVER_CONTENT_VERIFICATION_UNSUPPORTED",
                "V1 execution is disabled when an involved record has a cover because byte-exact cover preservation is not yet modeled.");
        if (definition.MetadataRetention.SourceRecordId != definition.TargetRecordId
            || definition.MetadataRetention.ExpectedMetadataStateRecordId != definition.TargetRecordId)
            Block(issues, "EXECUTION.METADATA_TRANSFER_UNSUPPORTED", "Cross-record metadata transfer is unsupported.");

        ExpectedRecordState target = definition.ExpectedLibraryState.Records.Single(value => value.RecordId == definition.TargetRecordId);
        Dictionary<string, ExpectedFormatState> targetFormats = target.Formats.ToDictionary(value => value.Format, StringComparer.Ordinal);
        Dictionary<string, FormatRetentionInstruction> retentions = definition.FormatRetentions.ToDictionary(value => value.Id, StringComparer.Ordinal);
        List<CleanupExecutionOperation> operations =
        [
            new(new("verify:metadata"), ExecutionOperationPhase.Precondition,
                ExecutionOperationKind.VerifyMetadataPreserved, definition.TargetRecordId,
                definition.TargetRecordId, null, null, false, []),
        ];

        foreach (FormatRetentionInstruction retention in definition.FormatRetentions.OrderBy(value => value.Format, StringComparer.Ordinal))
        {
            ExpectedRecordState? sourceRecord = definition.ExpectedLibraryState.Records.SingleOrDefault(value => value.RecordId == retention.SourceState.RecordId);
            if (sourceRecord is null || !sourceRecord.Formats.Contains(retention.SourceState))
            {
                Block(issues, "EXECUTION.RETAINED_SOURCE_INVALID", "A retained format source is not part of the exact expected state.", retention.SourceState.RecordId, retention.Format);
                continue;
            }

            targetFormats.TryGetValue(retention.Format, out ExpectedFormatState? existingTarget);
            if (retention.Mode == FormatRetentionMode.RetainInTarget)
            {
                if (retention.SourceState.RecordId != definition.TargetRecordId || existingTarget != retention.SourceState)
                {
                    Block(issues, "EXECUTION.RETAIN_IN_TARGET_INVALID", "An in-target retention does not match the expected target format.", retention.SourceState.RecordId, retention.Format);
                    continue;
                }

                operations.Add(new(new($"verify:format:{retention.Format}"), ExecutionOperationPhase.Precondition,
                    ExecutionOperationKind.VerifyTargetFormatPreserved, definition.TargetRecordId,
                    retention.SourceState.RecordId, retention.Format, retention.SourceState, false,
                    [new("verify:metadata")]));
                continue;
            }

            if (retention.Mode != FormatRetentionMode.RetainFromOtherRecord
                || retention.SourceState.RecordId == definition.TargetRecordId
                || !definition.RecordRemovals.Any(value => value.RecordId == retention.SourceState.RecordId))
            {
                Block(issues, "EXECUTION.RETAIN_FROM_SOURCE_INVALID", "An off-target retention has no supported source-record dependency.", retention.SourceState.RecordId, retention.Format);
                continue;
            }

            if (existingTarget is not null)
            {
                FormatRemovalInstruction? descriptiveReplacement = definition.FormatRemovals.SingleOrDefault(value =>
                    value.RecordId == definition.TargetRecordId && value.Format == retention.Format
                    && value.RetainedFormatInstructionId == retention.Id && value.ExpectedState == existingTarget);
                FormatRemovalReason expectedReason = existingTarget.Fingerprint == retention.SourceState.Fingerprint
                    ? FormatRemovalReason.ByteIdenticalAlternative
                    : FormatRemovalReason.ReviewedNonIdenticalReplacement;
                if (descriptiveReplacement?.Reason != expectedReason)
                {
                    Block(issues, "EXECUTION.REPLACEMENT_AMBIGUOUS", "A target format replacement is not explicitly and consistently reviewed.", definition.TargetRecordId, retention.Format);
                    continue;
                }
            }

            operations.Add(new(new($"construct:format:{retention.Format}"), ExecutionOperationPhase.Constructive,
                ExecutionOperationKind.AddOrReplaceFormat, definition.TargetRecordId,
                retention.SourceState.RecordId, retention.Format, retention.SourceState,
                existingTarget is not null, [new("verify:metadata")]));
        }

        foreach (FormatRemovalInstruction removal in definition.FormatRemovals)
        {
            if (!retentions.TryGetValue(removal.RetainedFormatInstructionId, out FormatRetentionInstruction? retention)
                || retention.Format != removal.Format)
            {
                Block(issues, "EXECUTION.REMOVAL_AMBIGUOUS", "A format-removal description has no supported retained destination.", removal.RecordId, removal.Format);
                continue;
            }

            if (removal.RecordId == definition.TargetRecordId)
            {
                bool supportedTargetReplacement = retention.Mode == FormatRetentionMode.RetainFromOtherRecord
                    && targetFormats.TryGetValue(removal.Format, out ExpectedFormatState? targetFormat)
                    && removal.ExpectedState == targetFormat
                    && removal.Reason is FormatRemovalReason.ByteIdenticalAlternative or FormatRemovalReason.ReviewedNonIdenticalReplacement;
                if (!supportedTargetReplacement)
                    Block(issues, "EXECUTION.TARGET_FORMAT_REMOVAL_UNSUPPORTED", "Standalone removal of a target format is unsupported.", removal.RecordId, removal.Format);
            }
            else if (!definition.RecordRemovals.Any(value => value.RecordId == removal.RecordId))
            {
                Block(issues, "EXECUTION.STANDALONE_FORMAT_REMOVAL_UNSUPPORTED", "A non-target format is not subsumed by a supported record removal.", removal.RecordId, removal.Format);
            }
        }

        List<ExecutionOperationId> destructiveDependencies = operations.Select(value => value.Id).ToList();
        foreach (RecordRemovalInstruction removal in definition.RecordRemovals.OrderBy(value => value.RecordId.Value))
        {
            CleanupExecutionOperation operation = new(new($"destroy:record:{removal.RecordId.Value}"), ExecutionOperationPhase.Destructive,
                ExecutionOperationKind.RemoveRedundantRecord, definition.TargetRecordId,
                removal.RecordId, null, null, false, destructiveDependencies);
            operations.Add(operation);
            destructiveDependencies.Add(operation.Id);
        }

        if (issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError))
            return new(null, Array.AsReadOnly(issues.ToArray()));

        try
        {
            return new(new CleanupExecutionOperationGraph(operations), Array.AsReadOnly(issues.ToArray()));
        }
        catch (ArgumentException exception)
        {
            Block(issues, "EXECUTION.OPERATION_GRAPH_INVALID", exception.Message);
            return new(null, Array.AsReadOnly(issues.ToArray()));
        }
    }

    private static void Block(List<ExecutionIssue> issues, string code, string explanation, CalibreBookId? recordId = null, string? format = null) =>
        issues.Add(new(code, ExecutionIssueSeverity.BlockingError, explanation, recordId, format));
}
