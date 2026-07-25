using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed record RecoveryReconciliationRowViewModel(
    string LogicalRecord,
    string OriginalRecordId,
    string CurrentRecordId,
    string Classifications,
    string Formats);

public sealed record RecoveryOperationRowViewModel(
    string Order,
    string Phase,
    string Kind,
    string LogicalRecord,
    string TargetRecord,
    string Format,
    string Dependencies,
    string Risk,
    string Capability,
    string Reason);

public sealed record RecoveryIssueRowViewModel(
    string Severity,
    string Code,
    string Subject,
    string Explanation)
{
    public static RecoveryIssueRowViewModel From(RecoveryIssue issue) =>
        new(issue.Severity.ToString(), issue.Code, issue.Subject, issue.Explanation);
}

public sealed record RecoveryPreservationRowViewModel(
    string LogicalRecord,
    long CurrentRecordId,
    string Format,
    string Sha256,
    long SizeInBytes,
    string Reason);

public sealed record RecoveryRecordMappingRowViewModel(
    string LogicalRecord,
    long OriginalRecordId,
    string CurrentRecordId,
    long RecoveredRecordId,
    string RestoredFormats,
    string Identifiers);
