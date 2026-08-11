# Staged Workflow Large-Library Acceptance

## Status

- Checklist prepared: 2026-08-10.
- Disposable large-library staged run: **Completed**.
- Safety and functional acceptance: **Accepted with documented deviations**.
- Candidate-preparation UX acceptance: **Accepted after same-scale requalification**.
- No performance threshold is defined. Record actual counts, bytes, durations,
  outcomes, and deviations from the application log.

The run used the user-selected disposable copy `Calibre Temp Library 4` after
two separate explicit external-backup confirmations. Acceptance applies to this
copy and build only; measured durations are observations, not thresholds.

## Measured Synthetic Reference

This opt-in baseline measures deterministic in-memory grouping, candidate
generation, and snapshot serialization. It does not measure library I/O,
hashing, EPUB/PDF inspection, cache reuse, Calibre mutation, restart behavior,
or the developer's library.

Command:

```powershell
$env:CALIBRE_RUN_MATCHING_BENCHMARK = '1'
dotnet test .\tests\CalibreLibraryCleaner.Infrastructure.Tests\CalibreLibraryCleaner.Infrastructure.Tests.csproj `
  --no-build `
  --filter FullyQualifiedName~MatchingScaleBaselineTests `
  --logger "console;verbosity=detailed"
Remove-Item Env:CALIBRE_RUN_MATCHING_BENCHMARK
```

Measured on 2026-08-10 with .NET SDK 10.0.301, Windows 10.0.22631, an Intel
Core i7-11850H (8 cores/16 logical processors), and 31.7 GiB RAM:

| Metric | Result |
| --- | ---: |
| Records | 20,000 |
| Exact Metadata groups | 10,000 |
| Exact grouping duration | 386 ms |
| Exact grouping allocated bytes | 91,892,904 |
| Matching profile duration | 1,238 ms |
| Matching profile allocated bytes | 424,510,488 |
| Candidate generation duration | 704 ms |
| Candidate generation allocated bytes | 226,480,880 |
| Total matching allocated bytes | 650,991,368 |
| Retained matching bytes after full collection | 26,846,776 |
| Candidate pairs | 10,000 |
| Proposed directed pairs | 20,000 |
| Capped records | 0 |
| Maximum author bucket | 2 |
| Snapshot serialization duration | 1,067 ms |
| Serialized snapshot bytes | 18,976,606 |

These are one-run observations, not acceptance limits or performance claims for
other machines or libraries.

## Safety Preconditions

- [x] Use the developer's intended disposable large-library copy; record no title,
  author, identifier, or content in this checklist.
- [x] Ensure Calibre and all ebook viewers are closed before starting.
- [x] Confirm no unrelated process will modify the selected library during the run.
- [x] Confirm a complete external library backup exists immediately before Exact
  cleanup. Do not infer this from an application-created artifact.
- [x] Confirm the backup again immediately before Candidate cleanup. The two
  mutation stages are separate runs.
- [x] Do not manually alter the real library to test fail-closed reconciliation.
  Run that negative check only against a disposable clone or synthetic fixture.

## Run Record

| Field | Actual value |
| --- | --- |
| Date/time and time zone | 2026-08-10 22:22-23:40 +02:00 |
| Commit/worktree identifier | `710112d452dd7eea0f8f5431ba3bfd44cbe9d61d`; dirty worktree containing the staged implementation |
| Application configuration | Debug `net10.0-windows`, launched with `dotnet run --no-build` |
| .NET runtime | SDK 10.0.301; .NET/Windows Desktop runtime 10.0.9 |
| Calibre version | 9.12 |
| CPU/logical processors/RAM | Intel Core i7-11850H; 8 cores/16 logical processors; 31.7 GiB |
| Initial technical baseline | 74,663 files; 33,961,095,113 bytes; `metadata.db` 52,891,648 bytes |
| Initial catalog | 27,952 records; 29,298 format associations |
| External backup confirmation before Exact | Explicitly confirmed by user |
| External backup confirmation before Candidate | Explicitly reconfirmed by user |
| Final technical baseline | 47,777 files; 24,529,571,168 bytes; `metadata.db` 52,891,648 bytes |
| Final disposition | Durable authoritative `Completed`; accepted with deviations below |

Use the bounded Serilog files under
`%LOCALAPPDATA%\CalibreLibraryCleaner\logs`. Preserve only technical event
lines; do not add book metadata or paths to the acceptance record.

## Metric Events

Record these production events exactly as emitted:

| Stage | Event and required fields |
| --- | --- |
| Exact analysis | `Exact-only analysis completed`: Books, Formats, HashedBytes, ExactGroups, CatalogMilliseconds, ResolutionMilliseconds, HashingMilliseconds, GroupingAndPublicationMilliseconds, TotalMilliseconds |
| Exact cleanup | `Exact duplicate cleanup ... finished`: Outcome, Operations, RemovedFormats, MergedRecords, RemovedRecords, SkippedRecords, TotalMilliseconds |
| Post-Exact refresh | `Post-Exact refresh completed`: CatalogRecords, CatalogFormats, ReusedFingerprints, ReusedFingerprintBytes, PreservedInvalidPaths, ReboundTransfers, TargetedHashes, TargetedHashBytes, ReusedAssessments, FreshAssessments, TotalMilliseconds |
| EPUB/PDF facts | `Residual format facts prepared`: EpubTargets, ReusedEpubAssessments, FreshEpubAssessments, PdfTargets, ReusedPdfAssessments, FreshPdfAssessments, EpubMilliseconds, PdfMilliseconds, TotalMilliseconds |
| Candidate signatures | `Candidate EPUB signature resolution completed`: UniqueFingerprints, UniqueFingerprintBytes, CacheHits, CacheHitBytes, Inspections, InspectionAttemptBytes, SignatureRecords, UnavailableRecords, aggregate phase durations, TotalMilliseconds |
| Unified discovery | `Residual candidate analysis completed`: Records, ExactMetadataGroups, ExpandedGroups, UnifiedGroups, ExpandedLimitExceeded, TotalMilliseconds |
| Candidate cleanup | `Unified Candidate cleanup ... finished`: Outcome, Operations, TransferredFormats, RemovedFormats, RemovedRecords, SkippedGroups, TotalMilliseconds |

## Acceptance Procedure

### 1. Exact Analysis

- [x] Start the application and explicitly scan the selected library.
- [x] Record the complete Exact analysis metric event above.
- [x] Verify `Formats` covers every resolvable current catalog format and `HashedBytes` is
  the successfully hashed byte total reported for this run.
- [x] Verify there are no EPUB assessment, PDF assessment, Candidate signature,
  residual analysis, or recommendation events between Exact scan start and
  completion.
- [x] Record missing, inaccessible, unsafe, or changed-file findings as deviations.

Actual result: **Passed with 63 pre-existing invalid-path associations**.
`Books=27,952`, `Formats=29,235`, `HashedBytes=30,738,918,140`,
`ExactGroups=5,750`, `CatalogMilliseconds=1,482`,
`ResolutionMilliseconds=1,500`, `HashingMilliseconds=47,858`,
`GroupingAndPublicationMilliseconds=264`, and `TotalMilliseconds=51,105`.
The catalog contained 29,298 associations; the 63-association difference was
persisted as `InvalidPath` findings and was not sent to hashing.

### 2. Restart At `ExactReady`

- [x] Close the application normally after Exact analysis.
- [x] Verify the application and its workers exit and no library file lock remains.
- [x] Restart and explicitly Load the persisted state without scanning.
- [x] Verify Exact cleanup is enabled, Candidate cleanup is disabled, and the
  Exact review state is restored for the same generation.

Actual result: **Passed**. The app exited normally, `metadata.db` opened with
exclusive read, and `ExactReady` restored the expected actions and groups.

### 3. Exact Cleanup

- [x] Reconfirm the complete external backup.
- [x] Execute Exact cleanup with the reviewed keeper/Skip selections.
- [x] Record the Exact cleanup metric event and actual outcome.
- [x] Verify the fixed persistent worker is the only mutation engine and chunks
  contain at most 100 operations.
- [x] Record exact removed formats, merged records, removed records, skipped
  records, operations, and duration. Do not estimate transfer count or duration.
- [x] Verify failure or ambiguity marks state uncertain and blocks Candidate
  cleanup; do not induce failure on the real library.

Actual result: **Passed**. `Outcome=Completed`, `Operations=13,542`,
`RemovedFormats=6,777`, `MergedRecords=3`, `RemovedRecords=6,762`,
`SkippedRecords=1`, and `TotalMilliseconds=284,465`. The fixed worker exited;
the post-Exact copy had 57,467 files and 27,409,637,893 bytes. Failure handling
was covered by automated tests rather than induced on the accepted copy.

### 4. Restart At `CandidatePreparationReady`

- [x] Close normally after successful Exact cleanup or NothingToDo.
- [x] Verify process exit and absence of library locks.
- [x] Restart and Load without scanning.
- [x] Verify Exact cleanup remains disabled and Candidate preparation is enabled.

Actual result: **Passed**. The app/worker exited, `metadata.db` opened exclusively,
and `CandidatePreparationReady` restored with Exact disabled and Candidate enabled.

### 5. Candidate Preparation And Analysis

- [x] Activate Candidate cleanup once and verify this activation performs
  preparation/analysis only, with no mutation worker or backup prompt.
- [x] Record Post-Exact refresh, EPUB/PDF facts, Candidate signature, and unified
  discovery events.
- [x] Verify `ReusedFingerprints + ReboundTransfers + PreservedInvalidPaths = CatalogFormats`, or record
  and explain the deviation.
- [x] Verify targeted hashes correspond only to explained affected associations.
- [x] Verify reused/fresh EPUB and PDF assessment counts equal their target counts.
- [x] Record cache hits and inspections without inferring a hit rate not shown by
  the counters.
- [x] Verify analysis runs against the residual post-Exact catalog.
- [x] In a disposable clone or synthetic fixture, make an unexplained catalog or
  association change and verify preparation requires a new Exact analysis.
- [x] Verify sampled unified rows have keeper ID, title, and authors, and that
  keeper override and Skip remain responsive at the observed group count.

Actual result: **Passed after one acceptance-discovered correction**. The first
attempt failed closed with 63 `POST_EXACT.UNEXPLAINED_CHANGE` issues because
unchanged pre-existing `InvalidPath` associations were treated as changes. No
state was published, no marker/worker existed, and file/byte counts were unchanged.
The corrected policy preserves only an unchanged catalog association that remains
unresolvable as non-executable `InvalidPath`; a newly resolvable or otherwise
changed association still fails closed. Focused tests and the full suite passed
before retry.

The successful refresh recorded `CatalogRecords=21,190`, `CatalogFormats=22,524`,
`ReusedFingerprints=22,458`, `ReusedFingerprintBytes=24,819,959,298`,
`PreservedInvalidPaths=63`, `ReboundTransfers=3`, `TargetedHashes=3`,
`TargetedHashBytes=1,336,933`, `ReusedAssessments=0`,
`FreshAssessments=21,044`, and `TotalMilliseconds=1,696,356`.
The accounting identity is `22,458 + 3 + 63 = 22,524`.

Assessment preparation recorded 20,571 fresh EPUB and 473 fresh PDF assessments,
zero reused assessments, `EpubMilliseconds=752,439`, `PdfMilliseconds=871,617`,
and `TotalMilliseconds=1,624,071`. Signature resolution recorded 8,267 unique
fingerprints/6,595,735,298 bytes, 8,010 cache hits/6,367,068,300 bytes, 257
inspections/228,666,998 attempt bytes, 8,128 signature records, 295 unavailable
records, and `TotalMilliseconds=22,991`. Residual analysis recorded 32 Exact
Metadata groups, 3,189 Expanded groups, 3,190 unified groups, no limit outcome,
and `TotalMilliseconds=148,008`. Sampled keeper fields and override behavior passed.

### 6. Restart At `CandidateAnalysisReady`

- [x] Close normally after Candidate analysis.
- [x] Verify process exit and absence of library locks.
- [x] Restart and Load without scanning.
- [x] Verify the unified groups, evidence, generated keeper, and Candidate cleanup
  availability are restored; session-only overrides/Skip choices may reset.

Actual result: **Passed**. All 3,190 groups and Candidate cleanup availability
restored after normal close; no process or database lock remained.

### 7. Candidate Cleanup

- [x] Review keeper and Skip choices after restart.
- [x] Reconfirm the complete external backup.
- [x] Activate Candidate cleanup and record the complete cleanup metric event.
- [x] Verify `Operations = TransferredFormats + RemovedFormats + RemovedRecords`.
- [x] Verify the fixed worker, maximum-100 chunks, transfer-first ordering,
  format removals, and empty-record removals.
- [x] Verify successful completion reaches durable `Completed`; failure or
  ambiguity reaches uncertain/Rescan-required and does not retry.

Actual result: **Passed**. `Outcome=Completed`, `Operations=7,813`,
`TransferredFormats=229`, `RemovedFormats=3,908`, `RemovedRecords=3,676`,
`SkippedGroups=8`, and `TotalMilliseconds=190,646`; `229 + 3,908 + 3,676 = 7,813`.
Durable state is authoritative `Completed` at revision 7,813 with no pending
mutation marker. Both cleanup actions became disabled.

### 8. Final Process And Lock Check

- [x] Close the application normally.
- [x] Verify no unexpected `CalibreLibraryCleaner.Wpf`,
  `CalibreLibraryCleaner.PdfWorker`, `testhost`, or tool-created `vstest.console`
  process remains. IDE-owned persistent test consoles must be identified by
  parent process rather than counted as application leaks.
- [x] Verify no selected-library file remains locked by the application or worker.
- [x] Record every nonzero process, parent process, command line, and disposition.

Suggested process inventory:

```powershell
Get-CimInstance Win32_Process |
  Where-Object {
    $_.Name -in @(
      'CalibreLibraryCleaner.Wpf.exe',
      'CalibreLibraryCleaner.PdfWorker.exe',
      'testhost.exe',
      'vstest.console.exe')
  } |
  Select-Object ProcessId, ParentProcessId, Name, CommandLine
```

Actual result: **Passed**. No app, PDF worker, Calibre worker, viewer, or `testhost`
process remained. `metadata.db` opened read-only with exclusive sharing. Two
persistent `vstest.console` processes were traced to Visual Studio `DevHub` and
`ServiceHub.TestWindowStoreHost` parents and were classified as IDE-owned.

## Final Acceptance

- [x] All required metric fields contain actual values from one coherent staged run.
- [x] Every restart and safety gate passed or has a documented deviation.
- [x] No unexplained process or file lock remained.
- [x] The complete deviation and risk list was reviewed before declaring acceptance.

Acceptance decision: **Safety, function, and Candidate-preparation UX accepted.**

The follow-up [Candidate preparation progress and observability plan](../plans/candidate-preparation-progress-and-observability.md)
passed same-scale qualification on 2026-08-11 without Candidate mutation. Progress
remained visible across phases, all 17,369 EPUB/PDF assessments were reused, 77
unified groups were published, default logs totaled 9,948 bytes in one segment,
library counts were unchanged, and normal close released all processes and locks.
Manual cancellation was not repeated because preparation completed; its immediate
feedback and no-publication behavior pass focused automated tests.

## Deviations And Remaining Risks

1. Candidate preparation initially rejected 63 unchanged invalid-path catalog
  associations. The run safely published nothing. The root cause was corrected
  and covered by focused tests before retry; changed/resolved associations remain
  fail-closed and invalid paths remain non-executable.
2. While assessment was active, the UI continued to display `Verifying transferred
  format bytes` without animated or numeric sub-progress. Logs and process I/O
  showed active EPUB work. This is a presentation defect; Cancel remained enabled.
3. Per-chapter Debug EPUB diagnostics rolled eight log segments totaling about
  183 MiB during Candidate preparation. Retention remained bounded, but log volume
  is high for this library and should be reduced or sampled later.
4. No compatible assessment baseline existed, so all 21,044 EPUB/PDF assessments
  were fresh. This run does not establish assessment-reuse performance.
5. The negative unexplained-change gate was verified by synthetic automated tests,
  including the newly resolved invalid-path case; no additional external change
  was induced after the accepted destructive run.
6. One intermediate 624-test run saw the unchanged scheduler-sensitive PDF
  orchestration test observe concurrency 1 instead of 2. It passed alone and in
  the final clean serial result of 624/624.