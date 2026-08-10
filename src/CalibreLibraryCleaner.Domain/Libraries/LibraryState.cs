namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record LibraryStateGenerationId
{
    public LibraryStateGenerationId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A library-state generation ID cannot be empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }
    public override string ToString() => Value.ToString("D");
}

public readonly record struct LibraryStateRevision
{
    public LibraryStateRevision(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public long Value { get; }
    public LibraryStateRevision Next() => new(checked(Value + 1));
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public enum LibraryStateStatus
{
    Authoritative,
    Uncertain,
}

public enum LibraryWorkflowPhase
{
    RequiresExactAnalysis,
    ExactReady,
    CandidatePreparationReady,
    CandidateAnalysisReady,
    Completed,
}

public sealed record LibraryWorkflowPolicyVersions
{
    public static LibraryWorkflowPolicyVersions Current { get; } = new(
        "staged-cleanup/1.0.0",
        "exact-analysis/1.0.0",
        "exact-cleanup/1.0.0",
        "candidate-analysis/1.0.0");

    public LibraryWorkflowPolicyVersions(
        string workflow,
        string exactAnalysis,
        string exactCleanup,
        string candidateAnalysis)
    {
        Workflow = Validate(workflow, nameof(workflow));
        ExactAnalysis = Validate(exactAnalysis, nameof(exactAnalysis));
        ExactCleanup = Validate(exactCleanup, nameof(exactCleanup));
        CandidateAnalysis = Validate(candidateAnalysis, nameof(candidateAnalysis));
    }

    public string Workflow { get; }
    public string ExactAnalysis { get; }
    public string ExactCleanup { get; }
    public string CandidateAnalysis { get; }

    private static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        if (normalized.Length > 128)
            throw new ArgumentException("A workflow policy version exceeds its bound.", parameterName);
        return normalized;
    }
}

public sealed record LibraryWorkflowCheckpoint
{
    public LibraryWorkflowCheckpoint(
        LibraryWorkflowPhase phase,
        LibraryStateGenerationId generationId,
        LibraryStateRevision revision,
        LibraryWorkflowPolicyVersions policyVersions,
        DateTimeOffset publishedAtUtc)
    {
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        Phase = phase;
        GenerationId = generationId ?? throw new ArgumentNullException(nameof(generationId));
        Revision = revision;
        PolicyVersions = policyVersions ?? throw new ArgumentNullException(nameof(policyVersions));
        PublishedAtUtc = publishedAtUtc.ToUniversalTime();
    }

    public LibraryWorkflowPhase Phase { get; }
    public LibraryStateGenerationId GenerationId { get; }
    public LibraryStateRevision Revision { get; }
    public LibraryWorkflowPolicyVersions PolicyVersions { get; }
    public DateTimeOffset PublishedAtUtc { get; }
}

public sealed record LibraryStateUncertainty
{
    public LibraryStateUncertainty(
        string code,
        string explanation,
        DateTimeOffset occurredAtUtc,
        string? operationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        if (code.Length > 128 || explanation.Length > 1024 || operationId?.Length > 256)
            throw new ArgumentException("Library-state uncertainty evidence exceeds its bound.");
        Code = code.Trim();
        Explanation = explanation.Trim();
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
        OperationId = string.IsNullOrWhiteSpace(operationId) ? null : operationId.Trim();
    }

    public string Code { get; }
    public string Explanation { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public string? OperationId { get; }
}

public sealed record LibraryStateMutationIntent
{
    public LibraryStateMutationIntent(
        string intentId,
        LibraryStateGenerationId generationId,
        LibraryStateRevision expectedRevision,
        int operationCount,
        DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        ArgumentNullException.ThrowIfNull(generationId);
        if (intentId.Length > 160 || operationCount < 1)
            throw new ArgumentException("The mutation intent is invalid.", nameof(operationCount));
        IntentId = intentId;
        GenerationId = generationId;
        ExpectedRevision = expectedRevision;
        OperationCount = operationCount;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    public string IntentId { get; }
    public LibraryStateGenerationId GenerationId { get; }
    public LibraryStateRevision ExpectedRevision { get; }
    public int OperationCount { get; }
    public DateTimeOffset CreatedAtUtc { get; }
}

public sealed record LibraryState
{
    public LibraryState(
        LibraryStateGenerationId generationId,
        LibraryStateRevision revision,
        LibraryStateStatus status,
        LibrarySnapshot snapshot,
        DateTimeOffset projectedAtUtc,
        LibraryStateUncertainty? uncertainty = null,
        LibraryWorkflowCheckpoint? workflowCheckpoint = null)
    {
        ArgumentNullException.ThrowIfNull(generationId);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == LibraryStateStatus.Uncertain) != (uncertainty is not null))
            throw new ArgumentException("Only uncertain library state can contain uncertainty evidence.", nameof(uncertainty));
        DateTimeOffset projected = projectedAtUtc.ToUniversalTime();
        if (projected < snapshot.ScannedAt.ToUniversalTime())
            throw new ArgumentException("Projected state cannot predate its baseline scan.", nameof(projectedAtUtc));
        LibraryWorkflowCheckpoint checkpoint = workflowCheckpoint ?? new(
            LibraryWorkflowPhase.RequiresExactAnalysis,
            generationId,
            revision,
            LibraryWorkflowPolicyVersions.Current,
            projected);
        if (checkpoint.GenerationId != generationId || checkpoint.Revision.Value > revision.Value)
            throw new ArgumentException(
                "The workflow checkpoint must bind to this generation at or before its current revision.",
                nameof(workflowCheckpoint));
        GenerationId = generationId;
        Revision = revision;
        Status = status;
        Snapshot = snapshot;
        ProjectedAtUtc = projected;
        Uncertainty = uncertainty;
        WorkflowCheckpoint = checkpoint;
    }

    public LibraryStateGenerationId GenerationId { get; }
    public LibraryStateRevision Revision { get; }
    public LibraryStateStatus Status { get; }
    public LibrarySnapshot Snapshot { get; }
    public DateTimeOffset ProjectedAtUtc { get; }
    public LibraryStateUncertainty? Uncertainty { get; }
    public LibraryWorkflowCheckpoint WorkflowCheckpoint { get; }
    public bool IsAuthoritative => Status == LibraryStateStatus.Authoritative;
    public bool IsWorkflowCheckpointCurrent => IsAuthoritative
        && WorkflowCheckpoint.Revision == Revision
        && WorkflowCheckpoint.PolicyVersions == LibraryWorkflowPolicyVersions.Current;

    public static LibraryState FromScan(LibrarySnapshot snapshot, LibraryStateGenerationId generationId) =>
        new(generationId, new(0), LibraryStateStatus.Authoritative, snapshot, snapshot.ScannedAt);

    public LibraryState AdvanceWorkflow(
        LibraryWorkflowPhase phase,
        DateTimeOffset publishedAtUtc)
    {
        if (!IsAuthoritative)
            throw new InvalidOperationException("Workflow phase cannot advance while library state is uncertain.");
        if (WorkflowCheckpoint.PolicyVersions != LibraryWorkflowPolicyVersions.Current)
            throw new InvalidOperationException("Workflow phase cannot advance from incompatible policy versions.");
        if (!IsAllowedTransition(WorkflowCheckpoint.Phase, phase))
            throw new InvalidOperationException("The requested workflow phase transition is invalid.");
        DateTimeOffset published = publishedAtUtc.ToUniversalTime();
        if (published < ProjectedAtUtc || published < WorkflowCheckpoint.PublishedAtUtc)
            throw new InvalidOperationException("The workflow checkpoint predates authoritative state.");
        return new(GenerationId, Revision, Status, Snapshot, ProjectedAtUtc, null,
            new(phase, GenerationId, Revision, LibraryWorkflowPolicyVersions.Current, published));
    }

    public LibraryState MarkUncertain(LibraryStateUncertainty uncertainty) =>
        new(GenerationId, Revision, LibraryStateStatus.Uncertain, Snapshot,
            uncertainty.OccurredAtUtc < ProjectedAtUtc ? ProjectedAtUtc : uncertainty.OccurredAtUtc,
            uncertainty, WorkflowCheckpoint);

    private static bool IsAllowedTransition(LibraryWorkflowPhase current, LibraryWorkflowPhase next) =>
        current switch
        {
            LibraryWorkflowPhase.RequiresExactAnalysis => next == LibraryWorkflowPhase.ExactReady,
            LibraryWorkflowPhase.ExactReady => next == LibraryWorkflowPhase.CandidatePreparationReady,
            LibraryWorkflowPhase.CandidatePreparationReady => next == LibraryWorkflowPhase.CandidateAnalysisReady,
            LibraryWorkflowPhase.CandidateAnalysisReady => next == LibraryWorkflowPhase.Completed,
            _ => false,
        };
}
