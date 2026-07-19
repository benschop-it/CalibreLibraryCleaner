# ADR 0006: Use Immutable, Non-Executable Cleanup Plan Artifacts

- Status: Accepted
- Date: 2026-07-18

## Context

Milestone 6 must convert one current, explicitly reviewed Milestone 5 recommendation into a durable cleanup description without granting authority or adding mechanisms to modify a Calibre library. Approval must remain bound to the exact reviewed content, and changed source state must fail closed.

## Decision

Use the separate `cleanup-plan/1.0` JSON schema and `cleanup-plan-policy/1.0.0` policy. A cleanup plan contains an immutable semantic body plus immutable lifecycle revisions. The body records the expected library, record, metadata, and format state; declarative retention and removal intentions; complete backup requirements; and recommendation/review provenance.

The canonical SHA-256 content digest covers only the versioned semantic body in deterministic order. Lifecycle timestamps, validation issues, approval, revocation, and presentation state are excluded. Explicit approval records and must match that digest. Any operational content change requires regeneration with a new plan ID.

Lifecycle transitions are limited to `Draft -> Valid|Blocked`, `Valid -> Approved|Stale|Blocked`, and `Approved -> Stale|Revoked`. Blocked, stale, and revoked definitions are terminal. Staleness invalidates approval for all future use while preserving the prior approval as audit information.

Cleanup-plan files are imported and exported only through an Infrastructure-owned, bounded, deterministic store outside the physically resolved selected Calibre library. Imported JSON is untrusted, must pass schema, bounds, graph, canonical-digest, lifecycle, and current-snapshot validation, and preserved imported approval is informational only.

## Guardrails

- A cleanup plan is inert data. It contains no command, process argument, script, executable callback, or Calibre operation sequence.
- Milestone 6 adds no execution, simulation, lock, backup creation, mutation, verification-after-mutation, audit execution, or rollback service.
- Domain owns plan values and invariants and references no JSON, filesystem, SQLite, WPF, parser, logging, DI, or Calibre tooling types.
- Application owns generation, validation, staleness, approval, revocation, import, and export orchestration.
- Infrastructure alone owns canonical serialization/hashing integration and guarded external plan-file I/O.
- WPF owns review and explicit interaction only; ViewModels do not serialize or access files directly.
- Approval is not a signature and does not replace mandatory live revalidation, backup verification, and supported-tool checks in a later milestone.

## Consequences

Plans can be reviewed, shared, revoked, and detected as stale without touching Calibre. Conservative terminal states and strict imports may require regeneration after changes, including benign reversions. Artifacts can prove internal integrity and approval binding but not author identity or resistance to a malicious rewrite and rehash.
