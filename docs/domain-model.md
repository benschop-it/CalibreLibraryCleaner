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

Milestone 2 adds `Sha256Digest`, `FormatFileFingerprint`, and `ExactBinaryDuplicateGroup`. A fingerprint contains the observed file length and SHA-256 digest. An exact binary group contains at least two distinct Calibre-managed format-file references with matching length and digest, and reports how many distinct book records those files span.

## Invariants

- Record-duplicate groups contain at least two distinct records. Exact binary file groups contain at least two distinct managed files and may occur within one record or across records.
- Scores are derivable from findings.
- A recommendation selects at most one source per final format.
- A cleanup plan cannot remove its target record.
- Destructive plans require backups and expected pre-operation states.
- Approved plans are immutable.
- AI confidence is distinct from deterministic duplicate confidence.
