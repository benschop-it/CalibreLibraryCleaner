using System.Globalization;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Executions;

public sealed record CleanupExecutionId
{
    public CleanupExecutionId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("An execution ID cannot be empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public sealed record ExecutionToolIdentity
{
    public ExecutionToolIdentity(
        string canonicalExecutableIdentity,
        string productVersion,
        Sha256Digest executableSha256,
        string capabilityProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalExecutableIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityProfile);
        CanonicalExecutableIdentity = canonicalExecutableIdentity.Trim();
        ProductVersion = productVersion.Trim();
        if (string.IsNullOrWhiteSpace(executableSha256.Value))
            throw new ArgumentException("The executable digest is required.", nameof(executableSha256));
        ExecutableSha256 = executableSha256;
        CapabilityProfile = capabilityProfile.Trim();
    }

    public string CanonicalExecutableIdentity { get; }
    public string ProductVersion { get; }
    public Sha256Digest ExecutableSha256 { get; }
    public string CapabilityProfile { get; }
}

public enum ExecutionIssueSeverity
{
    BlockingError,
    Warning,
    Information,
}

public sealed record ExecutionIssue
{
    public ExecutionIssue(
        string code,
        ExecutionIssueSeverity severity,
        string explanation,
        CalibreBookId? recordId = null,
        string? format = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        if (!Enum.IsDefined(severity)) throw new ArgumentOutOfRangeException(nameof(severity));
        if (code.Length > 128 || explanation.Length > 1024)
            throw new ArgumentException("Execution issue text exceeds its bounds.");
        Code = code.Trim();
        Severity = severity;
        Explanation = explanation.Trim();
        RecordId = recordId;
        Format = string.IsNullOrWhiteSpace(format) ? null : format.ToUpperInvariant();
    }

    public string Code { get; }
    public ExecutionIssueSeverity Severity { get; }
    public string Explanation { get; }
    public CalibreBookId? RecordId { get; }
    public string? Format { get; }
}
