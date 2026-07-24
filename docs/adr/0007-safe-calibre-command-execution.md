# ADR 0007: Execute Cleanup Plans Through a Typed, Verified Calibre Boundary

- Status: Accepted
- Date: 2026-07-19

## Context

Milestone 7 is the first milestone allowed to change a Calibre library. A
successful process exit is not sufficient evidence that the requested semantic
state was produced, and an interrupted multi-command consolidation can leave a
valid but partially changed library. Direct SQLite or managed-file mutation
would bypass Calibre's consistency rules and is prohibited by ADRs 0001 and
0003.

## Decision

Execute exactly one current, approved `cleanup-plan/1.0` artifact at a time
through a closed Application-owned command contract implemented by
Infrastructure with `calibredb`. The initial compatibility profile accepts
exactly Calibre 9.11.0. Other versions fail closed until a reviewed profile and
opt-in disposable-library compatibility tests are added.

The V1 mutation allow-list is:

- `add_format` to add a selected format or replace an explicitly reviewed
  inferior target format; and
- non-permanent `remove` to remove a redundant source record after every
  retained contribution is present and verified on the target.

Target metadata and formats already retained on the target are verified no-ops.
Because the V1 snapshot records only cover presence rather than cover bytes,
plans involving a present target or source cover fail closed. Cross-record
metadata or cover transfer, standalone target format removal, extra-data
mutation, unknown plan shapes, and arbitrary commands are unsupported.

The profile is disabled by default until the opt-in disposable-library
compatibility suite passes with an explicitly supplied Calibre 9.11.0
executable and test root. Controlled executable tests validate boundary
mechanics but cannot qualify real Calibre behavior.

Infrastructure launches the canonical executable directly with
`ProcessStartInfo.UseShellExecute = false`, `CreateNoWindow = true`, and
`ArgumentList`. It never invokes a command shell or script. Output parsing and
version probing remain inside Infrastructure. Mutating processes are never
terminated for cancellation.

## Required execution gates

1. Hold an application-owned cross-process lease scoped to the canonical
   library identity and stored outside the library.
2. Recompute and validate the immutable plan digest and approval.
3. Perform a complete fresh read-only scan and reject stale or unsupported
   state.
4. Create a complete external recovery bundle and independently verify its
   deterministic manifest.
5. Repeat the complete read-only scan and all identity checks immediately before
   mutation.
6. Flush a hash-chained write-ahead `MutationStarting` journal entry.
7. Execute constructive format operations serially, scanning and semantically
   verifying after each command.
8. Revalidate the destructive gate, then remove redundant records serially and
   verify after each command.
9. Perform a complete final read-only scan before reporting completion.

Local confirmation is bound to the canonical library root and deterministic
operation-graph digest as well as the library UUID, immutable plan body,
executable identity, and backup destination. The executor repeats the complete
read-only scan and all of these checks immediately before every mutating
command, not only once after backup.

Any uncertainty before mutation prevents mutation. Any uncertainty after the
mutation marker stops later commands and produces a durable
`RecoveryRequired` result.

Before the first mutation marker, the application atomically writes an
application-local recovery guard keyed by the library identity. The guard is
replaced by the terminal history result only after execution is classified.
Startup reconciliation accepts post-mutation completion only when the final
hash-chained journal event and immutable terminal summary agree.

## Backup and durability

Recovery bundles are created under a user-selected existing directory that is
proven to be outside the Calibre library. Every involved record is exported with
documented Calibre export behavior, including OPF, cover when present, formats,
and extra data. Exact format bytes are additionally streamed read-only from the
validated managed files into create-new external files and independently
rehash-verified. The verified external format copy, never the live source file,
is passed to `add_format`.

Unmodeled extra data blocks V1 execution even though it is exported. A sealed
manifest, the exact plan, tool identity, preflight evidence, and a versioned
append-only hash-chained journal are flushed outside the library. On Windows,
the implementation uses write-through file handles and `Flush(flushToDisk:
true)` at mutation boundaries. Destinations where creation, free-space checks,
or durable verification cannot be proven fail closed.

Executable and selected-format backup files remain opened without replacement
sharing from identity verification through process completion. Backup,
configuration, history, lease, journal, and manifest paths reject reparse
points and any location that resolves in or around the library. Calibre receives
only the explicitly allow-listed minimum process environment.

`check_library --csv` is supplementary evidence only. It is not required for
V1 completion and never uses a write option.

## Cancellation and crash recovery

Cancellation before `MutationStarting` stops normally and prevents mutation.
After that marker, cancellation becomes a durable safe-stop request. The active
Calibre process is allowed to exit, its result is scanned and verified, and no
later operation begins. An incomplete execution with a mutation marker is
`RecoveryRequired` on startup. Milestone 7 never retries, resumes, repairs, or
rolls back it automatically.

The lease protects only against another Calibre Library Cleaner execution; it
does not exclude Calibre or third-party writers. Fresh state validation remains
mandatory, and the UI requires explicit acknowledgement that other mutators are
closed.

## Rejected alternatives

- Direct writes to `metadata.db` or Calibre-managed files.
- Calibre GUI automation or termination.
- Shell invocation, generated scripts, or arbitrary command arguments.
- Optimistic version ranges or runtime capability inference from localized help.
- `backup_metadata`, `embed_metadata`, `restore_database`, permanent removal,
  or write-capable library checks.
- Killing a mutating process for cancellation.
- Blind retry, resume, undocumented repair, or automated rollback.

## Consequences and guardrails

Execution is deliberately slower because a full read-only scan follows every
mutation. The exact-version profile limits availability, and partial failure can
still require manual recovery or Milestone 8 rollback. In return, every command
is allow-listed, backed up, journaled, and semantically verified, and uncertainty
cannot silently become a success result.

Tests use fakes, controlled helper executables, temporary directories, and
synthetic libraries. Real-Calibre tests are explicitly opt-in, require an exact
executable and a separately supplied disposable root, reject known user-library
paths, and never discover a default library.
