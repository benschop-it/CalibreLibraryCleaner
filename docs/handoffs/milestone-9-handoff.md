# Milestone 9 PDF Assessment Handoff

## Purpose and baseline

Milestone 9 was implemented on 2026-07-31. Read this handoff with
`AGENTS.md`, `PLANS.md`, the accepted ADRs, the Milestone 9 execution plan,
and the Milestone 7 and 8 handoffs. The pre-change baseline was 408 passed,
zero failed, and two skipped caller-gated real-Calibre tests. No Milestone 10
comparison, similarity, or cross-file content-fingerprint work is included.

## Implementation completed

The read-only library scan now assesses PDF formats already present in its
verified snapshot. Each result retains status, a PDF-only score when eligible,
classification and confidence, page and sampling facts, encryption state,
bounded metadata, text/image aggregates, outline availability, checksum-valid
ISBN evidence, inert active-content marker counts, repeated-page evidence,
findings, disqualifiers, verified file identity, and all policy versions.

The implementation includes:

- provider-neutral Domain and Application models with PdfPig types confined to
  Infrastructure;
- one strict, disposable, bounded worker per PDF with a versioned stdio
  protocol, Windows Job Object, GC heap hard limit, CPU/working-set/wall-time
  watchdogs, and responsive cancellation;
- canonical read-only opening, restrictive file sharing, reparse/path checks,
  snapshot fingerprint association, SHA-256 verification, and before/after
  observation checks;
- deterministic page sampling and progress with bounded scan concurrency;
- bounded metadata/XMP, text, image-placement, outline, object, stream,
  resource, identifier, and active-marker inspection without retaining full
  text or decoding images;
- findings-derived 85/15 technical/metadata scoring, capped repeated
  penalties, separate disqualifiers, and explicit version compatibility;
- a WPF PDF tab with result summaries, classification/confidence, sampling,
  encryption, text/image/outline/metadata details, versions, progress,
  cancellation, and severity filtering; and
- generated PDF fixtures plus Domain, Application, Infrastructure,
  architecture, safety, orchestration, protocol, and WPF tests.

The shared assessment core was generalized to carry explicit components and a
verified observation. `EpubAssessment` remains a typed wrapper; existing EPUB,
recommendation, cleanup execution, and recovery behavior remains unchanged.

## Files changed

### Project, package, and composition

- `CalibreLibraryCleaner.sln`
- `Directory.Packages.props`
- `NuGet.Config`
- `src/CalibreLibraryCleaner.Infrastructure/CalibreLibraryCleaner.Infrastructure.csproj`
- `src/CalibreLibraryCleaner.Wpf/CalibreLibraryCleaner.Wpf.csproj`
- `src/CalibreLibraryCleaner.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- `src/CalibreLibraryCleaner.Wpf/App.xaml.cs`
- new `src/CalibreLibraryCleaner.PdfWorker/` project

### Documentation

- `docs/adr/0009-select-isolated-pdf-inspection-stack.md`
- `docs/architecture.md`
- `docs/domain-model.md`
- `docs/quality-scoring.md`
- `docs/safety-and-rollback.md`
- `docs/test-strategy.md`
- `docs/roadmap.md`
- `docs/plans/milestone-9-pdf-assessment.md`
- `docs/handoffs/milestone-9-handoff.md`

### Domain and Application

- new PDF semantic models in `PdfAssessment.cs`, `PdfAssessmentValues.cs`, and
  `PdfFeatureSummary.cs`
- new typed `EpubAssessment.cs`
- shared changes in `AssessmentFinding.cs`, `AssessmentValues.cs`, and
  `FormatAssessment.cs`
- new `Application/Abstractions/IPdfInspector.cs`
- all files under `Application/Assessments/Pdf/`
- PDF scan orchestration changes in `Libraries/LibraryAnalysisOptions.cs`,
  `LibraryErrorCode.cs`, `LibraryScanPhase.cs`, and `ScanLibraryUseCase.cs`
- snapshot association changes in `Domain/Libraries/LibrarySnapshot.cs`
- mechanical typed-EPUB adaptations in the EPUB assessment and recommendation
  files; recommendation semantics did not change

### Infrastructure and WPF

- all files under `Infrastructure/Pdf/`
- `Infrastructure/Properties/AssemblyInfo.cs` for focused internal tests
- WPF composition/UI changes in `MainWindow.xaml` and
  `ViewModels/MainWindowViewModel.cs`
- new `PdfAssessmentRowViewModel.cs` and
  `PdfAssessmentFindingRowViewModel.cs`
- typed-EPUB adaptation in `EpubAssessmentRowViewModel.cs`

### Tests

- new PDF tests in Domain and Application assessment test folders
- all generated-fixture and worker tests under
  `tests/CalibreLibraryCleaner.Infrastructure.Tests/Pdf/`
- new WPF PDF row tests
- updated scan, synthetic-library safety, architecture, EPUB compatibility,
  recommendation compatibility, and test-composition files

## Package and dependency decision

`PdfPig` 0.1.15 is pinned centrally and referenced directly only by
Infrastructure. The parser is opened only over a canonical seekable read-only
stream with `UseLenientParsing=false`, `SkipMissingFonts=false`,
`ClipPaths=false`, `UseActualText=true`, and `MaxStackDepth=64`.

The repository-level `NuGet.Config` selects only nuget.org. This makes the
required plain `dotnet restore` deterministic and avoids the known machine
`NU1507` failure caused by nine inherited sources under central package
management. No other package was added.

## Frozen versions

- analyzer: `pdf-inspector/1.0.0`
- scoring model: `pdf-quality/1.0.0`
- classification: `pdf-classification/1.0.0`
- sampling: `pdf-sampling/1.0.0`
- limits: `pdf-limits/1.0.0`
- worker protocol: `pdf-worker-protocol/1.0`

Assessment comparison requires identical scoring-model versions; incompatible
versions are not silently ordered.

## Classification rules

Classification is independent of quality score and always includes evidence,
threshold margin, analyzed/requested page counts, confidence, policy version,
and a limitation-aware explanation.

| Classification | Deterministic V1 rule |
| --- | --- |
| `Unreadable` | Required open/page-tree facts unavailable, excluding the explicit encryption cases |
| `Encrypted` | Password required, unsupported encryption, or empty-password accessible encryption |
| `EmptyOrNearEmpty` | At least 80% of analyzed pages have fewer than 10 useful characters, under 5% image coverage, and fewer than 10 nontrivial operations |
| `Mixed` | At least five pages and material (at least 20%) opposing text/image cohorts |
| `ScannedWithOcr` | At least five pages; at least 80% image-dominant and useful-text pages, median image coverage at least 85%, and consistent geometry |
| `ScannedWithoutOcr` | At least five pages; at least 80% image-dominant, under 10% useful-text pages, median useful characters under 20, and consistent geometry |
| `DigitalText` | At least 80% useful-text pages and under 50% image-dominant pages |
| `Unknown` | Evidence is missing, ambiguous, or does not safely meet another rule |

The scan-like-with-text rule states that it does not prove OCR provenance and
no OCR was run. Scanned, image-heavy, illustrated, or text-free content is not
penalized merely for those properties. Sampling, incomplete facts, capped
counters, small cohorts, and threshold margin reduce confidence.

## Sampling behavior

Documents of at most 200 pages request every page. Larger documents request
exactly 200 stable pages: pages 1-5, the final three pages, up to 32 distinct
valid outline targets plus each following page, then an even integer
distribution and ascending fill. The selected list is distinct and sorted.
Results record total pages, selected pages, parsed count, all-pages/sample
mode, and `pdf-sampling/1.0.0`; sampled results explicitly avoid
whole-document certainty.

## Scoring rules

`pdf-quality/1.0.0` clamps technical findings to 0-85 and embedded metadata to
0-15, then sums them. Every non-zero contribution is an `AssessmentFinding`;
finding order and aggregation are deterministic. Classification findings are
informational and do not alter score.

Technical rules include +50 strict open, +10 valid page tree, up to +10 sample
parse coverage, up to +8 observable content, +5 resources within soft limits,
and +2 outline present. Penalties are -2 per unreadable sampled page capped at
-10, non-observable content -8, unusual resources -5 each capped at -15,
unsupported optional font/filter facts -5 each capped at -15, suspicious
blank pages -2 each capped at -8, unusual dimensions -3 each capped at -9,
exact repeated extras -4 and likely repeated extras -2 with a combined -12
cap. Text absence, images, scan classification, outline absence, encryption
accessible with an empty password, and inert active markers are neutral.

Metadata rules are title +4, author +4, qualifying creation date +2,
checksum-valid bounded ISBN +3, and another useful bounded field +2.
Malformed/truncated metadata is -1 per field capped at -3. Invalid ISBN
candidates are ignored and reported at zero weight.

Missing/inaccessible/unsafe/changed/zero-byte/non-PDF/truncated/malformed
required structure, password-required or unsupported encryption, zero pages,
hard resource limits, timeout/crash/protocol failure, or more than 20% sampled
page failure are disqualifying and produce no score. Fewer page failures remain
scored with capped findings. A result can never have a score without findings.

## Configured V1 safety limits

| Resource | Advisory | Hard bound/action |
| --- | --- | --- |
| File/header/tail | 512 MiB file | 1 GiB file; first 1 KiB header; final 1 MiB tail |
| Pages/sample | sample above 200 | 100,000 pages; 200 sampled pages |
| Objects | 250,000 | 500,000 |
| Parser stack | 48 planned advisory | hard depth 64 |
| Encoded/decoded stream | 32 MiB each | 64 MiB each |
| Aggregate decoded | 256 MiB | 512 MiB |
| Metadata/XMP | 32 KiB field; 1 MiB XMP | 64 KiB field, retain 512 chars; 4 MiB XMP |
| Useful characters | 250,000/page; 5,000,000/sample | 1,000,000/page; 10,000,000/sample |
| Fingerprint/early ISBN input | none | 100,000 chars/page; 32,768 early chars |
| Images | 500/page; 10,000/sample | 1,000/page; 20,000/sample |
| Image dimensions/pixels | 50,000 samples; 100,000,000 pixels | 100,000 samples per dimension; 500,000,000 pixels/image |
| Operations | 250,000/page | 1,000,000/page; 5,000,000/sample |
| Page dimension | 14,400 points | 72,000 points |
| Outline | 5,000 nodes/depth 32 | 10,000 nodes/depth 64 |
| Retained evidence | none | 20 examples/clusters/identifiers; 128 findings |
| Worker protocol | 64 KiB message | 4 MiB message and total response budget |
| Time | wall 45 s; CPU 30 s | wall 60 s; CPU 45 s |
| Memory | heap 384 MiB; working set 512 MiB | GC heap 512 MiB; working set 768 MiB |

Hard breaches fail closed and discard partial facts. The quota filter uses
checked per-stream and aggregate counters. Soft breaches produce bounded
resource warnings/findings and never expand retained data.

## Cancellation and concurrency

The scan reuses the established bounded worker queue and never creates an
unbounded task per PDF. Completion is published in canonical target order;
page updates are serialized and deterministically coalesced to at most about
40 updates per file.
Cancellation propagates through orchestration, protocol I/O, preflight, page
boundaries, and bounded inner loops. The parent terminates only the disposable
read-only PDF worker when cancellation, timeout, crash, protocol failure, or a
hard resource breach prevents cooperative completion; the WPF dispatcher is
not blocked by parser work.

## Security safeguards

- no write handle is opened for a PDF or `metadata.db`;
- execution and recovery scans explicitly omit PDF assessment, preserving the
  Milestone 7/8 revalidation and mutation behavior;
- no network API exists in the PDF path;
- no JavaScript/action is executed and no URI/external reference is opened;
- no attachment API, name, or bytes are accessed;
- no image is rendered/decoded and no raw image bytes are read;
- no OCR, PDF application, shell, renderer, or native codec is invoked;
- complete PDF text/XMP, raw parser exceptions, content, links, or attachment
  data are neither retained nor logged; and
- architecture tests prevent parser types from escaping Infrastructure and
  guard prohibited API/mutation surfaces.

## Post-review remediation

The completed implementation received a safety and correctness review on
2026-07-31. Every reported finding was corrected without widening Milestone 9:

- execution/recovery revalidation uses `includePdfAssessments: false` and a
  regression proves that the PDF inspector is not invoked;
- Windows startup fails closed if Job Object assignment fails, cancellation
  uses a bounded asynchronous termination wait, worker progress is serialized
  through a bounded channel, and worker output/parent response has a cumulative
  4 MiB budget;
- V1 assessment rejects any non-frozen limit profile while lower test-only
  inspector limits remain available for hard-limit regressions;
- object count comes from the cross-reference table and inert active-content
  evidence comes from bounded catalog graph traversal that does not enter
  content, metadata, image, font-file, or embedded-file streams;
- large-document sampling distributes optional pages across the whole range,
  outline order is preserved, useful text requires letters or digits, and a
  truncated text fingerprint is never treated as complete;
- unresolved/non-embedded fonts and invisible text rendering lower
  classification confidence and are disclosed without a score penalty;
  image-heavy, scan-like, illustrated, and partially observable documents
  remain neutral unless an independent quality rule is met;
- ISBN-13 evidence requires a valid checksum and the `978` or `979` prefix,
  and XMP identifier extraction is confined to approved identifier fields;
- repeated-page penalties require at least two nonadjacent runs, retain their
  combined cap, and do not treat one contiguous page run as multiple repeats;
- disqualified WPF rows hide component numbers and disclose page selection,
  sampling limitations, classification evidence, no-OCR provenance, and every
  policy/resource version; and
- WPF no longer references the worker project. Solution build ordering keeps
  the worker deployable without violating `Domain <- Application <-
  Infrastructure <- WPF` project-reference rules.

Regression coverage now includes containment-assignment failure, cumulative
protocol exhaustion, cancellation latency, structural-marker spoofing, XMP
source restriction, non-ISBN EAN rejection, truncated fingerprints,
non-embedded fonts, invisible text, whole-range sampling, adjacent-page
repeats, partial observability, frozen limits, execution-scan isolation,
disqualified UI, and the corrected architecture boundary.

## Commands and results

Commands actually run after implementation:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
git diff --check
```

Final results:

- restore succeeded with all projects up to date;
- build succeeded with zero warnings and zero errors;
- 503 tests passed, zero failed, and two caller-gated real-Calibre
  compatibility tests were skipped;
- PDF-focused suites passed across Domain, Application, Infrastructure,
  Architecture, and WPF;
- format verification passed after repository formatting normalized patched
  line endings; and
- `git diff --check` reported no whitespace errors (only Git's informational
  future CRLF-conversion warnings).

## Known parser limitations and unsupported cases

- PdfPig 0.1.15 exposes the hard parser stack option but not observed recursion
  depth, so the hard depth 64 is enforced while the planned depth-48 advisory
  cannot be emitted.
- Supported high-level page/image APIs do not expose stable indirect content
  or image-object identities. Production exact repeat detection therefore uses
  only complete bounded normalized-text hashes with matching dimensions and
  nonadjacent pages. Shared-content/likely-image evidence remains
  `Insufficient` rather than decoding images or using unstable internals.
- PdfPig filter/XMP APIs may allocate decoder output before returning it to the
  post-decode quota check. Per-file GC/working-set containment and worker
  termination are the outer hard defense.
- Non-embedded or malformed fonts and optional filters can be host/parser
  dependent. Strict missing-font behavior is enabled; unavailable optional
  facts are disclosed, and required failures disqualify.
- Only empty-password accessible encrypted PDFs can complete. V1 accepts no
  supplied password; password-required and unsupported encryption are
  disqualified.
- Active-content evidence is a bounded inert marker count, not semantic action
  execution or a complete security scanner.
- No OCR, rendering, attachment extraction, online lookup, comparison,
  cross-file similarity, or retained-PDF selection is supported.

## Deviations, remaining risks, and next exact step

The only approved-plan capability deviation is the unobservable parser-depth
48 advisory described above. The nuget.org-only repository configuration was
added so the mandated plain restore succeeds in the inherited multi-source
environment. No scoring, classification, hard-limit, safety, or scope rule was
weakened.

Automated synthetic-library acceptance is complete and proves no PDF,
`metadata.db`, or other Calibre-managed file changes. A human has not yet
performed the plan's manual WPF walkthrough, so that checklist item remains
open. It is safe to perform only on Windows in a copied, disposable synthetic
Calibre library, with Calibre closed, execution/recovery disabled, and no
irreplaceable files in scope. Do not use a personal library or treat worker
isolation as a complete hostile-code sandbox. Residual risk is concentrated in
parser behavior before quota callbacks; the isolated worker and process limits
contain, but do not eliminate, a malicious native/runtime defect.

The next exact step is to run the manual WPF synthetic-library acceptance on a
generated fixture set, verify the PDF tab/filter/progress/cancel presentation,
record the result in the Milestone 9 plan, and only then begin a separately
approved Milestone 10 plan.
