# Milestone 8: Verified Rollback and Recovery

## Objective

Add a safe, explicit, verified rollback and recovery workflow for one cleanup
execution produced by Milestone 7.

Recovery will restore the affected library as closely as Calibre safely permits
to its verified semantic pre-execution state. It will not blindly invert the
cleanup plan. It will reconcile:

1. the approved `cleanup-plan/1.0` body and canonical content digest;
2. the verified original execution backup and manifest;
3. the durable operation boundaries in the hash-chained execution journal; and
4. a new complete, read-only scan of the actual current library.

The reconciled result will be captured in a new immutable, versioned recovery
plan. The user must review and explicitly approve that plan before any recovery
mutation. Immediately before mutation, the application will acquire the shared
library-mutation lease, repeat all eligibility and staleness checks, create and
independently verify a second-generation backup of the current affected state,
and repeat the current-state comparison.

Recovery will use only typed, qualified Calibre commands through the existing
direct process boundary. Constructive restoration and its semantic verification
will precede any destructive cleanup of post-execution state. A successful
process exit will never be treated as successful recovery without a fresh,
read-only semantic verification.

Milestone 8 is successful only when it can durably distinguish:

- no recovery mutation occurred;
- constructive restoration partially completed;
- destructive recovery started;
- semantic pre-state was restored;
- semantic pre-state was restored with different Calibre record IDs;
- unexpected post-execution content was preserved;
- final semantic verification failed; and
- manual intervention is still required.

No recovery path writes directly to `metadata.db`, writes into a
Calibre-managed folder through filesystem APIs, invokes an arbitrary shell,
automates or terminates the Calibre GUI, automatically starts after a cleanup
failure, automatically resumes, blindly retries a mutation, or launches a
rollback of a failed rollback.

## Scope

Milestone 8 includes:

- recovery of exactly one selected Milestone 7 execution at a time;
- strict loading and integrity validation of its approved cleanup plan,
  canonical cleanup-plan digest, execution journal, terminal summary when
  present, original backup manifest, and original backup artifacts;
- compatibility validation for every source artifact schema, application
  version, journal model, backup model, and exact Calibre capability profile;
- deterministic recovery eligibility with blocking, acknowledgement-warning,
  and informational severities;
- a fresh, complete, read-only library scan before recovery-plan generation;
- three-way reconciliation of verified pre-state, durable execution progress,
  and actual current state;
- an immutable, versioned declarative recovery plan and canonical body digest;
- explicit local approval and revocation of the recovery plan;
- staleness detection whenever a source artifact, capability, library identity,
  current-state fingerprint, operation body, or preservation decision changes;
- semantic record identity independent of Calibre's numeric record IDs;
- record-ID mapping when Calibre creates a replacement record with a new ID;
- conservative preservation or blocking of unexpected post-execution data;
- a mandatory external current-state safety backup for every affected current
  record and every additional record selected by reconciliation;
- an independently verified current-state backup manifest and immutable backup
  chain linking the original cleanup backup to the recovery execution;
- an expanded, exact-version Calibre recovery capability profile;
- typed command mappings for only capabilities that have been documented,
  controlled-boundary tested, and qualified against disposable real libraries;
- constructive restoration, an intermediate verification barrier, an explicit
  destructive-recovery gate, destructive recovery last, and final verification;
- a shared cross-process library-mutation lease that excludes cleanup and
  recovery from one another;
- a versioned, hash-chained, durable recovery journal and terminal summary;
- safe-boundary cancellation;
- conservative failure, partial-recovery, idempotency, and continuation rules;
- append-only recovery history and resolution links without rewriting original
  Milestone 7 journals or backups;
- incremental WPF recovery review, approval, backup, execution, cancellation,
  verification, ID-mapping, and export workflow; and
- unit, integration, architecture, safety, process, backup, reconciliation,
  lifecycle, verification, and UI tests.

### Supported recovery scenarios

Supported source executions include:

- a completed execution the user explicitly chooses to undo;
- a partially applied constructive execution;
- a failure after one or more destructive operations;
- an execution whose final verification failed;
- an interrupted or crashed execution with a valid, known last durable journal
  boundary;
- recognized metadata mutations made through a qualified Calibre capability;
- formats added to or replaced on a target;
- formats removed through a recognized supported operation;
- records removed after verified backup;
- records created through a recognized supported operation; and
- cover changes only when both exact backup bytes and the exact Calibre restore
  mapping are available and qualified.

This list describes the Milestone 8 recovery model. A particular recovery plan
remains blocked if its required exact-version capability is not enabled. The
current Milestone 7 Windows Calibre 9.11.0 profile remains disabled because its
opt-in real-Calibre qualification was not run; Milestone 8 must not reinterpret
that candidate profile as production proof.

### Unsupported or blocked recovery scenarios

These are recovery-domain cases rather than general out-of-scope product work.
They must produce blocking issues or explicit manual-intervention results:

- incomplete, tampered, substituted, or incompatible original backups;
- missing or contradictory durable journal evidence;
- a source command or state transition unknown to the compatibility model;
- a different current library identity;
- an affected current item that cannot be uniquely reconciled;
- a same-format, metadata, identifier, series, or cover conflict that would
  require silent overwrite of unexpected current data;
- a deleted source whose backup cannot be associated with one logical record;
- a recreated/created record that cannot be uniquely rediscovered;
- a required command not enabled in the exact Calibre capability profile;
- an operation that would require a direct database write or an in-library
  filesystem mutation;
- recovery storage that is unsafe, aliased, inside/around the library, or too
  small;
- an active cleanup/recovery lease or conflicting unresolved recovery chain;
- a current-state safety backup that cannot be completed and verified; and
- any continuation whose actual current state no longer matches the approved
  recovery-plan fingerprint.

## Out of scope

- Direct SQLite repair, any write to `metadata.db`, or use of database APIs as a
  recovery mutation mechanism.
- Manual reconstruction, copying into, replacement, renaming, moving, or
  deletion of files or folders within a Calibre-managed library.
- `calibredb restore_database`, `backup_metadata`, `embed_metadata`,
  `remove --permanent`, GUI automation, or GUI termination.
- Automatic rollback immediately after a Milestone 7 failure.
- A one-click rollback action on a failure notification.
- Automatic retry, automatic resume, or automatic rollback of a rollback.
- Treating an unknown destructive command result as though it had no effect.
- Bulk rollback, recovery queues, or recovery of multiple cleanup executions.
- Arbitrary historical versioning or general-purpose Calibre backup software.
- Recovery without a complete, verified original backup manifest.
- Recovery from an unknown application or artifact version unless an explicit
  compatibility entry has been implemented and tested.
- Recovery of unrelated user changes made after cleanup.
- Silent deletion or overwrite of unexpected post-execution content.
- Direct preservation of an old Calibre numeric record ID when Calibre cannot
  do so through a qualified supported command.
- Online metadata lookup, AI-assisted recovery, new AI behavior, new duplicate
  detection, new recommendation logic, PDF analysis changes, EPUB analysis
  changes, or new metadata acquisition.
- General Calibre library repair or corruption recovery.
- Importing arbitrary command lines, scripts, or user-supplied executable
  arguments.
- Discovering or mutating a user's default Calibre library in automated tests.
- Future roadmap items beyond Milestone 8.

## Relevant requirements

The following sources are authoritative for implementation:

- `AGENTS.md` and all nested `AGENTS.md` files;
- `PLANS.md`;
- `docs/product-vision.md`;
- `docs/functional-requirements.md`;
- `docs/architecture.md`;
- `docs/domain-model.md`;
- `docs/safety-and-rollback.md`;
- `docs/test-strategy.md`;
- Milestone 8 in `docs/roadmap.md`;
- `docs/workflows/implement-feature.md`;
- accepted ADRs 0001 through 0007;
- completed Milestone 0 through Milestone 7 execution plans; and
- `docs/handoffs/milestone-7-handoff.md`.

The governing constraints are:

- Calibre databases remain read-only during analysis and verification.
- All library mutations use qualified supported Calibre tooling.
- Recovery starts only from an explicitly approved immutable recovery plan.
- The original cleanup plan, original journal, original backup manifest, and
  every original backup artifact are verified before recovery planning and
  again immediately before recovery execution.
- Original Milestone 7 backup bundles are read-only recovery inputs and are
  never modified, upgraded in place, completed, repaired, or deleted.
- Every affected current record is backed up outside the library and that
  current-state backup is independently verified before the first recovery
  mutation.
- A fresh scan, not process output or the intended cleanup graph, determines the
  actual current state.
- Unexpected post-execution content is preserved or blocks automation.
- Constructive recovery is verified before destructive recovery is enabled.
- Final semantic verification is required before `Recovered` is legal.
- The Domain remains integration-free, Application depends only on Domain,
  Infrastructure owns concrete integrations, and WPF uses Application use
  cases.

The official
[Calibre 9.11.0 `calibredb` documentation](https://manual.calibre-ebook.com/generated/en/calibredb.html)
documents candidate commands for adding an empty record, adding or replacing a
format, removing a format, setting metadata, removing a record, exporting a
record, and machine-readable listing. Documentation is necessary but not
sufficient. Each candidate must also pass the closed command-mapping tests and
the opt-in exact-version real-Calibre suite before its profile flag is enabled.

## Existing implementation inspected

### Milestone 6 plan and semantic pre-state

The current `CleanupPlan` aggregate stores:

- `cleanup-plan/1.0` schema and policy versions;
- a deterministic canonical content digest over its immutable definition;
- source library UUID and schema version;
- exact involved record IDs;
- exact expected record metadata;
- exact format type, relative path, length, SHA-256 digest, and file
  observations;
- declarative format-retention, format-removal, and record-removal
  instructions;
- required backup identities;
- approval, revocation, validation, staleness, and lifecycle history.

`CleanupPlanDefinition`, `ExpectedLibraryState`, `ExpectedRecordState`, and
`ExpectedFormatState` provide the primary semantic pre-execution model.
`CleanupPlanContentDigestPolicy` recomputes the immutable body digest.
`CleanupPlanJsonSerializer` is strict, versioned, depth-bounded, collection
bounded, duplicate-property rejecting, and reconstructs the Domain aggregate
instead of trusting stored digest text.

The plan does not contain cover bytes. It models only cover presence. Metadata
is limited to the existing modeled fields; arbitrary custom columns and all
possible OPF fields are not proven recovery inputs.

### Milestone 7 execution model

`CleanupExecutionOperationGraphPolicy` translates a supported Milestone 6 plan
into deterministic preparation, constructive, and destructive operations.
Milestone 7 supports target format additions/replacements followed by
non-target record removal. It rejects cover-bearing plans, cross-record metadata
changes, standalone target-format removal, extra-data mutation, unapproved
plans, and unsupported schemas.

`CleanupExecution` owns a closed lifecycle and prevents destructive operations
from preceding constructive verification. `CleanupExecutionVerificationPolicy`
performs semantic comparison after every mutation and at final verification,
including exact format hashes and an unaffected-library digest.

`ExecuteApprovedCleanupPlanUseCase` currently:

- acquires one cleanup lease keyed by canonical root and library UUID;
- reconciles prior recovery-required history;
- discovers a fixed Calibre executable and candidate profile;
- performs fresh read-only scans and plan/staleness validation;
- creates and verifies the original external backup;
- repeats tool, plan, library, backup, lease, and confirmation gates;
- writes an application-local recovery guard before the mutation marker;
- appends durable journal events at operation and command boundaries;
- executes serial typed Calibre commands;
- performs a full read-only scan after every command;
- stops only at safe boundaries; and
- requires final semantic verification, journal completion, and terminal
  summary agreement.

There is deliberately no retry, resume, or rollback in this use case.

### Original backup

`FileExecutionBackupStore` creates `execution-{executionId}` outside the library.
The bundle contains the approved plan, tool identity, application identity,
local confirmation, preflight evidence, managed-state inventory, raw read-only
copies of affected formats, and `calibredb export` output including bounded OPF,
expected formats, and a cover when expected. Extra exported content fails
closed.

`execution-backup-manifest/1.0` entries bind artifact kind, safe relative path,
length, SHA-256 digest, backup requirement IDs, and optional record/format
identity. `VerifyAvailableAsync` reopens and hashes the listed entries and
requires a non-empty journal.

Important constraints for Milestone 8:

- `backup-manifest.json` describes the backup content but is not itself an entry
  in that manifest;
- the execution journal is not a backup-manifest entry;
- the original bundle must therefore be verified as a set of cross-linked but
  separately identified artifacts;
- original manifests and journals must not be rewritten to retrofit new fields;
- the current OPF can supply backup evidence only after strict parsing and
  cross-validation against the cleanup plan; and
- a coordinated actor with write access to every unkeyed hash artifact is
  outside the authenticity guarantee. Milestone 8 detects integrity,
  substitution, identity, and cross-link mismatches and restricts storage ACLs,
  but it will not claim cryptographic authorship for legacy Milestone 7
  artifacts.

### Journal, history, and durable boundaries

`JsonLinesExecutionJournalStore` writes
`cleanup-execution-journal/1.0` JSON lines with monotonically increasing
sequence numbers, previous-entry hashes, entry hashes, execution/plan/library
identity, application identity, event payloads, and immediate flushes. A
completed journal is accepted only with a matching create-new terminal summary.

The journal records operation start, mapped command and arguments, sanitized
bounded stdout/stderr, exit code, verification outcome, operation completion,
cancellation, mutation status, and terminal state. It can establish the last
known durable operation boundary. It does not store a full library snapshot.
A command-success event without the corresponding semantic verification and
operation-completion events is an uncertain result, not a completed operation.

`FileExecutionHistoryStore` stores a secondary per-execution index and a
pre-mutation recovery guard. It is not authoritative over the journal.
Milestone 8 needs a strict, public, bounded reader that returns a verified
immutable journal projection; it must not depend on private DTOs or infer state
from a chat or in-memory object.

### Lease and storage boundaries

`FileCleanupExecutionLease` uses an application-owned file outside the library
and `FileShare.None`. Its key is the SHA-256 digest of the uppercase canonical
root plus library UUID. As currently typed, it excludes only another caller of
the cleanup lease abstraction. Milestone 8 must generalize this exact shared key
to a library-mutation lease used by both cleanup and recovery so the two paths
cannot run concurrently.

`ExecutionPathGuard` rejects storage inside the library, storage that contains
the library, reparse points, and unsafe contained paths. Recovery storage must
reuse and extend this policy.

### Calibre command boundary

`DirectCalibreProcessRunner` is the only production `ProcessStartInfo` boundary.
It uses a canonical executable, `UseShellExecute = false`, `ArgumentList`, a
minimal environment, bounded and sanitized output, and a closed cancellation
policy. Mutation processes are not killed.

`CalibreCommandGateway` currently exposes only typed export,
add-or-replace-format, and non-permanent record removal. Its exact Windows
Calibre 9.11.0 profile is intentionally disabled because the opt-in
real-Calibre qualification was skipped. Existing controlled executable tests
prove command construction and process safety, not real-Calibre semantics.

### WPF and tests

`CleanupExecutionWorkspaceViewModel` supports Milestone 7 preparation,
acknowledgement, local confirmation, execution, safe-stop, result, and history.
History rows are not yet a recovery-selection workflow. The UI explicitly does
not offer rollback.

Existing tests cover cleanup execution graph ordering, lifecycle safety,
manifest coverage, backup sealing and tamper detection, external path guards,
cross-process lease exclusion, journal recovery classification, exact command
arguments, no shell execution, non-termination of mutations, cancellation
boundaries, per-command fresh scans, semantic verification, recovery-required
outcomes, architecture boundaries, and a disabled opt-in disposable real
Calibre suite. At the Milestone 7 handoff, 344 tests passed and the one
real-Calibre test remained skipped.

## Proposed design

### Recovery authority and trust order

Recovery decisions use the following authority order:

1. A strictly reconstructed and digest-verified approved cleanup-plan body
   defines the intended semantic pre-state.
2. A strictly reconstructed and independently rehashed original manifest and
   its artifacts prove what recovery bytes are available.
3. A valid, internally consistent journal chain and terminal summary, when
   required, identify commands attempted and operations durably verified.
4. A fresh full read-only scan is authoritative for the actual present library.
5. A newly verified current-state backup is authoritative for preservation of
   state immediately before recovery mutation.

The execution graph alone never authorizes a reverse operation. The journal
alone never proves current state. A zero exit code alone never proves a command
effect. Filenames, timestamps, and file sizes alone never identify equivalent
content.

### Recovery eligibility

`RecoveryEligibilityResult` contains deterministically ordered
`RecoveryIssue` values with one of three severities:

- `Blocking`: no plan approval or recovery mutation is allowed.
- `AcknowledgementRequired`: recovery may proceed only when the exact warning
  is included in the immutable plan and explicitly acknowledged by approval.
- `Information`: a difference or outcome that does not require a decision.

Eligibility is valid only when all of these checks pass:

- the source cleanup plan exists, has a supported schema/model/policy, is
  reconstructable, and its recomputed canonical body digest matches every
  recorded reference;
- the execution journal exists, has a supported schema, has a valid complete
  hash chain, stays within size/count/depth limits, and belongs to the selected
  execution, plan, and library;
- a terminal summary is present and matches whenever the source journal claims
  a terminal completed state;
- the original backup manifest exists, has a supported schema, reconstructs
  successfully, has a valid internal digest, and belongs to the source
  execution, plan, and library;
- the journal proves that the original backup was verified before the original
  mutation marker;
- every backup item required by the completed or possibly applied operations is
  present, is a safe regular file beneath the original bundle, is not a
  symlink/reparse-point substitution, and still matches its recorded length and
  SHA-256 digest;
- the cleanup-plan ID/digest, execution ID, library UUID, journal identity,
  summary, manifest identity, tool identity, application identity, and bundle
  location agree;
- the source application and Calibre versions are in an explicit tested
  compatibility table;
- the selected current library UUID, schema version, and canonical local
  root identity match the source execution confirmation;
- the journal state and last durable operation boundary are known;
- each operation has a legal event sequence and recorded completion statuses do
  not contradict one another or their dependency graph;
- a fresh scan is complete and readable;
- reconciliation identifies every affected logical record and required backup
  item uniquely;
- no cleanup or recovery mutation lease is held for the library;
- no other unresolved recovery execution conflicts with the selected source
  execution;
- original and proposed recovery storage are outside the library, neither
  contains the library, and pass reparse-point and capacity checks; and
- every proposed mutating recovery operation maps to an enabled, exact-version,
  tested Calibre capability.

Recovery is blocked when:

- an original artifact is missing, incomplete, tampered, substituted, or
  identity-inconsistent;
- the original backup was not verified before original mutation;
- the journal chain is corrupt, contradictory, over bounds, or has an unknown
  durable state;
- an attempted command has an uncertain effect that cannot be reconciled
  semantically;
- the current library is a different library;
- the library or an affected item cannot be scanned completely;
- a required backup item cannot be uniquely associated with its expected
  record/format;
- current state differs in a way that would require silent loss of unexpected
  unique content;
- a required metadata field, cover behavior, record creation, record lookup,
  format operation, or removal behavior is not qualified;
- an exact source or current record cannot be identified without relying only
  on filename, timestamp, or size;
- an active shared lease or unresolved conflicting recovery exists;
- storage is within/around the library, aliased, unsafe, or too small; or
- recovery would require direct database or managed-library filesystem
  mutation.

Typical acknowledgement warnings include:

- a recreated record will have a different Calibre ID;
- a current post-execution variant will be preserved in a separate recovered
  record rather than overwritten;
- an explicitly requested cleanup-added format or cleanup-created record will
  be removed during the destructive phase;
- a supported metadata subset can be restored but unmodeled metadata will be
  left unchanged; and
- semantic recovery can succeed while physical paths, timestamps, and IDs
  differ.

Typical informational differences include:

- an operation was planned but never crossed a durable mutation boundary;
- an affected item is already semantically restored and needs no action;
- an unrelated record changed before recovery but is excluded and preserved;
  and
- an original numeric ID is unavailable but a unique semantic match exists.

### Source artifact reader

Add an Infrastructure reader behind an Application port that:

- opens an explicitly selected original bundle read-only;
- canonicalizes and validates every path before opening;
- rejects bundle and entry reparse points, links, traversal, rooted relative
  paths, alternate source substitution, duplicate properties, unknown required
  fields, deeply nested JSON, excessive entries, excessive lines, and oversized
  files;
- reconstructs the cleanup plan and original manifest using versioned readers;
- verifies the cleanup-plan digest and manifest digest;
- reads the journal line by line under hard line, event-count, and total-size
  bounds;
- verifies sequence, previous hash, event hash, cross-artifact identities, event
  transition legality, operation identities, and terminal-summary agreement;
- recomputes independent file identities for the manifest and journal;
- rehashes every required original backup file while holding safe read handles;
  and
- returns immutable Domain/Application projections, not JSON types or paths
  exposed to Domain.

The reader never repairs or normalizes the original bundle. An older artifact is
accepted only through an explicit compatibility adapter with tests.

### Three-way current-state reconciliation

`ICurrentStateReconciler` receives:

- the verified expected pre-state from the cleanup plan;
- verified backup artifacts and semantic metadata facts;
- the verified source operation graph and journal projection;
- the source execution's unaffected-library baseline;
- a fresh complete `RecoveryCurrentStateSnapshot`, built from the existing full
  `LibrarySnapshot` plus recovery-only exact cover/backup evidence for affected
  records; and
- the supported recovery capability profile.

It produces a versioned `CurrentStateReconciliation` containing:

- source input identities;
- current library identity;
- full current snapshot fingerprint;
- affected-state fingerprint;
- current unrelated-state fingerprint;
- ordered logical-record and format comparisons;
- durable journal classification for every source operation;
- candidate current and recovered record mappings;
- unexpected current content;
- preservation requirements;
- blocking, warning, and informational issues; and
- a deterministic reconciliation digest.

The full current fingerprint is a length-prefixed SHA-256 digest over the
versioned semantic scan model ordered by record ID, metadata field, format name,
relative path, size, exact format hash, cover state/hash where available,
observation facts, and findings. It does not depend on enumeration order. The
affected and unrelated fingerprints are also stored separately.

The existing `IExecutionLibraryScanner` remains the authoritative complete
library scan for identity, database metadata, format inventory/hashes, and
readability. Add `IRecoveryCurrentStateScanner` as a narrow Application port
that composes that scan with read-only, recovery-specific evidence such as
exact current cover bytes. It must not weaken or replace the full scan, and it
must not leak paths or filesystem types into Domain/Application.

The original unaffected baseline can reveal that unrelated state changed since
cleanup. Because recovery of unrelated user changes is out of scope, such a
difference is informational unless it compromises library identity or an
affected logical match. A second unrelated baseline from recovery preflight is
authoritative for proving that recovery itself did not change unrelated
records.

Each affected record and format receives one or more deterministic
classifications:

- `UnchangedFromPreState`;
- `MatchesExpectedPostState`;
- `CompletedAndMatchesJournalPostState`;
- `CompletedButCurrentStateDiffers`;
- `NotCompletedButCurrentStateChanged`;
- `CommandOutcomeUncertain`;
- `Missing`;
- `NewlyCreatedByExecution`;
- `ReplacedByExecution`;
- `AlreadyRestored`;
- `IndependentlyRecreated`;
- `IndependentlyModifiedAfterExecution`;
- `UnexpectedUniqueContent`;
- `DifferentCurrentRecordId`; or
- `Ambiguous`.

Classification rules include:

- A source operation is durably completed only when its journal event sequence
  includes command completion, successful semantic verification, and operation
  completion in a legal order.
- A command start or success without semantic verification is uncertain.
- A completed operation whose current state matches its expected post-state is
  recoverable from its actual present state.
- A completed operation whose current state differs is treated as a later edit
  or ambiguity; it is never blindly overwritten.
- A non-completed operation whose effect appears in the library is reconciled by
  exact semantic facts. If its effect cannot be uniquely proven, recovery is
  blocked or becomes manual.
- A missing original ID is not itself a failure. A different record is a
  semantic match only when identifiers, modeled metadata, exact backed-up
  format hashes, and provenance produce one unique candidate.
- Reuse of an old numeric ID by semantically different content never matches the
  old record and never authorizes mutation of the new occupant.
- Matching title/author, filename, timestamp, size, or record ID alone is
  insufficient.
- State that differs from both expected pre-state and expected journal post-state
  is unexpected and preserved or blocked.

### Recovery Domain model

Add a `Recoveries` Domain area with immutable values equivalent to:

- `RecoveryPlanId`;
- `RecoveryPlanSchemaVersion`;
- `RecoveryModelVersion`;
- `RecoveryPolicyVersion`;
- `ReconciliationModelVersion`;
- `RecoveryPlanArtifactRevision`;
- `RecoveryPlanContentDigest`;
- `RecoveryPlanState`;
- `RecoveryInputIdentity`;
- `RecoveryProvenance`;
- `RecoveryEligibilityResult`;
- `RecoveryIssue`;
- `RecoveryIssueSeverity`;
- `CurrentStateReconciliation`;
- `LogicalRecoveryRecordId`;
- `RecoveryRecordIdentity`;
- `RecoveryRecordIdMapping`;
- `RecoveryOperationId`;
- `RecoveryOperation`;
- `RecoveryOperationKind`;
- `RecoveryOperationPhase`;
- `RecoveryOperationDependency`;
- `RecoveryRiskLevel`;
- `RecoveryVerificationExpectation`;
- `ExpectedRecoveredState`;
- `PreservedContentExpectation`;
- `RecoveryBackupChain`;
- `RecoveryApproval`;
- `RecoveryRevocation`;
- `RecoveryPlanCompletion`;
- `RecoveryExecution`;
- `RecoveryExecutionState`;
- `RecoveryFailureClassification`; and
- `RecoveryVerificationResult`.

Domain values contain no filesystem, `Process`, SQLite, JSON, WPF, Calibre CLI,
logging, or dependency-injection types.

`RecoveryInputIdentity` binds:

- source cleanup-plan ID, schema, revision, and canonical body digest;
- source execution ID;
- source journal schema, independent file digest, final entry hash, and terminal
  summary digest when applicable;
- original backup-manifest schema, internal digest, and independent file digest;
- source application and Calibre tool/profile identities;
- current library UUID and schema;
- a local canonical-root identity digest;
- fresh full, affected, and unrelated state fingerprints;
- reconciliation schema and digest; and
- recovery capability-profile identity.

Absolute paths remain local execution/configuration data. The exported immutable
body uses safe identities and digests rather than authorizing a path by text.

### Recovery plan definition

The immutable recovery-plan body contains:

- source identities and provenance;
- the verified original backup-chain identity;
- current library identity and current-state fingerprints;
- ordered reconciled logical records and formats;
- ordered proposed operations and dependencies;
- explicit non-action and manual-intervention entries;
- every current item that will be preserved and how;
- every blocking issue, warning, and informational difference;
- warning acknowledgement requirements;
- the expected final semantic state;
- expected record-ID mappings where identity change is anticipated;
- constructive and destructive verification expectations;
- schema, model, reconciliation, policy, and capability-profile versions; and
- deterministic operation-graph and body digests.

Creation time, validation time, approval, revocation, lifecycle history,
execution progress, actual new Calibre IDs, and completion are in a mutable
lifecycle envelope whose transitions are validated by Domain policy. They are
excluded from the immutable body digest. The approved body itself never changes.

If a source artifact, current fingerprint, reconciliation fact, preservation
choice, capability profile, expected result, operation, dependency, issue, or
warning changes, the approved plan becomes `Stale` and a newly generated body
receives a new `RecoveryPlanId`. Approved bodies are never edited in place.

### Recovery plan lifecycle

Recovery-plan states:

- `Draft`: generated but not fully validated;
- `Blocked`: contains one or more blocking issues;
- `Valid`: deterministic validation passed and it can be reviewed;
- `Approved`: the exact body digest and warning acknowledgements were explicitly
  approved locally;
- `Stale`: a bound input or current-state fingerprint changed;
- `Revoked`: the user explicitly withdrew approval;
- `Completed`: a linked recovery execution reached verified `Recovered`.

Legal transitions:

| From | To | Condition |
| --- | --- | --- |
| `Draft` | `Blocked` | Deterministic validation found blocking issues. |
| `Draft` | `Valid` | Validation passed. |
| `Blocked` | `Draft` | A new revision is regenerated before approval. |
| `Valid` | `Approved` | Explicit approval binds digest and warnings. |
| `Valid` | `Blocked` | Revalidation finds a blocker. |
| `Valid` or `Approved` | `Stale` | Any bound semantic input changes. |
| `Valid` or `Approved` | `Revoked` | Explicit local revocation. |
| `Approved` | `Completed` | A linked journal proves final semantic recovery. |

`Blocked`, `Stale`, and `Revoked` bodies cannot execute. A failed or partial
recovery does not mark a plan `Completed`; its execution journal remains the
authoritative result.

### Immutable body and canonical hashing

Use a Domain-owned canonical digest policy rather than hashing incidental JSON
layout. It will:

- prefix every value with a field/type discriminator and length;
- normalize enum and version values to fixed ordinal-independent text;
- use UTC round-trip timestamps only where timestamps belong to the body;
- normalize format names to uppercase and digests to lowercase hexadecimal;
- sort records by logical recovery ID;
- sort formats by format, source record ID, and backup artifact identity;
- sort issues by severity, code, subject, and stable evidence;
- sort dependencies and operations topologically with stable tie-breakers;
- reject duplicate IDs or ambiguous ordering; and
- include explicit markers for null, absent, empty, and non-mutating entries.

The serializer emits deterministic UTF-8 JSON for human inspection and export,
but reconstruction and the Domain digest policy are authoritative. Deserialization
rejects duplicate properties, excessive depth/count/size, undefined enum values,
unknown required schema versions, and inconsistent digest/lifecycle fields.

`RecoveryApproval` binds the plan ID, artifact revision, canonical body digest,
source execution ID, current library/root identity, current-state fingerprint,
capability profile, every warning code requiring acknowledgement, and approval
time. A separate local execution confirmation also binds the chosen
current-state backup destination and destructive-operation digest.

### Recovery operation model

Declarative operation kinds:

- `CreateRecoveredRecord`;
- `RestoreMetadataFromBackup`;
- `RestoreCoverFromBackup`;
- `RestoreFormatFromBackup`;
- `RemoveFormatAddedByExecution`;
- `RemoveRecordCreatedByExecution`;
- `PreserveUnexpectedCurrentFormat`;
- `CreateRecoveryCopyInsteadOfOverwrite`;
- `NoActionAlreadyRestored`; and
- `ManualInterventionRequired`.

The first six are potentially dispatchable. Preservation, no-action, and manual
entries are explicit plan evidence and never become raw commands.

Every operation contains:

- stable operation ID;
- affected logical record;
- original, current, and proposed recovered Calibre IDs when known;
- original backup artifact identity, or an explicit `NotApplicable`;
- exact current-state expectation;
- target semantic result;
- dependency IDs;
- constructive, destructive, or non-mutating classification;
- verification expectation;
- reason and source journal evidence;
- risk level;
- preserved-current-content dependencies; and
- required qualified capability.

Classification is based on data loss, not a friendly command name:

- creating a record and adding a format to an empty slot are constructive;
- replacing an existing same-format payload is destructive because current
  bytes cease to be the active variant;
- overwriting metadata or a cover is destructive when current state differs;
- removing a format or record is destructive;
- creating a separate recovery copy is constructive; and
- no-action/preservation/manual entries are non-mutating.

Where same-format current and backup variants cannot coexist on one record, the
default is to create and verify a separate recovered record before considering
an approved replacement. If record creation is unavailable, the plan blocks.

### Record identity and ID mapping

`LogicalRecoveryRecordId` is derived deterministically from the source execution
ID and original record ID. It remains stable when the Calibre ID changes.

`RecoveryRecordIdentity` tracks:

- logical recovery ID;
- original Calibre record ID;
- current matched Calibre record ID, if any;
- recovered Calibre record ID, once created;
- modeled title/author fingerprint;
- identifiers;
- exact backed-up format set and hashes;
- original backup provenance; and
- relationship to a current separate or preserved record.

Record creation never requires the old numeric ID. After a qualified create
command, a fresh scan must identify exactly one new record by before/after
inventory, command result where machine-readable, and semantic facts. Ambiguous
creation is a safe-stop failure.

Every discovered mapping is appended durably to the recovery journal before a
dependent command. Final verification resolves expectations by logical identity.
A changed numeric ID is an informational recovery outcome, not an error, when
all semantic expectations pass. The WPF result and exports display both IDs.

### Unexpected post-execution content policy

Milestone 8 never silently removes or overwrites state that is neither verified
pre-state nor the recognized result of the source cleanup execution.

Policies:

- A new unique format type on an affected record is included in the current-state
  backup and left in the library. The plan contains
  `PreserveUnexpectedCurrentFormat`.
- A same-format payload whose hash differs from pre-state and expected post-state
  cannot be retained beside another payload of the same format on one record.
  The default is a separate recovered record/copy; otherwise automatic recovery
  is blocked.
- Independently edited metadata is preserved. A field-scoped automatic restore
  is allowed only for fields proven changed solely by the source execution and
  proven not independently edited. Otherwise create a separate recovered record
  or require manual selection.
- An independently changed cover is preserved. Cover replacement is blocked
  unless exact current and original cover bytes are backed up and the approved
  plan explicitly chooses a qualified safe result.
- An unrelated format, record, or metadata field is never removed merely because
  it was absent from the original cleanup plan.
- A target differing from both pre-state and journal post-state is ambiguous.
  The application cannot choose based on filename, timestamp, or size.
- User selection cannot bypass missing bytes, integrity, identity, lease, or
  capability blockers. It may choose only among precomputed safe alternatives.

Every preservation choice, separate-copy choice, and warning is part of the
immutable recovery body and final verification expectations.

### Original and current-state backup chain

The immutable chain is:

```text
verified Milestone 7 pre-cleanup backup
    -> selected cleanup execution and journal
    -> fresh reconciled current state
    -> verified Milestone 8 current-state backup
    -> recovery execution and journal
```

`RecoveryBackupChain` binds:

- source cleanup-plan ID and digest;
- source execution ID;
- source journal file digest and final entry hash;
- original manifest file digest and internal manifest digest;
- recovery plan ID and body digest;
- recovery execution ID;
- current-state manifest file and internal digests; and
- recovery journal identity.

Original bundle paths are opened read-only. Milestone 8 never places files in,
renames, replaces, or deletes anything from the Milestone 7 bundle. The new
bundle contains independently hashed copies of the source cleanup plan, source
journal, source terminal summary when present, and original manifest for
portable audit, plus references to the original canonical bundle. These are
new files under a new create-only recovery execution directory.

Retention is conservative: no automatic deletion or pruning is implemented in
Milestone 8. The UI reports both bundle locations and their relationship.

### Current-state safety backup

The current-state backup is mandatory for every current affected record,
semantic match, independently recreated candidate selected by the plan, and
record containing unexpected content that a recovery command could affect.

No recovery mutation starts until the backup store:

- validates the selected destination outside and not containing the library;
- rejects reparse points, aliasing, traversal, existing execution directories,
  and insufficient free space;
- creates a unique `recovery-{recoveryExecutionId}` directory with create-new
  semantics;
- copies only outward through read-only managed-file handles;
- uses qualified `calibredb export` for a second independent representation;
- captures all current formats, metadata OPF, exact cover bytes when present,
  current format hashes and observations, record inventory, current library
  fingerprint, and selected unexpected content;
- captures the approved recovery plan and local execution confirmation;
- creates new audit copies of the source cleanup plan, source journal, source
  terminal summary, and original manifest without touching the originals;
- writes a versioned current-state manifest;
- closes, reopens, reparses, and rehashes every item independently;
- cross-checks export contents against the fresh scan and expected inventory;
  and
- persists `CurrentStateBackupVerified` before the first mutation marker.

Proposed layout:

```text
recovery-{recoveryExecutionId}/
  source-artifacts/
    approved.cleanup-plan.json
    cleanup-execution.journal.jsonl
    cleanup-execution-summary.json
    original-backup-manifest.json
  recovery/
    approved.recovery-plan.json
    local-recovery-confirmation.json
    eligibility.json
    reconciliation.json
  current-state/
    managed-state.json
    raw-formats/{logicalRecordId}/book.{format}
    exports/{logicalRecordId}/...
  current-state-backup-manifest.json
  recovery.journal.jsonl
  recovery-summary.json
```

Names are illustrative but must be fixed by schema and created once. The
current-state manifest includes artifact kind, safe relative path, length,
SHA-256, logical/current record identity, format or cover identity,
preservation role, source scan fingerprint, recovery plan/operation IDs, and
backup-chain identities.

Current-state backup failure, incomplete export, extra unexpected export
content, hash mismatch, disappearing source, changed scan, low space, or journal
failure stops before mutation.

### Calibre recovery capability matrix

Each capability has four independent statuses:

1. documented for the exact Calibre version;
2. closed typed command mapping and controlled executable tests complete;
3. opt-in real-Calibre qualification on a disposable generated library passed;
4. enabled in the immutable exact-version profile.

All four must be true before dispatch. Help-text similarity or a process exit
code is insufficient.

| Recovery capability | Candidate supported-tool mapping | Verification | Initial Milestone 8 status |
| --- | --- | --- | --- |
| Export current record | Existing typed `calibredb export` | Export inventory, OPF/cover bounds, exact format hashes | Candidate; requalify with exact 9.11.0 profile |
| Create new empty record | Typed `calibredb add --empty` with fixed metadata arguments | Before/after full scan identifies exactly one semantic record | Disabled until exact real-Calibre qualification |
| Find created record | Machine-readable output where documented plus unique full-scan delta | Unique new ID and semantic facts | Disabled; block if result is not unique |
| Add backed-up format | Existing typed `calibredb add_format` | Fresh scan and exact backup hash | Candidate; exact profile still disabled |
| Replace existing format | Typed `add_format` without `--dont-replace` | Current backup verified first; fresh exact-hash comparison | Destructive; disabled until exact qualification |
| Restore metadata | Prefer field-scoped typed `set_metadata --field`; use minimal OPF only if no collateral fields change | Fresh semantic scan of each supported field and unrelated fields | Disabled until field-by-field qualification |
| Restore identifiers | Typed field-scoped metadata mapping | Exact type/value set | Disabled until qualification |
| Restore series/index | Typed field-scoped metadata mapping | Exact series and numeric index | Disabled until qualification |
| Restore cover | Only an exact documented typed cover mapping | Exact cover hash and unchanged unrelated state | Blocked unless separately qualified |
| Remove cleanup-added format | Typed `calibredb remove_format` | Fresh scan proves only approved format association absent | Disabled until exact qualification |
| Remove cleanup-created record | Existing non-permanent typed `calibredb remove` | Fresh scan proves approved logical record absent and preserved data present | Candidate command; recovery semantics unqualified |
| Verify restored content | Existing full read-only scanner, extended for exact cover/metadata facts where required | Semantic state and exact hashes | Required for every enabled operation |

`add --automerge`, `remove --permanent`, `restore_database`, shell commands, and
raw database/filesystem mutations are never capability candidates.

The metadata profile must enumerate individual supported fields. Restoring a
whole OPF is not allowed if it could clear or overwrite unmodeled/custom fields.
Empty/null field semantics, author ordering, author sort, identifiers, series
index, languages, publication date, and publisher require explicit real-Calibre
tests. Unsupported fields remain unchanged and are reported.

Milestone 7 could not execute cover-bearing plans, so most current source
bundles cannot prove an original cover mutation. Cover recovery remains
future-compatible but blocked unless the selected bundle includes exact original
bytes, the current state includes exact current bytes, and the exact-version
mapping and verification are qualified.

### Dependency graph and deterministic ordering

The Domain operation graph must be acyclic, complete, and deterministic.
Required dependency rules:

- recovered record creation precedes metadata, cover, and format restoration to
  that record;
- a created record must be uniquely rediscovered and journaled before dependent
  operations;
- metadata needed to identify a recovered record precedes ambiguous format
  association checks;
- a backup format must be restored and exact-hash verified before a redundant
  cleanup-added copy can be removed;
- a deleted source record and all required content must be recreated and
  verified before a cleanup target record created solely by execution can be
  removed;
- a safe separate recovery copy must be complete and verified before an
  existing current same-format payload can be replaced;
- every preservation operation depends on inclusion in the verified
  current-state backup;
- all constructive operations precede the intermediate verification barrier;
- all destructive operations depend on that barrier and explicit destructive
  confirmation; and
- final verification depends on every executed, no-action, preservation, and
  manual expectation.

Stable ordering:

1. logical recovery record ID;
2. phase;
3. operation-kind rank;
4. normalized format;
5. source backup artifact identity;
6. operation ID.

Topological sorting uses that order as its tie-breaker. Duplicate IDs, missing
dependencies, dependency cycles, destructive-to-constructive back-edges, and a
destructive operation without a verification dependency are blocking errors.

### Recovery execution phases

#### Phase 1: preparation

1. Reconstruct the approved recovery plan and recompute its body digest.
2. Verify approval, warning acknowledgements, and local confirmation.
3. Acquire the shared library-mutation lease.
4. Strictly re-read and reverify every original source artifact and backup item.
5. Rediscover and rehash the Calibre executable and exact capability profile.
6. Perform a fresh full read-only scan.
7. Rerun eligibility and reconciliation and require the approved current-state
   and reconciliation fingerprints to match.
8. Validate capacity and create the recovery journal and durable pre-mutation
   recovery guard.
9. Create the mandatory current-state backup.
10. Reopen, reparse, and independently verify its manifest and every entry.
11. Perform another fresh full scan and require it to match the approved input
    and current-state backup.
12. Persist `CurrentStateBackupVerified`.

Cancellation through this phase prevents recovery mutation.

#### Phase 2: constructive restoration

Execute serially at safe boundaries:

- create missing recovered records;
- rediscover and journal their new IDs;
- apply only qualified non-destructive metadata fields;
- restore missing formats;
- restore a missing cover only when supported; and
- create separate recovery copies needed to preserve conflicting current
  variants.

Before every command, repeat the complete command gate: lease, plan/approval,
tool identity, original backup, current-state backup, expected current state,
dependencies, and confirmation. Persist operation-start and mutation-start
events before launching the command. Never terminate an active mutation.

After every command, persist sanitized process results, perform a fresh full
read-only scan, run the operation's semantic verification, persist the result,
and only then mark the operation complete.

#### Phase 3: intermediate verification

Freshly verify:

- every constructed record is readable and uniquely mapped;
- restored format associations exist and exact hashes match original backups;
- restored supported metadata matches expected pre-state;
- restored covers match exact backup hashes where supported;
- separate safe copies and all unexpected current content remain present;
- the unrelated recovery-preflight fingerprint is unchanged;
- every dependency for a destructive operation is satisfied; and
- both backup chains and the lease are still valid.

Failure prevents destructive recovery.

#### Phase 4: destructive recovery

The WPF workflow requires a second explicit confirmation bound to the exact
destructive-operation digest after intermediate verification.

Only then may the executor, serially and with the complete per-command gate:

- replace a same-format payload after its current variant and safe recovered
  variant are verified;
- overwrite metadata or a cover only when the approved conflict policy permits;
- remove a format proven to have been added solely by the source execution; or
- non-permanently remove a record proven to have been created solely by the
  source execution.

Destructive recovery is always last. It never removes an item whose actual
current hash/state differs from the approved expectation.

#### Phase 5: final verification

Perform another full read-only scan and compare it to the immutable expected
recovered semantic state. Persist full differences, record-ID mappings,
preservation results, unaffected-state digest, backup-chain availability, and
the terminal result. Only a passing result may transition to `Recovered` and
complete the recovery plan.

### Destructive-recovery gate

`ReadyForDestructiveRecovery` requires:

- current-state backup independently verified;
- all original backup items still available and verified;
- all constructive operations verified;
- intermediate verification passed;
- all preservation expectations passed;
- all destructive dependencies satisfied;
- plan, approval, warning acknowledgements, library, tool, lease, and current
  state unchanged;
- no manual-intervention entry needed by a destructive operation;
- exact destructive operation body/digest displayed; and
- explicit local destructive confirmation.

Any mismatch returns to a non-mutating blocked/stale outcome. The application
does not silently regenerate an approved plan during execution.

### Recovery execution lifecycle

Execution states:

- `Created`;
- `PreflightValidating`;
- `BackingUpCurrentState`;
- `CurrentStateBackupVerified`;
- `RestoringConstructiveState`;
- `VerifyingConstructiveState`;
- `ReadyForDestructiveRecovery`;
- `ApplyingDestructiveRecovery`;
- `FinalVerifying`;
- `Recovered`;
- `PartiallyRecovered`;
- `VerificationFailed`;
- `CancelledBeforeMutation`;
- `RecoveryFailed`;
- `ManualInterventionRequired`;
- `Revoked`;
- `Stale`.

The aggregate separately records:

- whether any recovery mutation started;
- whether any constructive operation completed;
- whether destructive recovery started;
- last verified safe boundary;
- semantic pre-state restored;
- changed-ID outcome;
- preserved-unexpected-content outcome; and
- unresolved manual requirements.

`Recovered` is legal only after final semantic verification. Changed IDs and
preserved unexpected content are outcome flags, not failure states.

### Cancellation

- Before mutation: cancel immediately and finish as
  `CancelledBeforeMutation`.
- During current-state backup: finish or safely abandon the external partial
  artifact, record the result, and do not begin mutation.
- During constructive restoration: latch the request while a Calibre mutation
  runs, perform the mandatory fresh verification, and stop at the next verified
  safe boundary as `PartiallyRecovered` when work completed.
- After destructive recovery begins: latch the request, never terminate the
  active process, scan and verify its actual effect, then stop at the next
  verified safe boundary. The result normally requires manual review.
- Persist request time, active operation, actual stop time, stop boundary, and
  resulting state.

Cancellation never implies reversal of already completed recovery work.

### Failure and partial-recovery behavior

#### Eligibility or preflight failure

No mutation. Persist ordered blockers and mark the plan blocked or stale.

#### Current-state backup failure

No mutation. Preserve the original bundle, partial external recovery bundle,
and recovery journal. Never modify the original backup.

#### Constructive restoration failure

Do not begin destructive recovery. Wait for the active command, perform a fresh
scan, persist actual state and mappings, and finish as `PartiallyRecovered` or
`ManualInterventionRequired`.

#### Intermediate verification failure

Do not begin destructive recovery. Persist differences and finish as
`PartiallyRecovered` or `ManualInterventionRequired`.

#### Destructive recovery failure

Stop at the next safe boundary, perform a fresh full scan, record actual
semantic state and all uncertain effects, retain both backup generations, and
finish as `ManualInterventionRequired`.

#### Final verification failure

Never report `Recovered`. Persist exact semantic differences and finish as
`VerificationFailed` or `ManualInterventionRequired`.

#### Journal or storage failure

Before mutation, stop with no mutation. After the mutation marker, complete the
active command without termination, scan when possible, preserve the backup
chain, and treat the result as manual intervention. Do not continue without a
durable journal boundary.

No failure path automatically invokes another recovery.

### Final semantic verification

Final verification requires:

- the selected library UUID/schema and canonical-root identity still match;
- the library scan completes and the library remains readable;
- every expected logical record exists exactly once;
- every supported metadata field matches the expected recovered state;
- identifiers and series data match where supported;
- every restored format exists and its hash matches the original backup;
- every restored cover matches its original backup hash where supported;
- an approved cleanup-added format is absent only when its removal operation
  completed and verified;
- an approved cleanup-created record is absent only when its removal operation
  completed and verified;
- every unexpected current item selected for preservation remains present with
  the expected exact hash or semantic facts;
- current variants stored as separate recovered records remain present;
- the recovery-preflight unrelated-state fingerprint remains unchanged;
- current and original backup manifests and all journals remain available and
  valid;
- every changed Calibre ID has a durable logical mapping; and
- no blocking/manual expectation remains.

Verification compares semantic content, not original directory names, file
timestamps, or numeric IDs that Calibre does not promise to reproduce. Full
semantic recovery with a new ID is successful and explicitly reported.

### Shared lease and recovery resolution

Replace the cleanup-specific Application lease contract with
`ILibraryMutationLease` (or a semantically equivalent shared contract) carrying
an operation kind of `Cleanup` or `Recovery`. Both use the existing canonical
root plus UUID key and the same external lease root. Different file names must
not create separate lock domains.

The selected source execution's existing `RecoveryRequired` guard is expected
input, not a competing execution. Other active or unresolved cleanup/recovery
chains remain blockers.

Original Milestone 7 history and journal remain immutable. A successful
recovery writes an append-only recovery-resolution record linking source
execution, recovery plan, recovery execution, final journal, and status. Cleanup
preflight consults that resolution index: an unresolved guard continues to
block; a verified recovery can resolve it without rewriting the source history.
A failed recovery adds another unresolved chain and never hides the original.

### Recovery journal

Persist `cleanup-recovery-journal/1.0` outside the library using create-new,
single-writer, append-only, hash-chained JSON lines and durable flushes.

The header and events record:

- recovery execution ID;
- recovery plan ID, revision, body digest, and schema/model/policy versions;
- source cleanup-plan ID and digest;
- source execution ID;
- original journal identity and final entry hash;
- original backup-manifest identities;
- current-state backup-manifest identities;
- library and canonical-root identities;
- exact application and Calibre capability profiles;
- lifecycle transitions;
- eligibility and reconciliation digests and ordered results;
- current-state fingerprints;
- every recovery operation and dependencies;
- mapped typed Calibre command and sanitized argument description;
- operation start, process completion, bounded sanitized stdout/stderr and exit
  code, semantic verification, and durable operation completion;
- cancellation requests and actual stop boundaries;
- mutation marker and destructive-phase marker;
- record-ID mappings;
- preserved unexpected content;
- failures and manual-intervention requirements;
- intermediate and final verification; and
- resulting recovery status and outcome flags.

No raw book content or secrets are logged. Paths are reduced to safe bundle
relative paths or redacted identities. Output has per-stream, per-event, and
total-journal bounds.

`recovery-summary.json` is create-new and must agree with the terminal journal
entry. Startup reconciliation treats any post-mutation incomplete/corrupt
recovery journal as unresolved manual intervention. Crash understanding never
depends on memory, WPF state, or a chat session.

### Idempotency, retry, and continuation

- No mutation command is automatically retried.
- No recovery journal is automatically resumed.
- A failed destructive command is never assumed to have had no effect.
- Any later continuation begins with strict artifact verification and a fresh
  scan/reconciliation.
- If semantic input changed, generate a new recovery plan ID and approval.
- An operation may become `NoActionAlreadyRestored` only when current semantic
  verification proves its complete expected effect.
- Constructive operations may be repeated only through a newly approved plan
  that proves the repeat is non-destructive and idempotent for actual state.
- Unknown destructive effects block or require manual intervention.
- The old recovery journal remains immutable and is linked as provenance.
- The system never automatically rolls back the rollback.

### Application use cases and abstractions

Application use cases:

- `InspectRecoverySourceExecutionUseCase`;
- `EvaluateRecoveryEligibilityUseCase`;
- `ReconcileCurrentRecoveryStateUseCase`;
- `GenerateRecoveryPlanUseCase`;
- `ValidateRecoveryPlanUseCase`;
- `ApproveRecoveryPlanUseCase`;
- `RevokeRecoveryPlanUseCase`;
- `EvaluateRecoveryPlanStalenessUseCase`;
- `PrepareRecoveryExecutionUseCase`;
- `ExecuteApprovedRecoveryPlanUseCase`;
- `VerifyRecoveryStateUseCase`;
- `ReadRecoveryHistoryUseCase`; and
- `ExportRecoveryArtifactsUseCase`.

Meaningful ports:

- `IRecoverySourceArtifactReader`;
- `IRecoveryEligibilityValidator`;
- `ICurrentStateReconciler`;
- `IRecoveryPlanGenerator`;
- `IRecoveryPlanValidator`;
- `IRecoveryPlanStore`;
- `IRecoveryExecutionService`;
- `IRecoveryStateBackupService`;
- `IRecoveryStateVerifier`;
- `IRecoveryJournalStore`;
- `IRecoveryHistoryStore`;
- `IRecoveryIdGenerator`;
- `ILibraryMutationLease`;
- `IRecoveryCalibreGateway`;
- `ICalibreExecutionProfileProvider`;
- `IRecoveryCurrentStateScanner`;
- existing `IExecutionLibraryScanner`;
- existing clocks, ID generators, application identity, capacity probe, and
  storage/path abstractions where their semantics match.

The Application layer owns orchestration, eligibility, reconciliation,
dependency ordering, destructive gating, staleness, cancellation, and semantic
verification. It consumes declarative values and typed results; it contains no
JSON, filesystem, SQLite, process, WPF, or command-line construction.

### Infrastructure design

Infrastructure owns:

- strict source bundle, cleanup-plan, original manifest, execution-journal, and
  terminal-summary readers;
- independently rehashing original backup artifacts;
- deterministic recovery-plan serialization and external persistence;
- current-state backup storage, export validation, manifest sealing, and
  independent verification;
- shared cleanup/recovery lease implementation;
- recovery journal, terminal summary, history, and resolution index;
- extended external path, traversal, reparse-point, and storage guards;
- exact-version recovery capability discovery and profiles;
- typed Calibre recovery command mapping through
  `DirectCalibreProcessRunner`;
- machine-readable output parsing only where exact behavior is qualified;
- executable and input-handle locking across command completion;
- bounded sanitized output capture; and
- composition-root registrations.

Infrastructure may read managed files for scanning and outward backup using
read-only handles. It never writes, renames, moves, replaces, or deletes inside
the library. All recovery mutations are performed by a qualified typed Calibre
command.

### WPF workflow

Incrementally extend execution history; do not add bulk selection or an
automatic failure action.

The user flow:

1. Select one Milestone 7 history entry and choose `Review recovery`.
2. Inspect the original cleanup plan, canonical digest, execution journal,
   terminal status, original manifest, and backup verification.
3. Run a fresh scan and inspect three-way current-state differences.
4. Inspect eligibility grouped into blockers, acknowledgement warnings, and
   information.
5. Inspect proposed operations, dependencies, risk, preservation decisions,
   unsupported/ambiguous items, and expected final semantic state.
6. Generate a versioned recovery plan.
7. Approve the immutable body and each required warning explicitly.
8. Select/confirm an external recovery-backup destination.
9. Run fresh preflight and create/verify the current-state backup.
10. Review intermediate results and separately confirm the exact destructive
    operation set, if any.
11. Execute while viewing phase, operation, verification, ID mapping,
    preservation, and safe-stop state.
12. View final verification, semantic outcome, changed IDs, retained content,
    bundle chain, and manual requirements.
13. Export the recovery plan, journal, terminal summary, and manifest copies.

If the source execution is ineligible, the UI offers inspection/export, not a
mutation button. If current state changes, the approved plan is shown as stale
and cannot execute. A failure notification may link to recovery review but never
starts or preapproves recovery.

ViewModels call Application use cases and never access files, processes,
SQLite, or Infrastructure implementations. Destructive confirmation and
closing-window behavior use the same safe-boundary principles as Milestone 7.

### Security threats and mitigations

| Threat | Mitigation |
| --- | --- |
| Tampered cleanup plan | Strict reconstruction and canonical body digest recomputation; cross-link journal and manifest; block mismatch. |
| Tampered recovery plan | Strict reconstruction; recompute body and graph digests; approval binds exact body/current state/warnings. |
| Tampered execution or recovery journal | Verify every sequence/previous/entry hash, transition, identity, operation, and terminal summary; block inconsistent chains. |
| Tampered manifest or backup | Recompute internal manifest digest, independent manifest-file digest, and every entry hash; reopen before each phase. |
| Backup source substitution | Canonical contained paths, artifact identity/digest binding, regular-file checks, locked handles, no alternate search paths. |
| Wrong library or bundle | UUID/schema/root identity plus plan/execution/journal/manifest cross-links. |
| Path traversal or storage escape | Reject rooted/parent/empty segments, canonical containment failures, reparse points, and storage that contains/is contained by the library. |
| Symlink, junction, or reparse race | Reject at every ancestor and entry, open with safe sharing, revalidate identity before use. |
| Executable substitution | Canonical fixed executable path, exact version/hash/profile, locked executable handle, rediscovery per command. |
| Malicious command arguments | Closed typed requests and `ArgumentList`; no raw command strings, shell, scripts, or user options. |
| Oversized/deep imported JSON | UTF-8 byte, depth, property, collection, journal-line, event-count, and total-size bounds; duplicate rejection. |
| Sensitive journal output | Bounded sanitization/redaction; no book content, raw OPF, secrets, full managed paths, or environment dump. |
| Unexpected current data overwritten | Exact reconciliation, mandatory current backup, preservation operation, separate-copy policy, destructive gate, pre-command expected-state check. |
| Recovery backup collision | Create-new execution directory and files; never reuse/overwrite original or prior recovery bundles. |
| Coordinated rewrite of legacy unkeyed artifacts | Restrictive external-storage ACLs and independent cross-links; explicitly do not claim hostile-admin authenticity for Milestone 7 artifacts. |

All uncertainty fails closed.

### Persistence and versioning

Introduce:

- `cleanup-recovery-plan/1.0`;
- `cleanup-recovery-model/1.0.0`;
- `cleanup-recovery-policy/1.0.0`;
- `cleanup-reconciliation/1.0`;
- `cleanup-current-state-backup-manifest/1.0`;
- `cleanup-recovery-journal/1.0`;
- `cleanup-recovery-history/1.0`;
- `cleanup-recovery-resolution/1.0`; and
- an exact `calibredb/windows/9.11.0/recovery-1.0` capability profile.

Every reader supports an explicit closed compatibility table. Unknown schema,
model, application, and capability versions block. Migrations create new
artifacts and never modify source backups or journals.

Everything is stored outside the Calibre library. Primary plans, manifests, and
journals use create-new publication and deterministic UTF-8 serialization.
History and resolution indexes are secondary; authoritative artifacts remain
the immutable bundle and hash chain.

### Performance considerations

- Recovery remains single-library, single-execution, and serial.
- Full read-only scans before planning, before backup, before mutation, after
  every command, at the intermediate barrier, and at final verification are
  intentional safety costs.
- Stream format and cover hashing/copying with bounded buffers; never load large
  ebooks into memory.
- Bound concurrency for backup hashing/export validation, with a conservative
  default and propagated cancellation before mutation.
- Keep Calibre mutations serial.
- Reconciliation indexes books by ID, identifiers, and exact format hashes for
  deterministic near-linear matching, but requires a unique semantic result.
- Calculate whole-library unrelated fingerprints in a streaming deterministic
  order.
- Estimate current-state backup space from current affected bytes plus export
  overhead and a safety margin; fail before backup if unavailable.
- Journal writes are small, bounded, append-only, and flushed only at required
  durable boundaries.
- WPF loads summary rows first and bounded artifact details on selection; it
  must not parse large journals on the UI thread.

## Files expected to change

Names may be refined during implementation, but responsibilities and layer
ownership must remain as listed.

### Documentation

- `docs/plans/milestone-8-verified-rollback-and-recovery.md` — this plan.
- `docs/adr/0008-reconciliation-based-verified-recovery.md` — accept the
  three-way reconciliation, backup chain, semantic identity, and shared lease
  boundary.
- `docs/architecture.md` — add recovery components and shared mutation lease.
- `docs/domain-model.md` — add recovery plan/execution/reconciliation/identity
  values and transitions.
- `docs/safety-and-rollback.md` — replace the Milestone 8 placeholder with the
  verified workflow and failure states.
- `docs/test-strategy.md` — add recovery test layers and disposable real-Calibre
  gates.
- `docs/roadmap.md` — mark only Milestone 8 complete after implementation and
  acceptance.

### Domain

- `src/CalibreLibraryCleaner.Domain/Recoveries/RecoveryPlanValues.cs`
- `src/CalibreLibraryCleaner.Domain/Recoveries/RecoveryIssues.cs`
- `src/CalibreLibraryCleaner.Domain/Recoveries/RecoveryReconciliation.cs`
- `src/CalibreLibraryCleaner.Domain/Recoveries/RecoveryOperations.cs`
- `src/CalibreLibraryCleaner.Domain/Recoveries/RecoveryPlan.cs`
- `src/CalibreLibraryCleaner.Domain/Recoveries/RecoveryPlanContentDigestPolicy.cs`
- `src/CalibreLibraryCleaner.Domain/Recoveries/RecoveryPlanLifecyclePolicy.cs`
- `src/CalibreLibraryCleaner.Domain/Recoveries/RecoveryExecution.cs`
- `src/CalibreLibraryCleaner.Domain/Recoveries/RecoveryVerificationPolicy.cs`
- `src/CalibreLibraryCleaner.Domain/Recoveries/RecoveryRecordIdentity.cs`
- `src/CalibreLibraryCleaner.Domain/Recoveries/RecoveryBackupChain.cs`

### Application

- `src/CalibreLibraryCleaner.Application/Abstractions/IRecoverySourceArtifactReader.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/IRecoveryPlanStore.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/IRecoveryStateBackupService.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/IRecoveryJournalStore.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/IRecoveryHistoryStore.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/IRecoveryCalibreGateway.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/IRecoveryIdGenerator.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/IRecoveryCurrentStateScanner.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/IRecoveryResolutionStore.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/ILibraryMutationLease.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/ICalibreExecution.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/ICleanupExecutionServices.cs`
- `src/CalibreLibraryCleaner.Application/Recoveries/RecoveryContracts.cs`
- `src/CalibreLibraryCleaner.Application/Recoveries/RecoveryEligibilityValidator.cs`
- `src/CalibreLibraryCleaner.Application/Recoveries/CurrentStateReconciler.cs`
- `src/CalibreLibraryCleaner.Application/Recoveries/GenerateRecoveryPlanUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Recoveries/RecoveryPlanLifecycleUseCases.cs`
- `src/CalibreLibraryCleaner.Application/Recoveries/PrepareRecoveryExecutionUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Recoveries/ExecuteApprovedRecoveryPlanUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Recoveries/RecoveryStateVerifier.cs`
- `src/CalibreLibraryCleaner.Application/Executions/ExecuteApprovedCleanupPlanUseCase.cs`
  — use the shared mutation lease and resolved-recovery index without changing
  cleanup semantics.
- `src/CalibreLibraryCleaner.Application/Abstractions/ICleanupExecutionLease.cs`
  — remove after callers move to the shared contract.

### Infrastructure

- `src/CalibreLibraryCleaner.Infrastructure/Recovery/RecoverySourceArtifactReader.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Recovery/RecoveryPlanJsonSerializer.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Recovery/FullRecoveryCurrentStateScanner.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Recovery/FileRecoveryPlanStore.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Recovery/FileRecoveryStateBackupStore.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Recovery/JsonLinesRecoveryJournalStore.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Recovery/FileRecoveryHistoryStore.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Recovery/FileRecoveryResolutionStore.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Execution/FileLibraryMutationLease.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Execution/FileCleanupExecutionLease.cs`
  — replace after compatibility tests prove the same lock key.
- `src/CalibreLibraryCleaner.Infrastructure/Execution/ExecutionPathGuard.cs`
  — generalize safe external recovery storage checks.
- `src/CalibreLibraryCleaner.Infrastructure/Calibre/CalibreExecutionOptions.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Calibre/CalibreRecoveryProfileCatalog.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Calibre/CalibreToolDiscovery.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Calibre/CalibreCommandGateway.cs`
  — implement closed recovery mappings while retaining one direct runner.
- `src/CalibreLibraryCleaner.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`

### WPF

- `src/CalibreLibraryCleaner.Wpf/ViewModels/RecoveryWorkspaceViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/RecoveryRows.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/CleanupExecutionWorkspaceViewModel.cs`
  — selectable history and explicit handoff to recovery review.
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MainWindowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml`
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/IRecoveryConfirmationService.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/MessageBoxRecoveryConfirmationService.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/IRecoveryBackupFolderPicker.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/OpenFolderDialogRecoveryBackupFolderPicker.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/IRecoveryArtifactExportFilePicker.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/SaveFileDialogRecoveryArtifactExportFilePicker.cs`
- `src/CalibreLibraryCleaner.Wpf/App.xaml.cs`

### Tests

- `tests/CalibreLibraryCleaner.Domain.Tests/Recoveries/RecoveryPlanTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Recoveries/RecoveryReconciliationTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Recoveries/RecoveryLifecycleTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Recoveries/RecoveryVerificationTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Recoveries/RecoveryEligibilityTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Recoveries/CurrentStateReconcilerTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Recoveries/GenerateRecoveryPlanUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Recoveries/ExecuteApprovedRecoveryPlanUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Recovery/RecoverySourceArtifactReaderTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Recovery/RecoveryStateBackupStoreTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Recovery/RecoveryJournalTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Recovery/RecoveryLeaseTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Calibre/CalibreRecoveryCommandBoundaryTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Calibre/RealCalibreRecoveryCompatibilityTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/RecoveryWorkspaceViewModelTests.cs`
- `tests/CalibreLibraryCleaner.Architecture.Tests/DependencyDirectionTests.cs`
- existing Milestone 7 tests affected by shared-lease and resolution-index
  integration.

## Package changes

No new NuGet packages are expected.

Use .NET cryptography, JSON, filesystem, process, and XML facilities already
available through the appropriate Infrastructure boundary. If implementation
discovers that a package is necessary, stop and update this plan with its
security, maintenance, architecture, and deterministic-serialization rationale
before adding it.

## ADR additions or updates

Add accepted ADR 0008 before substantial code:

**Decision:** Recovery is a separately approved immutable plan produced by
three-way semantic reconciliation. It uses a verified original backup, a
mandatory verified current-state backup, semantic record identity with explicit
ID mappings, a shared cleanup/recovery lease, constructive-before-destructive
execution, and final semantic verification.

The ADR must record rejected alternatives:

- blind inversion of the cleanup graph;
- automatic rollback after failure;
- direct SQLite or managed-folder restoration;
- reuse/modification of the original Milestone 7 bundle;
- command-exit-code success;
- requiring old numeric record IDs;
- overwriting unexpected current content;
- distinct cleanup and recovery lock domains; and
- automatic resume/retry/rollback-of-rollback.

ADRs 0001 through 0007 remain unchanged unless implementation reveals an actual
decision conflict. Documentation clarifications are not retroactive edits to
their accepted decisions.

## Safety considerations

- Recovery has two independently verified backup generations.
- Original Milestone 7 artifacts are immutable inputs.
- Read-only scans are complete and repeated at every material gate.
- Every mutation is a typed, exact-version-qualified Calibre command.
- The direct process boundary remains unique and shell-free.
- No active mutation process is terminated.
- Current unexpected data is preserved or blocks automation.
- Replacement/overwrite is destructive even when expressed by an add or
  metadata command.
- Destructive recovery cannot precede constructive verification.
- Every command effect is verified semantically.
- A shared lease excludes cleanup and recovery for the same library.
- Any unknown state, output, schema, operation, identity, integrity result, or
  capability fails closed.
- No test discovers or uses a user's library.
- Original and current backups, plans, manifests, journals, summaries, and ID
  mappings remain available after every terminal outcome.

## Incremental implementation steps

1. **Document the decision and freeze supported inputs**
   - Add ADR 0008 and update architecture/domain/safety/test documentation.
   - Define closed supported source schema/application/tool compatibility.
   - Keep all mutation capabilities disabled.
   - Add tests that unknown source versions and current disabled profiles block.

2. **Add Domain recovery values and lifecycle**
   - Implement version, identity, issue, reconciliation, operation, dependency,
     backup-chain, plan, approval, execution, mapping, and verification values.
   - Implement legal transitions, deterministic ordering, graph validation, and
     canonical body hashing.
   - Add exhaustive Domain invariants before persistence or commands.

3. **Read and verify Milestone 7 source artifacts**
   - Add bounded strict cleanup-plan/manifest/journal/summary readers behind the
     Application port.
   - Verify all cross-links, event legality, original backup timing, and every
     required backup hash.
   - Never modify the source bundle.
   - Add corruption, substitution, traversal, bounds, and compatibility tests.

4. **Implement fresh three-way reconciliation**
   - Build journal operation projections and expected post-operation facts.
   - Fingerprint current full/affected/unrelated state.
   - Classify every affected logical record/format.
   - Identify unexpected content and safe alternatives.
   - Block ambiguous same-format, metadata, cover, and ID matches.

5. **Generate, persist, approve, revoke, and stale recovery plans**
   - Generate deterministic operation dependencies and expected final state.
   - Add strict deterministic serialization and external create-new storage.
   - Bind approval to body/current state/capability/warnings.
   - Require new plan IDs for changed semantic input.
   - Implement inspection/export without execution.

6. **Generalize the mutation lease and history guard**
   - Replace the cleanup-specific contract with one shared lock domain while
     preserving the current canonical key.
   - Move Milestone 7 execution to the shared lease.
   - Add append-only recovery history/resolution links.
   - Prove cleanup/recovery and recovery/recovery exclusion across processes.

7. **Implement mandatory current-state backup**
   - Add layout, capacity/path guard, raw outward copies, typed exports,
     manifest, sealing, and independent verification.
   - Include unexpected selected content and source artifact audit copies.
   - Keep original and previous recovery bundles untouched.
   - Do not add a recovery mutation API yet.

8. **Qualify the recovery Calibre capability profile**
   - Extend typed requests and controlled executable behavior one capability at
     a time.
   - Run the opt-in exact 9.11.0 disposable-library tests.
   - Enable only operations whose command, side effects, lookup, and semantic
     verification all pass.
   - Leave cover or metadata fields blocked when proof is incomplete.

9. **Implement constructive execution and journaling**
   - Add preflight, shared lease, source revalidation, current backup, repeat
     scan, recovery guard, operation journal, create/lookup, metadata/format
     restore, per-command scan, and safe-stop.
   - Keep all destructive recovery disabled.
   - Exercise crash points and partial constructive outcomes.

10. **Add intermediate verification and destructive gate**
    - Verify constructive and preservation expectations.
    - Add the explicit destructive-operation confirmation.
    - Add replacement, format removal, and non-permanent record removal only
      through enabled capabilities and verified dependencies.

11. **Add final verification and terminal recovery history**
    - Compare semantic logical records, exact hashes, metadata/covers,
      preservation, approved absences, unrelated baseline, backup chain, and ID
      mappings.
    - Require journal/summary agreement.
    - Resolve the source guard only after verified recovery.

12. **Add the incremental WPF workflow**
    - Make one history row selectable for recovery review.
    - Add eligibility/difference/operation/preservation views.
    - Add plan generation, approval, backup destination, preflight, progress,
      safe-stop, destructive confirmation, final result, mappings, and export.
    - Keep failure notifications non-executable and bulk recovery absent.

13. **Run the complete verification and manual acceptance suite**
    - Run standard formatting/build/tests.
    - Run safety and architecture searches.
    - Run opt-in real-Calibre tests only with an explicit disposable root.
    - Complete the manual scenarios on disposable generated libraries.
    - Review the complete diff and update this plan's progress/outcome.

## Tests

### Eligibility

- Successful recovery eligibility.
- Missing cleanup plan.
- Missing execution journal.
- Missing backup manifest.
- Tampered cleanup plan.
- Tampered or recomputed-but-cross-link-inconsistent journal.
- Tampered backup manifest.
- Tampered required backup entry.
- Wrong library UUID, schema, or root identity.
- Unsupported cleanup-plan, journal, manifest, application, or Calibre version.
- Unknown or contradictory execution state.
- Mutation recorded before original backup verification.
- Active cleanup lease.
- Active recovery lease.
- Unresolved conflicting recovery.
- Unsupported recovery operation.
- Incomplete original backup.
- Recovery storage inside/containing the library.
- Reparse-point and backup substitution.

### Reconciliation

- Cleanup never mutated the library.
- Constructive operation completed.
- Target format was added but the source record was not removed.
- Destructive operation completed.
- A record was removed and a later source execution operation failed.
- Operation recorded complete and current state matches.
- Operation recorded complete but current state differs.
- Operation not recorded complete but state changed.
- Command success without semantic verification.
- User changed metadata afterward.
- User added a new unique format afterward.
- Target record is missing.
- Removed record was recreated independently.
- Original numeric ID was reused by different content.
- Ambiguous same-format conflict.
- Post-state was already manually restored.
- Semantically restored record has a different current Calibre ID.
- Current state differs from both pre-state and expected post-state.
- Unrelated post-execution change is preserved and excluded.
- Filename/timestamp/size-only matches do not qualify.

### Recovery-plan generation

- Restore a deleted record.
- Restore a removed format.
- Restore a replaced format through separate-copy then destructive replacement.
- Restore supported metadata fields.
- Restore identifiers and series data only with enabled capabilities.
- Restore a cover only with exact supported mapping.
- Remove a format added solely by cleanup.
- Remove a record created solely by cleanup.
- Preserve an unexpected unique format.
- Create a recovery copy instead of overwriting.
- Block ambiguous overwrite.
- Produce no-action entries for already restored state.
- Produce manual-intervention entries for unsupported state.
- Deterministic operation ordering.
- Complete dependencies and no cycles.
- Destructive operations depend on constructive verification.
- New plan ID after semantic input changes.
- Approved body is immutable.
- Canonical digest stable across enumeration and JSON layout.
- Approval binds required warning acknowledgements.
- Approved plan becomes stale when current fingerprint changes.

### Current-state backup

- Complete backup of all affected current records.
- Metadata, formats, covers, hashes, paths, inventory, and fingerprints included.
- Unexpected current content selected for preservation included.
- Source plan/journal/summary/original-manifest audit copies included.
- Current-state manifest independently verifies.
- Hash mismatch prevents mutation.
- Missing or extra export content prevents mutation.
- Backup path is outside and does not contain the library.
- Current backup never overwrites original or prior recovery backup.
- Insufficient free space.
- Partial backup failure.
- Cancellation during backup.
- Reparse/path traversal/source substitution rejection.
- Source or current state changes during backup.

### Execution ordering

- Shared lease before preflight.
- Fresh scan before plan execution.
- Original backup reverified before current backup.
- Current-state backup verified before mutation marker.
- Repeat scan after current-state backup and before mutation.
- Constructive recovery before destructive recovery.
- Created record uniquely mapped before dependent operations.
- Restored format verified before cleanup-added format removal.
- Recreated record verified before cleanup-created record removal.
- Separate preserved copy verified before same-format replacement.
- Intermediate verification before destructive confirmation.
- Destructive recovery occurs last.
- Operation dependencies enforced.
- Full fresh scan after every command.

### Process safety

Reuse and extend Milestone 7 coverage:

- one direct process boundary;
- no shell execution;
- fixed canonical executable;
- `ArgumentList` rather than command strings;
- exact command and option allowlist;
- no permanent removal;
- no `restore_database` or GUI command;
- stdout/stderr and exit code captured and sanitized;
- output bounds and redaction;
- read-only timeout behavior;
- active mutation not killed on timeout/cancellation;
- safe-boundary cancellation;
- executable and backup source handles locked;
- minimal environment;
- unknown output fails closed;
- unsupported capability is never dispatched;
- process success with wrong state fails semantic verification.

### Cancellation and failure

- Cancellation before mutation.
- Cancellation during current-state backup.
- Cancellation during constructive restoration.
- Delayed cancellation during destructive recovery.
- Constructive command failure.
- Constructive command success with verification failure.
- Intermediate verification failure.
- Destructive command failure.
- Destructive command unknown result.
- Final verification failure.
- Journal failure before mutation.
- Journal failure after mutation.
- Recovery journal survives simulated crash after each durable boundary.
- Terminal summary mismatch remains unresolved.
- No automatic retry.
- No automatic resume.
- No automatic secondary rollback.
- Prior partial recovery requires new scan, reconciliation, plan, and approval.

### Final verification

- Restored format hashes match original backup.
- Supported metadata restored.
- Identifiers and series restored.
- Cover restored only where supported and exact hash matches.
- Semantic records restored with new Calibre IDs.
- ID mappings persisted and displayed.
- Cleanup-added formats removed only when approved.
- Cleanup-created records removed only when approved.
- Unexpected current content remains present.
- Separate recovered variants remain present.
- Unrelated records unchanged from recovery preflight.
- Library remains readable.
- All plans, manifests, journals, summaries, and backup chains remain available.
- Process success with wrong state produces `VerificationFailed`.
- Successful semantic restoration produces `Recovered`.
- Changed ID and preserved content produce successful outcome flags.

### Lifecycle, architecture, and safety

- Every legal and illegal plan transition.
- Every legal and illegal execution transition.
- `Recovered` impossible without passing final verification.
- Destructive phase impossible without current backup and intermediate
  verification.
- Domain has no filesystem, process, SQLite, JSON, WPF, Calibre, logging, or DI
  references.
- Application has no filesystem, process, SQLite, JSON, or WPF references.
- WPF ViewModels do not invoke Infrastructure or filesystem/process APIs.
- Production has no SQLite writes or writable database open modes.
- Production has no manual managed-library file mutation.
- Production has no shell invocation.
- Production has no automatic rollback trigger.
- Original backup files are never opened for write.
- Cleanup and recovery share one lease key.
- Automated tests never discover a user library.

### WPF

- One history execution can be selected; multiple/bulk selection cannot.
- Ineligible source displays blockers and disables generation/execution.
- Original plan/journal/manifest and current differences are inspectable.
- Warnings require individual acknowledgement.
- Approval binds the displayed immutable body.
- Current change invalidates approval.
- Backup location cannot be inside/around the library.
- Current backup progress precedes mutation progress.
- Destructive confirmation is separate and exact.
- Safe-stop wording changes after mutation/destructive boundaries.
- Final verification and ID mappings display.
- Preserved unexpected content displays.
- Export uses Application use cases.
- Failure notification has no automatic rollback command.

## Optional real-Calibre integration tests

Add a trait such as `OptInRealCalibreRecovery`. It runs only when:

- the exact expected `calibredb.exe` is explicitly supplied;
- an explicit test root is supplied;
- the test root is outside the repository and user library;
- the root contains a test-owned marker;
- each scenario creates a new library under that root;
- no default-library discovery is called; and
- teardown validates the canonical target before deleting the disposable test
  library.

Scenarios:

1. Successful cleanup followed by successful rollback.
2. Cleanup stopped after a constructive operation, followed by recovery.
3. Record removal followed by semantic record recreation from backup.
4. Format replacement followed by original-format restoration.
5. A post-cleanup unique format is added and preserved or blocks as designed.
6. Current metadata is changed after cleanup and preserved/blocked.
7. Recovery completes with a different Calibre record ID.
8. A tampered original backup prevents recovery.
9. A controlled recovery command failure produces
   `ManualInterventionRequired`.
10. Final semantic verification catches an incorrect command result.

The suite must qualify each individual capability in the matrix, including
empty/null metadata behavior, record-ID discovery, same-format replacement,
non-permanent removal, and cover behavior if cover support is proposed.

## Manual acceptance tests

Run only on disposable generated libraries:

1. Complete execution is rolled back successfully.
2. Partial constructive execution is reconciled and recovered.
3. Deleted record is recreated from original backup.
4. Replaced EPUB is restored.
5. New post-execution AZW3 remains preserved.
6. Ambiguous post-execution edit blocks automatic recovery.
7. Current-state backup failure prevents rollback mutation.
8. Calibre command failure produces a durable partial-recovery journal.
9. Recovery completes with a changed internal record ID and reports mapping.
10. Final semantic verification succeeds.
11. Application crash simulation leaves a usable recovery journal.
12. No unrelated library record changes.

Also inspect both external bundle generations and confirm the original
Milestone 7 files remain byte-for-byte unchanged.

## Verification commands

Run from the repository root:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check
```

Run focused recovery tests during implementation:

```powershell
dotnet test --no-build --filter "FullyQualifiedName~Recover"
dotnet test --no-build --filter "FullyQualifiedName~CalibreRecoveryCommandBoundaryTests"
dotnet test --no-build --filter "FullyQualifiedName~DependencyDirectionTests"
```

Run the opt-in real-Calibre suite only with explicit disposable-library
environment variables defined by that suite:

```powershell
dotnet test --no-build --filter "Category=OptInRealCalibreRecovery"
```

Perform source-safety reviews:

```powershell
rg -n "metadata\.db|FileMode\.(Create|Append|Truncate)|File\.(Delete|Move|Replace)|Directory\.(Delete|Move)" src
rg -n "ProcessStartInfo|UseShellExecute|cmd\.exe|powershell\.exe|bash|/bin/" src
rg -n "restore_database|--permanent|backup_metadata|embed_metadata|remove_format|set_metadata|add_format|\"remove\"|\"add\"" src/CalibreLibraryCleaner.Infrastructure
rg -n "AutomaticRollback|AutoResume|Retry|RollbackRollback|BulkRecovery" src tests
```

Review every search result in context; a string match is not itself a failure.
Do not claim success unless all relevant commands actually succeed and the
exact-version disposable-library qualification has enabled every production
capability required by the acceptance scenarios.

## Risks

- The existing exact Windows Calibre 9.11.0 profile is disabled. Milestone 8
  cannot ship mutation-enabled recovery until exact real-Calibre qualification
  succeeds.
- Documented command behavior may differ in exit output, localization, record-ID
  reporting, metadata clearing, hooks, path selection, cover handling, or
  failure behavior. Fresh semantic scans are authoritative.
- `calibredb add --empty` may not expose an unambiguous machine-readable created
  ID. Unique before/after semantic discovery must be proven or creation remains
  unsupported.
- Field-scoped metadata restoration may not cover every OPF/custom field and may
  have collateral effects. The supported field set must remain narrow.
- Milestone 7 blocked cover-bearing plans and modeled cover presence rather than
  cover hashes. Cover recovery will often be unsupported for existing bundles.
- Milestone 7 journals identify durable boundaries but do not contain complete
  snapshots. Three-way recovery depends on cleanup-plan pre-state, verified
  backup bytes, journal progress, and the fresh scan.
- Existing M7 manifest and journal identities are separate and use unkeyed
  cryptographic hashes. Cross-link validation detects ordinary tampering and
  substitution but not a coordinated hostile rewrite of all legacy artifacts.
- Calibre may assign different record IDs, directories, filenames, or timestamps
  during semantic recreation.
- Same-format variants cannot coexist on one Calibre record; preserving both can
  require a separate record and extra user review.
- External Calibre writers cannot be forcibly excluded. Per-command scans and
  the application lease detect changes but do not lock third-party applications.
- A full current-state backup can require substantial space and time.
- Current-state export may contain extra-data or fields not modeled by recovery;
  ambiguity must block rather than discard them.
- Recovery history resolution must not rewrite or hide the original
  `RecoveryRequired` evidence.
- Journal or disk failure after mutation can leave a semantically changed
  library with incomplete application evidence; current backup and safe-stop
  behavior limit but cannot eliminate that risk.

## Unresolved questions and conservative defaults

These do not authorize implementation shortcuts:

1. **Which exact Calibre 9.11.0 capabilities can be enabled?**
   Unresolved until the opt-in disposable-library qualification runs. Default:
   disabled per capability.

2. **How is a newly created record identified?**
   Prefer documented machine-readable output plus a unique before/after scan
   delta. Default: block when exactly one semantic new record is not proven.

3. **How much metadata can be restored?**
   Only individually modeled and qualified fields. Default: preserve unmodeled
   current metadata and block any mapping with collateral changes.

4. **How is a same-format conflict handled?**
   Default: create and verify a separate recovered record; otherwise block. Do
   not overwrite based on user acknowledgement alone.

5. **Are current-state backups optional for no-op recoveries?**
   No mutation means no backup is required to report `NoActionAlreadyRestored`.
   Any recovery execution that could mutate requires a verified backup of every
   affected current record.

6. **Can original Calibre IDs be recreated?**
   No requirement is imposed. Default: accept and report a new ID after semantic
   verification.

7. **Can covers be restored?**
   Default: blocked unless both exact original/current bytes and a separately
   qualified supported command are available.

8. **Can an older application bundle be recovered?**
   Default: blocked unless a closed compatibility adapter and tests explicitly
   establish support.

9. **How long are recovery artifacts retained?**
   Default: indefinitely; Milestone 8 provides no automatic deletion.

10. **How is a source cleanup recovery guard resolved?**
    Default: append a verified recovery-resolution link; never rewrite the
    original history, journal, summary, manifest, or backup.

## Milestone boundary and mandatory safety review

### Planning review completed

- [x] The scope was reviewed against Milestone 8 in `docs/roadmap.md`: rollback
  plans, supported restore operations, verification, and execution-history UI
  are included; later roadmap work is excluded.
- [x] Recovery is based on plan/backup/journal/current-state reconciliation,
  never blind inversion.
- [x] Original Milestone 7 backups are immutable and never modified.
- [x] Current affected state must be backed up and independently verified before
  recovery mutation.
- [x] Unexpected post-execution data cannot be silently lost.
- [x] Destructive recovery occurs only after constructive recovery and
  intermediate verification.
- [x] Semantic success can be reported with changed Calibre record IDs and a
  durable mapping.
- [x] No direct database or manual managed-library file mutation is designed.
- [x] No automatic rollback, automatic resume, automatic retry, or
  rollback-of-rollback is designed.
- [x] All automated destructive tests use explicitly disposable generated
  libraries and never discover the default library.

### Implementation exit review

Before Milestone 8 is considered implementation-complete, review the final diff
and record evidence that:

- [x] The implementation remains within Milestone 8 in `docs/roadmap.md`.
- [x] Recovery is reconciliation-driven and never blindly inverted.
- [x] Original Milestone 7 backups remain byte-for-byte unchanged and available
  through final verification.
- [x] A semantically verified, durably linked current-state backup precedes every
  recovery mutation.
- [x] Unexpected post-execution content, including affected-record metadata, is
  preserved or blocks.
- [x] Constructive semantic verification and exact target verification precede
  every destructive recovery operation.
- [x] Changed Calibre record IDs are uniquely matched, durably mapped, and can
  produce semantic success without numeric-ID assumptions.
- [x] No direct database or manual managed-library mutation exists.
- [x] No automatic rollback/retry/resume/rollback-of-rollback exists.
- [x] All automated destructive tests use reparse-safe disposable generated
  libraries.
- [x] No Milestone 9 or later feature was implemented.

## Progress

- [x] Read repository and nested agent instructions.
- [x] Read the required product, requirements, architecture, domain, safety,
  testing, roadmap, and workflow documents.
- [x] Read accepted ADRs 0001 through 0007.
- [x] Read completed Milestone 0 through Milestone 7 execution plans.
- [x] Read the Milestone 7 handoff.
- [x] Inspected Milestone 7 execution, backup, journal, lease, verification,
  Calibre command, WPF, and test code.
- [x] Reviewed the candidate recovery commands in the official Calibre 9.11.0
  command documentation without treating documentation as qualification.
- [x] Created the Milestone 8 execution plan.
- [x] Add ADR 0008.
- [x] Update authoritative design documents after the implementation contracts
  are verified.
- [x] Implement the Domain and Application recovery model.
- [x] Implement source verification, reconciliation, plan persistence, backups,
  lease integration, journal, and Calibre mappings.
- [x] Implement the WPF recovery workflow.
- [x] Complete automated, formatting, architecture, and source-safety
  verification.
- [x] Complete the post-implementation safety-critical review on 2026-07-25.
- [x] Bind every executable semantic field to the canonical plan,
  reconciliation, and destructive-operation digests.
- [x] Enforce strict semantic record identity, complete metadata restoration,
  exact destructive targets, and semantically cross-validated current backups.
- [x] Make source and recovery journal reconciliation, backup-chain provenance,
  terminal persistence, resolution, and recovery-window shutdown fail-safe.
- [x] Add adversarial regression coverage for every safety-review finding.
- [x] Rerun restore, build, tests, formatting, source audits, and the complete
  diff review after remediation.
- [ ] Run the optional exact Calibre 9.11.0 disposable-library recovery
  qualification. No caller-marked disposable root was supplied, so production
  recovery mutation capabilities remain disabled.

Implementation started on 2026-07-24. The pre-change `dotnet test --no-restore`
attempt was blocked before compilation by the known machine-level `NU1507`
multiple-source configuration described in the Milestone 7 handoff. Verification
used a temporary single-source NuGet configuration without changing repository
package policy. The normal `dotnet restore` and restore-performing
`dotnet format --verify-no-changes` commands remain blocked by that machine
configuration; the temporary-config restore and
`dotnet format --no-restore --verify-no-changes` both succeeded. Exact
real-Calibre recovery capabilities remain disabled until the opt-in 9.11.0
disposable-library suite is supplied and passes.

## Final outcome

### Safety-review remediation completed on 2026-07-25

The reopened safety-critical findings were corrected without broadening the
Milestone 8 mutation surface. Canonical recovery-plan and destructive-operation
digests now cover every executable semantic field, and approval and execution
confirmation bind the exact plan ID, artifact revision, immutable body,
warnings, source chain, library identity, capability profile, backup
destination, and destructive graph.

Source recovery accepts an incomplete Milestone 7 execution only from its
verified plan, journal, manifest, and complete original backup artifacts. A
terminal summary is authoritative only when the source journal contains its
terminal event; an orphan summary is retained as audit evidence but ignored for
state classification. Execution requires the exact selected source-bundle
identity as well as all stored digests, preventing a byte-identical substituted
bundle.

The current-state backup now records and revalidates complete affected semantic
inventory, OPF cross-checks, raw formats, covers, source-chain audit copies, and
both backup-generation identities. The source bundle is read-only and rehashed
at every command gate and final verification. A durable recovery guard,
verified current-backup journal event, and post-backup matching fresh scan all
precede the first mutation marker. Unplanned affected-record formats,
affected-record metadata changes, preserved-format changes, and unrelated
record changes block before success.

Only closed V1 operation kind/phase/risk combinations are representable.
Standalone copy/manual operations and destructive record removal are blocked;
the only dispatchable destructive recovery is an exact semantic record plus
exact format-hash removal target. Every constructive command and preservation
expectation is freshly verified before the separately confirmed destructive
graph can start. Process exit zero is only transport success and never replaces
semantic verification.

Recreated records are discovered from a fresh full scan delta and semantic
facts, never by assuming that a numeric ID can be recreated or safely reused.
The journal persists the initial actual mapping and, after final semantic
verification, a finalized mapping whose logical ID, original ID, actual
recovered ID, cleanup target, restored formats, verified identifiers, and
timestamp must agree with the approved plan and initial mapping. A missing,
substituted, truncated, or out-of-order mapping makes a nominal `Recovered`
terminal state corrupt and unresolved.

Recovery mutation commands cannot be cancelled by terminating the Calibre
process. Cancellation is latched to verified boundaries, close is blocked while
either cleanup or recovery is busy, and journal/storage failure after mutation
cannot be reported as success. The ordinary and opt-in Calibre harnesses require
an exact caller-supplied executable, an explicitly marked operating-system
temporary root, physical non-reparse containment for every test path, and refuse
cleanup unless the complete generated tree is still physical.

The post-remediation full automated suite completed with 408 passed, zero
failed, and two skipped caller-gated real-Calibre tests. The focused recovery
suite completed with 76 passed and one skipped qualification. Build completed
with zero warnings and zero errors, and formatting and whitespace verification
were clean. Normal restore remains blocked only by the documented machine-wide
`NU1507` nine-feed configuration; a temporary one-source configuration restored
the same declared packages successfully and was removed immediately.

Production recovery capabilities remain disabled until the optional exact
Calibre 9.11.0 disposable-library qualification passes. This is the sole open
external acceptance gate and does not weaken the completed code-level safety
remediation.

### Original 2026-07-24 outcome

The 2026-07-24 implementation outcome below was reopened by the safety-critical
review completed on 2026-07-25. It is historical evidence, not current
acceptance, until every reopened exit criterion and remediation item above is
complete.

Implementation and repository verification completed on 2026-07-24. The
Domain, Application, Infrastructure, and WPF recovery slices now provide strict
Milestone 7 source verification, deterministic three-way reconciliation,
canonical immutable plans, explicit approval/revocation, two-generation backup
chaining, semantic record-ID mapping, shared cleanup/recovery leases,
constructive/destructive gates, per-command scans, final semantic verification,
hash-chained journals, append-only history, resolution links, safe-boundary
cancellation, and explicit UI confirmations.

The final build completed with zero warnings and zero errors. The full automated
suite completed with 386 passed, zero failed, and two skipped opt-in
real-Calibre tests. The focused recovery suite completed with 54 passed and one
skipped opt-in qualification. Recovery command-boundary and architecture tests
completed with 24 passed. Formatting
verification and `git diff --check` completed without errors. Source audits
confirmed read-only SQLite access, external create-only recovery storage,
ArgumentList-based no-shell Calibre dispatch, non-permanent removal, no managed
library filesystem mutation, immutable source backups, verified current-state
backup before mutation, constructive verification before destruction, and no
automatic recovery/retry/resume behavior.

The exact Calibre 9.11.0 recovery capabilities remain a release gate. Closed
argument mappings and controlled-executable tests are implemented, but no
production recovery mutation capability is enabled until its opt-in
disposable-library behavior has been qualified. This is a deliberate fail-closed
capability status, not an assertion that the real Calibre mappings have passed.
