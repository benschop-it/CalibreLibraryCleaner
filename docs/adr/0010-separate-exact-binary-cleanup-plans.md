# ADR 0010: Use Separate Plans for Single-Keeper Exact-Binary Consolidation

- Status: Accepted
- Date: 2026-08-01

## Context

Exact-binary groups are valid cleanup candidates even when their Calibre records have different metadata. The workflow must consolidate a multi-record group to exactly one user-selected keeper, never zero and never an ambiguous subset.

## Decision

Use a separate `exact-binary-cleanup-plan/1.0` aggregate. The user selects one current exact-group member as keeper. Every other distinct record ID in the group is derived as a deletion target. The body records library/group identity, shared fingerprint, retained association, complete exact evidence, complete involved-record state, backup requirements, and review time.

Metadata equality is not an eligibility condition. Metadata and additional formats on non-keeper records are included in backup and removed with those records. The keeper and unrelated records are preservation expectations.

The plan follows valid, approved, stale, and revoked lifecycle concepts, with approval bound to its canonical SHA-256 body digest. Relevant state changes invalidate the plan.

Use a separate compact executor. It creates raw-format copies and complete Calibre exports outside the library, seals and rechecks a manifest, obtains final confirmation naming exact record IDs, calls typed non-permanent `calibredb remove`, and performs a full scan after each command. Recovery may use this bundle or a user-maintained full library copy; automatic rollback is not required.

## Guardrails

- Exactly one current exact-group member is retained.
- The group must span at least two distinct records.
- Exactly one record is the keeper.
- Every non-keeper record has current exact-binary evidence matching the keeper fingerprint and must be in the deletion set.
- Every involved record and format has complete expected state and backup coverage.
- The exact Calibre cleanup profile remains version-, identity-, and probe-gated.

## Consequences

Users can consolidate exact duplicate books independently from metadata candidates. The separate aggregate and executor keep this single-keeper authority distinct from recommendation-driven consolidation.
