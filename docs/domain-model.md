# Domain Model

## Principal types

```csharp
public sealed record LibrarySnapshot(
    LibraryIdentity Identity,
    DateTimeOffset ScannedAt,
    IReadOnlyList<CalibreBook> Books,
    IReadOnlyList<LibraryFinding> Findings);

public sealed record CalibreBook(
    CalibreBookId Id,
    string Title,
    string AuthorSort,
    IReadOnlyList<AuthorName> Authors,
    IReadOnlyDictionary<IdentifierType, string> Identifiers,
    string? Series,
    decimal? SeriesIndex,
    IReadOnlyList<string> Languages,
    IReadOnlyList<BookFormat> Formats,
    string RelativeDirectory);

public sealed record AssessmentFinding(
    string RuleId,
    FindingSeverity Severity,
    decimal ScoreAdjustment,
    string Description,
    IReadOnlyDictionary<string, string> Evidence);
```

Additional types: `BookFormat`, `DuplicateGroup`, `NormalizedBookIdentity`, `FormatAssessment`, `BookAssessment`, `ConsolidationRecommendation`, `CleanupPlan`, and expected pre-operation file states.

Milestone 4 adds immutable `FormatAssessment`, `AssessmentFinding`, `QualityScore`, `AnalyzerVersion`, `ScoringModelVersion`, and `EpubFeatureSummary` values. Each EPUB assessment is associated with a Calibre book ID, canonical `EPUB` format, presentation-safe expected relative path, and observed file fingerprint. Completed assessments have a 0-through-100 score that equals the clamped sum of their ordered findings. Disqualified assessments have no numeric score and at least one disqualifying finding. Snapshots keep assessments in deterministic book/format/path order and reject duplicate associations.

Milestone 2 adds `Sha256Digest`, `FormatFileFingerprint`, and `ExactBinaryDuplicateGroup`. A fingerprint contains the observed file length and SHA-256 digest. An exact binary group contains at least two distinct Calibre-managed format-file references with matching length and digest, and reports how many distinct book records those files span.

Milestone 3 adds `NormalizedTitle`, `NormalizedAuthorName`, `NormalizedAuthorSet`, `NormalizedBookIdentity`, and `ExactMetadataDuplicateGroup`. The author set is non-empty, duplicate-free, ordinally sorted, and structurally equal by normalized author values. An exact metadata group contains at least two distinct Calibre book record IDs whose normalized title and complete normalized author set are exactly equal. Its deterministic ID is derived from the normalized identity, and it records reason code `EXACT_NORMALIZED_TITLE_AUTHOR_SET`.

Milestone 5 adds stored `BookPublicationMetadata` (publisher, publication date, series/index, ordered languages, and Calibre's cover flag) and immutable `ConsolidationRecommendation` aggregates. A recommendation records independent metadata and per-format sources, all format candidates, exact-binary exclusions, unresolved conflicts, retained-separate/potentially-redundant records, linked reasons/warnings, decision strength, qualitative confidence, model version, and canonical input version. `UserRecommendationOverride` remains separate from the generated aggregate; `ReviewedConsolidationRecommendation` records current/effective/stale state and review status without modifying the generated value.

Milestone 6 adds `FormatFileObservation` to present `BookFormat` values and immutable cleanup-plan values under `Domain.Plans`. A cleanup plan has a frozen semantic definition containing expected state, one target/metadata source, final-format retentions, reviewed format removals, non-target record removals, complete declarative backup requirements, and recommendation/review/override provenance. Its canonical SHA-256 covers only that deterministic semantic definition. Lifecycle revisions (`Draft`, `Valid`, `Blocked`, `Approved`, `Stale`, `Revoked`) reuse the same body and digest; operational-content changes require a new plan ID.

Milestone 7 adds a separate immutable execution model under `Domain.Executions`.
It records a plan/digest-bound confirmation, a deterministic dependency-ordered
operation graph, verified backup-manifest identity, lifecycle transitions,
operation status, verification findings, failure classification, mutation
boundary, and recovery disposition. Cleanup execution never changes the
Milestone 6 plan body or lifecycle.

## Invariants

- Record-duplicate groups contain at least two distinct records. Exact binary file groups contain at least two distinct managed files and may occur within one record or across records.
- Exact metadata groups never fall back to title-only matching. Records with no usable normalized title, no authors, any unusable normalized author, or a missing/invalid catalog author reference are ineligible.
- Exact binary file groups and exact metadata record groups are independent evidence collections. Neither implies the other or authorizes a merge or deletion.
- Scores are derivable from findings.
- Assessment evidence is bounded and contains no retained book prose, absolute external paths, raw exceptions, parser objects, or mutable collections.
- Analyzer and scoring-model versions are recorded independently; fact/limit changes bump the analyzer version and weight/formula/disqualification changes bump the scoring-model version.
- A recommendation selects at most one source per final format.
- A cleanup plan cannot remove its target record.
- Destructive plans require backups and expected pre-operation states.
- Approved plans are immutable.
- Only `Draft -> Valid|Blocked`, `Valid -> Approved|Stale|Blocked`, and `Approved -> Stale|Revoked` transitions are legal. Blocked, stale, and revoked plan definitions are terminal.
- Approval binds to the canonical immutable-body digest. Staleness prevents further approval and preserves any prior approval only as audit information.
- Every involved record requires a metadata backup; every affected format requires file and managed-state backups; reported covers require later resolution/backup; and plan/audit artifacts remain mandatory.
- An execution cannot cross the mutation boundary without an approved current
  plan, a held lease, an exact supported tool, two fresh matching scans, and a
  complete verified backup manifest.
- Constructive operations precede destructive record removals. Each mutation is
  serial and must be semantically verified before dependent operations start.
- Any incomplete or unverifiable execution after the mutation boundary requires
  recovery and cannot be reported as completed.
- AI confidence is distinct from deterministic duplicate confidence.
- Recommendation confidence is distinct from exact-metadata match evidence, exact-binary equality, EPUB assessment status, EPUB quality score, and per-decision strength.
- A non-identical unassessed same-format conflict has no generated source or exclusion. A proposed redundant record has at least one available format and exact-binary coverage for every available format, and contributes no selection or unresolved/unavailable/separate evidence.
- Staleness is equality over canonical relevant input/model identity, not scan time. A stale override has no effective final selection until reset or reapplied.
