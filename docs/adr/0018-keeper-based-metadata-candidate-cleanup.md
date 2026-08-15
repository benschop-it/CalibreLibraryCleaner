# ADR 0018: Use Keeper-Based Metadata Candidate Cleanup

- Status: Superseded by ADR 0020's Unified Candidate cleanup
- Date: 2026-08-08
- Amends: ADR 0013, ADR 0017

> Historical scope: the keeper-authoritative semantics informed Unified Candidate
> cleanup, but the standalone Metadata mutation command and workspace were retired.

## Context

The Metadata candidates tab exposed recommendation review concepts such as overrides, retained-separate records, metadata/format source editors, reasons, warnings, and export state. That workflow did not directly match the user's goal: choose one record to keep, skip groups that should remain unchanged, and process the rest in one batch.

Exact normalized title and author sets are candidate evidence, not proof of identical content or edition. Metadata cleanup is therefore more destructive than exact-binary cleanup and requires explicit keeper review and external-backup confirmation.

## Decision

The Metadata candidates tab always starts with exactly one keeper and every Skip checkbox unchecked. The generated keeper is the recommendation policy's selected metadata source. When the policy does not select a metadata source, the first member in deterministic group order becomes the keeper. The user reviews that choice, may select another member as the sole keeper, and may independently Skip any group when no acceptable decision can be made. Choices are session/snapshot-scoped.

One command processes every unskipped group. The selected keeper retains its existing metadata and existing formats unchanged. For each format absent from the keeper, the generated recommendation's selected present source is transferred to the keeper. If no selected present source exists, the group is skipped before mutation.

Every format on every non-keeper is then removed and each emptied non-keeper record is removed. When the keeper already has the same format, the keeper's file wins even if a non-keeper's file is not byte-identical. The confirmation dialog states this consequence and requires confirmation that a complete external library backup exists.

Cleanup uses the same trusted persistent `calibre-debug` worker, library mutation lease, bounded chunks, run marker, complete-chunk projected-state commits, structured logging, uncertainty handling, and final checkpoint as exact-binary cleanup. There is no direct-command fallback, retry, continuation after ambiguity, application-created backup, or automated recovery.

## Consequences

- The Metadata candidates UI matches the keeper-oriented Exact file duplicates workflow.
- Normalized metadata groups can be processed efficiently in one batch.
- The keeper's metadata is never rewritten from another record.
- Complementary formats can be consolidated onto the keeper.
- Non-identical same-format alternatives on Remove records are intentionally deleted.
- Ambiguous keeper decisions fall back to the first deterministic group member; unresolved complementary format decisions still skip the group at execution rather than guessing a source.
- A complete external backup is essential because automated restoration is not provided.

## Rejected alternatives

- Preserve the recommendation override/editor UI.
- Allow multiple keepers in one metadata group.
- Copy generated metadata onto a user-selected keeper.
- Automatically choose a complementary source when the recommendation is unresolved.
- Preserve non-identical same-format source records automatically.
- Process one group per worker run.
