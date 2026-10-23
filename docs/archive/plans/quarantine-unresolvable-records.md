# Quarantine unresolvable records

## Objective
As the final one-time cleanup step, move reviewed records whose metadata remains
unresolvable from the cleaned source library into a Calibre library beside it in the
same parent directory. The destination name is the source folder name plus
` - Unresolvable`.

## Scope
- Detect final unresolved, conflicting, and exhausted suspect records after all
  supported metadata and author attempts.
- Include badly described records such as `Unknown`, `Unkown`, and `User` regardless
  of which displayed/catalog/layout fact caused them to be identified.
- Add one final review surface with checked rows, reason codes, formats, and the
  computed destination path.
- Create or reuse the fixed destination library beside the source, under the source's
  parent directory, through supported Calibre APIs.
- Copy all checked records and verify the complete destination before deleting any
  source record.
- Project verified source deletions into the current authoritative state.

## Out of scope
- Moving records before Candidate, rich metadata, Final authors, and managed-name
  normalization are complete.
- Periodic synchronization, retry/resume, rollback, undo, or recovery.
- Direct SQLite writes, filesystem copies/moves/deletes, `calibredb`, shell commands,
  GUI automation, or a second mutation engine.
- Treating a specific library's titles as hard-coded aliases or exceptions.
- Automatically repairing the quarantined library.

## Relevant requirements
- The product is a one-time cleanup of a disposable backed-up source library.
- Analysis remains read-only; only the fixed persistent `calibre-debug` worker may
  mutate libraries through supported Calibre APIs.
- Every mutation run requires the existing explicit source-backup confirmation.
- Failed or ambiguous mutation stops without retry and requires explicit Rescan.
- Ordinary logs contain no titles, authors, paths, custom values, or format bytes.
- Final records must retain all supported local metadata, identifiers, covers,
  custom fields, and exact format bytes in the destination.

## Existing implementation inspected
- Final author review exposes `Known`, `Suspect`, `Unresolved`, and `Conflicting`
  outcomes plus evidence exhaustion and executable proposals.
- Current author suspicion reasons cover missing/placeholder, numeric prefixes,
  title-like authors, malformed separators, pseudonym annotations, and related
  deterministic signals.
- A read-only sample showed several supplied labels in author metadata, but the user
  clarified that examples are general badly described entries and must not be assumed
  to belong to one field. Detection therefore evaluates whole-record facts and allows
  explicit manual inclusion.
- The current fixed worker opens one source library and supports SetMetadata,
  RefreshManagedName, same-library TransferFormat, RemoveFormat, and RemoveRecord.
- Calibre APIs already used by controlled scripts expose `create_book_entry`,
  `add_format`, cover/metadata access, and `remove_books`; cross-library completeness,
  custom-column replication, and destination conflict handling are not yet proven.
- `RemoveRecordWithContentLibraryStateDelta` already exists for projecting a record
  removal that intentionally includes formats, but no current worker operation owns
  that authority.

## Proposed design

### 1. Capability gate on disposable fixtures

Before product implementation, add a destructive synthetic acceptance probe that
uses the installed supported Calibre Python API through `calibre-debug` to prove:

1. reopening/reusing an existing non-empty compatible destination at the fixed path;
2. independent source and destination cache handles in one fixed process;
3. recreation of non-composite custom-column definitions needed by selected records;
4. creation of records with complete ordinary metadata, identifiers, tags, rating,
   comments, languages, series/index, publication date, author display/sorts, and
   custom values;
5. cover copy and exact cover SHA-256 read-back;
6. format streaming through bounded temporary files and exact size/SHA-256 read-back;
7. preservation of source records during the entire copy/verification phase; and
8. duplicate destination record creation with `add_duplicates=True` when equivalent
  metadata already exists, without overwriting or merging existing records; and
9. supported source deletion with formats only after destination verification.

New-library creation itself is proven on 2026-10-16 with installed Calibre 9.12.
Through `calibre-debug`, `calibre.library.db(absent_path, read_only=False).new_api`
created the absent library directory and `metadata.db`, initialized with zero records,
accepted a synthetic record through `create_book_entry(..., add_duplicates=True)`,
and reopened with the same generated library UUID and exact title/author read-back.
The disposable parent and library were removed after the probe. No existing library
was opened. Keep creation in the product capability test so future supported Calibre
versions must reproduce this behavior before mutation.

If any required field/schema/cover/format behavior cannot be proven with supported
APIs, stop the feature. Do not add a direct database, shell, or filesystem fallback.

### 2. Final disposition policy

Add a versioned provider-neutral Domain `FinalRecordDispositionPolicy`. It consumes
the final authoritative book, Final author outcome, and bounded record-quality facts.
Catalog title/author metadata, identifiers, format state, and managed layout labels
may contribute explainable evidence, but no single field or folder name is assumed to
be the record's semantic author/title. A record is proposed for quarantine only when
the workflow is Completed and:

- author resolution is `Unresolved` or `Conflicting`; or
- author resolution is `Suspect`, there is no currently applicable checked/candidate
  correction, bounded embedded/front-matter inspection is exhausted, and one or more
  deterministic strong reasons remain; or
- a normalized bounded record label is exactly a supported placeholder such as
  `Unknown`, `Unkown`, or `User`; or
- the user explicitly includes the current record from the final review.

Extend generic suspicion vocabulary only with independently justified, bounded
patterns needed by this domain, for example numeric-only labels, a leading
parenthesized four-digit year followed by title-like text, repeated/trailing
placeholder punctuation, a small explicit placeholder set, and generic book-label
forms such as `Deel <number>`. Apply these to bounded candidate labels with retained
field/layout provenance rather than presuming they are authors. Add negative fixtures
for legitimate numbered names, initials, organizations, years in credible names, and
ordinary punctuation. Never encode the user's example strings as aliases.

Each proposal retains stable reason codes and source status. It does not become
mutation authority.

### 3. Final quarantine review

Add an `Unresolvable` tab/action available only after:

- authoritative current workflow phase `Completed`;
- no active library operation;
- Final author preparation has run against the current generation/revision;
- no checked/applicable final-author corrections remain; and
- every proposed target still matches current title, ordered authors/sorts, formats,
  fingerprints, cover state, and policy versions.

The grid shows Check, status, reasons and provenance (title, author, identifier,
layout, or manual), record ID, current title/authors, formats, cover/custom-field
availability, and the fixed destination preview. All final
unresolved, conflicting, and exhausted strong-suspect proposals default checked. The
user can change any selection. Exact
`Unknown`/`Unkown`/`User` placeholder records are included. The review also supports
explicit add-by-record-ID/manual selection for badly described records missed by
deterministic signals. Decisions are session review state only; no generalized
durable quarantine history is introduced.

The destination uses the source's parent directory, not the source directory itself:

```text
<parent of source>\<source folder name> - Unresolvable
```

Derive this path from the canonical source parent; do not accept a path inside the
source library. Canonicalize both paths and reject roots that are equal, nested,
reparse points, reserved/invalid, overlong, or on an unsupported location. If absent,
create the destination through the accepted Calibre API. If present, require a valid
compatible Calibre library at the exact fixed path; capture and bind its
UUID/schema/current record set to the review. Never overwrite/reinitialize it. Show
the final path and whether it will be created or reused before confirmation without
writing it to ordinary logs. Require external-backup confirmation for both source and
an existing destination.

### 4. Typed two-library worker protocol

Extend the existing fixed embedded worker and Application contracts; do not create a
second worker implementation. Add a dedicated quarantine run/operation family rather
than overloading same-library TransferFormat:

- `CreateQuarantineLibrary`
- `CopyRecordToQuarantine`
- `VerifyQuarantineLibrary`
- `RemoveQuarantinedSourceRecordWithContent`

The worker starts from the source root/UUID as today. The quarantine request contains
one validated destination root, expected destination existence/UUID/schema/starting
record identities, plus bounded typed record expectations. The worker opens or
creates the destination using supported Calibre APIs and never accepts arbitrary
scripts or operation kinds.

For each selected source record:

1. validate live source identity and protected metadata against the review;
2. create one destination record with complete supported metadata and per-author
  sorts, always requesting duplicate creation when equivalent destination metadata
  already exists; never merge into or replace an existing record;
3. stream each source format through the existing worker temp root, verify source
   size/SHA-256, add it through the destination API, then verify destination bytes;
4. copy/verify cover bytes;
5. verify all ordinary/local/custom metadata and the destination managed path; and
6. return only technical source/destination IDs, counts, hashes, and failure codes.

Metadata equivalence/conflict is therefore resolved before it becomes an operation
failure: a fresh destination ID is expected and verified, then cleanup continues.
If Calibre cannot create a duplicate or returns ambiguous identity, stop rather than
continuing after failure. Bound records, formats, metadata values, bytes, message
sizes, temporary space, and operations per chunk. Preserve record ordering and
deterministic operation IDs.

### 5. Copy-all, verify-all, then delete

Cross-library move is not atomic. Use this fail-safe ordering:

1. create or open/revalidate destination;
2. copy every selected record;
3. reopen/read back destination and verify complete selected count, metadata, covers,
   formats, and hashes;
4. verify every pre-existing destination record/schema fact is unchanged and every
  selected source has one newly created verified destination ID;
5. only after complete destination verification, delete checked source records with
  content through the typed worker operation; and
6. append `RemoveRecordWithContentLibraryStateDelta` only for individually verified
   source deletions, then checkpoint the source state as Completed.

This ordering matches Calibre 9.12's cross-library Move implementation. Its copy
worker processes the selected records first and returns the successfully processed
IDs; only after that worker completes does the GUI delete those source IDs. If the
copy phase is canceled, Calibre explicitly deletes no source records. This product is
stricter: any failed or ambiguous copy/verification stops the run before all source
deletion rather than continuing with other records.

No source deletion may begin if any destination record is missing or ambiguous.
Deletion is record-by-record or bounded chunks with exact read-back. The source
backup confirmation and, when reused, destination backup confirmation remain
mandatory; the app does not create a backup.

### 6. Failure semantics

- Destination create/open/copy/verification failure: stop; source remains untouched;
  leave any newly created records in the destination for explicit external review;
  require source Rescan before any further mutation.
- Source deletion failure after full destination verification: stop immediately;
  destination remains complete, source may contain a verified deleted prefix; mark
  source state uncertain and require Rescan. Do not infer success, continue, retry,
  delete the destination, or offer rollback.
- Process crash/timeout/ambiguous read-back: same stop-and-Rescan rule.
- Existing invalid/incompatible/changed destination at preflight or execution: stop
  before source deletion. A valid bound existing destination is reused.
- Equivalent destination metadata is not a failure: create and verify a separate
  duplicate destination record, then continue. Any inability/ambiguity doing so is a
  normal failed mutation and stops the run under the global safety rule.

Records copied by a failed attempt may remain in the destination. A later run may add
another duplicate; the app does not infer or reconcile earlier copies.

### 7. Successful result

On success, show aggregate moved record/format/cover/byte counts and the destination
path in the immediate UI only. Keep technical logs path-private. Clear quarantine
review state, keep source workflow `Completed`, and project the reduced source
snapshot. The destination is an ordinary independent Calibre library; the app does
not synchronize or maintain it afterward. Existing destination records remain
unchanged; each moved source maps to one newly created destination record even when
equivalent metadata already existed.

## Files expected to change
- Domain: final disposition values/policy/reasons and focused tests.
- Application: review workspace/use case, destination path port, typed quarantine
  contracts, orchestration, progress/result, source state deltas and tests.
- Infrastructure: path/disk preflight, fixed worker protocol/implementation,
  Calibre API acceptance probe, parser/session tests and DI.
- WPF: final review grid, destination preview, confirmation service, command/progress,
  operation exclusion and tests.
- Architecture/privacy tests, core documentation, release acceptance script and plan.

## Safety considerations
- Do not inspect or mutate while Calibre/Cleaner writers are active. During planning,
  read-only audit detected concurrent writes from Cleaner PID 52064, calibre-debug
  PID 44028, and calibre-parallel PID 23512; inspection stopped immediately.
- Require the normal complete external backup confirmation for the source and, when
  reused, the existing destination.
- Use only supported Calibre APIs in the existing fixed worker.
- Never write source/destination SQLite directly or copy/delete managed files.
- Destination is either absent or an explicitly bound compatible Calibre library.
  Existing records are never overwritten/merged; equivalent metadata creates a new
  duplicate record.
- Preserve complete supported metadata, custom fields, covers, and exact formats.
- Copy/verify all before any source delete.
- Stop on every failure/ambiguity; no retry, continuation, rollback, or recovery.
- Keep ordinary logs free of metadata, content, paths, provider payloads and custom
  values.

## Implementation steps
1. Finish active mutation; run a fresh read-only audit only with all writers closed.
2. Implement and manually run the disposable Calibre API capability gate.
3. Add labeled whole-record final-disposition policy fixtures, provenance, manual
  inclusion and frozen positive/negative metrics.
4. Add final review workspace and WPF review-only surface.
5. Add absent/create and existing/reuse destination path, UUID, record-set,
   disk/custom-schema preflight.
6. Extend typed contracts and fixed worker with destination create/copy/verify.
7. Add copy-all verification and source delete-with-content phase.
8. Project verified deletions and wire stop/uncertainty handling.
9. Run focused tests, standard verification, packaged disposable-library acceptance,
   complete diff/privacy/resource review, and documentation reconciliation.
10. Archive this plan only after measured destructive acceptance succeeds.

## Tests
- Domain positive/negative corpus for unresolved/conflicting/exhausted suspect,
  whole-record placeholder labels (`Unknown`, `Unkown`, `User`), provenance and
  explicit manual inclusion; deterministic reason/order/version tests.
- Workflow gate tests: unavailable before Completed/final evidence, stale generation,
  active operations, pending corrections, unchecked rows and changed/incompatible
  destination state.
- Path tests: exact same-parent naming, Unicode, invalid/reserved/long names, rejection
  of source-contained/equal/unrelated paths, nesting, reparse, absent destination
  creation, valid existing destination reuse, wrong UUID/schema, concurrent
  destination changes and free-space bounds.
- Worker protocol malformed/unknown/oversize/order tests.
- Synthetic source/destination tests preserving all ordinary metadata, multiple
  authors/sorts, identifiers, tags, ratings, comments, supported custom columns,
  cover and multiple formats with exact hashes.
- Copy failure proves zero source deletions.
- Destination verification failure proves zero source deletions; pre-existing
  destination record/schema identities remain unchanged.
- Equivalent/conflicting destination metadata creates another verified record and
  continues; duplicate-create failure/ambiguity stops without source deletion.
- Source deletion failure proves stop, uncertain source state, complete destination,
  and no continued suffix.
- Cold/warm memory/temp/disk/progress bounds and no metadata/path log leakage.
- Architecture tests preserve Domain/Application/Infrastructure/WPF direction and
  the single fixed worker mutation boundary.
- Packaged Windows acceptance on disposable synthetic libraries only; never the
  personal Calibre library.

## Verification commands
```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build --maxcpucount:1
dotnet format --verify-no-changes
git diff --check
dotnet list package --vulnerable --include-transitive
```

Also run the new explicit destructive capability/packaged acceptance command against
fresh disposable source/destination roots and record actual record/format/cover/hash
counts, cold/warm timing, failure-boundary evidence, and zero source deletion before
complete destination verification.

## Risks
- Two-library mutation is not atomic; failure can leave a complete/partial destination
  plus a partial source deletion prefix. Reusing a destination may add another
  duplicate after a prior failed run because the app does not infer/reconcile earlier
  copies. Ordering and external backups reduce data-loss risk but cannot make it
  transactional.
- Calibre may not expose supported APIs for creating an empty library or reproducing
  every custom-column schema/value. The capability gate may block the feature.
- Custom plugins/hooks may change destination data during `add_format`; exact read-back
  must detect this and stop.
- Large quarantine sets can require substantial destination/temp disk and long worker
  lifetime; enforce explicit bounds and truthful progress.
- Heuristic suspect patterns can quarantine legitimate records; use labeled negatives,
  field/layout provenance, explicit manual inclusion, explainable reasons and
  mandatory review checkboxes.
- Destination can contain malformed metadata and duplicate records by design; it is
  quarantine, not an automatically cleaned secondary library.
- Destination path disclosure is necessary in the UI but prohibited in ordinary logs.

## Unresolved questions
None. The installed Calibre 9.12 capability and fixed-worker acceptance probes passed.

## Progress
- Current docs, final-author status model, worker operations, Calibre scripts, safety
  rules, architecture and tests inspected.
- Installed Calibre 9.12 API creation probe passed on a disposable absent path:
  directory/database creation, empty initialization, record creation, close/reopen,
  stable library UUID, exact title/author read-back, and cleanup all succeeded.
- Disposable API capability acceptance passed for custom schema/value replication,
  duplicate record creation, metadata, cover/format hashes, reopen, and source delete
  only after verification. Calibre requires cache reopen after schema changes and a
  library root shorter than 75 characters.
- Fixed worker protocol 1.4 acceptance passed against disposable Calibre libraries:
  destination creation, exact copied record read-back, duplicate-add mode, custom
  values, and separately authorized source deletion all succeeded.
- Protocol 1.4 managed-name acceptance also passed: author display/sort and managed
  path/basename read-back remained exact and format bytes were unchanged.
- Standard verification passed: restore, warnings-as-errors build, 1,110 tests,
  formatting, diff whitespace, and direct/transitive vulnerability audit. No known
  vulnerable NuGet packages were reported.
- Standard Windows package acceptance passed for the 79-file framework-dependent
  archive. Manifest/dependency/forbidden-file checks and executable smoke passed;
  package SHA-256 is
  `bc9db2a32448589a20e18a535e755c072f44765f32d814c7c36d8edd5d558eb4`.
- User confirmed the fixed ` - Unresolvable` suffix, all final `Conflicting` rows
  default checked, and derived-only composite verification with a hard stop on
  incompatible custom-column schemas.
- A representative sample happened to appear in author metadata, but examples are
  intentionally treated as general badly described records rather than author/title
  assertions.
- Concurrent writer activity detected during read-only audit; further personal-library
  inspection stopped. Implementation and mutation acceptance used disposable
  synthetic libraries only.

## Final outcome
Implemented and verified on disposable synthetic libraries with installed Calibre
9.12. The final workflow now prepares reviewed whole-record quarantine proposals,
binds the fixed sibling destination, copies and verifies all selected records through
the fixed worker, then deletes and projects verified sources. Release packaging was
not changed because the worker remains an embedded resource in the existing package.