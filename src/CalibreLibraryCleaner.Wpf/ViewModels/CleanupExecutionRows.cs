namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed record CleanupExecutionOperationRowViewModel(
    string Phase,
    string Operation,
    long TargetRecordId,
    long? SourceRecordId,
    string Format,
    string Effect,
    string Dependencies);

public sealed record CleanupExecutionIssueRowViewModel(
    string Severity,
    string Code,
    string Subject,
    string Explanation);

public sealed record CleanupExecutionEffectRowViewModel(
    string Effect,
    long RecordId,
    string Format,
    string Detail);

public sealed record CleanupExecutionHistoryRowViewModel(
    string ExecutionId,
    string State,
    string Disposition,
    string Failure,
    DateTimeOffset FinishedAtUtc,
    bool MutationStarted,
    string BundlePath);
