# Review Feature Workflow

Review the implementation against repository documentation.

## Safety

Check for direct database writes, analysis-time file mutation, missing external-
backup confirmation, mutation outside the fixed worker, continuation/retry after an
ambiguous mutation, or provider/model evidence invoking mutation.

## Architecture

Check Domain purity, Application-owned abstractions, Infrastructure implementations, project references, and absence of persistence or ebook-analysis logic in WPF.

## Correctness

Check missing/malformed handling, path validation, bounded concurrency/resources,
progress visibility, deterministic/versioned evidence, safe process/network/model
boundaries, cache identity/invalidation, and explicit failure behavior. Review
cancellation only where the feature retains it.

## Matching and performance

Check labeled precision/recall evidence, hard negatives, candidate-cap effects,
provider/model provenance, deterministic IDs, cache cold/warm behavior, and whether
the change avoids unnecessary whole-library work.

## Tests

Check success, invalid input, malformed content, matching/cache regressions, safety
assertions, architecture boundaries, and absence of personal-library dependencies.

## Output

Order findings by severity. For each finding include the affected file or area, explanation, minimal fix, and relevant requirement or ADR. State explicitly when no significant issue is found.
