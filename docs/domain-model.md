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
- AI confidence is distinct from deterministic duplicate confidence.
