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
        IEnumerable<string> operationIds,
        DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        ArgumentNullException.ThrowIfNull(generationId);
        ArgumentNullException.ThrowIfNull(operationIds);
        string[] operations = operationIds.Select(value =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (value.Length > 256) throw new ArgumentException("A mutation operation ID is too long.", nameof(operationIds));
            return value;
        }).ToArray();
        if (intentId.Length > 160 || operations.Length is < 1 or > 100
            || operations.Distinct(StringComparer.Ordinal).Count() != operations.Length)
            throw new ArgumentException("The mutation intent is invalid.", nameof(operationIds));
        IntentId = intentId;
        GenerationId = generationId;
        ExpectedRevision = expectedRevision;
        OperationIds = Array.AsReadOnly(operations);
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    public string IntentId { get; }
    public LibraryStateGenerationId GenerationId { get; }
    public LibraryStateRevision ExpectedRevision { get; }
    public IReadOnlyList<string> OperationIds { get; }
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
        LibraryStateUncertainty? uncertainty = null)
    {
        ArgumentNullException.ThrowIfNull(generationId);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == LibraryStateStatus.Uncertain) != (uncertainty is not null))
            throw new ArgumentException("Only uncertain library state can contain uncertainty evidence.", nameof(uncertainty));
        DateTimeOffset projected = projectedAtUtc.ToUniversalTime();
        if (projected < snapshot.ScannedAt.ToUniversalTime())
            throw new ArgumentException("Projected state cannot predate its baseline scan.", nameof(projectedAtUtc));
        GenerationId = generationId;
        Revision = revision;
        Status = status;
        Snapshot = snapshot;
        ProjectedAtUtc = projected;
        Uncertainty = uncertainty;
    }

    public LibraryStateGenerationId GenerationId { get; }
    public LibraryStateRevision Revision { get; }
    public LibraryStateStatus Status { get; }
    public LibrarySnapshot Snapshot { get; }
    public DateTimeOffset ProjectedAtUtc { get; }
    public LibraryStateUncertainty? Uncertainty { get; }
    public bool IsAuthoritative => Status == LibraryStateStatus.Authoritative;

    public static LibraryState FromScan(LibrarySnapshot snapshot, LibraryStateGenerationId generationId) =>
        new(generationId, new(0), LibraryStateStatus.Authoritative, snapshot, snapshot.ScannedAt);

    public LibraryState MarkUncertain(LibraryStateUncertainty uncertainty) =>
        new(GenerationId, Revision, LibraryStateStatus.Uncertain, Snapshot,
            uncertainty.OccurredAtUtc < ProjectedAtUtc ? ProjectedAtUtc : uncertainty.OccurredAtUtc,
            uncertainty);
}
