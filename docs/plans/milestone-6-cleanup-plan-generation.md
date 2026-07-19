# Milestone 6 Cleanup Plan Generation

This execution plan is a living document. During implementation, keep `Progress`, decisions, deviations, failed approaches, verification results, and `Final outcome` current. Follow `PLANS.md`, every applicable `AGENTS.md`, and all accepted ADRs.

This plan covers Milestone 6 only. It authorizes generation, validation, lifecycle review, deterministic JSON import/export, approval, revocation, and WPF presentation of non-executable cleanup plans. It does not authorize a Calibre mutation, simulation, backup, lock, command, script, execution service, or partial application.

## Objective

Convert one current, explicitly reviewed Milestone 5 consolidation recommendation into an immutable, versioned, descriptive cleanup plan.

The plan must identify the expected source library, duplicate group, involved record IDs, surviving target record, metadata source, retained format sources, formats and records proposed for later removal or replacement, expected pre-operation metadata and file states, required backups, warnings, provenance, validation state, approval, and revocation.

The resulting artifact must be reviewable in WPF and round-trip through deterministic, versioned JSON stored outside the Calibre library. It remains inert data. Milestone 6 must not execute, simulate, prepare, or partially apply any described change.

## Scope

- Generate a cleanup plan only through an explicit user action on one current Milestone 5 reviewed recommendation.
- Require the reviewed result to be `Accepted` or `ManuallyAdjusted`, current, internally consistent, and fully resolved.
- Reject deferred, unreviewed, keep-separate, not-duplicate, stale, unavailable, unresolved, or data-discarding inputs before publishing a plan.
- Use the reviewed metadata source as the deterministic surviving target record in cleanup-plan schema V1 while representing target and metadata source as separate concepts.
- Record every duplicate-group member ID and exactly one surviving target.
- Record one final source for every retained canonical format.
- Describe existing formats expected to be retained in place, copied conceptually to the target, replaced, or removed with a source record.
- Describe every non-target record proposed for removal only after all of its retained contributions have been preserved and verified.
- Capture the complete expected metadata state of every involved record.
- Capture the expected relative path, status, length, SHA-256, creation time, last-write time, and provider-neutral attribute bits for every affected format file.
- Require a current hash and observed file state for every present affected ebook format.
- Record declarative backup requirements for affected record metadata, every affected format, covers where Calibre reports one, managed paths and file facts, the plan artifact, and the later execution audit.
- Preserve bounded provenance for the duplicate group, generated recommendation, reviewed recommendation, user override, recommendation input/model versions, and cleanup-plan schema version.
- Model blocking errors, non-blocking warnings, and informational notices separately.
- Support immutable lifecycle revisions with `Draft`, `Valid`, `Blocked`, `Approved`, `Stale`, and `Revoked` states.
- Detect staleness by comparing the plan with a current completed snapshot and current recommendation identity.
- Approve and revoke plans explicitly without writing to Calibre.
- Export and fully import one cleanup plan per deterministic, versioned JSON artifact.
- Reject cleanup-plan import/export paths at or below the selected Calibre library, including physical reparse-point aliases.
- Add a focused WPF cleanup-plan workspace showing contents, expected state, issues, provenance, lifecycle, approval, and revocation.
- Retain session plan revisions and imported plans in application memory unless the user explicitly exports them.
- Add Domain, Application, Infrastructure, architecture, serialization, safety, WPF, cancellation, malformed-input, and scale tests using only synthetic fixtures.

## Out of scope

- Invoking Calibre or discovering a Calibre executable.
- Calling `calibredb`, launching a process, or generating command arguments.
- Updating `metadata.db` directly or indirectly.
- Copying, moving, renaming, replacing, overwriting, or deleting an ebook, cover, metadata file, record directory, or Calibre record.
- Creating a backup, choosing a backup destination, verifying a backup, or restoring one.
- Acquiring or simulating a library lock.
- Executing, dry-running, simulating, staging, partially applying, or estimating execution of a cleanup plan.
- Calling or scaffolding a future cleanup execution service.
- Generating PowerShell, shell, batch, Python, Calibre CLI, or other executable scripts.
- Reloading Calibre after a mutation, verifying a mutated library, audit execution history, or rollback.
- Adding PDF/AZW3/MOBI analysis, content equivalence, fuzzy duplicate matching, AI, or network services.
- Changing Milestone 5 recommendation policy, review states, recommendation JSON schema, or review-artifact import behavior.
- Treating a Milestone 5 `recommendation-review/1.0` JSON artifact as a cleanup plan.
- Automatically generating a plan as part of scanning or recommendation generation.
- Automatically approving a plan.
- Persisting plan files inside the selected library or an application cache.
- Implementing Milestone 7 or later abstractions speculatively.

## Relevant requirements

- `AGENTS.md`: destructive work requires an immutable, explicitly approved plan; unique formats cannot be silently discarded; approved plans are immutable; analysis remains read-only; later execution must revalidate.
- `PLANS.md`: this cross-project, architecture- and safety-sensitive milestone requires a maintained execution plan with the repository-standard sections.
- `docs/product-vision.md`: safety, explainability, reversibility, local operation, and no silent loss take precedence.
- `docs/functional-requirements.md`: cleanup plans contain record IDs, metadata/format choices, removals, expected hashes, backup requirements, warnings, and approval details.
- `docs/architecture.md`: plans and invariants belong in Domain, use cases and ports in Application, JSON/filesystem integration in Infrastructure, and interaction/presentation in WPF.
- `docs/domain-model.md`: cleanup plans cannot remove their targets, destructive plans require backups and expected states, and approved plans are immutable.
- `docs/duplicate-detection.md`: exact binary and exact metadata groups remain distinct evidence; neither alone authorizes removal.
- `docs/quality-scoring.md`: an EPUB preference is valid only with current compatible assessments and decisive evidence; a score is not content equivalence.
- `docs/safety-and-rollback.md`: plan execution must later revalidate library identity, records, paths, hashes, format state, target, conflicts, backup destination, and tooling. Milestone 6 records the applicable preconditions and backup requirements but performs none of that execution work.
- `docs/test-strategy.md`: plans, stale plans, approval, missing/malformed inputs, safety invariants, architecture boundaries, and WPF behavior require automated coverage with synthetic fixtures.
- `docs/roadmap.md`: Milestone 6 is immutable plans, expected states, validation, approval, export/import, and no mutation.
- `docs/workflows/implement-feature.md`: preserve safety invariants, use async/cancellation at integration boundaries, add focused tests, and avoid future roadmap work.
- ADR 0001: database access remains read-only/query-only and no SQL mutation path is added.
- ADR 0002: preserve clean dependency direction and third-party/integration isolation.
- ADR 0003: future mutations use supported Calibre tooling, but Milestone 6 adds no tooling or mutation boundary.
- ADR 0004: plan generation consumes only provider-neutral assessment values and never parser types or ebook content.
- ADR 0005: a recommendation review artifact is not a cleanup plan. Generated recommendation, reviewed state, model version, and input identity remain distinct provenance.
- Nested Domain, Application, Infrastructure, WPF, and test instructions remain applicable.

## Existing implementation inspected

The repository is at completed Milestone 5 commit `5a96eca`, with unrelated modified Rider state in `.idea/workspace.xml` and an untracked `.ai/` directory. Implementation must preserve and exclude those files from milestone claims.

### Current analysis and recommendation flow

- `ScanLibraryUseCase` validates the selected library, reads schema 27 through the read-only SQLite boundary, resolves and hashes every safe format, assesses EPUBs, creates exact binary and exact metadata groups, generates recommendations, and publishes one complete `LibrarySnapshot`.
- `LibrarySnapshot` contains identity, scan time, books, findings, exact binary groups, exact metadata groups, EPUB assessments, and exactly one `ConsolidationRecommendation` per metadata group.
- `LibraryIdentity` contains Calibre UUID, schema version, and the current absolute library root. Milestone 5 JSON deliberately serializes only UUID/schema.
- `CalibreBook` carries stored title, author sort, ordered authors, identifiers, formats, relative directory, publisher, publication date, series/index, ordered languages, and Calibre's stored cover flag.
- `BookFormat` carries canonical format, stored basename, expected relative path, file status, and optional fingerprint. It does not retain the verified post-hash timestamps/attribute observation needed by Milestone 6.
- `FormatHashResult` already returns a provider-neutral `FormatFileObservation` containing length, creation time, last-write time, and integer attribute bits. `ScanLibraryUseCase` uses that observation for EPUB inspection but does not preserve it in `BookFormat` or `LibrarySnapshot`.
- `ConsolidationRecommendationPolicy` records model `consolidation-recommendation/1.0.2`, canonical input identity `recommendation-input/1.0.1`, metadata and per-format selections, every format candidate, exact-binary exclusions, unresolved/unavailable states, record dispositions, reasons, warnings, and confidence.
- The recommendation input identity includes source UUID/schema, group/members, stored metadata, format status/path/fingerprint, exact-binary membership, assessment association/fingerprint/status/version/decisive findings, and relevant library findings. It excludes scan and export time.
- `ApplyRecommendationOverrideUseCase` keeps generated values immutable, validates current overrides, produces an effective reviewed selection, preserves generated retained-separate records, and supports `Unreviewed`, `Accepted`, `ManuallyAdjusted`, `Deferred`, `KeepSeparate`, and `NotDuplicates`.
- `RecommendationReviewStalenessEvaluator` retains stale override details but clears the effective selection.
- A current accepted review has a current override even when it accepts the generated recommendation unchanged. A reset review is `Unreviewed` and must not be plan-eligible.

### Current JSON and path boundaries

- `RecommendationJsonSerializer` creates deterministic UTF-8, LF, one-final-newline JSON and validates the complete review-artifact graph before writing.
- Its reader only inspects and validates `recommendation-review/1.0`; it does not reconstruct a Domain recommendation or provide a user-facing import workflow.
- `VersionedJsonRecommendationExporter` writes through an external temporary sibling and publishes only after serialization succeeds.
- `RecommendationExportPathGuard` canonicalizes and physically resolves the library/destination, rejects the library root and descendants including aliases, requires an existing external directory, and cleans up only its own external temporary file.
- The recommendation JSON contains no plan, removal, backup, approval, expected-state, command, or execution data. Milestone 6 must add a separate schema/store rather than reinterpret this artifact.

### Current WPF review workflow

- `MainWindowViewModel` retains the current snapshot, session review state keyed by library UUID/group ID, and the selected metadata candidate.
- The metadata-candidate tab displays generated/reviewed metadata and format selections, record dispositions, reasons, warnings, freshness, and review actions.
- Review edits remain separate until explicitly saved. Export writes only the non-destructive review artifact.
- `MainWindowViewModel` is already large. Milestone 6 should add a cohesive cleanup-plan workspace ViewModel rather than placing all new lifecycle, import/export, and detail logic directly in the main ViewModel.
- `App.xaml.cs` is the WPF composition root and the only WPF production file allowed to reference Infrastructure.

### Current tests and safety guards

- Domain tests cover recommendation invariants, complementary formats, exact-binary exclusions, unresolved non-identical formats, EPUB assessment requirements, retained-separate records, redundancy coverage, metadata conflicts, canonical input identity, and immutable review values.
- Application tests cover deterministic generation, cancellation, explicit exclusions, staleness, generated retained-separate preservation, structured override errors, and export/current-instance validation.
- Infrastructure tests cover deterministic review JSON, malformed/unknown schemas, graph validation, external atomic publication, reparse aliases, cancellation, and no artifacts inside the library.
- WPF tests cover review display, stale choices, unavailable versus unresolved formats, session state, commands, and real synthetic-library flow.
- Recursive safety fixtures compare database and ebook names, bytes/hashes, sizes, attributes, and timestamps and assert no SQLite/report/temp sidecars inside the library.
- Architecture tests keep JSON/filesystem integration in Infrastructure and forbid `CleanupPlan` in production as a Milestone 5 future-scope guard. Milestone 6 must replace that guard with one that permits inert plan models but still forbids Calibre CLI, processes, backups, mutation, execution, scripts, and rollback.

## Proposed design

### Milestone boundary and end-to-end flow

Cleanup-plan generation is a separate explicit action after review; it is not added to `ScanLibraryUseCase`.

1. The user completes or accepts a Milestone 5 review for the selected metadata candidate group.
2. WPF invokes `GenerateCleanupPlanUseCase` with the current `LibrarySnapshot` and the selected `ReviewedConsolidationRecommendation`.
3. The use case proves that the reviewed value belongs to the current snapshot and evaluates the complete eligibility rules below.
4. If any eligibility rule fails, it returns no cleanup plan and an ordered `CleanupPlanValidationResult` containing blocking errors, warnings, and notices for display.
5. For an eligible review, the use case constructs an immutable `Draft` definition from current in-memory metadata, fingerprints, observed file states, generated/reviewed provenance, and injected time/ID.
6. The Domain policy validates target survival, complete format coverage, expected-state coverage, removal coverage, backup coverage, provenance, and no-silent-loss invariants.
7. A valid draft becomes a `Valid` immutable artifact revision. No file is written automatically.
8. WPF displays the plan and lets the user validate it against the current snapshot, approve it explicitly, revoke an approval with a reason, export it, or import a separate plan JSON file.
9. A successful later scan causes every in-memory plan for that source UUID to be compared with the new completed snapshot. Any safety-relevant mismatch creates a new immutable `Stale` lifecycle revision; it never edits or reactivates the old approved revision.
10. Import requires a current completed snapshot, reconstructs and validates the complete cleanup-plan domain aggregate, enforces that the file is outside that snapshot's selected library root, and immediately evaluates staleness before exposing approval actions.

Every step is descriptive or read-only. No plan action invokes a filesystem mutation inside the library, a database write, Calibre, a backup service, or a future executor.

### Exact eligibility rules

`CleanupPlanEligibilityPolicy` returns an ordered `CleanupPlanValidationResult`. It must not publish a `CleanupPlan`, allocate a publishable plan ID, write JSON, or change session review state when any blocking rule fails.

A reviewed result is eligible only when all of these are true:

1. The supplied `ReviewedConsolidationRecommendation.Generated` is the exact current recommendation instance associated with one current snapshot metadata group.
2. The duplicate group exists once, has at least two distinct current members, and its ordered members exactly match the recommendation.
3. Review freshness is `Current`.
4. Review status is `Accepted` or `ManuallyAdjusted`.
5. A current override exists, was created for the generated model/input versions, and references only current group members and represented format candidates.
6. The effective reviewed selection is non-null.
7. The effective metadata source is a current group member.
8. No generated or effective retained-separate record exists.
9. Every represented canonical format has exactly one effective `Selected` source.
10. No format remains `UnresolvedConflict`, `Unavailable`, or `ExplicitlyExcludedByUser`.
11. Every effective format source is a present current `BookFormat` owned by the referenced group record and matches format/path/fingerprint exactly.
12. Every declared format on every group record appears exactly once in the recommendation candidate graph; no current format is omitted and no outside format is introduced.
13. Every affected declared format is `Present` and has both a SHA-256 fingerprint and verified post-hash observation. Because every non-target record is proposed for removal, every declared group format is affected.
14. Every EPUB assessment used by a generated or reviewed EPUB preference matches the current record/format/path/fingerprint, has the expected analyzer/scoring versions, and is represented in the current recommendation input identity.
15. The current recommendation input/model versions equal the reviewed versions. A stale recommendation cannot be rescued by a current-looking override.
16. Strong identifier, language, material-year, edition-wording, or series conflict that Milestone 5 classified as blocking/retain-separate is absent.
17. The effective format selection retains one destination for every canonical format type represented by the group.
18. Every non-selected byte-distinct same-format candidate was resolved by the explicit current review. The plan records it as a reviewed replacement/removal, emits a warning, and requires its backup; it is never silently treated as redundant.
19. The V1 target record is the effective metadata source, is not proposed for removal, and remains the one surviving record.
20. The proposed record-removal set is exactly `group members - target`; therefore at least one record survives and the target is never removed.
21. Every source record/format contribution has a retained target destination before its source record can later be removed.
22. Every format proposed for later replacement/removal and every record proposed for later removal has a matching required backup item.
23. The constructed definition passes content-digest, expected-state, path-safety, provenance, collection-bound, and no-silent-data-loss invariants.

At minimum, these conditions produce blocking generation errors:

| Condition | Stable issue code |
| --- | --- |
| Review is unreviewed | `PLAN.REVIEW_REQUIRED` |
| Review is deferred | `PLAN.REVIEW_DEFERRED` |
| Group is marked keep separate or contains retained-separate records | `PLAN.RECORDS_KEEP_SEPARATE` |
| Group is marked not duplicates | `PLAN.NOT_DUPLICATES` |
| Review/recommendation is stale or mismatches the snapshot | `PLAN.RECOMMENDATION_STALE` |
| Override references a record/format outside the group | `PLAN.OVERRIDE_OUTSIDE_GROUP` |
| Effective selection is absent | `PLAN.EFFECTIVE_SELECTION_REQUIRED` |
| Same-format conflict remains unresolved | `PLAN.FORMAT_UNRESOLVED` |
| A final format is explicitly excluded | `PLAN.FORMAT_EXCLUDED` |
| A required/current format file is missing, inaccessible, invalid, or changed | `PLAN.SOURCE_FILE_UNAVAILABLE` |
| A required affected file has no current hash | `PLAN.SOURCE_HASH_REQUIRED` |
| A required affected file has no verified observation | `PLAN.FILE_OBSERVATION_REQUIRED` |
| An EPUB assessment used by the recommendation is stale/incomparable | `PLAN.EPUB_ASSESSMENT_STALE` |
| A blocking identifier/language/year/edition/series conflict remains | `PLAN.BLOCKING_METADATA_CONFLICT` |
| A canonical format has no retained destination | `PLAN.UNIQUE_FORMAT_NOT_RETAINED` |
| A current declared format is absent from recommendation provenance | `PLAN.FORMAT_COVERAGE_INCOMPLETE` |
| Target is invalid or in the removal set | `PLAN.TARGET_INVALID` |
| No record survives | `PLAN.NO_SURVIVING_RECORD` |
| Removal/backup/dependency coverage is incomplete | `PLAN.SAFETY_COVERAGE_INCOMPLETE` |
| Any result would silently discard data | `PLAN.SILENT_DATA_LOSS` |

The generation outcome retains all applicable issues rather than stopping at the first one. Errors are ordered by severity, subject record, format, code, and message. No generic exception text is shown.

### V1 target and intended-change policy

Milestone 5 selects a metadata source but has no separate target choice. Cleanup-plan schema V1 uses this deterministic rule:

- The effective reviewed metadata source is the surviving target record.
- `CleanupPlan.TargetRecordId` and `MetadataRetentionInstruction.SourceRecordId` are stored separately even though they are equal in V1.
- The target choice is explained by informational issue `PLAN.TARGET_IS_METADATA_SOURCE`.
- A future target-selection policy would require a cleanup-plan schema/policy version change; Milestone 6 does not add another user override.

The plan is descriptive. It names conceptual future outcomes without predicting Calibre-managed destination filenames or command sequences:

- retain the target record;
- retain the target's stored metadata from the reviewed metadata source;
- retain one final copy of each canonical format on the target;
- use an existing target format in place when it is already the reviewed source;
- conceptually copy a selected format from another record to the target in a later milestone;
- replace a target's existing same-format file when the reviewed source is another byte-distinct candidate;
- remove byte-identical alternatives;
- remove original selected-source copies only as part of their non-target record removal and only after target retention is verified;
- remove each non-target record only after every selected contribution is preserved and verified.

No instruction contains a command, process argument, source-code callback, filesystem destination, executable ordering language, or implementation-specific Calibre operation.

### Cleanup-plan Domain model

Add integration-free immutable values under `CalibreLibraryCleaner.Domain.Plans`. Exact filenames can be cohesive where that matches current conventions, but the following concepts remain distinct.

#### Identity, schema, revision, and state

- `CleanupPlanId`: strongly typed UUID. Production uses an Application-owned ID generator; tests inject fixed IDs.
- `CleanupPlanSchemaVersion`: initially `cleanup-plan/1.0`.
- `CleanupPlanPolicyVersion`: initially `cleanup-plan-policy/1.0.0`; changes to eligibility, target choice, coverage, backup, or lifecycle rules require a bump.
- `CleanupPlanArtifactRevision`: positive monotonic integer for immutable lifecycle revisions of one plan ID.
- `CleanupPlanState`: `Draft`, `Valid`, `Blocked`, `Approved`, `Stale`, `Revoked`.
- `CleanupPlanContentDigest`: SHA-256 over a versioned, length-prefixed canonical representation of all safety-relevant definition content.
- `CleanupPlanInputIdentity`: source UUID/schema, duplicate-group ID/member IDs, recommendation model/input versions, cleanup-plan policy version, and definition digest. It never contains the absolute library root.

`CleanupPlanId` distinguishes regeneration attempts. A regenerated plan receives a new ID even when it comes from the same duplicate group. Artifact revision distinguishes lifecycle transitions for one frozen definition.

#### Plan aggregate

`CleanupPlan` contains:

- plan ID, schema version, policy version, artifact revision, state, content digest, and input identity;
- creation and last-validation times;
- `ExpectedLibraryState`;
- target record ID and ordered involved record IDs;
- `MetadataRetentionInstruction`;
- ordered `FormatRetentionInstruction` values;
- ordered `FormatRemovalInstruction` values;
- ordered `RecordRemovalInstruction` values;
- ordered `BackupRequirement` values;
- `CleanupPlanProvenance`;
- generation/current `CleanupPlanValidationResult`;
- nullable `CleanupPlanApproval`;
- nullable `CleanupPlanRevocation`;
- ordered lifecycle transition history.

All constructors defensively copy collections and expose get-only values. There are no public setters or mutation methods. Lifecycle policies return a new aggregate revision that reuses the exact frozen definition/content digest.

#### Expected pre-operation state

`ExpectedLibraryState` contains:

- expected Calibre library UUID and schema version;
- duplicate-group ID and exact ordered member IDs;
- ordered `ExpectedRecordState` for every member;
- recommendation input/model versions used to generate the plan.

`ExpectedRecordState` contains:

- Calibre record ID;
- stored title and author sort;
- ordered author IDs, names, and sort names;
- ordered identifier type/value pairs;
- publisher, raw publication date, series/index, ordered languages, and stored cover flag;
- Calibre-managed relative directory;
- ordered expected canonical formats.

`ExpectedFormatState` contains:

- owning record ID and canonical format;
- stored basename and safe normalized managed relative path;
- expected `Present` status;
- byte length and SHA-256;
- verified creation time, last-write time, and provider-neutral attribute bits;
- a stable observation-source/version marker identifying the scan/hash observation contract.

Absolute library and file paths are not stored in Domain or JSON. `LibraryIdentity.LibraryRoot` is used only by Application/Infrastructure path guards in the current session.

The existing post-hash `FormatFileObservation` must be retained in the immutable analysis result so generation does not reopen or rehash files. Move or mirror the provider-neutral observation into Domain and add it to `BookFormat`, enforcing:

- `Present` requires both fingerprint and observation;
- observation length equals fingerprint length;
- non-present statuses have neither;
- hash/observation collections remain immutable.

This is the only required expansion of existing scan output. It does not change duplicate grouping, recommendation selection, or recommendation input identity.

#### Instructions

`MetadataRetentionInstruction` records target record, source record, and the expected source metadata state reference.

Each `FormatRetentionInstruction` records:

- canonical format;
- target record;
- selected source record and source relative path;
- source fingerprint/state reference;
- retention mode `RetainInTarget` or `RetainFromOtherRecord`;
- linked reviewed-selection/provenance reference;
- explicit preconditions that source/target expected states still match and backup requirements are satisfied before later execution.

Each `FormatRemovalInstruction` records:

- owning record, canonical format, and existing relative path;
- expected fingerprint/state reference;
- disposition reason:
  - `ByteIdenticalAlternative`;
  - `ReviewedNonIdenticalReplacement`;
  - `RemovedWithSourceRecordAfterRetention`;
- the retained-format instruction that prevents silent loss;
- required backup item;
- whether the removed bytes are identical to the retained source.

Each `RecordRemovalInstruction` records:

- non-target record ID;
- every format/metadata/cover backup requirement associated with it;
- every selected contribution that must first exist and be verified on the target;
- a condition that the record remains exactly in its expected state;
- a condition that the target survives.

The instruction model has no delegates, command names, arguments, shell text, path destinations, or execution status.

#### Backup requirements

`BackupRequirement` has a stable ID, kind, affected record/optional format, required flag, and explanation. Required kinds are:

- `RecordMetadataSnapshot` for every involved record;
- `FormatFile` for every affected declared format, including selected sources and byte-identical alternatives;
- `CoverIfPresent` for every record whose stored cover flag is true;
- `ManagedPathAndFileState` for every affected record/format;
- `CleanupPlanArtifact`;
- `ExecutionAudit` as a requirement for the later execution milestone.

Cover files are not resolved or hashed in Milestone 6. The plan records the expected stored cover flag, a required `CoverIfPresent` backup item, and informational notice `PLAN.COVER_FILE_STATE_DEFERRED`. Later execution validation must resolve and verify it before mutation. This limitation does not permit an ebook format to omit its hash.

#### Provenance

`CleanupPlanProvenance` contains bounded immutable snapshots of:

- exact metadata duplicate-group ID, normalized identity, reason code/category, and member IDs;
- the exact current generated recommendation: model/input versions, confidence, metadata source, format candidates/selections, exclusions, record dispositions, reasons, and warnings;
- current reviewed recommendation: status, freshness, effective metadata/format/record selections, and review time;
- current user override, including metadata source change, format actions, retained-separate IDs, requested status, and review time;
- `RecommendationModelVersion`, `RecommendationInputVersion`, and the canonical input policy/version;
- cleanup-plan schema and policy versions;
- plan creation time.

Provenance stores no stale override in an eligible plan, no absolute root/path, no book prose, no parser object, no raw exception, and no AI output.

### Validation issues and classification

`CleanupPlanIssue` contains:

- stable code;
- `CleanupPlanIssueSeverity`: `BlockingError`, `Warning`, or `Information`;
- subject kind `Plan`, `Library`, `Group`, `Record`, `Format`, `Assessment`, `Backup`, `Approval`, or `Provenance`;
- optional record ID and format;
- bounded explanation and ordinal evidence map.

`CleanupPlanValidationResult` contains ordered issue collections, `IsValid`, validated input identity, and validation time.

- A `BlockingError` prevents plan publication, approval, and later execution.
- A `Warning` remains visible and requires review but does not by itself prevent approval.
- An `Information` notice explains deterministic choices, deferred execution responsibilities, and the non-executable boundary.

Eligible plans can retain warnings such as:

- a reviewed byte-distinct same-format alternative is proposed for replacement/removal;
- an EPUB quality preference does not establish content equivalence;
- metadata completeness or an invalid non-conflicting identifier lowered Milestone 5 confidence;
- a record has no declared format but its metadata is still backed up;
- a cover is reported but its physical state is deferred.

Every plan includes information that no backup has been created, no Calibre operation has run, and Milestone 7 must revalidate the full plan immediately before execution.

### No-silent-data-loss validation

`CleanupPlanSafetyPolicy` performs a closed-world coverage proof:

1. Build the set of every current declared format association in every group member.
2. Prove the plan provenance candidate set equals that set exactly.
3. Prove each canonical format represented in the set has exactly one `FormatRetentionInstruction` targeting the surviving record.
4. Prove the retained source is current, present, hashed, observed, and one of the reviewed candidates.
5. Prove every existing format association is covered exactly once as:
   - retained in the target;
   - selected as a source to be retained on the target and later removed with its source record;
   - a byte-identical alternative linked to the retained fingerprint;
   - an explicitly reviewed byte-distinct replacement/removal linked to the reviewed selected source.
6. Reject any unavailable, omitted, outside-group, duplicate, or unclassified association.
7. Reject any `ExplicitlyExcludedByUser` final format because it has no retained destination.
8. Require a warning and backup for every byte-distinct non-selected candidate.
9. Prove every non-target record removal depends on preservation/verification of all its selected contributions.
10. Prove every replacement/removal/record removal has complete backup coverage.
11. Prove the target is absent from record removals and at least one record survives.

Exact-binary equality can classify only the matching file associations. It cannot cover another format or an entire record without the explicit per-format proof above.

### State model and legal transitions

State is explicit and separate from issue severity:

| State | Meaning | Approvable |
| --- | --- | --- |
| `Draft` | In-memory definition is being constructed and has not passed complete validation. | No |
| `Valid` | Definition is complete, current against its validation snapshot, and has no blocking issue. | Yes |
| `Blocked` | Definition/import is understood but fails an intrinsic safety, provenance, policy-support, or approval-integrity rule. | No |
| `Approved` | A user explicitly approved the exact valid content digest. | Already approved |
| `Stale` | A previously valid/approved plan no longer matches current expected library/recommendation state. | No |
| `Revoked` | The user explicitly revoked an approval. | No |

Legal transitions:

```text
Draft -> Valid
Draft -> Blocked
Valid -> Approved
Valid -> Stale
Valid -> Blocked
Approved -> Stale
Approved -> Revoked
```

`Blocked`, `Stale`, and `Revoked` are terminal for that plan definition. Recovery means regeneration from the current reviewed result, producing a new `CleanupPlanId` and a new `Draft`; there is no transition back to `Draft`, `Valid`, or `Approved`.

Generation eligibility failures occur before a publishable plan is returned. `Draft -> Blocked` is a fail-closed internal/import path for a definition that was structurally constructible but failed full safety/provenance validation; the user-facing generation outcome still has `Plan = null` and displays the blocking validation result.

### Immutability, content binding, approval, and revocation

- Every plan revision is immutable.
- Definition content never changes after the first `Valid` revision.
- State transitions append an immutable lifecycle entry and return a new artifact revision with the same plan ID/content digest.
- Prior revisions remain value-stable and can be retained by WPF for history/testing.
- `CleanupPlanApproval` records approval time, explicit-local-user method, artifact revision approved, and exact content digest.
- Approval is legal only from `Valid`, with a current matching validation result and no blocking issue.
- Approval cannot alter target, sources, removals, expected state, backups, warnings, or provenance.
- `CleanupPlanRevocation` records time, required bounded reason, prior approval revision/digest, and explicit-local-user method.
- Revocation is legal only from `Approved`.
- Approval/revocation use injected `IClock`; they perform no I/O unless the user separately exports the resulting revision.
- No state in Milestone 6 is executable. `Approved` means reviewed and consented to as data, not executed or safe without Milestone 7 revalidation/backup/tool checks.

The content digest detects accidental or invalid content changes and binds approval to exact content. It is not a digital signature or proof against an attacker who can rewrite and rehash a local artifact. Later execution must rely on strict live revalidation, not artifact authenticity alone.

### Stale-plan detection

`ValidateCleanupPlanUseCase` compares an immutable plan with one current completed `LibrarySnapshot` and its current generated recommendation. It performs no filesystem or SQLite access itself.

A plan becomes stale when any of these changes:

- Calibre library UUID or supported schema version;
- duplicate-group existence, ID, normalized identity, reason, or ordered membership;
- recommendation model version or canonical input version;
- target/metadata/format source membership;
- any expected stored metadata field, ordered author, identifier, publication value, cover flag, or relative directory;
- any declared format set, canonical format, stored basename, or managed relative path;
- any affected file status, length, SHA-256, creation time, last-write time, or attribute bits;
- exact-binary membership used by a removal;
- assessment association, observed fingerprint, status, score, analyzer/scoring version, or decisive evidence used by a selection;
- any current finding relevant to plan eligibility or safety;
- cleanup-plan policy/schema support;
- content digest or approval digest integrity.

`ScannedAt`, UI selection, filter state, plan export path/time, and a byte-identical JSON rewrite do not cause staleness.

Validation outcomes:

- A matching `Valid` or `Approved` plan remains in the same state and receives a new immutable validation record only if that record is persisted as a new artifact revision.
- A current-state mismatch transitions `Valid` or `Approved` to `Stale`.
- An intrinsic content/provenance/digest/policy violation transitions a `Draft`/`Valid` import to `Blocked` or rejects malformed input before Domain construction.
- A `Revoked`, `Stale`, or `Blocked` plan remains terminal.

Staleness is detected when the user imports/validates a plan or after a new successful scan. Milestone 6 does not monitor the filesystem in the background and the UI must show the last validation time.

### Deterministic cleanup-plan JSON

Use a new schema `cleanup-plan/1.0`. Do not extend `recommendation-review/1.0`.

One file contains one current immutable plan revision plus its lifecycle history. The root property order is fixed:

1. `schemaVersion`;
2. `cleanupPlanPolicyVersion`;
3. `planId`;
4. `artifactRevision`;
5. `state`;
6. `contentDigest`;
7. `inputIdentity`;
8. `sourceLibrary`;
9. `createdAtUtc`;
10. `lastValidatedAtUtc`;
11. `targetRecordId`;
12. `involvedRecordIds`;
13. `metadataRetention`;
14. `formatRetentions`;
15. `formatRemovals`;
16. `recordRemovals`;
17. `expectedLibraryState`;
18. `backupRequirements`;
19. `issues`;
20. `provenance`;
21. `approval`;
22. `revocation`;
23. `lifecycleHistory`.

Serialization rules:

- UTF-8 without BOM, two-space indentation, LF newlines, exactly one final LF.
- Explicit DTOs or `Utf8JsonWriter`; Domain/Application types have no JSON attributes.
- Fixed enum text, invariant integers/dates, canonical lowercase SHA-256, and explicit nullable values.
- Every collection is validated and written in its Domain canonical order.
- Evidence maps are arrays or explicitly ordered objects; dictionary iteration cannot affect bytes.
- Identical plan revisions produce byte-identical output.
- No absolute library/file/export path, command, script, executable callback, raw exception, parser type, book prose, or content snippet is serialized.
- Import is full reconstruction, not inspection-only. It validates schema/policy versions, bounds, required/unknown properties, enums, timestamps, UUIDs, safe relative paths, uniqueness, association graph, lifecycle transitions, content digest, approval/revocation digest binding, and every Domain invariant.
- Unknown schema versions are rejected with `UNSUPPORTED_CLEANUP_PLAN_SCHEMA`.
- A known schema with an unsupported policy/model version is imported only as a controlled `Blocked` result when enough structure can be safely interpreted; otherwise it is rejected.
- Malformed, duplicate, unsafe, oversized, too-deep, or invariant-breaking JSON returns structured failures and no plan.
- Proposed limits: 64 MiB file, depth 64, 100,000 records, 100,000 formats/instructions, 100,000 backup items, 10,000 issues/history entries, 100 evidence entries per issue/reason, and existing 512-character evidence bounds.

### Cleanup-plan file store and path safety

Application owns `ICleanupPlanStore`; Infrastructure implements `VersionedJsonCleanupPlanStore`.

Export:

- requires an explicit `.cleanup-plan.json` destination in an existing directory;
- canonicalizes and physically resolves selected library root and destination parent;
- rejects root/descendant/alias/reparse redirection into the Calibre library;
- writes a unique external temporary sibling with create-new semantics;
- flushes and atomically publishes/replaces only after complete serialization;
- cleans up only its own external temporary file after cancellation/failure.

Import:

- requires an explicitly selected existing regular file outside the library;
- applies the same physical containment/reparse rules;
- opens read-only with bounded streaming/length checks;
- creates no sidecar or temporary file;
- returns structured path/read/schema/invariant failures.

The store does not create directories and never writes plan, cache, lock, or temp files inside the library.

### Application use cases and ports

Add focused use cases:

- `GenerateCleanupPlanUseCase`: validates current reviewed input, creates ID/time, builds expected states/instructions/backups/provenance, and returns either one `Valid` plan or structured issues.
- `ValidateCleanupPlanUseCase`: compares a plan with a current completed snapshot/recommendation and returns the unchanged plan or a new `Stale`/`Blocked` revision plus validation result.
- `ApproveCleanupPlanUseCase`: requires `Valid` and current validation, records explicit approval, and returns a new `Approved` revision.
- `RevokeCleanupPlanUseCase`: requires `Approved` and a nonblank reason, records revocation, and returns a new `Revoked` revision.
- `ExportCleanupPlanUseCase`: validates the in-memory graph/current revision and calls `ICleanupPlanStore.WriteAsync`.
- `ImportCleanupPlanUseCase`: requires a current completed snapshot, calls `ICleanupPlanStore.ReadAsync` with that snapshot's library root, and immediately validates the imported plan against the snapshot.

Add meaningful ports only:

- `ICleanupPlanStore` for external deterministic JSON read/write;
- `ICleanupPlanIdGenerator` for production UUID generation and deterministic tests.

Use the existing `IClock`. Keep eligibility, coverage, digest canonicalization, state transitions, and equality policies as Domain services rather than interfaces.

All async use cases propagate `CancellationToken`. Expected eligibility/path/parse/staleness problems return structured outcomes. Cancellation propagates. Unexpected defects are not mislabeled as plan issues.

### WPF cleanup-plan workspace

Add a `_Cleanup plans` tab and a cohesive `CleanupPlanWorkspaceViewModel` exposed by `MainWindowViewModel`.

The main ViewModel supplies:

- the current completed snapshot;
- the currently selected metadata candidate's `ReviewedConsolidationRecommendation`;
- successful-rescan notifications for staleness evaluation.

The workspace owns:

- session plan revisions and imported plans;
- selected plan;
- generation validation issues when no plan was produced;
- commands for Generate, Validate, Approve, Revoke, Export, and Import;
- a required revocation-reason field;
- unsaved-revision status.

Display:

- plan ID, artifact revision, state, content digest, creation/validation time;
- source UUID/schema, group identity, all involved IDs, target, and metadata source;
- final format, source record/path/hash, target, retention mode, and preconditions;
- format removals/replacements with exact/reviewed classification and retained destination;
- records proposed for removal and preservation dependencies;
- expected record metadata;
- expected format paths, sizes, hashes, timestamps, and attribute facts;
- backup requirements and their declarative/not-yet-created status;
- blocking errors, warnings, and notices in separate text/severity columns;
- generated/reviewed/override provenance and all schema/model/policy versions;
- approval time/digest/revision and revocation time/reason;
- lifecycle history and last validation time.

Behavior:

- Generate is enabled only when a current selected review is present; eligibility failures remain visible and create no plan row.
- Approve is enabled only for `Valid`, current plans with no blocking issue and requires a WPF-only explicit confirmation prompt.
- Revoke is enabled only for `Approved` and requires a nonblank reason plus explicit confirmation.
- Import and Export use WPF-only Open/Save dialog services; ViewModels never read/write files. Import is enabled only after a successful current library scan.
- A successful rescan revalidates session plans after the new snapshot is atomically published.
- Changing filters, selection, approval, revocation, import, or export never invokes scan, hashing, parser, SQLite, Calibre, backup, or execution behavior.
- Imported approval is displayed but never treated as executable.
- There is no Execute, Dry run, Simulate, Backup, Restore, or Generate script command/control.

Use read-only virtualized grids, bulk collection replacement, selection repair, labels/access keys, automation names/help text, keyboard navigation, resize-friendly layout, high-DPI support, text state labels, and no color-only distinctions. Keep `MainWindow.xaml.cs` limited to view initialization.

### Performance and memory

- Generate one plan for one selected group; do not generate plans for all recommendations during scan.
- Index current books, formats, assessments, and groups once per generation/validation.
- Use dictionary/set coverage rather than pairwise format comparisons.
- Canonically sort only final immutable collections.
- Preserve already computed hashes/observations; never reopen, rehash, or reassess during plan generation.
- JSON write is streaming and import is size/depth/count bounded.
- WPF retains lightweight plan summary rows and materializes selected plan details on demand.
- Revalidate plans linearly in their involved records/formats after a successful scan. Bound session plan count or warn before large imports rather than spawning unbounded work.
- Add generated scale tests for a large duplicate group/format set and many session plans without brittle wall-clock thresholds.

## Files expected to change

Exact names can be consolidated to follow current cohesive-file conventions. Record material deviations in this plan before implementation.

### Documentation

- `docs/plans/milestone-6-cleanup-plan-generation.md`
- `docs/adr/0006-immutable-cleanup-plan-artifacts.md` — proposed accepted ADR before JSON/lifecycle implementation.
- `docs/domain-model.md` — cleanup-plan identities, expected state, instructions, coverage, lifecycle, approval, and immutability.
- `docs/architecture.md` — plan-generation/validation/store boundary and no-execution rule.
- `docs/safety-and-rollback.md` — Milestone 6 plan/revalidation/backup requirements versus Milestone 7 execution.
- `docs/test-strategy.md` — plan eligibility, lifecycle, import/export, staleness, and WPF coverage.
- `docs/roadmap.md` — clarify Milestone 6 completion boundary without implementing Milestone 7.

ADR 0006 should record the separate `cleanup-plan/1.0` schema, explicit generation from current reviewed state, immutable definition plus lifecycle revisions, content-digest approval binding, terminal stale/blocked/revoked states, external-only plan storage, and the strict absence of an execution boundary.

### Domain

- `src/CalibreLibraryCleaner.Domain/Libraries/BookFormat.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/FormatFileObservation.cs` — provider-neutral immutable post-hash observation moved/mirrored from Application.
- `src/CalibreLibraryCleaner.Domain/Plans/CleanupPlanValues.cs`
- `src/CalibreLibraryCleaner.Domain/Plans/CleanupPlanIssues.cs`
- `src/CalibreLibraryCleaner.Domain/Plans/CleanupPlanExpectedState.cs`
- `src/CalibreLibraryCleaner.Domain/Plans/CleanupPlanInstructions.cs`
- `src/CalibreLibraryCleaner.Domain/Plans/CleanupPlanProvenance.cs`
- `src/CalibreLibraryCleaner.Domain/Plans/CleanupPlanLifecycle.cs`
- `src/CalibreLibraryCleaner.Domain/Plans/CleanupPlan.cs`
- `src/CalibreLibraryCleaner.Domain/Plans/CleanupPlanEligibilityPolicy.cs`
- `src/CalibreLibraryCleaner.Domain/Plans/CleanupPlanSafetyPolicy.cs`
- `src/CalibreLibraryCleaner.Domain/Plans/CleanupPlanStalenessPolicy.cs`
- `src/CalibreLibraryCleaner.Domain/Plans/CleanupPlanContentDigestPolicy.cs`

`LibrarySnapshot` is not expected to own cleanup plans. Plans are post-scan review artifacts with an independent lifecycle.

### Application

- `src/CalibreLibraryCleaner.Application/Libraries/FormatFileObservation.cs` — remove after moving the provider-neutral value to Domain, or retain only a compatibility transport if implementation evidence requires it.
- `src/CalibreLibraryCleaner.Application/Libraries/FormatHashContracts.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/ScanLibraryUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/EpubInspectionContracts.cs` — namespace/type adjustment only if observation moves.
- `src/CalibreLibraryCleaner.Application/Abstractions/ICleanupPlanStore.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/ICleanupPlanIdGenerator.cs`
- `src/CalibreLibraryCleaner.Application/Plans/CleanupPlanContracts.cs`
- `src/CalibreLibraryCleaner.Application/Plans/GenerateCleanupPlanUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Plans/ValidateCleanupPlanUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Plans/ApproveCleanupPlanUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Plans/RevokeCleanupPlanUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Plans/ExportCleanupPlanUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Plans/ImportCleanupPlanUseCase.cs`

Existing Milestone 5 recommendation use cases and review JSON contracts are not expected to change except for constructor/import adjustments caused by `BookFormat` observations.

### Infrastructure

- `src/CalibreLibraryCleaner.Infrastructure/Hashing/StreamingSha256FormatFileHasher.cs` — return the Domain observation type without changing read-only behavior.
- `src/CalibreLibraryCleaner.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Plans/SystemCleanupPlanIdGenerator.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Plans/CleanupPlanJsonSerializer.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Plans/CleanupPlanPathGuard.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Plans/VersionedJsonCleanupPlanStore.cs`

Do not add a Calibre process wrapper, backup implementation, lock service, execution repository, command builder, or mutable library filesystem service.

### WPF

- `src/CalibreLibraryCleaner.Wpf/App.xaml.cs`
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MainWindowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/CleanupPlanWorkspaceViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/CleanupPlanRows.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/ICleanupPlanFilePicker.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/OpenSaveCleanupPlanFilePicker.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/ICleanupPlanConfirmationService.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/MessageBoxCleanupPlanConfirmationService.cs`

### Tests and fixtures

- `tests/CalibreLibraryCleaner.Domain.Tests/Libraries/LibraryValueTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Libraries/LibrarySnapshotTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Plans/CleanupPlanEligibilityPolicyTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Plans/CleanupPlanSafetyPolicyTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Plans/CleanupPlanLifecycleTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Plans/CleanupPlanStalenessTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Plans/CleanupPlanContentDigestTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Libraries/ScanLibraryUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Plans/GenerateCleanupPlanUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Plans/ValidateCleanupPlanUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Plans/CleanupPlanLifecycleUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Plans/CleanupPlanImportExportUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Hashing/StreamingSha256FormatFileHasherTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Plans/CleanupPlanJsonTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Plans/VersionedJsonCleanupPlanStoreTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/SyntheticCalibreLibrary.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/TestServices.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Safety/ReadOnlyLibraryScanSafetyTests.cs`
- `tests/CalibreLibraryCleaner.Architecture.Tests/DependencyDirectionTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/CleanupPlanWorkspaceViewModelTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/MainWindowViewModelTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/MainWindowTests.cs`

Existing recommendation policy/review/JSON tests remain green. Mechanical `BookFormat` constructor updates must supply matching observations for present synthetic formats rather than weakening the new invariant.

## Safety considerations

- Plan generation consumes one immutable completed snapshot and reviewed recommendation. It performs no database, filesystem, parser, process, network, or Calibre access.
- The scan continues to open `metadata.db` read-only/query-only and format files read-only. Preserving the existing post-hash observation adds no new file operation.
- Every group format is affected because non-target records are proposed for removal. Every such ebook file therefore requires a current hash, observation, expected state, removal/retention classification, and backup requirement.
- A unique canonical format cannot be omitted; it must have a retained target destination.
- An unresolved or unavailable same-format conflict blocks plan generation.
- An explicit Milestone 5 final-format exclusion blocks plan generation because no destination remains.
- A byte-distinct losing candidate can appear only after explicit current review, with visible replacement/removal wording, a warning that content equivalence is unproven, complete expected state, and required backup.
- Exact-binary evidence is rechecked against current fingerprints and applies only to named file associations.
- The target is the metadata source in V1, is never in record removals, and is the one surviving record.
- Record removal is conditional on target survival, preservation/verification of every selected contribution, unchanged expected state, and complete later backup.
- Backup entries are requirements only. The UI must state that no backup exists yet.
- Approval binds to the exact content digest and changes no plan definition or Calibre state.
- Revocation changes only immutable lifecycle data.
- Stale, blocked, and revoked plans cannot return to valid/approved; regeneration creates a new plan ID.
- Plan files contain relative managed paths only and are accepted/written only outside the physically resolved library.
- Import is bounded, strict, read-only, and cannot instantiate invalid Domain state.
- Export writes only an explicit external artifact through a temporary sibling. No automatic save/cache is introduced.
- Recursive safety tests compare the entire synthetic library before/after generation, validation, approval, revocation, export, import, failure, and cancellation and assert no report/temp/lock/backup sidecars inside it.
- Source and architecture searches must prove there is no Calibre CLI, process launch, backup creation, execution service, command/script generation, mutable library call, SQL mutation, or rollback implementation.

## Implementation steps

1. Re-read the governing documents and accepted ADRs, record any repository changes since this plan, and resolve the implementation questions below without expanding scope.
2. Write and accept ADR 0006 before adding cleanup-plan JSON/lifecycle code.
3. Move/mirror the provider-neutral verified file observation into Domain, attach it to present `BookFormat` values, enforce fingerprint/length/status invariants, and update mapping/tests without new I/O.
4. Add cleanup-plan identity/schema/policy/digest/state/issue values and immutable expected-state/instruction/backup/provenance aggregates with direct invariant tests.
5. Implement canonical content-digest construction and exact lifecycle transition policy, including immutable approval/revocation records and terminal states.
6. Implement the full eligibility and no-silent-data-loss policies with stable issue codes and exhaustive reviewed-state/format/target/backup coverage tests.
7. Add `GenerateCleanupPlanUseCase` with current-instance validation, injected ID/time, deterministic V1 target choice, expected-state capture, provenance, and structured no-plan failures.
8. Add staleness comparison and validate/approve/revoke use cases. Prove approval binds exact content and performs no I/O.
9. Add Application store contracts plus deterministic strict cleanup-plan JSON serialization/deserialization. Keep it wholly separate from recommendation review JSON.
10. Implement physical outside-library path validation and the external atomic store with bounded read-only import, cancellation, failure cleanup, and reparse tests.
11. Register plan services in Infrastructure/WPF composition roots. Add WPF-only file dialogs and approval/revocation confirmation services.
12. Add the cohesive cleanup-plan workspace, detail rows, lifecycle commands, issue display, provenance, expected states, backup requirements, and accessible virtualized XAML. Add no execution control.
13. Integrate current selected review/snapshot input and successful-rescan staleness reconciliation through `MainWindowViewModel` while preserving atomic snapshot publication and existing review state.
14. Extend architecture/source guards to permit inert Milestone 6 plans but forbid Milestone 7 execution, processes, Calibre CLI, backups, scripts, locks, mutation, and rollback.
15. Extend strict safety manifests for every plan operation and confirm all files produced by tests are outside synthetic libraries.
16. Run focused tests while iterating, then the full restore/build/test/format/package sequence, WPF STA startup test, optional interactive synthetic review, complete diff review, JSON golden/round-trip review, and mutation/future-scope searches.
17. Update this plan's decisions, deviations, failures, exact verification results, risks, progress, and final outcome.

Each step must leave the relevant solution portion buildable/testable. Stop rather than adding an execution abstraction if any design appears to require Calibre mutation, backup creation, command generation, or a future executor.

## Tests

Use xUnit, FakeItEasy, and FluentAssertions. All files/libraries are generated under test-owned temporary directories; no test uses a real user library.

### Domain eligibility tests

- `Accepted` current unchanged generated review is eligible.
- `ManuallyAdjusted` current fully resolved review is eligible and preserves override provenance.
- `Unreviewed`, `Deferred`, `KeepSeparate`, and `NotDuplicates` each return the exact blocking issue and no plan.
- Stale freshness, model/input mismatch, stale override, missing effective selection, and a non-current generated recommendation instance are rejected.
- Generated or effective retained-separate records are rejected.
- Outside-group metadata, format, or retained-separate override references are rejected even if a forged reviewed aggregate is supplied.
- Target equals effective metadata source, belongs to the group, survives, and cannot appear in record removals.
- A group with no surviving record is rejected.
- Every canonical format receives exactly one selected target destination.
- `UnresolvedConflict`, `Unavailable`, and `ExplicitlyExcludedByUser` each block generation.
- Missing, inaccessible, invalid-path, changed, unhashed, or unobserved affected formats block generation.
- Present format fingerprint/observation length mismatch is impossible in `BookFormat`.
- Current recommendation candidate coverage must exactly equal current declared formats.
- Stale/mismatched EPUB assessment used by a preference blocks generation.
- Blocking identifier, language, material-year, edition, and series outcomes block generation.
- A sole unique format is retained even when its source is a non-target record.
- A unique format with no retained destination is rejected.
- Byte-identical alternatives link to the retained fingerprint and do not imply whole-record equality.
- Byte-distinct reviewed alternatives produce warnings, required backups, and explicit replacement/removal classifications.
- A byte-distinct alternative without explicit current review is rejected.
- Every applicable eligibility failure is returned in deterministic issue order.

### Domain model and coverage tests

- IDs, versions, revisions, states, digest, timestamps, and issue values reject invalid input.
- Expected record/format states defensively copy and order nested collections.
- Every involved record appears once; every expected format association is unique.
- Metadata source and target are separate fields and equal under V1 policy.
- One final retention exists per canonical format.
- Each existing format association is covered exactly once by retention/removal semantics.
- Selected source on a removed record is linked to target retention before record removal.
- Target replacement records both old and new expected fingerprints/states.
- Target never appears in record removals.
- Record-removal set equals members minus target.
- Every removal has a retained destination and required backup.
- Every involved record has metadata backup; every affected format has file/state backup; reported covers have cover requirements.
- Missing backup coverage makes the plan blocked/no-plan.
- Provenance contains group/generated/reviewed/override/model/input/schema/policy facts and rejects stale/foreign associations.
- Content digest is invariant to source enumeration order and changes for every safety-relevant field change.
- Content digest excludes export path and UI state.
- Constructors expose no mutable collections or public setters.

### Lifecycle tests

- Only the documented transitions are legal.
- Generation passes through `Draft` and returns `Valid` only after complete validation.
- A full-validation failure returns no plan and ordered blocking issues.
- `Valid -> Approved` records exact digest/revision/time and leaves definition content value-identical.
- Approval is rejected for Draft, Blocked, Stale, Revoked, already Approved, or any plan with blockers.
- Changing any content invalidates approval digest validation.
- `Approved -> Revoked` requires a nonblank bounded reason and records approval reference/time.
- `Valid/Approved -> Stale` preserves definition, prior approval, and lifecycle history.
- Blocked, Stale, and Revoked are terminal.
- Regeneration uses a new plan ID and revision sequence.
- Fixed ID/time inputs produce deterministic aggregate values.

### Staleness tests

- Identical rescans remain current despite different `ScannedAt`.
- UUID/schema, group identity/membership, or recommendation model/input change marks stale.
- Missing/added record or format marks stale.
- Any stored title/author/identifier/publication/language/series/cover/path change marks stale.
- File status, path, size, SHA-256, creation time, last-write time, or attributes change marks stale.
- Exact-binary/assessment evidence used by a decision changing marks stale.
- Export time/path and UI review selection do not mark stale.
- A stale plan cannot be revalidated back to valid/approved; regeneration is required.
- Cancellation during a large validation comparison publishes no new revision.

### Application tests

- Generate validates the current snapshot/review association before allocating a plan ID.
- Ineligible input returns `Plan = null`, all structured issues, and makes no store call.
- Eligible input uses injected ID/clock, produces canonical instructions/expected states/backups/provenance, and performs no integration call.
- Validate compares only supplied immutable current state and propagates cancellation.
- Approve/revoke do no I/O and preserve the frozen definition.
- Export rejects invalid/inconsistent in-memory graphs before calling the store.
- Import requires and applies current-snapshot staleness validation and never changes the source file.
- Store path/read/write/cancellation failures return structured outcomes.
- Thousands of formats and many plans preserve deterministic order without unbounded task creation.

### Infrastructure JSON/store tests

- Same plan revision produces byte-identical UTF-8/no-BOM/LF/one-final-newline JSON.
- Fixed property/enum/collection/date/number/hash formatting matches a checked golden shape.
- Full valid, approved, stale, and revoked plan revisions round-trip value-equivalently.
- Approval/revocation/lifecycle history and all provenance/expected state/instructions/backups/issues round-trip.
- Unknown schema, unsupported policy/model, malformed JSON, duplicate properties/IDs, unknown enums, missing required values, unsafe paths, invalid transitions, invalid digest bindings, and graph inconsistencies return controlled results.
- Bounds cover file length, JSON depth, records, formats, instructions, backups, issues, history, evidence count, and evidence length.
- No absolute root/path, chapter prose, raw exception, parser type, command, script, executable ordering, or process argument appears.
- Export outside the library succeeds atomically and leaves no external temporary sibling.
- Export at/below the library or through an alias/reparse destination is rejected and creates nothing.
- Import from inside/aliasing the library is rejected even though it would be read-only.
- Pre-canceled and mid-read/write cancellation publish/return no partial artifact and release handles.
- Invalid serialization writes nothing and cleans only its own external temp.
- Overwrite affects only the explicitly confirmed external plan file.

### WPF tests

- Eligible selected review generates one visible `Valid` plan and selects it.
- Every ineligibility state shows blocking issue text and no plan row.
- Details display source identity, group/member IDs, target, metadata source, retained formats/sources, removals/replacements, record removals, expected metadata/files, hashes/sizes/timestamps, backups, provenance, issues, and versions.
- Blocking/warning/information states remain textually distinct without color.
- Approve requires explicit confirmation, updates only plan lifecycle state, and never calls scan/store unless the user separately exports.
- Revoke requires reason/confirmation and updates only lifecycle state.
- Validate marks a changed plan stale and disables approval.
- Successful rescan revalidates session plans after atomic snapshot publication.
- Import/export picker cancellation makes no store call.
- Import displays current/stale/approved/revoked state without enabling execution.
- Export status clearly names an external cleanup-plan artifact.
- No Execute/Simulate/Backup/Restore/Script command or control exists.
- Collections publish in bulk, detail is selected-plan scoped, and existing recommendation review/filter/navigation remains green.
- STA `MainWindow` construction/show/close activates every new binding and command.
- Manual review covers access keys, keyboard navigation, confirmation focus, automation names/help text, text wrapping, resize/high DPI, and screen-reader state descriptions.

### Architecture and safety tests

- Project-reference direction remains unchanged.
- Domain contains no JSON, filesystem, WPF, SQLite, parser, process, logging, or DI type/package.
- Application contains no concrete JSON/filesystem/dialog/process/parser type.
- Cleanup-plan JSON and external file I/O are confined to Infrastructure.
- WPF ViewModels contain no filesystem, JSON, SQLite, parser, process, Calibre CLI, or Infrastructure reference.
- WPF Infrastructure use remains confined to `App.xaml.cs`.
- No production type named/behaving as executor, command builder, backup creator, lock manager, rollback service, or mutation service exists.
- No `Process`, `ProcessStartInfo`, `calibredb`, shell/script generation, mutable SQLite statement, or mutable Calibre-library filesystem call is introduced.
- Strict recursive before/after manifests remain identical for generation success/failure, validation/stale detection, approval, revocation, export, import, malformed artifacts, and cancellation.
- No plan/temp/cache/lock/backup file appears inside the synthetic library.
- Existing read-only SQLite, hashing, EPUB, recommendation, review JSON, and safety tests remain green.

## Verification commands

Run focused suites while implementing:

```powershell
dotnet test tests/CalibreLibraryCleaner.Domain.Tests/CalibreLibraryCleaner.Domain.Tests.csproj --filter "FullyQualifiedName~CleanupPlan|FullyQualifiedName~BookFormat"
dotnet test tests/CalibreLibraryCleaner.Application.Tests/CalibreLibraryCleaner.Application.Tests.csproj --filter "FullyQualifiedName~CleanupPlan|FullyQualifiedName~ScanLibraryUseCase"
dotnet test tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj --filter "FullyQualifiedName~CleanupPlan|FullyQualifiedName~Safety|FullyQualifiedName~Hash"
dotnet test tests/CalibreLibraryCleaner.Wpf.Tests/CalibreLibraryCleaner.Wpf.Tests.csproj --filter "FullyQualifiedName~CleanupPlan|FullyQualifiedName~MainWindow"
dotnet test tests/CalibreLibraryCleaner.Architecture.Tests/CalibreLibraryCleaner.Architecture.Tests.csproj
```

Run the required final sequence:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
dotnet list package --vulnerable --include-transitive
```

Review repository, serialization, architecture, and safety surfaces:

```powershell
git status --short
git diff --check
git diff --stat HEAD
git diff HEAD
rg -n --glob 'src/**/*.cs' 'CommandText\s*=|SqliteOpenMode|query_only' src
rg -n --glob 'src/**/*.cs' -e 'Process(StartInfo)?' -e 'calibredb' -e 'File\.(Write|Delete|Move|Copy|Create|OpenWrite|Replace)' -e 'Directory\.(Create|Delete|Move)' -e 'FileMode\.(Create|Append|OpenOrCreate|Truncate)' -e '\b(INSERT|UPDATE|DELETE|DROP|ALTER|REPLACE|VACUUM|ATTACH|DETACH)\b' -e 'ExecuteCleanup|SimulateCleanup|CreateBackup|RestoreBackup|AcquireLock|rollback|\\.ps1|\\.bat|\\.sh' src
```

Review every mutable filesystem match. Only the guarded cleanup-plan/recommendation artifact stores may write, and only outside the selected library. Fixture builders may mutate only test-owned temporary directories.

Start the built WPF executable, verify it remains alive through startup, request graceful close, and require empty standard error. If an interactive session is available, use only a generated synthetic library to review eligible/ineligible generation, plan details, approval, revocation, import/export, stale-on-rescan behavior, keyboard access, high DPI, and screen-reader labels.

Do not claim command success until it actually completes. Record exact test counts, warnings/errors, safety results, JSON results, process/manual limitations, and diff findings in `Progress` and `Final outcome`.

## Risks

- A reviewed exact-metadata group can still contain genuinely different editions. Milestone 5 review is therefore mandatory, blocking edition conflicts remain ineligible, byte-distinct removals stay explicit/warned/backed up, and the plan remains non-executable.
- V1 uses metadata source as target. This is deterministic and minimizes metadata transfer but may not be the user's preferred record identity. A target override would require a later cleanup-plan policy/schema change.
- Preserving file observations in `BookFormat` causes broad constructor/test updates. The implementation must not weaken present-file fingerprint invariants or duplicate/recommendation behavior for convenience.
- Creation/last-write timestamps and attribute bits vary by filesystem precision/semantics. Strict mismatches should fail stale; fixtures must normalize supported precision without weakening hash/path assertions.
- The database and filesystem are not one transaction. A completed scan is a point-in-time observation, not a lock. Approval does not eliminate the later mandatory execution-time revalidation.
- Cover physical paths/hashes are unavailable in the current model. Milestone 6 records the cover flag and mandatory future backup/validation requirement rather than inventing a path or silently ignoring the cover.
- A content digest is integrity binding, not a digital signature. Malicious artifact authenticity is outside this milestone; live state validation remains authoritative.
- Strict terminal stale/blocked/revoked states can require regeneration after a transient change reverts. This is intentionally conservative and avoids silently reviving approvals.
- Full provenance and expected metadata can make JSON large and expose ordinary catalog metadata. It remains local, excludes absolute paths/content, is bounded, and is written only on explicit export.
- Atomic replacement and physical reparse resolution vary by filesystem. Fail closed where containment cannot be proven.
- Extending the current large WPF shell can reduce maintainability. A separate workspace ViewModel and selected-detail materialization limit that growth.
- Session plan history can grow. Keep it bounded/explicit and avoid automatic plan generation or persistence.
- Architecture source guards based on term searches can produce false positives. Pair them with dependency/API assertions and review matches rather than suppressing safety checks.
- The dirty Rider/agent state must remain untouched and excluded from diff claims.

## Unresolved questions

The plan adopts these Milestone 6 defaults; implementation must record any approved change before production edits:

1. **Target policy:** V1 target is the effective reviewed metadata source. Target and metadata source remain separate fields for clarity/versioning.
2. **Eligible reviews:** only current `Accepted` and `ManuallyAdjusted` reviews. `Unreviewed`, `Deferred`, `KeepSeparate`, and `NotDuplicates` are ineligible.
3. **Explicit final-format exclusion:** always blocks plan generation because it leaves no retained destination.
4. **Byte-distinct losing candidate:** allowed only after the current review explicitly resolves the same-format choice; the plan records removal/replacement, non-equivalence warning, expected state, and mandatory backup.
5. **Affected-file hash rule:** every declared format on every involved record requires a current fingerprint and observation because all non-target records are proposed for removal.
6. **Cover state:** record Calibre's cover flag and a mandatory later cover backup/validation requirement; do not resolve/hash/copy the cover in Milestone 6.
7. **Lifecycle recovery:** Blocked, Stale, and Revoked are terminal. Regeneration creates a new ID; no approval is silently revived.
8. **Approval durability:** approval/revocation first update immutable in-memory lifecycle state. Persistence occurs only through explicit external export.
9. **Plan granularity:** one duplicate group and one current plan revision per JSON file.
10. **Imported approval:** preserve/display it, validate against the current snapshot when available, and never treat it as executable.
11. **Plan location:** both import and export reject paths inside the physically resolved selected Calibre library.
12. **No signature:** V1 uses a deterministic content digest and strict live validation, not digital signatures or OS identity.

No unresolved question authorizes Calibre access, backup creation, execution, simulation, script generation, or another roadmap milestone.

## Progress

### 2026-07-19 review remediation

An independent completed-implementation review found eleven safety/correctness gaps. This hardening pass remains within Milestone 6 and makes the smallest corrections needed to:

- reconstruct and verify reviewed selections from the attached current override;
- bind staleness to the reviewed-selection identity and invalidate same-group plans when review changes;
- reject plan-ID lineage collisions with different frozen bodies;
- require full expected-state, instruction, target, version, and provenance graph consistency;
- bind or deterministically reconstruct immutable safety warnings and provenance fields in the content digest;
- validate approval/revocation against exact lifecycle history and keep imported approval informational;
- prevent cross-library session export from writing into another known Calibre library;
- reject missing/unknown JSON values, unsafe basenames, and excessive nested collections;
- describe selected non-target source removal explicitly;
- expose the latest local validation result without mutating the frozen definition; and
- add focused regression tests for every repaired invariant.

No remediation item authorizes Calibre tooling, database/filesystem mutation inside a library, execution, simulation, backup creation, locking, scripts, rollback, or later-roadmap functionality.

- [x] Read root `AGENTS.md`, `PLANS.md`, and every nested `AGENTS.md` under `src/` and `tests/`.
- [x] Read all requested product, functional, architecture, domain, duplicate, scoring, safety, test, roadmap, and workflow documents.
- [x] Read every accepted ADR under `docs/adr/`.
- [x] Read the completed Milestone 0 through Milestone 5 execution plans in full, including remediation notes, deviations, command results, risks, and final outcomes.
- [x] Inspect current solution/package/git state.
- [x] Inspect current Domain snapshot, file, recommendation, review, staleness, and invariant models.
- [x] Inspect current Application scan, recommendation generation/override/export use cases and file-observation contracts.
- [x] Inspect current Infrastructure recommendation serializer, path guard, exporter, DI boundary, and safety fixtures.
- [x] Inspect current WPF review state, commands, selected-detail materialization, XAML, and composition root.
- [x] Inspect representative Domain, Application, Infrastructure, architecture, safety, serialization, and WPF tests.
- [x] Create the Milestone 6-only execution plan.
- [x] Define exact eligibility, no-silent-loss coverage, V1 target policy, expected-state capture, backup requirements, lifecycle transitions, staleness, import/export, and WPF boundary.
- [x] Confirm no Milestone 7 execution, backup, lock, process, command, mutation, simulation, or rollback behavior is planned.
- [ ] Accept/adjust the adopted defaults before implementation if requested.
- [x] Adopt the documented defaults for implementation, as authorized by the explicit Milestone 6 implementation request on 2026-07-18.
- [x] Reconfirm the working tree before implementation: only the pre-existing `.idea/workspace.xml`, `.ai/`, and this approved plan are outside the Milestone 5 baseline.
- [x] Begin Milestone 6 implementation on 2026-07-18; no contradiction was found among the governing documents.
- [x] Write and accept ADR 0006 for immutable, non-executable cleanup-plan artifacts.
- [x] Preserve verified post-hash file observations in immutable Domain `BookFormat` values without adding file I/O.
- [x] Implement cleanup-plan expected state, declarative retention/removal instructions, complete backup requirements, provenance, issues, canonical body hashing, lifecycle, approval, revocation, and staleness.
- [x] Implement deterministic eligibility and closed-world safety validation; ineligible generation returns ordered issues and allocates no plan ID.
- [x] Implement Application generation, validation, approval, revocation, import, and export use cases.
- [x] Implement bounded `cleanup-plan/1.0` JSON reconstruction, canonical-hash validation, future-schema rejection, controlled future-policy blocking, and guarded external-only storage.
- [x] Implement the WPF cleanup-plan workspace with plan contents, expected state, backup requirements, issues, provenance, lifecycle, approval, revocation, staleness, and import/export interaction.
- [x] Add focused Domain, Application, Infrastructure, architecture, safety-manifest, serialization, cancellation, and WPF tests using synthetic values/libraries.
- [x] Implement Milestone 6 without adding Calibre tooling, process launch, backup creation, locking, mutation, simulation, scripts, execution, or rollback behavior.
- [x] Record failed verification attempt: the first final sequence passed restore/build and all 267 tests but `dotnet format --verify-no-changes` found six LF lines introduced by the final provenance immutability patch; `dotnet format` normalized them before the full sequence was restarted.
- [x] Record failed verification attempt: after adding explicit plan-time/override/observation presentation, restore passed but build rejected locale-sensitive record-ID formatting (`CA1305`); the display now uses invariant formatting and the final sequence was restarted.
- [x] Record failed focused verification attempt: the expanded immutability reflection test initially used pattern syntax unsupported in an expression tree (`CS8122`); an equivalent null comparison fixed the test before final verification.
- [x] Run final verification and complete diff/safety review.
- [x] Update final outcome.

## Final outcome

Milestone 6 is complete.

The implementation adds a separate, explicit, non-executable cleanup-plan workflow built from one current reviewed Milestone 5 recommendation. Present `BookFormat` values now retain the verified post-hash `FormatFileObservation`; generation reuses this immutable snapshot data and performs no new file access.

Domain now owns immutable cleanup-plan identities, expected library/record/format state, declarative metadata/format retention, format/record removal descriptions, complete backup requirements, provenance, ordered blocking/warning/information issues, closed-world coverage validation, canonical semantic hashing, staleness comparison, and legal lifecycle transitions. Operational body content is frozen. Lifecycle changes return new revisions with the same body/digest, approval binds to that digest, stale/blocked/revoked states cannot be approved, revocation retains approval audit information, and regeneration allocates a new plan ID.

Application now owns generation, validation, approval, revocation, import, and export use cases. Infrastructure owns deterministic `cleanup-plan/1.0` JSON reconstruction, canonical-hash verification, duplicate/unknown-property rejection, 64 MiB/depth/count limits, controlled future-policy blocking, future-schema rejection, and guarded import/export outside the physically resolved selected library. WPF now exposes plan revisions, identity/times/hash, source/group/target, provenance/override, retentions/removals, expected metadata/file observations, backup requirements, issues, approval/revocation, lifecycle, and staleness. No execution control exists.

No package was added. Cohesive filenames were used where the plan explicitly allowed consolidation; no material design or safety deviation was introduced. The only implementation limitation retained from the approved plan is the documented V1 cover policy: the stored cover flag and required later backup/validation are recorded, but cover files are not resolved or hashed in Milestone 6.

Final verification on 2026-07-18:

- `dotnet restore`: succeeded; all projects up to date.
- `dotnet build --no-restore`: succeeded with 0 warnings and 0 errors.
- `dotnet test --no-build`: succeeded, 272/272 tests (Domain 90, Application 56, Infrastructure 94, Architecture 17, WPF 15).
- `dotnet format --verify-no-changes`: succeeded.
- `dotnet list package --vulnerable --include-transitive`: succeeded; no vulnerable packages reported.
- Built WPF startup: process remained alive after startup, accepted a graceful close request, and emitted empty standard error.
- `git diff --check`: succeeded for milestone files.
- Safety/source review found no Calibre CLI, process launch, execution/simulation, backup creation, locking, script generation, rollback, SQL mutation, or Calibre-managed filesystem mutation. Production write matches are limited to the existing recommendation exporter and the new guarded external cleanup-plan store.
- Recursive synthetic-library safety coverage proved scan, generation, approval, external export, and import leave database/files/attributes/timestamps unchanged and create no plan/temp/backup/lock sidecar inside the library.

Remaining risks are the accepted ones: an exact-metadata group can still represent distinct editions and therefore requires explicit review; filesystem timestamp/attribute precision can conservatively cause staleness; approval is not a signature; no backup exists at Milestone 6; and every later destructive action still requires Milestone 7 live revalidation, verified backups, supported Calibre tooling, and post-operation verification.

### Review-remediation outcome (2026-07-19)

The completed implementation review findings are fixed. Reviewed selections must now reconstruct exactly from the current override; review changes stale existing plans; same-ID revisions must share one immutable lineage; expected state, instructions, backups, issues, provenance, lifecycle, and approval are cross-validated; canonical hashing distinguishes null from empty and includes stable review/provenance fields; imported approvals remain informational; JSON import rejects duplicate, missing, numeric-enum, over-deep, over-count, unsafe-path, and inconsistent graph data; selected formats sourced from records proposed for removal are represented explicitly; and WPF plan sessions cannot cross physical library roots.

Remediation verification:

- `dotnet restore`: succeeded; all projects up to date.
- `dotnet build --no-restore`: succeeded with 0 warnings and 0 errors.
- `dotnet test --no-build`: succeeded, 288/288 tests (Domain 91, Application 63, Infrastructure 98, Architecture 17, WPF 19).
- `dotnet format --verify-no-changes`: succeeded after the solution formatter normalized patched C# line endings.
- `dotnet list package --vulnerable --include-transitive`: succeeded; no vulnerable packages reported.
- `git diff --check`: succeeded for milestone files; warnings refer only to pre-existing workspace/plan line-ending normalization.
- Final source review found no Calibre CLI/process/script generation, SQL mutation, execution/simulation, backup creation, or Calibre-library file mutation. The cleanup-plan store writes only explicitly selected external artifacts and its own temporary file, guarded outside the physical library root.
