# ADR 0008: Use Reconciliation-Based Verified Recovery

- Status: Superseded by ADR 0017
- Date: 2026-07-24

## Context

Milestone 8 must recover one Milestone 7 cleanup execution without assuming that
the cleanup completed exactly as planned or that the library has remained
unchanged. A cleanup journal proves durable application boundaries, but it is
not a current library snapshot. The original backup proves available
pre-execution bytes, but it does not authorize overwriting later user data.
Calibre can also assign different numeric record IDs when a record is recreated.

Blindly reversing the cleanup operation graph would therefore risk losing
unexpected content, repeating an uncertain command, targeting a reused record
ID, or reporting process success without semantic restoration.

## Decision

Recovery is a separately approved immutable `cleanup-recovery-plan/1.0`
artifact produced by deterministic three-way reconciliation of:

1. the canonical, verified cleanup-plan pre-state and original backup;
2. the legal durable operation projection from the verified execution journal;
   and
3. a fresh complete read-only scan of the actual current library.

The original Milestone 7 bundle is immutable input. Recovery creates a new,
create-only bundle and independently verifies a complete backup of every
currently affected record before the first recovery mutation. The backup chain
links both generations without rewriting either generation.

Semantic logical record identities are stable across Calibre numeric-ID
changes. New IDs are discovered by a unique before/after semantic delta and are
durably mapped before dependent operations.

Every mutation uses the existing direct, typed, no-shell Calibre process
boundary. Recovery and cleanup share one canonical-root-plus-library-UUID lease
domain. Constructive operations and fresh intermediate verification precede a
separate destructive confirmation and every destructive operation. A fresh
semantic scan verifies every command and the final state.

Unexpected current content is backed up and preserved in place or in a
separate recovered record. If neither can be proven safe, recovery blocks or
ends as `ManualInterventionRequired`.

Each exact Calibre recovery capability is enabled only after documentation,
closed command mapping, controlled-executable tests, and opt-in disposable
real-Calibre qualification all pass. Unknown or disabled capabilities fail
closed.

## Guardrails

- Never write directly to `metadata.db`.
- Never copy, replace, move, rename, or delete Calibre-managed files through
  filesystem APIs.
- Never modify, repair, upgrade, or delete an original cleanup bundle.
- Never begin mutation until both the original backup and the new current-state
  backup have been independently verified.
- Never choose a destructive target from filename, timestamp, size, or numeric
  record ID alone.
- Never overwrite or remove unexpected unique current data silently.
- Never use a shell, arbitrary command text, GUI automation, elevation,
  permanent removal, or undocumented repair command.
- Never terminate an active Calibre mutation.
- Never treat exit code zero as semantic success.
- Never automatically retry, resume, start recovery, or roll back a failed
  recovery.
- Never resolve an original recovery-required guard by rewriting its source
  history; append a verified recovery-resolution link instead.

## Rejected alternatives

- Blind inversion of the cleanup operation graph.
- Automatic rollback after cleanup failure.
- Direct SQLite repair or managed-folder restoration.
- Reuse or modification of the original Milestone 7 bundle.
- Command-exit-code success without a semantic scan.
- Requiring Calibre to reproduce original numeric record IDs.
- Overwriting unexpected current content after only a user acknowledgement.
- Separate cleanup and recovery lock domains.
- Automatic retry, resume, or rollback-of-rollback.

## Consequences

Recovery is intentionally conservative and scan-heavy. Some recoveries remain
blocked when exact artifacts, unique semantic identity, preservation space, or
qualified Calibre capabilities are unavailable. In return, every automated
change is based on actual current state, protected by two verified backup
generations, ordered constructively before destruction, durably journaled, and
semantically verified.
