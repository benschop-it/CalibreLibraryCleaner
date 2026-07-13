# Review Feature Workflow

Review the implementation against repository documentation.

## Safety

Check for direct database writes, analysis-time file mutation, missing cleanup-plan approval, missing backups, stale-plan execution, missing verification, or autonomous AI deletion.

## Architecture

Check Domain purity, Application-owned abstractions, Infrastructure implementations, project references, and absence of persistence or ebook-analysis logic in WPF.

## Correctness

Check cancellation, missing and malformed file handling, path validation, bounded concurrency, deterministic scoring and normalization, safe external-process arguments, and explicit failure behavior.

## Tests

Check success, invalid input, cancellation, malformed content, safety assertions, stale plans, architecture boundaries, and absence of real user-data dependencies.

## Output

Order findings by severity. For each finding include the affected file or area, explanation, minimal fix, and relevant requirement or ADR. State explicitly when no significant issue is found.
