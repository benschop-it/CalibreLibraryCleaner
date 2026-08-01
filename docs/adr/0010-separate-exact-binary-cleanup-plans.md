# ADR 0010: Use Separate Plans for Explicit Exact-Binary Record Deletion

- Status: Accepted
- Date: 2026-08-01

## Context

Exact-binary groups are valid cleanup candidates even when their Calibre records have different metadata. Users may intentionally delete a duplicate record, including its additional metadata and formats, when they explicitly check it and have a full library copy or verified per-record backup.

## Decision

Use a separate `exact-binary-cleanup-plan/1.0` aggregate. The user selects one current exact-group member as keeper and checks record IDs to delete. Unchecked records are never inferred. The body records library/group identity, shared fingerprint, retained association, exact evidence for every checked record, complete expected keeper/checked-record state, backup requirements, and review time.

Metadata equality is not an eligibility condition. Metadata and additional formats on a checked record are included in its backup and removed with that record. The keeper, unchecked records, and unrelated records are preservation expectations.

The plan follows valid, approved, stale, and revoked lifecycle concepts, with approval bound to its canonical SHA-256 body digest. Relevant state changes invalidate the plan.

Use a separate compact executor. It creates raw-format copies and complete Calibre exports outside the library, seals and rechecks a manifest, obtains final confirmation naming exact record IDs, calls typed non-permanent `calibredb remove`, and performs a full scan after each command. Recovery may use this bundle or a user-maintained full library copy; automatic rollback is not required.

## Guardrails

- Exactly one current exact-group member is retained.
- At least one different group record is explicitly checked.
- Every checked record has current exact-binary evidence matching the keeper fingerprint.
- The keeper cannot be checked, and unchecked records cannot enter the deletion set.
- Every involved record and format has complete expected state and backup coverage.
- The exact Calibre cleanup profile remains version-, identity-, and probe-gated.

## Consequences

Users can delete checked duplicate books independently from metadata candidates. The separate aggregate and executor keep this explicit record-level authority distinct from recommendation-driven consolidation.
