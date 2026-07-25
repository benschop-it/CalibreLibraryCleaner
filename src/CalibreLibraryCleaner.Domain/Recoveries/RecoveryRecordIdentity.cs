using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Domain.Recoveries;

public sealed record LogicalRecoveryRecordId
{
    public LogicalRecoveryRecordId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128) throw new ArgumentException("A logical recovery record ID is too long.", nameof(value));
        Value = value.Trim().ToLowerInvariant();
    }

    public string Value { get; }
    public override string ToString() => Value;

    public static LogicalRecoveryRecordId Create(CleanupExecutionId executionId, CalibreBookId originalRecordId)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        string input = $"cleanup-recovery-logical-record/1.0:{executionId}:{originalRecordId.Value.ToString(CultureInfo.InvariantCulture)}";
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        return new($"record-{digest[..32]}");
    }
}

public sealed record RecoveryRecordIdentity
{
    public RecoveryRecordIdentity(
        LogicalRecoveryRecordId logicalRecordId,
        CalibreBookId originalRecordId,
        CalibreBookId? currentRecordId,
        CalibreBookId? recoveredRecordId,
        string metadataFingerprint,
        string? currentMetadataFingerprint,
        IEnumerable<string> identifiers,
        IEnumerable<RecoveryFormatIdentity> backedUpFormats,
        string originalBackupArtifactIdentity,
        CalibreBookId? preservedSeparateRecordId = null)
    {
        LogicalRecordId = logicalRecordId ?? throw new ArgumentNullException(nameof(logicalRecordId));
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalBackupArtifactIdentity);
        string[] orderedIdentifiers = identifiers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        RecoveryFormatIdentity[] orderedFormats = backedUpFormats
            .OrderBy(value => value.Format, StringComparer.Ordinal)
            .ThenBy(value => value.BackupArtifactIdentity, StringComparer.Ordinal).ToArray();
        if (orderedFormats.Select(value => value.Format).Distinct(StringComparer.Ordinal).Count() != orderedFormats.Length)
            throw new ArgumentException("A recovery record identity cannot contain duplicate formats.", nameof(backedUpFormats));
        OriginalRecordId = originalRecordId;
        CurrentRecordId = currentRecordId;
        RecoveredRecordId = recoveredRecordId;
        MetadataFingerprint = metadataFingerprint.Trim().ToLowerInvariant();
        CurrentMetadataFingerprint = string.IsNullOrWhiteSpace(currentMetadataFingerprint)
            ? null : currentMetadataFingerprint.Trim().ToLowerInvariant();
        if ((CurrentRecordId is not null) != (CurrentMetadataFingerprint is not null))
            throw new ArgumentException(
                "A current recovery record requires its exact semantic metadata fingerprint.",
                nameof(currentMetadataFingerprint));
        Identifiers = Array.AsReadOnly(orderedIdentifiers);
        BackedUpFormats = Array.AsReadOnly(orderedFormats);
        OriginalBackupArtifactIdentity = originalBackupArtifactIdentity.Trim();
        PreservedSeparateRecordId = preservedSeparateRecordId;
    }

    public LogicalRecoveryRecordId LogicalRecordId { get; }
    public CalibreBookId OriginalRecordId { get; }
    public CalibreBookId? CurrentRecordId { get; }
    public CalibreBookId? RecoveredRecordId { get; }
    public string MetadataFingerprint { get; }
    public string? CurrentMetadataFingerprint { get; }
    public IReadOnlyList<string> Identifiers { get; }
    public IReadOnlyList<RecoveryFormatIdentity> BackedUpFormats { get; }
    public string OriginalBackupArtifactIdentity { get; }
    public CalibreBookId? PreservedSeparateRecordId { get; }
}

public sealed record RecoveryFormatIdentity
{
    public RecoveryFormatIdentity(string format, FormatFileFingerprint fingerprint, string backupArtifactIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupArtifactIdentity);
        Format = format.Trim().ToUpperInvariant();
        Fingerprint = fingerprint;
        BackupArtifactIdentity = backupArtifactIdentity.Trim();
    }

    public string Format { get; }
    public FormatFileFingerprint Fingerprint { get; }
    public string BackupArtifactIdentity { get; }
}

public sealed record RecoveryRecordIdMapping
{
    public RecoveryRecordIdMapping(
        LogicalRecoveryRecordId logicalRecordId,
        CalibreBookId originalRecordId,
        CalibreBookId? cleanupTargetRecordId,
        CalibreBookId recoveredRecordId,
        IEnumerable<string> restoredFormats,
        IEnumerable<string> identifiers,
        DateTimeOffset mappedAtUtc)
    {
        LogicalRecordId = logicalRecordId ?? throw new ArgumentNullException(nameof(logicalRecordId));
        OriginalRecordId = originalRecordId;
        CleanupTargetRecordId = cleanupTargetRecordId;
        RecoveredRecordId = recoveredRecordId;
        RestoredFormats = Array.AsReadOnly(restoredFormats.Select(value => value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        Identifiers = Array.AsReadOnly(identifiers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        MappedAtUtc = mappedAtUtc.ToUniversalTime();
    }

    public LogicalRecoveryRecordId LogicalRecordId { get; }
    public CalibreBookId OriginalRecordId { get; }
    public CalibreBookId? CleanupTargetRecordId { get; }
    public CalibreBookId RecoveredRecordId { get; }
    public IReadOnlyList<string> RestoredFormats { get; }
    public IReadOnlyList<string> Identifiers { get; }
    public DateTimeOffset MappedAtUtc { get; }
    public bool NumericIdChanged => OriginalRecordId != RecoveredRecordId;
}

public static class RecoveryRecordFingerprintPolicy
{
    public static string Compute(ExpectedRecordState record)
    {
        ArgumentNullException.ThrowIfNull(record);
        StringBuilder value = new();
        Add(value, record.Title);
        Add(value, record.AuthorSort);
        foreach (ExpectedAuthorState author in record.Authors)
        {
            Add(value, author.Name);
            Add(value, author.SortName);
        }
        foreach (ExpectedIdentifierState identifier in record.Identifiers)
        {
            Add(value, identifier.Type);
            Add(value, identifier.Value);
        }
        Add(value, record.Publisher ?? string.Empty);
        Add(value, record.PublicationDate?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
        Add(value, record.Series ?? string.Empty);
        Add(value, record.SeriesIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        foreach (string language in record.Languages) Add(value, language);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString()))).ToLowerInvariant();
    }

    public static string Compute(CalibreBook record)
    {
        ArgumentNullException.ThrowIfNull(record);
        StringBuilder value = new();
        Add(value, record.Title);
        Add(value, record.AuthorSort);
        foreach (BookAuthor author in record.Authors)
        {
            Add(value, author.Name);
            Add(value, author.SortName);
        }
        foreach (BookIdentifier identifier in record.Identifiers.OrderBy(value => value.Type, StringComparer.Ordinal)
                     .ThenBy(value => value.Value, StringComparer.Ordinal))
        {
            Add(value, identifier.Type);
            Add(value, identifier.Value);
        }
        BookPublicationMetadata metadata = record.PublicationMetadata;
        Add(value, metadata.Publisher ?? string.Empty);
        Add(value, metadata.PublicationDate?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
        Add(value, metadata.Series ?? string.Empty);
        Add(value, metadata.SeriesIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        foreach (string language in metadata.Languages) Add(value, language);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString()))).ToLowerInvariant();
    }

    private static void Add(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
}
