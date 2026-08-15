# Milestone 7: Safe Calibre Execution

## Objective

Deliver the first mutation-capable vertical slice: execute exactly one approved,
current, immutable Milestone 6 cleanup plan against one Calibre library while
preserving a complete, verified recovery package and a durable audit trail.

The executor must fail closed. It must prove that the approved plan is unchanged,
revalidate every precondition against fresh read-only scans, acquire an exclusive
application lease, create and verify backups outside the library, and then use
only a narrowly typed, direct `calibredb` process boundary for mutations. Every
successful mutation must be followed by a fresh read-only verification before the
next dependent operation is allowed to start.

Milestone 7 is successful only when it can distinguish and durably report:

- completion with the planned final state verified;
- failure or cancellation before any mutation;
- a partially applied plan that requires recovery;
- a command that returned success but produced an unverifiable state; and
- a crash or journal interruption after the mutation boundary was crossed.

No Milestone 7 path writes directly to `metadata.db`, changes a
Calibre-managed file or directory through filesystem APIs, invokes a shell,
automates the Calibre GUI, automatically retries a mutation, resumes a partial
execution, or performs rollback.

## Scope

This milestone includes:

- execution of one locally confirmed, approved cleanup plan at a time;
- a separate execution aggregate and lifecycle without mutating the Milestone 6
  cleanup-plan artifact;
- local execution confirmation bound to the plan ID, revision, canonical content
  digest, library identity, backup destination, and supported Calibre tool;
- an application-level, cross-process exclusive execution lease;
- two fresh, complete, read-only library scans: one during preflight and one at
  the final mutation gate after backup;
- deterministic cleanup-plan validation and staleness evaluation using the
  existing Milestone 6 policies;
- Calibre executable discovery, canonical-path validation, version discovery,
  executable hashing, and an exact-version compatibility profile;
- complete external backups for every record and managed payload that the plan
  may change or remove;
- backup manifests and independent hash verification before mutation;
- a closed mapping from supported Milestone 6 declarative operations to typed
  Calibre commands;
- direct process invocation with a fixed executable and `ArgumentList`;
- dependency-safe execution of target format additions/replacements before
  source-record removals;
- fresh read-only intermediate verification after every mutating command;
- a fresh read-only final verification;
- a write-ahead, hash-chained execution journal and an application history index;
- cancellation before mutation and a safe-stop request after mutation starts;
- durable, explicit partial-failure and recovery-required outcomes;
- WPF review, confirmation, progress, safe-stop, result, and execution-history
  experiences; and
- automated tests at Domain, Application, Infrastructure, architecture, and WPF
  boundaries, plus an opt-in real-Calibre compatibility suite using a
  test-owned library.

The supported input is the existing `cleanup-plan/1.0` artifact produced by
Milestone 6. The executor does not silently reinterpret or upgrade it.

## Out of scope

- Automated rollback, rollback-plan generation, or rollback execution. These
  belong to Milestone 8.
- Retrying, resuming, or continuing a partial execution.
- Executing multiple cleanup plans as a batch or queue.
- Parallel Calibre mutations.
- Direct SQLite writes, write pragmas, schema changes, or SQLite transactions
  used as a mutation mechanism.
- Manual renaming, moving, replacing, or deleting of files or directories inside
  a Calibre library.
- `cmd.exe`, PowerShell, Bash, shell scripts, batch files, generated scripts, or
  `UseShellExecute = true`.
- Calibre GUI automation or terminating a running Calibre GUI.
- Arbitrary user-supplied executable paths or command arguments at the command
  gateway.
- Cross-record metadata merging or editing.
- Cross-record cover selection or replacement.
- Standalone removal of a format from the surviving target record.
- Mutation of Calibre extra-data files.
- `calibredb backup_metadata`, `embed_metadata`, `restore_database`,
  `add --automerge`, `remove --permanent`, or
  `check_library --vacuum-fts-db`.
- Support for an unverified Calibre version, even if its command-line help looks
  similar to a supported version.
- Repairing a failed library through undocumented Calibre behavior or direct
  database/filesystem intervention.
- Future recommendation, plan-schema, fuzzy-duplicate, or bulk-execution roadmap
  work.

## Relevant requirements

The following documents were read before this plan was written and remain
authoritative:

- `AGENTS.md`, every nested `AGENTS.md`, and `PLANS.md`;
- `docs/product-vision.md`;
- `docs/functional-requirements.md`;
- `docs/architecture.md`;
- `docs/domain-model.md`;
- `docs/duplicate-detection.md`;
- `docs/quality-scoring.md`;
- `docs/safety-and-rollback.md`;
- `docs/test-strategy.md`;
- `docs/roadmap.md`;
- `docs/workflows/implement-feature.md`;
- accepted ADRs `0001` through `0006`; and
- the completed Milestone 0 through Milestone 6 execution plans.

The controlling safety and architecture requirements are:

- `metadata.db` is read-only to this application at every stage.
- Analysis and verification never mutate the library.
- Calibre-managed data changes only through supported, documented Calibre
  tooling behind an Infrastructure implementation of an Application port.
- Domain remains independent; Application depends only on Domain;
  Infrastructure implements Application ports; WPF contains no execution
  business logic and references Infrastructure only in the composition root.
- The Milestone 6 plan is an inert, immutable artifact. Approval never directly
  triggers execution.
- Every destructive command is preceded by verified backup and a durable
  write-ahead journal entry.
- Every applied change is verified, and sufficient recovery information is
  preserved even when execution stops midway.
- Unsupported or ambiguous intent blocks before mutation.

The implementation must add and accept an ADR for the exact Milestone 7 command,
lease, backup, journaling, and cancellation boundaries before production code is
merged. If that ADR conflicts with this plan or another accepted ADR, update this
plan and resolve the architectural decision before implementation continues.

## Existing implementation inspected

### Milestone 6 contract

The existing implementation already provides:

- immutable `cleanup-plan/1.0` artifacts;
- canonical serialization and `ContentDigest`;
- input identity with definition digest;
- approval data bound to the plan digest;
- lifecycle states including `Approved`, `Stale`, and `Revoked`;
- deterministic plan validation, eligibility, and staleness policies;
- expected pre-execution and final states;
- explicit format retention, removal, and backup requirements;
- an external JSON artifact store guarded against library paths; and
- a WPF review and approval workspace.

Under the current Milestone 6 V1 policy:

- the target record is also the retained metadata source;
- one retained source is selected for each canonical format;
- formats may already be on the target or may come from another record;
- a selected off-target format may replace an inferior format on the target;
- all non-target records are removed only after their retained content is
  present on the target; and
- cover state is recorded, but no declarative cross-record cover-selection
  operation exists.

Those constraints permit a deliberately narrow Milestone 7 implementation.
Milestone 7 must validate them rather than assuming them. Any future plan shape
outside this closed profile is unsupported.

### Read-only scan and verification foundation

`ScanLibraryUseCase` already performs the complete read-only scan needed for
execution validation: library validation, read-only SQLite access, safe managed
path inspection, format hashing, EPUB assessment, duplicate grouping, and
recommendation generation. Execution preflight and verification should compose
this use case rather than create a weaker execution-only scan.

The existing snapshot, fingerprint, observation, issue, and cleanup-plan
staleness types provide most comparison inputs. Milestone 7 must add
execution-specific comparison results instead of leaking process, filesystem, or
SQLite types into Domain.

### Missing execution capabilities

No current production code owns:

- Calibre executable discovery or compatibility profiles;
- a typed Calibre process gateway;
- execution leases;
- complete backup creation and verification;
- an execution aggregate or operation graph;
- durable execution journals or audit-history persistence;
- intermediate/final execution verification; or
- WPF execution progress and recovery-required reporting.

Existing architecture tests intentionally reject process, execution, and backup
concepts because earlier milestones were read-only. They must be replaced by
more precise Milestone 7 boundary tests, not merely deleted.

### Repository and test baseline

At planning time the repository contains completed Milestone 0 through Milestone
6 implementations and tests. The standard build uses .NET 10, xUnit,
FakeItEasy, and FluentAssertions. No new third-party production dependency is
required by this design.

The worktree also contains unrelated user-owned `.idea/workspace.xml` and `.ai/`
changes. Implementation must preserve and exclude those changes.

## Calibre capability investigation

### Evidence inspected

The planning workstation contains:

- `C:\Program Files\Calibre2\calibredb.exe`, version 7.14;
- `C:\Program Files\Calibre2\calibre.exe`, version 7.14; and
- `C:\Program Files\Calibre2\ebook-meta.exe`, version 7.14.

Planning inspected local `calibredb` help for the top-level command and for
`add_format`, `remove_format`, `set_metadata`, `remove`, `export`,
`show_metadata`, `check_library`, and `backup_metadata`.

The current official documentation was also inspected:

- [Calibre 9.11.0 command-line interface](https://manual.calibre-ebook.com/generated/en/calibredb.html)
- [Official Windows release page](https://calibre-ebook.com/download_windows)

The documentation supports the following relevant behavior:

- `add_format` adds a format to a record and replaces the existing format of the
  same type unless replacement is disabled;
- `remove` removes records, with permanent deletion being an optional behavior
  that this milestone will not use;
- `remove_format` removes one or more formats from a record;
- `export` can export formats, cover, OPF metadata, and extra data, and
  `--dont-update-metadata` avoids updating metadata embedded in exported book
  files;
- `show_metadata --as-opf` can render record metadata; and
- `check_library --csv` can produce machine-readable library-check output.

`backup_metadata` is intentionally unsuitable because it writes OPF files into
Calibre-managed book directories. `embed_metadata` is unsuitable because it
writes book files in place. Neither may be called.

### Initial compatibility policy

The first production compatibility profile is exact Calibre version `9.11.0`.
It is the version documented during planning and must be proven by the opt-in
real-Calibre compatibility suite before the profile is enabled. The locally
installed 7.14 tools are discovery evidence only and are **not** considered
compatible.

Compatibility is fail-closed:

- an absent executable blocks execution;
- version 7.14, any other older version, any newer version, or unparseable
  version output blocks execution;
- a supported version whose executable identity changes between backup and a
  mutation blocks execution;
- adding a version requires a reviewed compatibility profile, command fixtures,
  successful real-Calibre integration tests, and documentation/ADR updates; and
- runtime help text is not parsed to infer or broaden capabilities.

The implementation should keep version profiles data-driven enough to add a
reviewed exact version later, but must not use optimistic version ranges.

### Capability matrix

The V1 mapper must exhaustively match the existing declarative values rather
than recognize them by display text:

- `FormatRetentionMode.RetainInTarget`;
- `FormatRetentionMode.RetainFromOtherRecord`;
- `FormatRemovalReason.ByteIdenticalAlternative`;
- `FormatRemovalReason.ReviewedNonIdenticalReplacement`;
- `FormatRemovalReason.RemovedWithSourceRecordAfterRetention`; and
- every `BackupRequirementKind` listed in the backup matrix below.

An unknown enum value, an internally inconsistent combination, or a new
serialized value is unsupported.

| Milestone 6 declarative intent | V1 support | Calibre boundary | Ordering and verification |
| --- | --- | --- | --- |
| Retain metadata from the target record | Supported as a verified no-op | No command | Confirm the target is the declared metadata source before backup, at the final mutation gate, after every command, and finally. |
| Retain metadata from another record or merge metadata | Unsupported | None | Block during capability mapping. Do not use `set_metadata` as an inferred merge. |
| `RetainInTarget` | Supported as a verified no-op | No command | Require exact planned size, SHA-256, and observation before and after all dependent operations. |
| `RetainFromOtherRecord` when the target lacks the format | Supported | `calibredb add_format` using the verified external backup file | Back up source and target first. Add one format, then perform a full fresh read-only scan and require exact selected bytes on the target. |
| `RetainFromOtherRecord` plus `ReviewedNonIdenticalReplacement` | Supported | `calibredb add_format` with documented replacement behavior; do not pass `--dont-replace` | Preserve and verify the original target format in backup. Add from the verified selected-format backup, then require the target fingerprint to equal the selected source. |
| Planned off-target source is already byte-identical on target | Supported as an execution-time satisfied no-op | No command | Record the no-op and verify the exact expected fingerprint. Avoid an unnecessary replacement. |
| `ByteIdenticalAlternative` or `RemovedWithSourceRecordAfterRetention` on a non-target record | Supported through record removal | `calibredb remove` for the non-target record | Do not issue a separate format removal. Remove the record only after all retained contributions are verified on target. |
| Remove a standalone format from the surviving target | Unsupported in V1 | None | Although `remove_format` exists, the current plan schema/policy does not need this shape. Block rather than infer intent. |
| Remove a source record after retained content is present | Supported | `calibredb remove` without `--permanent` | Remove one record at a time only after all of its retained contributions and the complete final target format set are verified. Scan and verify after each removal. |
| Retain the target cover and recorded metadata representation | Supported as verified preservation plus backup | No mutation command; `calibredb export` for backup evidence | Require the target cover/OPF backup when declared present and verify that the target metadata and cover observation do not unexpectedly change. |
| Select or copy a cover from another record | Unsupported | None | Current V1 plans do not declare a source cover or target cover replacement. Block before mutation. |
| Combine formats from several records into one surviving record | Supported | Several ordered `add_format` calls, followed by ordered `remove` calls | Add and verify each canonical format serially; then remove source records serially. Never parallelize commands. |
| Preserve or mutate Calibre extra-data files | Unsupported | None | Detect extra data during export. Because V1 does not model its retention, block before mutation and retain the failed backup bundle as evidence. |

No generic “run Calibre command” application API is permitted. The compatibility
profile maps only the supported typed operations above. A plan is executable
only when every declarative operation maps to this allow-list.

The existing backup declarations map as follows:

| `BackupRequirementKind` | V1 fulfillment |
| --- | --- |
| `RecordMetadataSnapshot` | Export and validate OPF metadata for every involved record. |
| `FormatFile` | Create and independently hash-verify a raw external copy; also require the exported counterpart to be classifiable. |
| `CoverIfPresent` | Export and validate the cover whenever the plan's fresh state declares one. |
| `ManagedPathAndFileState` | Record canonical managed relative paths, safe observations, sizes, timestamps used by the observation model, and SHA-256 values in the sealed manifest. |
| `CleanupPlanArtifact` | Preserve the exact approved artifact bytes and verify their digest in the execution bundle. |
| `ExecutionAudit` | Create the write-ahead journal, hashed process/scan attachments, immutable terminal summary, and history reference. |

An absent requirement, duplicate/conflicting requirement, unknown requirement
kind, or requirement that cannot be fulfilled completely blocks before
mutation.

## Proposed design

### Execution aggregate and lifecycle

Create an execution aggregate separate from `CleanupPlan`. A cleanup plan remains
byte-for-byte immutable and retains its Milestone 6 state. The execution
aggregate references it by:

- cleanup-plan ID and revision;
- schema and policy versions;
- canonical content digest;
- input definition digest;
- approval digest and approval identity;
- library root and stable Calibre library UUID;
- execution ID; and
- local execution-confirmation digest.

Use the following lifecycle:

```text
Created
  -> AcquiringLease
  -> PreflightValidating
  -> ReadyForBackup
  -> BackingUp
  -> BackupVerified
  -> ReadyToExecute
  -> Executing
  -> Verifying
  -> Completed
```

Terminal or recovery states are:

- `PreflightFailed`;
- `BackupFailed`;
- `ExecutionFailedBeforeMutation`;
- `ExecutionPartiallyApplied`;
- `VerificationFailed`;
- `CancelledBeforeMutation`; and
- `RecoveryRequired`.

`ExecutionPartiallyApplied` and `VerificationFailed` are durable failure
classifications in the journal. The user-facing terminal disposition is
`RecoveryRequired` whenever the mutation-start marker exists and completion was
not fully verified. Preserve both the triggering classification and the final
disposition.

Each operation has its own immutable identity, dependencies, planned effect,
source/target records, canonical format where applicable, expected before/after
state, and status:

```text
Planned -> Starting -> Succeeded -> Verified
                    \-> Failed
Planned -> SatisfiedNoOp
Planned -> NotStarted
```

The Domain transition policy must reject skipped dependencies, commands after a
terminal state, a second mutation in flight, completion before final
verification, and any mutation before backup verification.

### Local confirmation and immutable-plan checks

An approved cleanup plan is necessary but not sufficient to execute. The WPF
flow must collect a new local execution confirmation after showing:

- plan ID, revision, digest, library identity, and approval;
- every mapped no-op and mutation in dependency order;
- the exact source records that will be removed;
- any target format that will be replaced;
- the supported Calibre executable path, version, and hash;
- the external backup destination;
- the inability to cancel a running mutation safely;
- the absence of automatic rollback in this milestone; and
- a required acknowledgement that Calibre and other library-mutating tools are
  closed.

Imported approval remains informational until this confirmation is captured.
The confirmation creates no command by itself.

At initial preflight, at the final mutation gate, and immediately before every
mutating process:

1. Re-serialize the cleanup-plan body canonically.
2. Recompute its content digest.
3. Require equality with `CleanupPlan.ContentDigest`.
4. Require the input identity definition digest to match the recomputed
   definition.
5. Require the approval to reference the same plan ID, revision, and digest.
6. Require the local execution confirmation to reference the same plan,
   executable identity, library, backup destination, and operation graph.

Any mismatch stops before the next command. After mutation has begun, the same
mismatch produces `RecoveryRequired`.

### Application-level execution lease

Acquire the lease before preflight and hold it until the terminal journal and
history entries are durably flushed.

The Infrastructure implementation should:

- derive a lease key from the canonical physical library root and stable library
  UUID using SHA-256;
- keep lease files under
  `%LocalAppData%\CalibreLibraryCleaner\leases`, never inside the library;
- canonicalize and reject reparse-point/alias ambiguity before deriving the key;
- acquire a held file handle with `FileShare.None`;
- write and flush execution ID, PID, process start identity, library identity,
  and acquisition time; and
- treat the operating-system handle, not file contents, as ownership authority.

A stale file without a held lock does not prevent acquisition, but an incomplete
prior journal must be reconciled before a new execution is allowed. A prior
journal with no mutation-start marker can be closed as failed before mutation.
A prior journal with a mutation-start marker is `RecoveryRequired` and blocks
new execution for that library.

The lease prevents concurrent executions by this application. It cannot prove
that Calibre GUI or another tool is inactive, so the WPF acknowledgement remains
mandatory. Do not enumerate and kill external processes.

### Fresh preflight

While holding the lease:

1. Load the exact approved artifact and local confirmation.
2. Recompute all canonical digests and validate the approved lifecycle state.
3. Discover and validate the Calibre executable.
4. Require the exact enabled compatibility profile and hash the executable.
5. Run `ScanLibraryUseCase` to produce a new complete read-only snapshot.
6. Re-run cleanup-plan validation and `CleanupPlanStalenessPolicy`.
7. Require the same library UUID and canonical physical root.
8. Require every record, managed path, metadata value, format fingerprint,
   format observation, and cover observation named by the plan to match.
9. Require unaffected involved state used by operation verification to be
   captured as a baseline.
10. Map every declarative operation through the closed capability matrix and
    build a deterministic dependency graph.
11. Validate the external backup destination and estimated capacity.
12. Persist and flush the preflight result before entering `ReadyForBackup`.

Any scan issue that prevents proving a plan precondition is an error, not a
warning. Unsupported operations, malformed expected state, missing files,
unsafe paths, changed bytes, changed metadata, new formats, missing covers,
library relocation ambiguity, or tool incompatibility produce
`PreflightFailed`.

### Backup boundary

Backups must be created in an existing, user-selected parent directory outside
the Calibre library and outside every canonical alias of it. Create a unique
execution directory with create-new semantics. Reject reparse points and any
path that cannot be proven external.

For every involved target and source record, create a complete recovery package:

- the exact approved cleanup-plan artifact;
- local execution confirmation;
- fresh preflight summary;
- tool path, version, and executable hash;
- a `calibredb export` payload containing OPF metadata, cover when present, all
  formats, and extra data, using documented `--dont-update-metadata`;
- byte-exact raw copies of every planned format read from its validated managed
  file using read-only access and restrictive sharing;
- target files that may be replaced;
- managed relative-path, file-size, timestamp/observation, and SHA-256 evidence;
- a backup manifest that hashes every backup file; and
- the execution journal and command-output attachments.

The raw format copy is the authoritative mutation input because its bytes can be
matched to the approved plan before execution. It is read from the library but
never written back through filesystem APIs.

Backup creation must:

- stream files with bounded buffers;
- create destination files without overwrite;
- calculate SHA-256 while copying;
- compare source size/hash/observation with the plan and preflight snapshot;
- close and reopen each backup, then independently recalculate size/hash;
- validate the exported OPF and required cover;
- classify every exported payload;
- fail when free-space availability is unknown or insufficient;
- seal an immutable manifest only after all files verify; and
- flush the manifest and journal before reporting `BackupVerified`.

Any exported payload that is not bijectively classified as expected OPF,
expected cover, expected format, or an explicitly supported payload is
unmodeled extra data. Because `cleanup-plan/1.0` does not model extra-data
retention, record `EXECUTION.UNMODELED_EXTRA_DATA`, fail before mutation, and
leave the failed backup bundle available for inspection.

Do not use `calibredb backup_metadata`; it would write OPFs inside the library.

### Final mutation gate

Backup activity creates a time window in which the library may change. Therefore
`BackupVerified` does not authorize mutation by itself.

Immediately before the first mutation:

1. Revalidate the cleanup plan, approval, confirmation, operation graph, and
   executable identity.
2. Verify the sealed backup-manifest digest and every mutation-input file.
3. Run a second complete fresh read-only scan.
4. Repeat all precondition and staleness comparisons.
5. Reconfirm the application lease.
6. Write and flush `ReadyToExecute`.

If any value differs, stop as `ExecutionFailedBeforeMutation`; retain the backup
and journal. Do not regenerate, edit, or automatically reapprove the plan.

### Dependency-safe operation graph

Build a deterministic directed acyclic graph, then execute its serial
topological order:

1. Verify target metadata preservation and every in-target retained format as
   no-op prerequisites.
2. Process off-target retained formats by canonical format ordinal.
3. If the target already has the exact selected fingerprint, record a
   `SatisfiedNoOp`.
4. Otherwise add the selected format from the verified external raw backup to
   the target with `add_format`. Existing target same-format bytes may be
   replaced only when the plan explicitly declares that replacement.
5. After each addition/replacement, run a complete fresh read-only scan and
   require:
   - target metadata and identity are unchanged;
   - the processed target format exactly matches the selected fingerprint;
   - unprocessed planned inputs remain unchanged;
   - all remaining source records and source formats remain unchanged; and
   - involved unaffected baseline state has not changed.
6. After every planned target format is present and verified, process
   non-target record removals in ascending stable record ID.
7. Before each removal, re-prove that all contributions from that record are
   present on the target and that the complete planned target format set is
   intact.
8. Remove one source record using `calibredb remove` without `--permanent`.
9. Run a complete fresh read-only scan and require the removed record to be
   absent, the target and remaining sources to match expected state, and
   unrelated involved state to remain unchanged.

Commands are never parallelized. A source record is never removed merely
because the preceding process returned exit code zero.

### Isolated Calibre command boundary

Application owns typed ports such as:

- discover supported Calibre tool;
- export one record to one controlled directory;
- add one verified format to one target record;
- remove one record non-permanently; and
- run a supported read-only library check.

Infrastructure alone translates those requests to the exact argument ordering
and options of an enabled compatibility profile. There is no raw command name,
argument array, or arbitrary executable supplied by Domain, WPF, or caller.

Every process uses:

- the canonical, verified executable;
- `ProcessStartInfo.UseShellExecute = false`;
- `CreateNoWindow = true`;
- `ProcessStartInfo.ArgumentList`, one argument per element;
- redirected, asynchronously drained standard output and standard error;
- a controlled working directory outside the library;
- no inherited secret environment data beyond a documented allow-list;
- bounded captured output with full output stored as hashed audit attachments
  when needed; and
- an explicit result containing start/end time, duration, exit code, typed
  command identity, executable identity, redacted arguments, output attachment
  digests, and cancellation disposition.

Never interpolate a command string and never invoke a shell. Paths and metadata
must not be emitted to normal application logs. The local execution bundle may
contain the detail required for recovery, with access errors surfaced clearly.

Revalidate the executable path, version, and hash immediately before every
mutating command. A change blocks the command.

### Mutation boundary and cancellation

Before launching the first mutating process, append and flush an irreversible
`MutationStarting` journal entry. From that point onward, the execution is
potentially partial until final verification succeeds.

Before `MutationStarting`:

- honor `CancellationToken` normally;
- allow cancellation of discovery, scanning, hashing, and non-mutating export;
- terminate a non-mutating helper only through the process gateway's tested
  cancellation path;
- clean only application-created external temporary files; and
- report `CancelledBeforeMutation`.

After `MutationStarting`:

- replace Cancel with a “Stop safely after current operation” request;
- latch the request durably;
- do not pass cancellation that can terminate a running mutating Calibre
  process;
- never call `Kill` on a mutating process;
- await the process exit, capture its result, and perform the required fresh
  verification;
- start no further operation after the safe-stop request; and
- report `RecoveryRequired` unless the complete plan had already been applied
  and finally verified.

Application shutdown while a mutation is running must warn that safe shutdown
is unavailable until Calibre exits and verification completes. If the process
is forcibly terminated or the application crashes, startup reconciliation uses
the durable marker to classify the execution as `RecoveryRequired`. Milestone 7
does not automatically inspect and continue it.

### Intermediate and final verification

Exit code zero is necessary but never sufficient.

The final verification uses another complete fresh read-only scan and requires:

- the target record still exists with the planned identity and metadata;
- every canonical target format exists exactly once and has the selected size,
  SHA-256, and safe observation;
- every planned source record is absent;
- no unexpected involved record or format was added;
- cover state and other preserved observations satisfy the V1 expected state;
- the cleanup plan, approval, confirmation, executable, backup manifest, and
  operation graph identities remain unchanged; and
- all journaled operations have a successful verified result.

For the exact compatibility profile, optionally capture
`calibredb check_library --csv` before mutation and after final mutation using
the fixed documented read-only invocation. It is supplementary evidence, not a
replacement for the repository scan. Never pass `--vacuum-fts-db`. New or
worsened check findings fail final verification; the raw CSV is stored as a
hashed attachment.

Any nonzero exit, timeout, unexpected output contract, post-command scan failure,
state mismatch, journal failure, or verification failure stops the graph. If no
mutation marker exists, report `ExecutionFailedBeforeMutation`; otherwise
preserve the specific failure and report `RecoveryRequired`. Do not attempt
database repair, filesystem repair, automatic retry, automatic resume, or
rollback.

### Durable journal and audit history

The external execution bundle contains the authoritative append-only JSON Lines
journal. Each entry contains:

- execution ID, monotonic sequence, UTC timestamp, and previous-entry hash;
- entry payload and canonical entry hash;
- plan, approval, confirmation, library, tool, and backup identities as
  appropriate;
- lifecycle transition and mutation-boundary status;
- typed operation and dependency identity;
- write-ahead process-start intent;
- process result and hashed output attachments;
- scan/verification identity and issues;
- cancellation or safe-stop requests; and
- failure classification, recovery requirements, and terminal disposition.

Use create-new semantics, single-writer ownership, deterministic canonical
serialization, write-through where supported, and flush-to-disk after every
state transition and before every external command. A terminal immutable summary
is written atomically only after the journal is complete.

Maintain a separate application-local history index outside the library for WPF
discovery. It contains summaries and references the authoritative bundle; it is
not allowed to rewrite the primary journal. If the history-index update fails
after the primary terminal record is durable, report the history error while
preserving the primary outcome.

Startup reconciliation validates the hash chain and finds incomplete journals:

- no mutation marker: classify as failed before mutation;
- mutation marker present without verified completion: `RecoveryRequired`;
- corrupt or missing journal tail after a mutation marker:
  `RecoveryRequired`; and
- verified completion with a missing history index: rebuild only the index from
  the immutable terminal summary.

Do not infer that an operation is safe to repeat.

### WPF experience

Add a dedicated execution workspace rather than expanding the Milestone 6 plan
workspace with business logic.

The execution workspace provides:

- selection of one approved current plan;
- an external backup-folder picker;
- Calibre discovery and compatibility result;
- preflight issues and capability mapping;
- ordered rows for verified no-ops, additions/replacements, and record removals;
- prominent warnings for replacement, deletion, lack of automated rollback, and
  unsafe forced shutdown;
- plan, approval, confirmation, executable, and backup identities;
- required “Calibre and other mutators are closed” acknowledgement;
- a deliberate final confirmation control;
- phase and per-operation progress;
- Cancel before mutation and Safe Stop after mutation begins;
- a result that distinguishes completed, failed-before-mutation, partial,
  verification-failed, and recovery-required states;
- clickable external backup/journal locations where platform-safe; and
- durable execution-history summaries.

Disable competing scan, plan creation/approval, and execution actions for the
same library while the local execution owns the lease. The cross-process lease,
not only UI state, enforces exclusivity.

Do not expose rollback, repair, resume, arbitrary commands, or bulk execution.

## Files expected to change

The exact names may be refined while preserving these boundaries.

### Documentation

- `docs/adr/0007-safe-calibre-command-execution.md`
- `docs/architecture.md`
- `docs/domain-model.md`
- `docs/safety-and-rollback.md`
- `docs/test-strategy.md`
- `docs/roadmap.md`
- this execution plan, including progress and final outcome

### Domain

- `src/CalibreLibraryCleaner.Domain/Executions/CleanupExecution.cs`
- `src/CalibreLibraryCleaner.Domain/Executions/CleanupExecutionState.cs`
- `src/CalibreLibraryCleaner.Domain/Executions/ExecutionOperation.cs`
- `src/CalibreLibraryCleaner.Domain/Executions/ExecutionOperationGraph.cs`
- `src/CalibreLibraryCleaner.Domain/Executions/ExecutionCapabilityPolicy.cs`
- `src/CalibreLibraryCleaner.Domain/Executions/ExecutionVerification.cs`
- focused execution identifiers, digests, failure, and recovery value types

Domain types must not reference `Process`, `ProcessStartInfo`, filesystem,
SQLite, WPF, or Calibre-specific implementation types.

### Application

- `src/CalibreLibraryCleaner.Application/Abstractions/ICalibreToolDiscovery.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/ICalibreCommandGateway.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/ICleanupExecutionLease.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/IExecutionBackupStore.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/IExecutionJournalStore.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/IExecutionHistoryStore.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/ICleanupExecutionIdGenerator.cs`
- `src/CalibreLibraryCleaner.Application/Executions/PrepareCleanupExecutionUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Executions/ExecuteApprovedCleanupPlanUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Executions/ReconcileExecutionHistoryUseCase.cs`
- execution request/result, progress, typed command, backup, and verification
  contracts

### Infrastructure

- `src/CalibreLibraryCleaner.Infrastructure/Calibre/CalibreToolDiscovery.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Calibre/CalibreCompatibilityProfile.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Calibre/CalibreCommandGateway.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Execution/FileCleanupExecutionLease.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Execution/FileExecutionBackupStore.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Execution/JsonLinesExecutionJournalStore.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Execution/FileExecutionHistoryStore.cs`
- dependency-injection registration updates

Prefer cohesive folders and split files if the implementation would otherwise
become difficult to review. Do not add a generic process runner to the public
Application surface.

### WPF

- `src/CalibreLibraryCleaner.Wpf/ViewModels/CleanupExecutionWorkspaceViewModel.cs`
- focused execution operation/history row view models
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml`
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml.cs` only for view concerns
- `src/CalibreLibraryCleaner.Wpf/App.xaml.cs` composition updates
- backup-folder selection and explicit confirmation view abstractions

### Tests

- Domain execution lifecycle, graph, capability, and verification tests
- Application preparation, execution, cancellation, failure, and ordering tests
- Infrastructure process, discovery, backup, lease, journal, and history tests
- architecture and safety invariant tests
- WPF execution workspace tests
- an opt-in real-Calibre compatibility test project or clearly isolated test
  collection using only a temporary, test-owned library

## Safety considerations

The following are release-blocking invariants:

1. No production code issues an SQLite write statement or write pragma against a
   Calibre database.
2. Every repository-owned SQLite connection used for scans or verification is
   opened read-only.
3. No repository filesystem API writes, moves, renames, overwrites, or deletes a
   path inside the Calibre library.
4. Every library mutation passes through the typed Calibre command gateway.
5. The gateway invokes one canonical executable directly with `ArgumentList`
   and never uses a shell.
6. No mutation can start without a held lease, unchanged approved plan,
   compatible hashed executable, two current successful precondition scans, and
   a sealed verified backup.
7. The durable mutation marker is flushed before the first mutating process.
8. A running mutating process is never killed for cancellation.
9. Every successful command is followed by fresh read-only verification before
   its dependents run.
10. Any uncertainty after mutation begins stops further commands and produces
    `RecoveryRequired`.
11. Recovery artifacts are outside the library and are never deleted
    automatically.
12. Milestone 7 contains no automated rollback, repair, resume, or retry path.

Structured logs must use execution/operation IDs and issue codes. They must not
include book content, raw OPF, process output, or sensitive full paths by
default.

The executor should bound file-copy buffers, process-output capture, and all
concurrency. Calibre mutations are strictly serial. Hashing and scanning continue
to propagate cancellation only before the mutation boundary.

## Implementation steps

### 1. Record and accept the execution-boundary ADR

- Document the exact-version compatibility-profile policy.
- Record the typed `calibredb` boundary and forbidden commands.
- Record backup strategy, application lease, mutation marker, journal
  durability, verification, and post-mutation cancellation behavior.
- Confirm that rollback remains Milestone 8.
- Update affected architecture and safety documentation.

### 2. Add execution domain concepts

- Add execution IDs, states, terminal dispositions, operation kinds, statuses,
  dependencies, failure classifications, and recovery requirements.
- Add the closed V1 capability policy for `cleanup-plan/1.0`.
- Build and validate the deterministic operation graph.
- Add lifecycle transition guards and invariants.
- Add expected intermediate/final verification models.
- Unit-test invalid states before adding infrastructure.

### 3. Define narrow Application ports and contracts

- Define typed discovery, lease, backup, journal, history, and Calibre command
  ports.
- Define immutable requests/results and progress events.
- Keep raw process arguments, filesystem handles, and WPF types out of these
  contracts.
- Define stable issue codes for unsupported capabilities, stale plans,
  incompatible tools, backup failures, partial execution, verification failure,
  and recovery-required results.

### 4. Implement preparation and fresh preflight

- Load the approved plan without changing it.
- Validate/recompute all canonical identities.
- Acquire the library execution lease.
- Discover and validate the exact compatibility profile.
- Run the existing complete read-only scan and staleness policy.
- Build the operation graph and preflight baseline.
- Validate the backup destination and capacity.
- Persist a durable preflight result.
- Ensure every failure releases the lease after the terminal journal entry is
  flushed.

### 5. Implement complete backup and manifest verification

- Create the external execution bundle safely.
- Export each involved record using the typed, non-mutating Calibre export
  operation with `--dont-update-metadata`.
- Copy exact raw planned formats read-only to external storage.
- Detect missing OPF/cover, unknown payloads, and extra data.
- Reopen and hash every backup.
- Seal and flush the immutable manifest.
- Prove that no backup code can resolve a destination within the library.

### 6. Implement direct Calibre invocation and compatibility tests

- Implement exact 9.11.0 command mappings for export, format add/replace,
  non-permanent record removal, and optional read-only check.
- Use only direct `ProcessStartInfo.ArgumentList`.
- Capture and bound output asynchronously without deadlock.
- Record exact executable identity and process results.
- Implement safe cancellation only for non-mutating commands.
- Add fake executable tests that record argv exactly and simulate success,
  nonzero exit, timeout, long output, malformed output, and cancellation.
- Add the opt-in real-Calibre suite and do not enable the profile until it passes.

### 7. Implement write-ahead journal, history, and reconciliation

- Create the append-only, hash-chained journal.
- Flush state and command-intent entries at the required boundaries.
- Store output and scan attachments by digest.
- Write immutable terminal summaries and the separate history index.
- Reconcile incomplete or corrupt journals on startup without mutating the
  library.
- Block new execution when prior recovery is required.

### 8. Implement execution orchestration

- Repeat full preflight at the final mutation gate.
- Seal the deterministic operation order in the journal.
- Write and flush `MutationStarting`.
- Execute one typed command at a time.
- Run a full fresh read-only scan and exact intermediate verification after each
  command.
- Stop on the first command, scan, journal, or verification failure.
- Honor safe-stop only after the current mutation and its verification.
- Run and persist final verification.
- Release the lease only after the terminal durable outcome.

### 9. Add the WPF execution workspace

- Add single-plan selection and backup destination.
- Present immutable identities, compatibility, mapped operations, and warnings.
- Collect the explicit acknowledgements and confirmation.
- Show preflight/backup/execution/verification progress.
- Switch Cancel to Safe Stop at the mutation marker.
- Show partial/recovery-required outcomes and durable artifact locations.
- Add history and startup-reconciliation notices.
- Keep all decisions in the use cases and Domain policies.

### 10. Strengthen architecture and safety tests

- Replace obsolete broad bans with targeted rules that permit process use only
  in the Calibre Infrastructure boundary.
- Prove no direct database writes or library filesystem writes exist.
- Prove no shell, script, GUI automation, permanent removal, or forbidden
  Calibre command is reachable.
- Prove WPF cannot construct raw commands or bypass confirmation.
- Prove execution remains single-plan and serial.

### 11. Verify, review, and update this plan

- Run formatting, build, and all tests.
- Run the real-Calibre compatibility suite against an isolated temporary library
  with the exact supported executable.
- Inspect the complete diff and generated recovery artifacts.
- Manually exercise success, cancel-before-mutation, safe-stop-after-one-command,
  command failure, verification failure, and crash-reconciliation flows using
  test-owned libraries only.
- Record actual commands and outcomes in this plan.
- Update roadmap status only after all acceptance criteria pass.

## Tests

### Domain tests

- Every allowed and forbidden execution lifecycle transition.
- Backup verification required before `ReadyToExecute`.
- Mutation marker required before `Executing`.
- Completion impossible without all operations verified and final verification.
- Recovery disposition for every post-marker non-completion state.
- Stable operation IDs and deterministic topological ordering.
- Addition/replacement dependencies before all record removals.
- A source removal blocked until all its contributions are verified.
- Closed capability mapping for every current Milestone 6 operation shape.
- Cross-record metadata, cover replacement, target-format removal, extra data,
  unknown plan schema/policy, and ambiguous operations rejected.
- Exact digest/approval/confirmation binding.
- Invalid input and duplicate/conflicting operations rejected.

### Application tests

- Approved, unchanged, current plan prepares successfully.
- Draft, blocked, stale, revoked, malformed, imported-but-unconfirmed, or
  digest-mismatched plan never reaches backup or mutation.
- A new full scan is always used instead of a displayed or cached snapshot.
- Every record, format, observation, cover, and metadata precondition mismatch
  fails closed.
- Incompatible/missing/changed Calibre executable blocks execution.
- Lease contention and prior recovery-required history block execution.
- Backup precedes the second fresh scan and every mutation.
- Missing, corrupt, incomplete, aliased, in-library, or insufficient-space
  backup fails before mutation.
- Extra-data detection blocks the V1 plan.
- The second scan catches a change that occurred during backup.
- Plan/tool/manifest changes at the per-command gate stop execution.
- Target-retained formats are verified no-ops.
- Each off-target selected format is added from its verified backup, not its live
  source.
- Replacement is performed only when explicitly planned.
- Multiple source contributions are serialized and individually verified.
- No record removal begins until the complete final target format set verifies.
- Source records are removed one at a time and verified absent.
- Nonzero exit, missing expected bytes, unexpected target metadata change,
  source change, scan failure, or journal failure stops later commands.
- Cancellation before the marker yields `CancelledBeforeMutation`.
- Safe stop after the marker waits for the process, verifies it, starts no next
  command, and yields `RecoveryRequired` when incomplete.
- No automatic retry, resume, repair, or rollback occurs.
- Successful final verification is required for `Completed`.
- CancellationToken propagation is tested for every pre-mutation async path.

### Infrastructure tests

- Executable discovery canonicalizes paths, rejects directories/reparse
  ambiguity, obtains version, and hashes the executable.
- Only exact enabled versions are accepted.
- A fake executable proves exact argv tokenization for each typed command,
  including spaces and hostile-looking path characters.
- `UseShellExecute` is false and no shell executable or script is invoked.
- stdout/stderr are drained concurrently, bounded, and stored as hashed
  attachments.
- Nonzero exit, start failure, timeout, very large output, malformed version,
  and cancellation are represented deterministically.
- A running mutating fake process is never killed after safe-stop.
- Backup path canonicalization rejects the library, descendants, ancestors used
  as ambiguous destinations, aliases, and reparse points.
- Backup uses create-new semantics, streams data, verifies source and destination
  hashes, and detects a source change during copy.
- Missing cover/OPF, unknown payload, extra data, and low/unknown free space
  fail closed.
- No test writes to the source test library except through the fake or real
  Calibre gateway.
- Lease contention is proven across processes; crash releases the OS handle.
- Journal entries are append-only, ordered, hash-chained, and flushed before
  fake commands observe their start.
- Truncated/corrupt journal reconciliation produces the correct pre/post-marker
  classification.
- History-index failure cannot erase or change the primary terminal journal.

### Real-Calibre compatibility tests

Gate these tests behind an explicit environment variable such as
`CALIBRE_TEST_EXE`. Never point them at a user library.

For exact Calibre 9.11.0, create a disposable library using supported Calibre
tooling and verify:

- executable version and identity discovery;
- export with OPF, cover, all formats, and `--dont-update-metadata`;
- add of a new target format from an external backup;
- replacement of an existing target format;
- non-permanent removal of one source record;
- scanner visibility and exact SHA-256 after every mutation;
- metadata preservation;
- optional `check_library --csv` baseline/final comparison; and
- recovery artifacts sufficient to describe every changed record and file.

The compatibility profile stays disabled if any test is skipped in release
validation or fails. Local 7.14 results must not be used to claim 9.11.0 support.

### Architecture and safety tests

- Domain has no project dependency and no forbidden implementation types.
- Application depends only on Domain.
- Process APIs occur only in the approved Infrastructure Calibre boundary.
- SQLite write verbs, write pragmas, and mutable SQLite open modes are absent
  from production execution code.
- Library-scoped filesystem writes/moves/deletes are structurally prohibited.
- Forbidden commands/options and shell executables do not occur in the command
  profile.
- WPF references Infrastructure only in composition and contains no operation
  mapping or execution decisions.
- There is no Milestone 7 rollback/resume/bulk-execution API.

### WPF tests

- Only one plan can be selected.
- Execute stays disabled until compatible tool, valid preflight, external backup,
  acknowledgements, and final confirmation exist.
- Plan or tool identity changes invalidate confirmation.
- Replacement and record-removal consequences are visible.
- Phase and operation progress are deterministic.
- Cancel is available only before the marker; Safe Stop is shown afterward.
- Partial and verification-failed results prominently show
  `RecoveryRequired` and artifact paths.
- Imported approval is labeled informational until local confirmation.
- Competing actions are disabled while the lease is held.
- No WPF handler directly starts a process or performs a mutation decision.

## Verification commands

Run from the repository root:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```

Run the exact-version compatibility suite separately with an explicitly supplied
test executable and test-owned temporary directory. The final command will be
recorded here when the test project and environment-variable contract exist; do
not invent it in advance.

Also review:

```powershell
git diff --check
git status --short
git diff --stat
git diff
```

Search the final production diff for direct database writes, mutable library
filesystem operations, shells, scripts, forbidden Calibre subcommands/options,
`UseShellExecute = true`, and process killing. Treat findings as release
blockers, not as a substitute for structural tests.

## Acceptance criteria

- Exactly one approved cleanup plan can be executed at a time.
- The plan body, canonical digest, definition digest, approval, and local
  confirmation remain unchanged through every mutation gate.
- A held application lease covers preflight through durable terminal outcome.
- Preflight and the final mutation gate use fresh complete read-only scans.
- Every current plan operation is either mapped by the capability matrix or
  blocks with a clear issue.
- Only an exact, integration-tested Calibre compatibility profile is enabled.
- Complete external backups are independently hash-verified before mutation.
- Every mutation uses a fixed typed direct-Calibre command and verified external
  input where applicable.
- Format additions/replacements complete and verify before source removals.
- Every command is serial and followed by fresh read-only verification.
- Final expected library state is fully verified before `Completed`.
- Journal and audit history survive success, failure, cancellation, and crash
  scenarios.
- Cancellation before mutation is clean; after mutation begins, only safe stop
  between verified operations is offered.
- Any uncertain or partial post-mutation state is `RecoveryRequired`.
- No direct database/library filesystem mutation, shell, GUI automation,
  automatic retry/resume, repair, or rollback exists.
- All standard tests and the exact-version real-Calibre compatibility suite pass.

## Risks

- **External writer race:** the application lease cannot exclude Calibre GUI or
  third-party tools. Mitigate with explicit acknowledgement, two pre-mutation
  scans, per-command gates, Calibre's own command behavior, and verification
  after every command. Do not claim global exclusivity.
- **Command behavior changes across versions:** exact version profiles and gated
  real-Calibre tests deliberately trade broad compatibility for safety.
- **Backup completeness:** Calibre extra data is not modeled by V1. Detecting it
  blocks execution rather than pretending the backup makes deletion safe.
- **Crash during command:** the command outcome may be unknowable. A flushed
  mutation marker and write-ahead command intent ensure restart reports
  `RecoveryRequired` without repeating the command.
- **Durability differences by filesystem:** flush/write-through semantics and
  free-space reporting vary. Unsupported or unverifiable destinations fail
  closed.
- **Long-running mutation:** killing Calibre could worsen uncertainty. Keep the
  UI responsive, display elapsed time, and offer only a latched safe-stop
  request.
- **Large libraries:** full scans after every command are intentionally
  conservative and may be slow. Do not optimize to partial scans in this
  milestone without separate evidence and a plan update.
- **Recycle/trash behavior:** non-permanent `remove` is safer than permanent
  deletion but is not the Milestone 7 rollback mechanism. The external verified
  backup remains mandatory.
- **Sensitive audit data:** recovery needs paths and metadata that normal logs
  should not expose. Keep detailed attachments local, hashed, and outside
  routine structured logs.
- **Version availability:** the planning machine has 7.14, not the initial 9.11.0
  profile. Implementation cannot claim the profile is enabled until the
  real-Calibre suite is run with 9.11.0.

## Unresolved questions

These questions must be resolved in the Milestone 7 ADR or during implementation
without broadening scope:

- Which platform-specific flush primitive provides the strongest practical
  durability guarantee for journal and manifest files on every supported
  filesystem, and which destinations must be rejected?
- Should `check_library --csv` be mandatory for the initial 9.11.0 profile or
  supplementary? It must remain read-only and cannot replace snapshot
  verification.
- Which narrowly defined environment variables, if any, must be inherited by
  `calibredb` for a supported Windows installation?
- What minimum free-space margin is required beyond measured backup input and
  bounded audit-output estimates?
- How should the WPF prevent ordinary application shutdown while a mutating
  process is still running while still handling forced OS termination honestly?

None of these questions authorizes a direct database/filesystem workaround,
shell invocation, broader version range, or rollback implementation.

## Progress

Implementation started on 2026-07-19. The pre-change baseline passed all 288
tests. The Domain, Application, Infrastructure, WPF, and automated-test
increments are implemented; the optional exact-Calibre integration suite
remains separately gated and must never use a user library.

Safety-critical post-implementation review on 2026-07-24 identified and
completed a focused hardening increment before disposable-library
qualification:

- bind local confirmation and durable audit state to the canonical library root
  and deterministic operation graph, not only the library UUID and plan digest;
- repeat complete semantic state verification immediately before every
  mutating command and reconfirm lease ownership at that boundary;
- persist an application-local recovery guard before the journal mutation
  marker so a crash cannot be bypassed by selecting a different backup parent;
- require the terminal summary to agree with the final hash-chained journal
  entry before reconciliation treats a prior mutation as completed;
- keep verified executable and format-backup handles open through process
  execution, preventing path substitution after hashing;
- reject reparse points during backup availability checks and avoid recursive
  traversal of exported extra-data directories; and
- give Calibre a fixed minimal environment rather than inheriting arbitrary
  parent-process variables.

The implementation and safety-hardening corrections were committed in
`607a829` (`Implement Milestone 7 safe Calibre execution`). Repository
line-ending policy and normalization were committed afterward; the handoff
baseline is `12871b7`. The exact-version profile remains disabled until the
opt-in Calibre 9.11.0 suite passes against a caller-marked disposable test root.

- [x] Read root and nested agent instructions and `PLANS.md`.
- [x] Read all requested product, requirements, architecture, domain, algorithm,
  safety, testing, roadmap, and workflow documentation.
- [x] Read accepted ADRs `0001` through `0007`.
- [x] Read completed Milestone 0 through Milestone 6 execution plans.
- [x] Inspect current production implementation, tests, project dependencies,
  and working-tree state.
- [x] Inspect installed Calibre executables and relevant local command help.
- [x] Inspect current official Calibre command documentation and release
  version.
- [x] Define the V1 capability matrix and fail-closed unsupported operations.
- [x] Create the Milestone 7 execution plan.
- [x] Accept ADR 0007 for the Milestone 7 execution boundary.
- [x] Implement Domain and Application execution behavior.
- [x] Implement Infrastructure lease, backup, process, journal, and history
  boundaries.
- [x] Implement the WPF execution workspace.
- [x] Complete the safety-critical hardening increment: canonical root/graph
  confirmation binding, per-command full preflight, application-local recovery
  guard, terminal journal/summary agreement, executable and backup-file
  substitution resistance, reparse-point hardening, minimal child-process
  environment, and fail-closed cover handling.
- [x] Pass Domain, Application, controlled-executable Infrastructure,
  architecture, and WPF tests.
- [ ] Run the opt-in exact-version real-Calibre test. It is implemented but was
  skipped because `CALIBRE_TEST_EXE` and `CALIBRE_TEST_ROOT` were not supplied;
  the runtime profile therefore remains disabled by default.
- [x] Complete diff and safety review.
- [x] Create `docs/handoffs/milestone-7-handoff.md` with the self-contained
  implementation state and continuation instructions.

Current verification on 2026-07-24:

- an isolated `dotnet restore --configfile .nuget-verification.config`
  using only `https://api.nuget.org/v3/index.json` succeeded;
- `dotnet build --no-restore` then succeeded with zero warnings and errors;
- `dotnet test --no-build` passed 344 tests, failed zero, and skipped the one
  opt-in real-Calibre test;
- `dotnet format style --verify-no-changes --no-restore` and
  `dotnet format analyzers --verify-no-changes --no-restore` succeeded;
- the unisolated build fails with `NU1507` because this machine contributes nine
  global NuGet feeds while central package management and warnings-as-errors are
  enabled; and
- full whitespace verification remains blocked in this checkout because
  tracked files declared as CRLF are physically present as LF or mixed endings.
  The Git index is normalized and a fresh local clone was verified to apply the
  attributes correctly, so this is a checkout/formatter issue rather than a
  Milestone 7 semantic test failure.

## Final outcome

Milestone 7 is implemented as a single-plan, serial, fail-closed execution
slice. ADR 0007 records the accepted boundary. Domain now owns the closed V1
operation graph, lifecycle, verified-backup values, semantic verification, and
recovery invariants. Application owns preparation, two fresh pre-mutation
scans, plan/approval/hash revalidation, backup sequencing, per-command gates,
constructive-before-destructive ordering, final destructive confirmation,
safe-stop behavior, verification after every command, and terminal
classification. Infrastructure owns the physical lease, external backup
bundle, deterministic verified manifest, exact tool discovery, fixed typed
command gateway, direct no-shell runner, hash-chained journal, history, and
crash reconciliation. WPF provides one-plan review, exact effects, two safety
acknowledgements, initial and destructive confirmations, progress, safe stop,
artifact locations, and accurate terminal/history presentation.

The only candidate compatibility profile is Windows Calibre 9.11.0 with:

| Declarative intention | Status | Mapping |
| --- | --- | --- |
| Preserve target metadata | Supported verified no-op | Fresh scans must match the approved target representation. |
| Preserve a present cover | Unsupported in V1 | Cover presence is modeled, but cover bytes are not; block before backup or mutation. |
| Preserve an existing target format | Supported verified no-op | Exact size/SHA-256 and inventory verification; no command. |
| Add an off-target retained format | Supported | `calibredb --with-library <library> add_format <target-id> <verified-external-backup>` |
| Replace an explicitly reviewed target format | Supported | The same `add_format` mapping, only when the plan describes the existing target and selected replacement. |
| Combine formats from several source records | Supported | Serial `add_format` commands, each freshly verified. |
| Remove formats with a redundant source record | Supported as a dependency effect | No standalone format deletion; non-permanent record removal occurs last. |
| Remove a redundant source record | Supported | `calibredb --with-library <library> remove <source-id>` with no `--permanent`. |
| Cross-record metadata/cover transfer, standalone target-format removal, extra-data mutation, ambiguous/unknown shapes | Unsupported | Blocks before backup or mutation with a stable issue. |

The profile requires the exact executable path, file hash, product version,
capability-profile ID, and successful probes for the documented global,
`export`, `add_format`, and `remove` help. Configuration cannot widen the
version. Because the real-Calibre compatibility test was not run in this
environment, `IsValidatedCompatibilityProfileEnabled` defaults to `false`;
the WPF application fails closed with
`EXECUTION.CALIBRE_PROFILE_NOT_VALIDATED`. Controlled executable tests validate
the complete direct-process mechanics but do not qualify real Calibre behavior.

Before mutation, the executor holds a cross-process `FileShare.None` lease keyed
by canonical library root and UUID outside the library; rejects unsafe backup
and journal paths; reconciles prior journals/history; freshly scans and
revalidates every affected record, format, hash, observation, metadata state,
inventory, plan digest, approval, and capability; creates Calibre exports plus
read-only raw copies of all affected formats into a create-new external bundle;
and independently reopens/hashes every mandatory OPF, cover, format, plan,
identity, and managed-state artifact. Missing, changed, unknown, extra-data,
low-space, aliased, or incomplete evidence blocks mutation.

All commands use one canonical executable through
`ProcessStartInfo.UseShellExecute = false` and `ArgumentList`; no shell, script,
GUI automation, elevation, direct SQLite write, or managed-library filesystem
write exists. Mutating processes have no timeout and ignore cancellation after
launch, so they are never killed. Cancellation before the mutation marker stops
immediately. Afterwards it is a latched safe-stop request honored only after the
active command and fresh verification, with an incomplete plan classified
`RecoveryRequired`. Record removals are serial and last. A zero exit code never
advances the graph until semantic verification passes.

The external JSON-lines journal is create-new, single-writer, versioned,
hash-chained, and flushed to disk at mutation/operation boundaries. It records
plan/library identity, lifecycle, mapped commands, sanitized bounded output,
full issue evidence, verification outcomes, cancellation, and terminal state.
Corrupt or incomplete post-marker journals block later execution as recovery
required; the executor never resumes, retries a mutation, repairs, rolls back,
or deletes recovery artifacts. A separate history index cannot replace the
primary journal, and history failure is reported without hiding the journal.

Final verification uses another complete read-only scan and requires the target
metadata, cover, inventory, selected hashes, planned absences, unaffected
library digest, and library readability to match before `Completed` is legal.
Any command, scan, journal, backup, intermediate, destructive, or final
uncertainty after the mutation marker becomes durable `RecoveryRequired`.

Verification performed on 2026-07-19:

- `dotnet restore` succeeded.
- `dotnet build --no-restore` succeeded with zero warnings and zero errors.
- `dotnet test --no-build` succeeded with 334 passed, zero failed, and one
  skipped opt-in real-Calibre test.
- `dotnet format --verify-no-changes` succeeded.
- `git diff --check` succeeded.
- Repository searches found no SQLite write statements or mutable open modes,
  no shell invocation, no in-library filesystem mutation, no forbidden command
  in the gateway, and no rollback/resume/bulk API. The only production
  `Process.Kill` is structurally limited to cancellable non-mutating probes and
  exports; mutating commands pass the non-terminating mode and are covered by a
  controlled long-running-process test. The only execution `File.Move` publishes
  an external create-new terminal summary inside the recovery bundle.

No packages were added. A framework-only controlled `calibredb.exe` test helper
project and an opt-in real-Calibre test were added. Optional
`check_library --csv` was not implemented because the approved plan made it
supplementary and semantic full-scan verification is authoritative. Manual
real-Calibre success/failure/crash exercises were not run because the available
Calibre is 7.14 rather than 9.11.0. That unqualified external-tool behavior is
the remaining release risk and is why the candidate profile stays disabled;
automatic rollback remains wholly in Milestone 8.

Safety-hardening verification performed on 2026-07-24:

- local confirmation now binds the canonical library root and deterministic
  operation graph;
- every command gate repeats the complete fresh read-only scan and all identity,
  lease, plan, tool, backup, and confirmation checks;
- an atomic application-local recovery guard precedes the journal mutation
  marker, while reconciliation requires a matching immutable terminal summary;
- executable and selected-format backup handles stay locked from verification
  through process completion, process environment is allow-listed, and external
  storage rejects aliases and reparse points;
- cover-bearing plans fail closed until cover bytes are modeled;
- `dotnet build --no-restore` succeeded with zero warnings and errors after a
  single-source restore isolated the machine's unrelated global NuGet feeds;
- `dotnet test --no-build` passed 344 tests; the one opt-in real-Calibre test
  remained skipped because its disposable-library environment variables were
  not supplied.
