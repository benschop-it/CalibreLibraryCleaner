# ADR 0017: Simplify to Worker-Only Exact Cleanup

- Status: Accepted
- Date: 2026-08-05
- Supersedes: ADR 0006, ADR 0007, ADR 0008, ADR 0010 for executable cleanup, application-created backup, and automated recovery behavior
- Amends: ADR 0012, ADR 0013, ADR 0015

## Context

The persistent `calibre-debug` exact-duplicate worker has completed cleanup of a library with more than 20,000 items without crashing. The repository still carries a second mutation engine, general immutable cleanup-plan execution, application-created backup verification, detailed execution journals/history, and a reconciliation-heavy recovery subsystem.

That complexity no longer matches the intended product. The developer or user maintains a complete external library backup. Failures remain useful development evidence and must be logged, but automated recovery and fallback behavior are not required.

## Decision

One-click exact binary duplicate cleanup is the only executable mutation workflow. Recommendation review remains analysis-only. General cleanup-plan authoring/execution and automated recovery are removed.

Every cleanup run requires explicit acknowledgement that a complete external library backup exists. The application does not create, inspect, or verify that backup.

The only mutation engine is one trusted persistent `calibre-debug` worker using the fixed, typed, bounded protocol from ADR 0015. There is no `calibredb` mutation fallback. Worker discovery, startup, handshake, lease, or other pre-mutation failure is logged and stops the run without mutation.

The workflow keeps deterministic keeper selection, unambiguous complementary-format transfer, exact-format removal, empty-record removal, the library mutation lease, operation ordering, chunks of at most 100 operations, typed state deltas, and checkpointing.

One durable cleanup-run marker is written before the first worker chunk. A chunk is projected only after the worker reports the complete chunk successful. On failed, ambiguous, interrupted, unpersistable, or unprojectable mutation, the application:

1. emits a structured error log containing run and operation identifiers, technical worker outcome/error codes, and exception details without book content;
2. stops immediately without retrying, continuing, inferring a successful prefix, or invoking another mutation engine;
3. marks projected state Rescan-required; and
4. blocks later mutation until an explicit successful Scan creates a new state generation.

Automated rollback/recovery, per-operation backup bundles, execution/recovery plans, recovery reconciliation, and recovery UI are removed. Logs and the user's external backup support manual diagnosis and restoration outside the application.

## Consequences

- Mutation behavior has one engine and one failure contract.
- Worker startup failures reduce availability rather than selecting a slower fallback.
- The application cannot restore a failed cleanup automatically.
- External backup quality is the developer or user's responsibility.
- Ambiguous mutation remains fail-closed: logging does not imply continuing.
- Considerable domain, application, infrastructure, WPF, and test surface can be deleted.
- Structured logs become the primary development diagnostic artifact for mutation failures.

## Rejected alternatives

- Continue maintaining both worker and direct `calibredb` mutation engines.
- Continue after a failed or ambiguous worker chunk.
- Retry possibly applied operations.
- Keep automated recovery solely because its implementation already exists.
- Silently assume mutation success without logs or projected-state blocking.