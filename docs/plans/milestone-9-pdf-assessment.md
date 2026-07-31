# Milestone 9: PDF Assessment

## Objective

Add a safe, deterministic, explainable PDF inspection and quality-assessment
vertical slice for PDF formats already present in the immutable, read-only
Calibre library snapshot.

Each PDF will be assessed independently. A completed assessment will disclose:

- whether the file opened and its required structures parsed;
- encryption and password-requirement status;
- page count and bounded document metadata;
- whether page text could be extracted and the sampled useful-text percentage;
- a deterministic classification of `DigitalText`, `ScannedWithoutOcr`,
  `ScannedWithOcr`, `Mixed`, `EmptyOrNearEmpty`, `Encrypted`, `Unreadable`, or
  `Unknown`, with confidence and sampling limitations;
- bounded image, page-dimension, outline, active-content, blank-page,
  repeated-page, and resource summaries;
- checksum-validated ISBN evidence from bounded metadata or early-page text;
- a PDF-specific quality score and separate technical and embedded-metadata
  components;
- every finding that contributes to, explains, or disqualifies that score; and
- analyzer, sampling-policy, resource-profile, and scoring-model versions.

The slice ends at analysis and presentation. It does not compare PDFs with one
another, select a preferred PDF, affect consolidation recommendations, generate
cleanup or recovery operations, perform OCR, render pages, or mutate the
library.

## Scope

Milestone 9 includes:

- selecting canonical `PDF` formats from the existing post-hash library scan;
- inspecting only formats whose snapshot status and verified observation permit
  read-only inspection;
- a provider-neutral Application port and bounded PDF assessment use case;
- a maintained managed PDF parser isolated behind Infrastructure;
- per-file parser-process isolation for timeout, cancellation, crash, and
  allocation containment;
- strict path, file-state, signature, size, object, page, stream, image, font,
  metadata, outline, action, and result-size limits;
- deterministic all-page or sampled-page analysis without retaining complete
  document text or decoded images;
- structured translation of missing, inaccessible, zero-length, renamed,
  malformed, truncated, encrypted, password-required, unsupported,
  resource-limited, timed-out, worker-crashed, and changed files;
- independently testable PDF fact-to-finding rules;
- a separately versioned PDF classification policy;
- a separately versioned PDF scoring catalog with capped penalties and explicit
  disqualifiers;
- checksum-valid ISBN-10 and ISBN-13 evidence from bounded sources only;
- deterministic result, classification, sample, identifier, and finding
  ordering;
- scan-level and per-file/per-page progress with responsive cancellation;
- bounded concurrency suitable for libraries containing thousands of PDFs;
- immutable PDF assessments on `LibrarySnapshot`;
- a WPF PDF-assessment view with score, classification, confidence, facts,
  versions, severity filtering, progress, and cancellation;
- synthetic and generated fixtures for valid, unusual, malicious, malformed,
  encrypted, image-based, OCR-layered, and mixed PDFs; and
- documentation, ADR, architecture, security, performance, and regression
  coverage for the completed slice.

## Out of scope

- OCR execution, OCR engine packages, OCR language packs, image preprocessing,
  or OCR correction.
- Rendering PDF pages, rasterizing vector content, launching an external PDF
  viewer, or adding native rendering/codecs.
- Decoding or retaining full-resolution images merely to classify a document.
- Extracting, opening, saving, or scanning embedded attachments.
- Executing JavaScript, launch actions, form actions, URI actions, or any other
  PDF action.
- Opening external links, resolving external resources, or any network access.
- Accepting, storing, prompting for, or brute-forcing PDF passwords in V1.
- Full-document text retention, indexing, export, logging, snippets, search, or
  content recognition.
- PDF-to-PDF or PDF-to-EPUB content fingerprints, similarity, comparison,
  ranking, clustering, or preferred-version selection.
- Milestone 10 EPUB/PDF content fingerprints.
- Changes to Milestone 5 consolidation recommendation policy other than a
  mechanical type adaptation required to preserve its existing EPUB-only
  behavior after the assessment-core refactor.
- Changes to cleanup-plan generation, approval, execution, backup, journaling,
  rollback, recovery, Calibre capability profiles, or mutation leases.
- Direct writes to `metadata.db` or any other Calibre database.
- Rename, move, overwrite, deletion, replacement, extraction, or creation of
  any file inside the Calibre library.
- Calibre CLI invocation of any kind from PDF analysis.
- Online identifier or metadata lookup, Calibre metadata rewriting, fuzzy
  duplicate detection, AI, or recommendation changes.
- Premature persistent assessment caching. The result identity will permit a
  later cache design, but Milestone 9 always computes a fresh assessment.
- Broad PDF repair, sanitization, normalization, conversion, or PDF/A
  conformance certification.
- Automated tests against copyrighted books, a personal library, or the user's
  real Calibre installation or library.

## Relevant requirements and milestone boundary

The following sources are authoritative:

- root and nested `AGENTS.md` files and `PLANS.md`;
- `docs/product-vision.md`;
- `docs/functional-requirements.md`;
- `docs/architecture.md`;
- `docs/domain-model.md`;
- `docs/quality-scoring.md`;
- `docs/safety-and-rollback.md`;
- `docs/test-strategy.md`;
- `docs/roadmap.md`;
- `docs/workflows/implement-feature.md`;
- accepted ADRs 0001 through 0008;
- completed Milestone 0 through Milestone 8 execution plans;
- `docs/handoffs/milestone-7-handoff.md`;
- `docs/handoffs/milestone-8-handoff.md`; and
- the implemented and remediated Milestone 4 EPUB assessment slice.

`docs/roadmap.md` currently places both PDF analysis and EPUB text fingerprints
under “Later milestones” rather than naming a Milestone 9 section. This plan
uses the user's explicit authorization to define PDF assessment as Milestone 9
only. The first documentation step will make that split explicit in the
roadmap: PDF assessment is Milestone 9, while content fingerprints remain
Milestone 10 or later. No content-fingerprint implementation is included here.

The governing invariants are:

- `metadata.db` remains strictly read-only.
- PDF files and every other Calibre-managed file remain read-only.
- Analysis produces immutable findings and assessments, never mutations.
- AI output cannot trigger destructive actions; Milestone 9 adds no AI.
- Domain remains integration-free; Application depends only on Domain;
  Infrastructure owns file, process, parser, JSON-protocol, and PDF concerns;
  WPF uses Application use cases and provider-neutral Domain results.
- All nonzero score adjustments appear on findings.
- Classification is evidence, not a score adjustment.
- A scan alone is not inferior to a born-digital document.
- Incompatible scoring models are never compared silently.
- Cancellation propagates as cancellation and never becomes a quality finding.
- A failed or cancelled scan does not publish a partial replacement snapshot.
- Milestone 7 execution and Milestone 8 recovery behavior remain unchanged.

## Existing implementation inspected

### Shared assessment model and EPUB specialization

Milestone 4 introduced integration-free values for `AssessmentFinding`,
`AssessmentStatus`, `QualityScore`, `AnalyzerVersion`, and
`ScoringModelVersion`. Findings are immutable, evidence is bounded, completed
scores are derived from findings, disqualified assessments have no numeric
score, and findings are ordered by severity, rule ID, evidence key, and
explanation.

The current `FormatAssessment` is not actually format-neutral. Its constructor:

- hard-codes format `EPUB`;
- requires `EpubFeatureSummary`; and
- owns the EPUB score/status/version/finding invariants.

`LibrarySnapshot.EpubAssessments`, the WPF EPUB rows, Application tests, and the
Milestone 5 recommendation policy all consume this type directly. Creating a
second copy named `PdfFormatAssessment` would duplicate the shared assessment
semantics, while putting PDF fields onto `EpubFeatureSummary` would make the
model incoherent. A small, behavior-preserving refactor is therefore required
before adding PDF results.

The implemented EPUB analyzer is `epub-inspector/1.0.1`; the unchanged EPUB
score model is `epub-quality/1.0.0`. Milestone 9 will not alter its limits,
weights, finding IDs, ordering, score values, inspection behavior, or UI
meaning.

### Application orchestration and scan pipeline

`AssessEpubFormatsUseCase` already provides the useful orchestration pattern:

- canonical target selection;
- preallocated result slots;
- `Parallel.ForEachAsync` with bounded concurrency;
- responsive cancellation;
- structured handling for non-present formats;
- canonical book/path result ordering; and
- synchronized progress.

`ScanLibraryUseCase` hashes present format files first, associates successful
hashes with verified `FormatFileObservation` values, then builds EPUB targets,
assesses them, constructs exact duplicate groups, and publishes one immutable
snapshot. `LibraryAnalysisOptions` currently has separate hashing and EPUB
concurrency limits. The PDF phase can reuse this orchestration shape without
forcing EPUB and PDF facts through one unclear inspector contract.

The scan currently feeds only EPUB assessments to the consolidation
recommendation policy. This is intentional Milestone 5 behavior and must remain
so.

### Infrastructure EPUB hardening

`VersOneEpubInspector` demonstrates the required security posture:

- canonical root/path and reparse-point validation;
- read-only access and before/after file-state checks;
- preflight before a third-party parser sees content;
- bounded streams and aggregate counters;
- no extraction and no network;
- parser exception translation without raw exception leakage;
- cancellation checks between bounded operations; and
- immutable, bounded, provider-neutral results.

PDFs have a materially different object graph, stream-filter, font, page, and
parser-hang threat model. The EPUB ZIP controls cannot simply be renamed. The
same trust principles will be reused with PDF-specific enforcement and a
stronger process boundary.

### Domain, recommendation, execution, and recovery coupling

`ConsolidationRecommendationPolicy` and `RecommendationSelections` currently
accept `FormatAssessment` as an EPUB assessment. The policy's decisive rules,
version checks, evidence, digests, and tests are EPUB-specific. The shared-core
refactor must make this parameter explicitly `EpubAssessment` and forward the
same score/findings/versions. It must not allow PDF assessments into that
policy, change a recommendation, change a canonical digest, or introduce a
PDF-retention decision.

Milestones 7 and 8 are complete. Their Calibre 9.11.0 execution and recovery
profiles remain disabled pending opt-in real-Calibre qualification. PDF
analysis does not touch their command runners, capability profiles, mutation
lease, immutable artifacts, backups, journals, verification, or UI workspaces.
Their `FullExecutionLibraryScanner` and `FullRecoveryCurrentStateScanner` are
separate semantic verification paths; neither will resolve or invoke
`IPdfInspector`.

### WPF and tests

`MainWindowViewModel` currently materializes EPUB assessment rows off the UI
thread, bulk-publishes them, maintains one selected assessment, lazily creates
finding rows, and exposes severity filtering and explicit disqualification
text. `MainWindow.xaml` has an EPUB assessment tab. The new PDF presentation can
reuse the generic finding-row/filter behavior and atomic publication pattern.

Existing tests cover:

- assessment value bounds, ordering, score derivation, and disqualification;
- EPUB rule weights, caps, orchestration, scale, concurrency, progress,
  cancellation, changed files, parser limits, and no-network/no-extraction
  behavior;
- scan association and atomicity;
- EPUB-only recommendation behavior;
- WPF row materialization and filtering; and
- project/package, parser, process, mutation, and UI boundaries.

The architecture suite currently assumes that the Calibre mutation runner is
the only `ProcessStartInfo` use in Infrastructure. Milestone 9 needs a second,
closed process boundary exclusively for the read-only PDF worker. The test must
be refined to distinguish and strictly constrain both boundaries, not weakened
to permit arbitrary process creation.

No PDF assessment implementation or PDF package exists in the current tree.
The working tree was clean when inspection began. A planning-time
`dotnet test --no-build` invocation was stopped by the short inspection timeout,
so this plan does not claim a new baseline test result; the verified Milestone 8
handoff remains the latest completed baseline.

## Reuse of the assessment framework

### Shared result core

Refactor `FormatAssessment` into a genuinely shared identity/result aggregate:

- canonical `CalibreBookId`, format, relative path, file fingerprint, and
  verified observation;
- `AssessmentStatus`;
- optional total `QualityScore`;
- analyzer and scoring-model versions;
- immutable ordered findings; and
- an immutable score-component breakdown.

It will no longer contain `EpubFeatureSummary`. Two typed wrappers provide
format-specific meaning:

- `EpubAssessment` = shared result core + `EpubFeatureSummary`;
- `PdfAssessment` = shared result core + `PdfFeatureSummary`.

The wrappers expose the common values needed by existing presentation and
policy code without making a generic type leak through every layer.
`LibrarySnapshot` will hold separately typed, independently ordered
`EpubAssessments` and `PdfAssessments`.

### Findings and score components

Add a bounded `AssessmentScoreComponentId` to `AssessmentFinding`. Existing
EPUB findings default to the single `overall` component and retain their exact
V1 behavior. PDF findings use:

- `technical`, capped to 85 points; and
- `embedded-metadata`, capped to 15 points.

`FormatAssessment` calculates each component as:

```text
component score = clamp(sum(non-disqualifying finding adjustments
                            assigned to the component), 0, component maximum)
total score     = sum(component scores)
```

The aggregate validates that:

- every nonzero finding belongs to exactly one declared component;
- component IDs and maxima are unique and deterministic;
- component maxima sum to 100;
- completed results contain no disqualifier and have a derived score;
- disqualified results contain at least one disqualifier, have zero score
  adjustments on disqualifying findings, and have no numeric score; and
- the supplied/displayed total exactly equals the derived component total.

This keeps PDF metadata quality separate from technical/file-content quality
without creating a parallel scoring framework. Zero-adjustment findings still
identify the relevant component and explain classification, encryption,
sampling, actions, attachments, and limitations.

### EPUB compatibility

The refactor is accepted only if regression tests prove:

- every existing EPUB assessment has the same status, score, versions, finding
  identities, finding adjustments, feature facts, and ordering;
- recommendation selection, evidence, canonical digests, and exports are
  byte-for-byte unchanged for existing fixtures;
- only `EpubAssessment` can enter the recommendation policy; and
- no PDF score or finding can affect recommendation, cleanup-plan, execution,
  or recovery behavior.

## Proposed architecture

```text
Domain
  FormatAssessment (shared result) <- EpubAssessment / PdfAssessment
       ^
Application
  AssessPdfFormatsUseCase -> IPdfInspector
  PdfAssessmentEngine -> rules, classification, sampling policy
       ^
Infrastructure
  IsolatedPdfInspector -> bounded worker protocol/process
  PdfPigPdfInspectionEngine -> read-only stream + PdfPig
       ^
PdfWorker composition root

WPF composition root -> Application use cases + Infrastructure registrations
WPF view models       -> Domain/Application results only
```

Application owns orchestration, fact-to-finding rules, sampling selection,
classification, score policy, progress contracts, and the `IPdfInspector`
port. Infrastructure owns paths, file handles, process control, worker IPC,
third-party parser configuration, PDF tokens, stream filters, text/image fact
collection, ISBN candidate extraction, and exception translation.

The worker is a .NET 10 console composition root that references
Infrastructure. Its `Program` contains no PdfPig type and invokes an
Infrastructure-owned worker host. Therefore PdfPig remains referenced and
named only inside Infrastructure even though its work runs out of process.

## Domain model changes

### `PdfAssessment`

`PdfAssessment` contains:

- the shared `FormatAssessment` result, whose canonical format is `PDF`;
- `PdfFeatureSummary`;
- `PdfScoreBreakdown` exposing technical raw contribution, technical score,
  metadata raw contribution, metadata score, and total.

Classification, confidence, and sampling disclosure live once in
`PdfFeatureSummary` and may be forwarded as convenience properties without
duplicated stored state.

It rejects an EPUB result, mismatched versions, a feature/result identity
mismatch, out-of-range percentages or counts, duplicate/unsorted bounded
evidence, a score breakdown inconsistent with findings, and contradictory
status/facts.

### `PdfFeatureSummary`

The immutable summary contains bounded facts only:

- `PdfOpenStatus`;
- `PdfEncryptionStatus`:
  `NotEncrypted`, `EncryptedAccessibleWithEmptyPassword`,
  `PasswordRequired`, `UnsupportedEncryption`, or `Unknown`;
- nullable page count and PDF version;
- bounded `PdfDocumentMetadataSummary` with sanitized title, author, subject,
  creator, producer, creation date, modification date, and per-field
  parse/presence status;
- `PdfTextSummary` with extraction status, sampled analyzable page count,
  text-bearing page count, useful-character count (capped/disclosed), useful
  text percentage, and density band;
- `PdfImageSummary` with sampled image-bearing pages, bounded image count,
  image-dominant page percentage, and `TextHeavy`, `ImageHeavy`, `Balanced`,
  `NoObservableContent`, or `Unknown`;
- outline presence and bounded entry count;
- active-content counts for JavaScript/action, external-reference, and
  embedded-file markers;
- aggregate `PdfPageAnalysisSummary` for sampled, failed, suspiciously blank,
  unusually sized, and resource-heavy pages;
- bounded repeated-page clusters with evidence strength;
- bounded validated `PdfIdentifierEvidence`;
- classification and confidence;
- `PdfSamplingSummary`;
- parser/resource-limit profile identifier; and
- flags that a fact is incomplete, capped, sampled, or unavailable.

The final snapshot will not retain every sampled page. The inspector may return
up to 200 transient page fact records to Application, but `PdfFeatureSummary`
retains aggregate counts and at most 20 deterministic page-number examples per
anomaly type and 20 repeat clusters. No retained value contains a page's text,
URI, JavaScript, attachment name/content, raw metadata XML, raw parser object,
absolute path, or exception message.

### Classification values

Add:

- `PdfDocumentClassification`;
- `PdfClassificationConfidence` with `Insufficient`, `Low`, `Medium`, `High`;
- `PdfSamplingMode` with `AllPages` and `DeterministicSample`;
- `PdfRepeatedPageEvidence` with `ExactSharedPageContent`,
  `ExactNormalizedText`, `LikelySharedImageContent`, and `Insufficient`;
- `PdfTextDensityBand`; and
- `PdfContentBalance`.

Classification is stored separately from `AssessmentStatus` and `QualityScore`.
For example, an accessible encrypted PDF can be classified `Encrypted` and
still have a completed score, while a password-required PDF is both classified
`Encrypted` and disqualified.

## Application use cases and interfaces

### `IPdfInspector`

`IPdfInspector.InspectAsync` accepts a provider-neutral `PdfInspectionRequest`
and a provider-neutral page-selection callback:

- canonical library root and safe expected relative path;
- record/format identity;
- expected SHA-256 fingerprint and verified file observation;
- V1 inspection/resource limits; and
- `IProgress<PdfInspectionProgress>` plus `CancellationToken`.

After the inspector opens the document, it invokes the callback exactly once
with a bounded `PdfDocumentHeaderFacts` value containing page count and safe
outline destinations. Application calls `PdfPageSamplingPolicy.Select` and
returns the immutable page-number list. The Infrastructure adapter validates
that list before forwarding it to the already-open worker session. This keeps
sampling policy in Application without parsing the document twice or exposing a
process/session/parser type.

It returns a provider-neutral `PdfInspectionResult`:

- sanitized document facts;
- bounded transient page facts in requested page-number order;
- bounded identifier candidates/evidence;
- structured `PdfInspectionProblem` codes;
- before/after observation and association identity; and
- resource counters and explicit incomplete/capped flags.

No contract contains `PdfDocument`, `Page`, token, filter, image, filesystem,
process, JSON, or logger types.

### Target selection and assessment orchestration

`AssessPdfFormatsUseCase` will:

1. select only exact case-insensitive `PDF` format labels from the snapshot
   inputs;
2. canonicalize and sort targets by `CalibreBookId` and normalized relative
   path;
3. convert missing, inaccessible, invalid, hash-failed, or observation-missing
   targets directly to structured disqualified assessments;
4. obtain page count through the isolated inspector's bounded header/open
   stage;
5. apply the deterministic sampling policy and request page inspection through
   the same worker session, avoiding a second parse;
6. run at most `MaxPdfAssessmentConcurrency` workers;
7. translate expected provider problems to facts/findings while allowing
   unexpected implementation failures to fail the atomic scan;
8. revalidate identity, observation, result bounds, and requested page order;
9. discard every partial fact if the file changed;
10. run the pure classification policy and independently testable rule catalog;
11. derive the score exclusively from findings;
12. store into preallocated canonical result slots; and
13. publish monotonic file/page progress without completion-order-dependent
    result ordering.

The “open then choose sample” interaction is one worker conversation. The
worker sends bounded document-header facts, waits for the parent-selected page
list, inspects those pages using the already-open document, and returns the
final facts. This avoids parsing each file twice while keeping sampling policy
in Application.

### Scan integration

`ScanLibraryUseCase` will add an `AssessingPdfFormats` phase after EPUB
assessment and before final duplicate/recommendation publication. The phases
remain sequential so EPUB and PDF parsers cannot multiply each other's memory
pressure. Hashing remains the source of the expected fingerprint/observation.

`LibraryAnalysisOptions` gains:

- `MaxPdfAssessmentConcurrency`, default 2; and
- immutable `PdfInspectionLimits.V1`.

The PDF phase is optional at constructor level only where current unit-test
fixtures require gradual composition. Production and complete integration
fixtures register it. On cancellation or an unexpected PDF implementation
failure, no partially updated `LibrarySnapshot` replaces the previous
published snapshot.

PDF assessments are added to the snapshot after exact association validation.
Recommendation generation continues to receive only
`snapshot.EpubAssessments`.

### Independently testable rules

Each PDF V1 rule implements a small pure rule contract over provider-neutral
facts and returns zero or more findings. `PdfAssessmentEngine` owns an immutable
rule list sorted by a frozen catalog order. Rules are instantiated directly,
not located through a service locator. Classification and scoring aggregation
are separate pure policies.

The engine validates at startup/test time that:

- rule IDs are unique;
- every score adjustment matches the frozen catalog;
- repeatable rule families declare a cap;
- disqualifier adjustments are zero;
- each nonzero adjustment has a valid score component;
- the maximum positive technical and metadata totals are 85 and 15;
- no classification finding has a nonzero adjustment; and
- output ordering is independent of rule evaluation or input completion order.

## PDF dependency evaluation

### Proposed package

Use [PdfPig 0.1.15](https://www.nuget.org/packages/PdfPig/0.1.15), pinned
centrally and referenced only by `CalibreLibraryCleaner.Infrastructure`.
Version 0.1.15 is the current stable release at planning time; newer 0.1.16
packages are prerelease builds and will not be selected. PdfPig is Apache-2.0
licensed and provides managed PDF parsing, encryption recognition/password
handling, page count, document information, letters and glyph geometry, image
object/bounds access, bookmarks, XMP metadata, cross-reference/token access,
and document creation useful for synthetic fixtures. Its
[official release](https://github.com/UglyToad/PdfPig/releases/tag/v0.1.15)
also includes additional stack-depth protections and exposed cross-reference
information.

The package targets frameworks compatible with .NET 10 and does not require a
renderer or native runtime for the planned inspection API. No
`PdfPig.Rendering.*`, DCT/JPEG, JBIG2, JPX, Skia, PDFium, Ghostscript, or other
optional rendering/codec package will be added.

### Required usage restrictions

Official
[`PdfDocumentFactory` source](https://github.com/UglyToad/PdfPig/blob/v0.1.15/src/UglyToad.PdfPig/Parser/PdfDocumentFactory.cs)
and
[`ParsingOptions` source](https://github.com/UglyToad/PdfPig/blob/v0.1.15/src/UglyToad.PdfPig/ParsingOptions.cs)
show that:

- `PdfDocument.Open(string)` calls `File.ReadAllBytes`;
- `PdfDocument.Open(Stream)` copies a non-seekable stream into memory;
- parser options have no `CancellationToken`;
- `MaxStackDepth` is available but is not a complete hostile-input quota;
- `Page.Letters` and page operations can still allocate while parsing a page;
- image byte/PNG helpers decode data; and
- the embedded-file API returns decoded attachment bytes.

Therefore Infrastructure will:

- open a canonical seekable `FileStream` itself with `FileMode.Open`,
  `FileAccess.Read`, restrictive sharing, asynchronous options, and no
  write-through path;
- call only `PdfDocument.Open(Stream, ParsingOptions)`;
- reject a non-readable or non-seekable stream before PdfPig;
- set explicit strict options:
  `UseLenientParsing = false`, `SkipMissingFonts = false`,
  `ClipPaths = false`, `UseActualText = true`, `MaxStackDepth = 64`,
  a bounded filter provider, and a no-op parser logger;
- try only the empty password implicitly used by PdfPig and never accept a
  password from UI/configuration;
- enumerate image metadata/bounds only and never call `TryGetBytes`,
  `TryGetPng`, or any rendering API;
- treat an image codec that is unnecessary for metadata/bounds inspection as
  inert zero-adjustment evidence; only an unsupported filter that prevents
  required page/content parsing can reduce or disqualify an assessment;
- detect embedded-file/action markers through a bounded token walk and never
  call `Advanced.TryGetEmbeddedFiles`;
- never call merge, writer, form mutation, annotation/action execution, URI
  resolution, or external resource APIs; and
- dispose the document, page-local facts, and file handle in the worker before
  returning.

Strict parsing is selected for V1 because lenient parsing does not provide a
complete, stable repair report from which explainable findings could be
derived. A malformed-but-recoverable PDF may therefore be reported unreadable
instead of silently “repaired.” Any future lenient fallback is an analyzer
behavior change requiring an ADR update, new findings, characterization tests,
and an analyzer-version bump.

### Alternatives considered

- PDFsharp is suitable for creation/manipulation but does not provide the same
  extraction and page-fact coverage for this slice.
- iText has capable parsing but its AGPL/commercial licensing is unsuitable for
  the current dependency posture.
- PDFium, MuPDF, Docnet, Skia renderers, Ghostscript, and external applications
  add native/rendering/execution surfaces that are unnecessary because
  Milestone 9 does not render or OCR.

PdfPig is preferred for the smallest managed read/extraction surface, not
because it is assumed safe against every hostile PDF.

### Versioning and ADR

Create ADR 0009 before adding the package:

`docs/adr/0009-select-isolated-pdf-inspection-stack.md`

It will record:

- PdfPig 0.1.15 and Apache-2.0 licensing;
- exact parser options and forbidden APIs;
- the seekable stream requirement;
- the per-file worker-process boundary and protocol;
- default hard/soft limits;
- no renderer/native codec/OCR packages;
- strict parsing and no password input;
- upgrade review and vulnerability-audit requirements; and
- analyzer/scoring version rules.

No floating or prerelease version is allowed. A parser dependency, parser
option, sample policy, fact semantic, threshold, or hard-limit change bumps
`pdf-inspector/1.x`; incompatible result semantics bump its major version.
Weight, cap, component, disqualifier, or formula changes bump
`pdf-quality/1.x`; incompatible scores bump its major version. Sampling and
resource profiles are additionally recorded as
`pdf-sampling/1.0.0` and `pdf-limits/1.0.0`.

The initial stored values are:

- analyzer: `pdf-inspector/1.0.0`;
- scoring model: `pdf-quality/1.0.0`;
- classification policy: `pdf-classification/1.0.0`;
- sampling policy: `pdf-sampling/1.0.0`;
- resource profile: `pdf-limits/1.0.0`; and
- worker protocol: `pdf-worker-protocol/1.0`.

No code may compare two PDF scores unless their scoring-model versions are
identical or an explicit, tested compatibility policy exists. Milestone 9 has
no score-comparison use case.

## Infrastructure design

### Per-file isolated worker

`IsolatedPdfInspector` is the production `IPdfInspector`. For each file it:

1. validates the canonical root, expected relative path, path containment,
   reparse points, and expected observation;
2. starts the fixed sibling `CalibreLibraryCleaner.PdfWorker` executable with
   `UseShellExecute = false`, a constant argument, a minimal environment,
   redirected standard streams, and no user-controlled command-line text;
3. sends one strict, versioned, length-bounded JSON request on standard input;
4. reads length-bounded progress/header/final messages from standard output;
5. enforces wall-clock, CPU, managed-heap, working-set, protocol-size, and
   cancellation limits;
6. terminates the read-only worker process tree on cancellation, timeout,
   allocation breach, protocol violation, or parent shutdown;
7. translates an expected worker exit/problem to a structured result and
   treats an invalid/crashed worker as a structured disqualifying parser
   problem; and
8. revalidates file state and association before returning.

Killing this worker is safe because it has no mutation capability and writes
neither library nor application artifacts. This policy is isolated from the
Milestone 7/8 rule that mutation processes are not terminated on cancellation.

Each PDF gets a new worker in V1. This has process-start overhead but prevents
state leakage and allows the OS to reclaim a pathological parser's allocations.
A prewarmed pool is a future optimization only after equivalent isolation is
proven.

PdfPig's synchronous calls run directly on the dedicated worker's main thread.
The WPF/Application process awaits asynchronous pipe/process operations; it
does not block the dispatcher, wrap parser calls in unbounded `Task.Run`, or use
sync-over-async.

The parent sets `DOTNET_GCHeapHardLimit` for the worker, disables diagnostics,
assigns the just-started, stdin-blocked worker to a Windows Job Object before
sending the request, monitors `WorkingSet64` and `TotalProcessorTime`, and owns
the final wall-clock watchdog. The Job Object permits one active process, sets
the process-memory limit, and uses kill-on-close; no third-party native package
is introduced. This narrow Windows API use is justified by the WPF application's
Windows-only target and the need for a hard process-memory boundary around a
non-cancellable parser, and it is recorded in ADR 0009. These are containment
layers, not a claim of a complete operating system sandbox. The worker has no
network client or resolver code; source and architecture tests prohibit network
APIs in the PDF production folders.

### Worker protocol

Use `pdf-worker-protocol/1.0` with a closed message-kind discriminator:

- `Request`;
- `DocumentOpened`;
- `PageSelection`;
- `Progress`;
- `Completed`; and
- `Failed`.

Messages reject unknown/duplicate properties, excessive nesting, strings,
arrays, counts, page numbers, and total bytes. The parent never passes the PDF
path as a process argument. Stderr is capped and discarded except for a
sanitized infrastructure event code; it is never stored as a PDF finding or
book-content log.

The header message permits Application to calculate page sampling while the
same document stays open. If the parent does not respond within the bounded
selection window, or requests pages outside the disclosed count/cap, the worker
exits without inspecting pages.

### Read-only file and stale-result guard

Inside the worker:

- recanonicalize root and full path;
- reject rooted/traversing relative paths and any reparse point in the root-to-
  file chain;
- compare length, last-write UTC, and stable file identity where available to
  the expected `FormatFileObservation`;
- read only through one seekable read-only handle;
- perform bounded signature/tail preflight;
- inspect through that same handle;
- flush no data and create no side files;
- compare observation and handle metadata after inspection; and
- return both observations.

The parent repeats the path/observation check. Any mismatch produces
`PDF.FILE.CHANGED_DURING_INSPECTION`, discards all facts, and disqualifies the
result. `FileShare.Read` blocks ordinary writes/replacements on Windows during
inspection. The existing documented residual race—an actor forging the same
size/time identity outside the held-handle guarantees—remains a limitation and
is not described as transactional protection.

### Parser and token handling

The worker performs:

- bounded header and `startxref`/tail plausibility checks before PdfPig;
- strict PdfPig open against the seekable handle;
- encryption/page-count/PDF-version/document-information collection;
- bounded XMP decode and DTD-disabled XML parsing only when its stream is
  within limits, using `DtdProcessing.Prohibit`, `XmlResolver = null`, bounded
  characters/entities/depth, and no schema/network resolution;
- page-tree and cross-reference count checks before sampling;
- bounded outline traversal with cycle/reference tracking;
- bounded catalog/name/action traversal to count JavaScript, launch/open/
  additional actions, remote/URI references, and embedded-file/file-spec
  markers;
- rejection of external file-stream/resource references used by required page
  content; their paths are never resolved or opened;
- one sequential parse of each selected page;
- letter-based text counters and glyph geometry;
- image-object placement/sample-dimension counters without byte decoding;
- bounded operation/resource/font problem counters;
- incremental normalized-text hashing; and
- before/after stale-file checks.

No JavaScript or action is executed. URIs are counted but not retained or
opened. Attachment markers are counted but names, MIME types, and bytes are not
read. Recursive walks use indirect-object visited sets, a maximum depth, and a
maximum object count. Cycles become structured findings rather than recursion.

### Exception translation

Expected conditions map to closed problem codes such as:

- `MissingFile`, `InaccessibleFile`, `UnsafePath`, `ChangedFile`;
- `ZeroLength`, `InvalidSignature`, `TruncatedFile`, `MalformedStructure`;
- `Encrypted`, `PasswordRequired`, `UnsupportedEncryption`;
- `PageTreeUnavailable`, `ZeroPages`, `PageUnreadable`;
- `MalformedFont`, `MalformedMetadata`, `MalformedOutline`;
- `UnsupportedFilter`, `UnsupportedStructure`;
- `StackDepthExceeded`, `ObjectLimitExceeded`, `StreamLimitExceeded`,
  `PageLimitExceeded`, `DimensionLimitExceeded`, `ImageLimitExceeded`,
  `OperationLimitExceeded`, `MetadataLimitExceeded`;
- `ParserTimeout`, `CpuLimitExceeded`, `MemoryLimitExceeded`;
- `WorkerCrashed`, `WorkerProtocolInvalid`; and
- `IncompleteInspection`.

Raw exception types/messages, object source, text, binary content, absolute
paths, and parser logs never enter Domain results, WPF rows, exports, or normal
logs. Unexpected in-process programming faults in the parent still fail the
atomic scan rather than being mislabeled as a PDF defect.

## Parsing and resource-safety limits

The following are proposed V1 defaults. They are configurable only through a
validated immutable profile. Raising, lowering, or disabling a limit changes
analyzer behavior and requires review/versioning.

| Resource | Soft/report threshold | Hard behavior |
| --- | ---: | --- |
| File length | 512 MiB | 1 GiB: disqualify before parser |
| Header scan | n/a | first 1 KiB only |
| Tail/`startxref` scan | n/a | last 1 MiB only |
| Wall time per file | 45 s warning | 60 s: terminate worker/disqualify |
| Worker CPU time | 30 s warning | 45 s: terminate/disqualify |
| Managed heap | 384 MiB warning | 512 MiB hard limit |
| Working set | 512 MiB warning | 768 MiB: terminate/disqualify |
| Parser stack depth | 48 | 64 |
| Cross-reference/object count | 250,000 | 500,000 |
| Page count | sampling after 200 | 100,000 |
| Sampled pages | n/a | 200 |
| Outline nodes/depth | 5,000 / 32 | 10,000 / 64 |
| Encoded stream length | 32 MiB | 64 MiB per stream |
| Decoded stream output | 32 MiB | 64 MiB per stream |
| Aggregate decoded bytes | 256 MiB | 512 MiB |
| Metadata input | 32 KiB per field | 64 KiB per field; retain 512 chars |
| XMP decoded XML | 1 MiB | 4 MiB |
| Useful/glyph characters per page | 250,000 | 1,000,000 |
| Useful/glyph characters per sample | 5,000,000 | 10,000,000 |
| Normalized fingerprint input | n/a | 100,000 characters/page |
| Early ISBN text | n/a | first 5 pages, 32,768 normalized chars total |
| Images per sampled page | 500 | 1,000 |
| Images across sample | 10,000 | 20,000 |
| Image samples per dimension | 50,000 | 100,000 |
| Declared pixels per image | 100 million | 500 million |
| Page graphics operations | 250,000 | 1,000,000/page, 5,000,000/sample |
| Page dimension | 14,400 pt (200 in) | 72,000 pt (1,000 in) |
| Retained page examples | n/a | 20 per anomaly kind |
| Retained repeat clusters | n/a | 20 |
| Retained identifiers | n/a | 20 |
| Retained PDF findings | n/a | 128; aggregate by rule before this point |
| Worker message | 64 KiB | 4 MiB total response |

The bounded filter provider rejects oversized encoded inputs and decoded
outputs and uses checked aggregate counters. A decompressor may allocate before
returning its output; therefore filter checks are not the sole defense. Worker
heap/working-set/CPU/wall-clock containment is mandatory.

Hard-limit breaches disqualify because V1 cannot produce a comparable complete
assessment. Soft thresholds produce bounded findings and may reduce the score.
The result records which counters were capped. Limits never cause the
application to continue with facts whose completeness is falsely implied.

Cancellation is checked:

- before worker start;
- while writing/reading every bounded protocol message;
- between preflight, open, metadata, outline, token-walk, and page stages;
- between every selected page;
- at bounded intervals while iterating letters, images, operations, outline
  nodes, object references, and metadata; and
- before final stale-file validation and result publication.

PdfPig calls themselves are not cooperatively cancellable. Parent cancellation
terminates the read-only worker, targeting a measured cancellation latency of
under one second after cancellation reaches the inspector.

## Safety considerations

Milestone 9 is an analysis-only feature. Its complete production authority is:

- read already-discovered snapshot metadata;
- open one canonical existing PDF through a read-only handle;
- create a short-lived application worker process;
- retain bounded immutable facts/findings in memory; and
- display those facts in WPF.

It has no authority to create or modify a file in the library, write SQLite,
invoke Calibre, execute PDF behavior, extract content to disk, or access the
network. The worker has no reference to execution/recovery abstractions and
receives no cleanup plan, approval, backup path, Calibre executable, mutation
profile, or library-write capability.

Safety is enforced in layers:

1. snapshot status, fingerprint, and observation gate target eligibility;
2. canonical path and reparse checks bind the target inside the expected
   library without granting write access;
3. bounded preflight rejects obviously unsafe or oversized input before the
   parser;
4. strict parser options, recursive/object/filter/page/resource quotas constrain
   work inside the worker;
5. heap, working-set, CPU, wall-clock, protocol, and cancellation watchdogs
   contain parser behavior that cannot cooperate;
6. active content, links, and attachments are token facts only and are never
   executed, opened, resolved, or extracted;
7. before/after observation checks discard stale results;
8. Application validates every provider result and derives every score from
   bounded findings; and
9. the scan publishes only a complete immutable snapshot.

Expected PDF failures are evidence about one PDF, not application crashes.
Unexpected programming-contract failures stop the atomic scan. Cancellation is
neither a defect nor a score. No recovery action is needed after a killed PDF
worker because the worker is read-only and creates no persistent artifact.

Structured logging may record event code, elapsed time, counters, status, and
safe record/format identity. It must not record page text, metadata values,
ISBN source text, hashes used for repeat evidence, URI/action values,
attachment names/content, raw exceptions, parser logs, or absolute paths.

## Deterministic page sampling

`pdf-sampling/1.0.0` selects pages after the worker reports a valid page count.

1. Zero pages is structurally invalid and disqualifying.
2. Documents with 1 through 200 pages inspect every page.
3. Documents with more than 200 pages inspect exactly 200 unique pages:
   - first 5 pages;
   - last 3 pages;
   - up to 32 resolved outline target pages and their immediately following
     page, traversed in document order and deduplicated;
   - evenly distributed pages calculated with integer arithmetic over
     `[1, pageCount]` until the cap is filled; and
   - if collisions leave slots, the lowest not-yet-selected page numbers fill
     them deterministically.
4. The final request is sorted numerically before inspection.

Outline-derived pages are included only when their targets resolve without
expanding beyond the outline limits. An absent or malformed outline does not
change the mandatory edge/even sample. Early ISBN inspection uses only the
already-selected first five pages.

The result records total pages, requested pages, successfully analyzed pages,
selection categories, sample percentage, policy version, whether all pages
were inspected, and any page failures. Classification of a sampled document is
explicitly labeled sample-based and cannot receive `High` confidence.

The sample is a policy input, not a content fingerprint. It is never compared
between books and retains no book text.

## Text, image, and page facts

### Text facts

For each sampled page, Infrastructure iterates glyph/letter values once and:

- counts Unicode letters and digits as useful characters;
- ignores whitespace/control-only values;
- computes a deterministic glyph-area upper bound clipped to page dimensions;
- feeds normalized uppercase letters/digits and single word separators into an
  incremental SHA-256 page-text hash;
- stops hashing at 100,000 normalized characters and marks the fingerprint
  incomplete; and
- discards glyph strings and parser page objects after the page.

Page text bands are:

- `None`: fewer than 10 useful characters;
- `Sparse`: 10 through 49;
- `Moderate`: 50 through 199; and
- `Useful`: at least 200, or at least 50 with clipped glyph-area coverage of at
  least 0.5 percent.

“Text-extraction available” means at least one sampled page produced glyph
facts without a text/parser failure. “Useful-text percentage” is the percentage
of successfully analyzed sampled pages in the `Useful` band. Counts and
percentages are evidence only; no extracted text is stored.

### Image and graphics facts

For each sampled page:

- enumerate image objects and placement bounds;
- read declared sample width/height only;
- compute a clipped sum of image placement areas capped at 100 percent of page
  area;
- count nontrivial graphics operations without retaining operands; and
- never decode an image stream.

The placement sum is disclosed as an approximation, not a pixel-accurate union.
An image-dominant page has at least one image covering 70 percent of the page or
an aggregate clipped image-placement coverage of at least 80 percent. A
text-dominant page has useful text and less than 50 percent image coverage.
A contentful page has useful/sparse text, at least 5 percent image coverage, or
at least 10 nontrivial graphics operations.

Percentages are retained as integer basis points and threshold comparisons use
integer cross-multiplication, avoiding floating-point/culture rounding. Medians
use the lower middle value of the sorted integer observations. “Consistent page
image geometry” means at least 80 percent of image-dominant pages have effective
page aspect ratio within 1 percent of the lower median and dominant-image
normalized bounds/aspect ratio within 2 percent of their lower medians. Rotated
pages are normalized before comparison. Missing, non-finite, capped, or invalid
geometry cannot satisfy this signal.

`PdfContentBalance` uses aggregate sampled facts:

- `TextHeavy`: at least 70 percent text-dominant pages;
- `ImageHeavy`: at least 70 percent image-dominant pages;
- `Balanced`: neither reaches 70 percent and both are present;
- `NoObservableContent`: at least 80 percent non-contentful; or
- `Unknown`: insufficient analyzable evidence.

Image heaviness has no automatic negative score.

## Classification algorithm

`pdf-classification/1.0.0` is a pure, ordered policy. It emits one classification
finding with zero score adjustment and the exact aggregate thresholds that
matched.

### Precedence and evidence

| Classification | Required evidence |
| --- | --- |
| `Unreadable` | Required open/page-tree facts are unavailable for a non-encryption reason, or a hard resource/parser failure prevents assessment. |
| `Encrypted` | An encryption dictionary is present. Password-required and unsupported encryption remain disqualified; empty-password-accessible encryption may still be inspected and scored. |
| `EmptyOrNearEmpty` | At least 80% of analyzable sampled pages have fewer than 10 useful characters, under 5% image coverage, and fewer than 10 nontrivial graphics operations. |
| `Mixed` | At least 20% are text-dominant and at least 20% are image-dominant without useful text, or the image-dominant cohort itself contains at least 20% useful-text and 20% no-useful-text pages. Requires at least 5 analyzable pages. |
| `ScannedWithOcr` | At least 80% are image-dominant, at least 80% contain useful text, median image coverage is at least 85%, page image geometry is consistent, and at least 5 pages are analyzable. This means “scan-like pages with a text layer,” not proof that an OCR engine created it. |
| `ScannedWithoutOcr` | At least 80% are image-dominant, fewer than 10% contain useful text, median useful characters are below 20, page image geometry is consistent, and at least 5 pages are analyzable. |
| `DigitalText` | At least 80% contain useful text, fewer than 50% are image-dominant, and the document does not satisfy `Mixed` or a scan-like rule. |
| `Unknown` | Evidence is insufficient, threshold margins are ambiguous, page failures are too numerous for a stable class, or an image-heavy/illustrated document lacks reliable scan cues. |

Within assessable content, precedence is
`EmptyOrNearEmpty`, `Mixed`, `ScannedWithOcr`, `ScannedWithoutOcr`,
`DigitalText`, then `Unknown`. This prevents a full-page scan with a text layer
from being labeled merely digital and prevents a mixed book from being reduced
to its majority.

### Confidence

- `Insufficient`: fewer than 3 analyzable pages when the document has at least
  3 pages, more than 20 percent sampled page failures, capped facts required by
  the matched rule, or unresolved hard ambiguity.
- `Low`: fewer than 5 pages, a matched percentage lies within 5 percentage
  points of a boundary, or image/geometry evidence is weak.
- `Medium`: all pages of a 5–19 page document were inspected with at least a
  10-point margin, or a deterministic sample of a larger document has at least
  a 15-point margin.
- `High`: all pages of a document with at least 20 pages were inspected and all
  matched thresholds have at least a 15-point margin.

`ScannedWithOcr` is capped at `Medium` because a text layer is not proof of OCR.
Any large document assessed by sampling is capped at `Medium`.

### Short and illustrated documents

One- through four-page documents inspect every page but cannot receive more
than `Low` classification confidence. A short image-based booklet is
`Unknown/Low`, not automatically `ScannedWithoutOcr` or defective. Comics,
illustrated books, technical drawings, music, and facsimile editions can be
image-heavy with little text; absent consistent full-page scan evidence they
remain `Unknown` or `Mixed`. Even when confidently scan-like, classification
does not reduce the score.

The UI explanation states that classification is heuristic, sample-based when
applicable, and not a value judgment or accessibility certification.

## Suspicious blank-page detection

A sampled page is a conservative blank candidate only when all are true:

- fewer than 10 useful characters;
- image coverage below 1 percent;
- fewer than 5 nontrivial graphics operations;
- valid nonzero page dimensions; and
- no page parse problem or capped content counter.

The first and last page are allowed as intentional blanks only when the
document has at least three analyzed pages and at least one other contentful
page. For documents with fewer than ten analyzed pages, all-pages blankness is
reported immediately; otherwise no warning is emitted until candidates beyond
the edge allowance exceed the greater of two pages or 5 percent of analyzed
pages. The finding records only counts and at most 20 page numbers. It says
“suspiciously blank,” never “corrupt.”

## Repeated-page detection

V1 distinguishes three evidence levels:

1. `ExactSharedPageContent`: both pages reference the same complete ordered set
   of indirect `/Contents` objects and the same effective resources, media/crop
   boxes, and rotation, with no direct/capped/failed value. This is exact shared
   PDF drawing content within the inspected document; annotations remain
   separately excluded and disclosed.
2. `ExactNormalizedText`: the complete, non-truncated normalized page-text
   SHA-256 matches, page dimensions match within 0.1 point, each page has at
   least 200 useful characters, and the pages are not adjacent title/template
   pages. This means exact normalized extracted text, not byte-identical PDF
   page content.
3. `LikelySharedImageContent`: image-only pages use the same indirect image
   object identities at the same placements, page dimensions match, image
   coverage is at least 70 percent, and no count/fingerprint was capped. This
   is labeled likely because a shared background/template can reuse an image.
4. `Insufficient`: short, sparse, truncated, standard-template-like, unsupported,
   or image metadata without stable object identity. No duplicate penalty is
   applied.

Header/footer repetition alone cannot match because the complete normalized
page text is hashed and pages below the 200-character threshold are excluded.
Adjacent intentional repeated title/chapter-template pages are not penalized.
Only extra pages after the first member of a non-adjacent cluster contribute a
penalty. A cluster uses its strongest evidence only, so exact content, exact
text, and likely image evidence cannot penalize the same pages more than once.
Results retain hashes/object-identity digests and page numbers, never
normalized text, token bodies, or image bytes.

Repeated empty candidates are reported through the blank-page count/pattern
evidence and never upgraded to an exact or likely duplicate-page claim solely
because both pages are empty.

No cross-document or cross-book fingerprint comparison is implemented.

## Identifier detection

ISBN detection is bounded to:

- sanitized document-information and bounded XMP title/subject/keywords/
  identifier fields; and
- at most 32,768 normalized characters across the first five pages already
  selected for inspection.

The detector recognizes ISBN-labeled or plausibly delimited ISBN-10/ISBN-13
candidates, removes permitted separators, and validates the ISBN-10 modulus-11
or ISBN-13 modulus-10 check digit. Invalid checksums produce no identifier
evidence or score. A bounded informational count may say invalid candidates
were ignored without retaining their digits.

Candidate scanning is single-pass over the bounded characters, or uses a
compiled non-backtracking regex with an explicit timeout. It never applies an
unbounded backtracking expression to parser-provided text.

Valid evidence records:

- normalized ISBN;
- source kind (`DocumentInformation`, `XmpMetadata`, or `EarlyPageText`);
- page number for early-page evidence; and
- deterministic evidence strength.

Duplicate evidence for the same ISBN is collapsed by normalized ISBN, then
ordered by source strength and page number. No lookup occurs and the evidence
does not change Calibre metadata. Candidate recognition and checksum validation
remain in the Infrastructure PDF adapter because they operate on bounded parser
metadata/page text. Application receives only provider-neutral validated
evidence and invalid-candidate counts. The existing EPUB detector remains
unchanged.

A date earns `PDF.METADATA.DATE_PRESENT` only when it is a parseable PDF
creation date or a schema-specific XMP date with creation/publication semantics.
A generic modification timestamp is displayed as bounded metadata but does not
receive the positive and is never relabeled as a publication date.

## Initial rule and finding catalog

All rule IDs are stable ordinal identifiers. Findings use bounded counts,
percentages, page numbers, status names, and version/profile IDs only.

### File, open, encryption, and structure

- `PDF.FILE.ZERO_LENGTH`
- `PDF.FILE.SIGNATURE_INVALID`
- `PDF.FILE.TRUNCATED`
- `PDF.FILE.MISSING`
- `PDF.FILE.INACCESSIBLE`
- `PDF.FILE.UNSAFE_PATH`
- `PDF.FILE.CHANGED_DURING_INSPECTION`
- `PDF.OPEN.SUCCESS`
- `PDF.OPEN.MALFORMED`
- `PDF.OPEN.UNSUPPORTED`
- `PDF.OPEN.TIMEOUT`
- `PDF.OPEN.RESOURCE_LIMIT`
- `PDF.ENCRYPTION.NONE`
- `PDF.ENCRYPTION.EMPTY_PASSWORD_ACCESSIBLE`
- `PDF.ENCRYPTION.PASSWORD_REQUIRED`
- `PDF.ENCRYPTION.UNSUPPORTED`
- `PDF.STRUCTURE.PAGE_TREE_VALID`
- `PDF.STRUCTURE.PAGE_TREE_INVALID`
- `PDF.STRUCTURE.OBJECT_GRAPH_CYCLE`
- `PDF.STRUCTURE.UNSUPPORTED_INTERNAL`
- `PDF.PAGE.COUNT_AVAILABLE`
- `PDF.PAGE.COUNT_ZERO`
- `PDF.PAGE.COUNT_LIMIT`

### Metadata and outline

- `PDF.METADATA.TITLE_PRESENT`
- `PDF.METADATA.TITLE_MISSING`
- `PDF.METADATA.AUTHOR_PRESENT`
- `PDF.METADATA.AUTHOR_MISSING`
- `PDF.METADATA.DATE_PRESENT`
- `PDF.METADATA.DATE_MISSING`
- `PDF.METADATA.OTHER_USEFUL`
- `PDF.METADATA.MALFORMED`
- `PDF.METADATA.LIMIT`
- `PDF.OUTLINE.PRESENT`
- `PDF.OUTLINE.ABSENT`
- `PDF.OUTLINE.MALFORMED`
- `PDF.OUTLINE.LIMIT`

### Page text, image, classification, and anomalies

- `PDF.PAGE.SAMPLE_ALL`
- `PDF.PAGE.SAMPLE_BOUNDED`
- `PDF.PAGE.SAMPLE_PARSE_COVERAGE`
- `PDF.PAGE.SAMPLE_INCOMPLETE`
- `PDF.PAGE.UNREADABLE`
- `PDF.TEXT.EXTRACTION_AVAILABLE`
- `PDF.TEXT.EXTRACTION_UNAVAILABLE`
- `PDF.TEXT.USEFUL_PERCENTAGE`
- `PDF.IMAGE.PRESENT`
- `PDF.IMAGE.ABSENT`
- `PDF.CONTENT.TEXT_HEAVY`
- `PDF.CONTENT.IMAGE_HEAVY`
- `PDF.CONTENT.BALANCED`
- `PDF.CONTENT.NOT_OBSERVABLE`
- `PDF.CONTENT.OBSERVABLE`
- `PDF.CLASSIFICATION.DIGITAL_TEXT`
- `PDF.CLASSIFICATION.SCANNED_WITHOUT_OCR`
- `PDF.CLASSIFICATION.SCANNED_WITH_OCR`
- `PDF.CLASSIFICATION.MIXED`
- `PDF.CLASSIFICATION.EMPTY_OR_NEAR_EMPTY`
- `PDF.CLASSIFICATION.ENCRYPTED`
- `PDF.CLASSIFICATION.UNREADABLE`
- `PDF.CLASSIFICATION.UNKNOWN`
- `PDF.PAGE.SUSPICIOUS_BLANK`
- `PDF.PAGE.REPEATED_CONTENT_EXACT`
- `PDF.PAGE.REPEATED_TEXT_EXACT`
- `PDF.PAGE.REPEATED_LIKELY`
- `PDF.PAGE.REPEAT_INSUFFICIENT`
- `PDF.PAGE.DIMENSIONS_UNUSUAL`
- `PDF.PAGE.DIMENSIONS_LIMIT`

### Identifiers, resources, and inert active content

- `PDF.IDENTIFIER.ISBN_VALID`
- `PDF.IDENTIFIER.INVALID_CHECKSUM_IGNORED`
- `PDF.RESOURCE.COUNTS_WITHIN_LIMITS`
- `PDF.RESOURCE.COUNT_UNUSUAL`
- `PDF.RESOURCE.LIMIT`
- `PDF.STREAM.EXPANSION_LIMIT`
- `PDF.FONT.MALFORMED`
- `PDF.FILTER.UNSUPPORTED`
- `PDF.FONT.NON_EMBEDDED`
- `PDF.ACTION.JAVASCRIPT_PRESENT`
- `PDF.ACTION.EXTERNAL_PRESENT`
- `PDF.RESOURCE.EXTERNAL_REFERENCE`
- `PDF.ATTACHMENT.PRESENT`
- `PDF.WORKER.CRASHED`
- `PDF.WORKER.PROTOCOL_INVALID`

Rules may consolidate many page occurrences into one deterministically ordered
finding with a count and bounded page examples. They must not create an
unbounded finding per page or object.

## PDF-specific scoring model

### Range and components

`pdf-quality/1.0.0` ranges from 0 through 100 for completed assessments:

- technical/file-and-content assessability: 0–85;
- embedded metadata/identifier completeness: 0–15.

Disqualified PDFs have no numeric score. A zero numeric score is therefore not
used as a synonym for unreadable, changed, or password-required.

### Positive catalog

| Finding | Component | Adjustment | Rationale |
| --- | --- | ---: | --- |
| `PDF.OPEN.SUCCESS` | technical | +50 | A successfully opened strict parse establishes the neutral base. |
| `PDF.STRUCTURE.PAGE_TREE_VALID` | technical | +10 | Required page structure and a useful nonzero count exist. |
| `PDF.PAGE.SAMPLE_PARSE_COVERAGE` | technical | up to +10 | All requested pages parsed earns +10; partial levels are +8 at >=99%, +5 at >=95%, otherwise 0. |
| `PDF.CONTENT.OBSERVABLE` | technical | up to +8 | At least 90% of analyzed pages have text, image, or graphics content; 50–89% earns +4. The medium is irrelevant. |
| `PDF.RESOURCE.COUNTS_WITHIN_LIMITS` | technical | +5 | Inspected resources were internally processable and below soft limits. |
| `PDF.OUTLINE.PRESENT` | technical | +2 | Useful navigation is a modest positive; absence is not a penalty. |
| `PDF.METADATA.TITLE_PRESENT` | embedded metadata | +4 | Useful embedded title. |
| `PDF.METADATA.AUTHOR_PRESENT` | embedded metadata | +4 | Useful embedded author. |
| `PDF.METADATA.DATE_PRESENT` | embedded metadata | +2 | Parseable meaningful creation/publication-like date. |
| `PDF.IDENTIFIER.ISBN_VALID` | embedded metadata | +3 | At least one checksum-valid ISBN from a bounded source. |
| `PDF.METADATA.OTHER_USEFUL` | embedded metadata | +2 | At least one coherent bounded subject/creator/producer/keywords field. |

The maximum technical total is 85 and metadata total is 15. Text extraction,
digital classification, and absence of encryption do not add points by
themselves. An image-only scan can receive the same technical positives as a
digital document when it opens, has a valid page tree, yields coherent page
content facts, and stays within limits.

### Negative catalog and caps

| Finding family | Adjustment | Cap | Notes |
| --- | ---: | ---: | --- |
| sampled page unreadable | -2 per page | -10 | Also reduces the sample-completeness positive; >20% failures disqualify. |
| `PDF.CONTENT.NOT_OBSERVABLE` | -8 once | -8 | At least 80% of analyzed pages have no safely observable text, image, or graphics content; this is fact-driven and separate from classification. |
| suspicious blank pages beyond allowance | -2 per page | -8 | Intentional edge blanks are allowed. |
| exact shared-content or normalized-text repeat | -4 per extra page | combined repeat cap -12 | Strongest evidence only; exact shared content and complete nontrivial text matches do not stack. |
| likely shared-image repeat | -2 per extra page | combined repeat cap -12 | Labeled likely; insufficient evidence is 0. |
| unusual page dimensions | -3 per page | -9 | Hard dimensions disqualify. |
| unusual image/object/operation resources | -5 per affected sampled page/group | -15 | Hard resource limits disqualify. |
| partially unsupported font/filter/structure | -5 | combined unsupported cap -15 | Only when inspection remains sufficiently complete. |
| malformed embedded metadata | -1 per field | -3 | Metadata-only component; missing metadata merely forgoes positives. |

Repeated penalties remain visible through one aggregate finding per rule family
with raw occurrence count, applied count, per-item adjustment, and cap. Caps are
applied before component floors. A cap/floor cannot create an unexplained score
change: raw and applied component contributions are both displayed.

### Critical weighting review

The model deliberately differs from EPUB V1:

- a PDF is not a ZIP publication, so no EPUB package/spine/navigation weights
  are copied;
- successful strict open carries a larger neutral base because unreadable
  documents are disqualified rather than numerically scored;
- text availability and digital generation are zero-weight evidence;
- image content earns the same observable-content positive as text;
- the intentional absence of an optional image decoder is not a quality defect
  when image placement/dimension facts remain available;
- outline weight is modest and its absence is neutral;
- metadata can contribute at most 15 points and cannot disguise structural
  defects in the technical component;
- page anomalies and resource risks are capped so a repeated family cannot
  dominate indefinitely; and
- changed, timed-out, password-required, or hard-limit results are not
  misleading low scores.

Before implementation the catalog will be encoded in one table-driven test that
proves the maxima, minima, caps, component separation, and that classification
never changes a score.

## Disqualifying conditions

The following produce a zero-adjustment `Disqualifying` finding and no score:

- missing, inaccessible, unsafe, reparse-aliased, or observation-less file;
- zero bytes or non-PDF signature;
- changed before, during, or immediately after inspection;
- password required or unsupported encryption;
- strict open failure, truncated xref/trailer, malformed required root/page
  tree, zero pages, or unsupported required structure;
- page count, file length, stack depth, object count, decoded stream, image,
  operation, page dimension, metadata, or other hard resource limit exceeded;
- parser wall-clock/CPU/heap/working-set limit;
- invalid/crashed worker or invalid/oversized protocol result;
- more than 20 percent sampled pages unreadable;
- too little successfully analyzed evidence to produce a comparable technical
  assessment after required page parsing; or
- stale/contradictory result identity or requested-page set.

The classification for these cases is still `Encrypted` or `Unreadable` where
known, with `Insufficient` confidence as appropriate. Ordinary low metadata,
image-heavy content, no outline, no text layer, scan-like classification, a
small number of blank pages, or a small number of recoverable page/font defects
does not disqualify.

## Deterministic ordering

- Targets/results: `CalibreBookId`, normalized relative path, ordinal format.
- Selected pages: numeric ascending after deterministic set construction.
- Metadata fields: frozen field order.
- Identifiers: normalized ISBN, source strength, source kind, page number.
- Repeat clusters: evidence strength, first page, then remaining pages.
- Problems: frozen problem-code order, page number, evidence key.
- Rules: frozen catalog order for evaluation.
- Findings in the aggregate: existing severity precedence
  (`Disqualifying`, `Error`, `Warning`, `Information`, `Positive`), then ordinal
  rule ID, score-component ID, evidence key, and explanation.
- WPF rows reuse the aggregate order and never resort based on completion time.

Culture, current time, thread scheduling, dictionary enumeration, parser object
addresses, process ID, absolute path, and random values do not affect facts,
classification, findings, score, or ordering.

## Cancellation, concurrency, and progress

- Default PDF concurrency is 2 and is independently bounded from hash/EPUB
  concurrency.
- One worker processes one PDF and pages sequentially.
- The parent never starts a worker after cancellation is observed.
- Cancellation terminates active read-only workers and awaits process exit
  before propagating `OperationCanceledException`.
- Completed results stay in canonical preallocated slots; no partial
  assessment is returned.
- Progress contains phase, completed files, total files, current book/format
  identity, inspection substage, sampled pages completed/total, and capped
  resource status.
- Per-page worker progress is coalesced so a large library does not flood the
  WPF dispatcher; completion progress remains monotonic and serialized.
- A parser timeout/resource breach is a file result, not global cancellation.
  An invalid parent/worker implementation contract remains an atomic scan
  failure.
- UI cancellation is enabled during the PDF phase and returns to the established
  prior snapshot rather than publishing a partly assessed snapshot.

Tests will measure cancellation after a deliberately hung worker and require
the worker process to be gone and the use case to cancel within one second on
the test machine.

## WPF changes

Add a “PDF assessments” tab using MVVM only. The list shows:

- record ID, title/author context, safe relative PDF path;
- completed/disqualified status and explicit “Not scored — disqualified” text;
- total score plus technical and embedded-metadata components;
- classification, confidence, and sampled/all-pages badge;
- page count;
- useful-text percentage and extraction status;
- content balance, image-bearing/image-dominant summary;
- encryption/password status;
- outline status; and
- analyzer and scoring-model versions.

The selected detail shows bounded metadata values, classification evidence and
limitations, page/sample/resource summaries, identifier evidence, active
content/attachment marker counts, anomaly counts, and findings. Findings use
the shared generic finding row and severity filter, show score component, rule
ID, explanation, adjustment, cap evidence, and bounded safe evidence.

Refactor the current EPUB-only finding row/filter into generic assessment
presentation types; preserve the EPUB tab's behavior and tests. Add
`PdfAssessmentRowViewModel` with lazy detail/finding materialization. Build PDF
rows off the dispatcher and bulk-publish once, following the existing EPUB
pattern.

The UI must:

- clearly state that “scanned with OCR” is inferred from an existing text layer
  and that no OCR was run;
- avoid labels/colors that imply digital is inherently superior;
- never display extracted page text, raw links, JavaScript, attachment names,
  absolute paths, exceptions, or parser logs;
- remain keyboard navigable with accessible names;
- keep selection/filter state coherent across scans; and
- add no “keep,” consolidation, cleanup, execute, or recovery action.

## Performance and memory considerations

- Parse each PDF once in one short-lived worker.
- Stream from a seekable read-only file; never use path/byte-array parser opens.
- Inspect all pages only through 200 pages; otherwise inspect a deterministic
  200-page sample.
- Dispose page-local parser objects before the next selected page where PdfPig
  permits and dispose the document at file completion.
- Retain counts, bands, hashes, limited page numbers, and limited identifiers;
  never retain full text, XMP XML, image bytes, operation operands, or token
  graphs.
- Never decode full-resolution images.
- Bound every recursive traversal, collection, protocol message, finding, and
  retained evidence set.
- Use process-per-file isolation so memory is reclaimed even after a parser
  fault.
- Keep PDF concurrency separate and low by default; validate options against a
  hard maximum of 8.
- Coalesce page progress and lazily create WPF details.
- Avoid reparsing for sampling by using the two-stage worker conversation.
- Record fingerprint, analyzer version, scoring version, sampling version, and
  limit profile so a later cache can key safely; do not implement that cache.
- Add a 2,000-target fake-orchestration test and a representative generated-PDF
  performance diagnostic with explicit time/allocation observations, not a
  brittle universal wall-clock assertion.

For thousands of PDFs, process startup is the principal expected new overhead.
The plan accepts that cost for V1 containment. A future worker pool requires a
separate security/performance decision and must prove per-document memory and
state isolation.

## Exact files expected to change

The implementation should update this list before coding if review changes the
shape. It must not silently broaden into unrelated files.

### Root, documentation, and decision records

- `CalibreLibraryCleaner.sln`
- `Directory.Packages.props`
- `docs/adr/0009-select-isolated-pdf-inspection-stack.md` (new)
- `docs/architecture.md`
- `docs/domain-model.md`
- `docs/functional-requirements.md`
- `docs/quality-scoring.md`
- `docs/safety-and-rollback.md`
- `docs/test-strategy.md`
- `docs/roadmap.md`
- `docs/plans/milestone-9-pdf-assessment.md`

### Domain

- `src/CalibreLibraryCleaner.Domain/Assessments/AssessmentFinding.cs`
- `src/CalibreLibraryCleaner.Domain/Assessments/AssessmentValues.cs`
- `src/CalibreLibraryCleaner.Domain/Assessments/FormatAssessment.cs`
- `src/CalibreLibraryCleaner.Domain/Assessments/EpubAssessment.cs` (new)
- `src/CalibreLibraryCleaner.Domain/Assessments/PdfAssessment.cs` (new)
- `src/CalibreLibraryCleaner.Domain/Assessments/PdfAssessmentValues.cs` (new)
- `src/CalibreLibraryCleaner.Domain/Assessments/PdfFeatureSummary.cs` (new)
- `src/CalibreLibraryCleaner.Domain/Libraries/LibrarySnapshot.cs`
- `src/CalibreLibraryCleaner.Domain/Recommendations/ConsolidationRecommendationPolicy.cs`
- `src/CalibreLibraryCleaner.Domain/Recommendations/RecommendationSelections.cs`

### Application

- `src/CalibreLibraryCleaner.Application/Abstractions/IPdfInspector.cs` (new)
- `src/CalibreLibraryCleaner.Application/Assessments/AssessEpubFormatsUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/EpubAssessmentEngine.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Pdf/PdfInspectionContracts.cs` (new)
- `src/CalibreLibraryCleaner.Application/Assessments/Pdf/PdfPageSamplingPolicy.cs` (new)
- `src/CalibreLibraryCleaner.Application/Assessments/Pdf/PdfClassificationPolicy.cs` (new)
- `src/CalibreLibraryCleaner.Application/Assessments/Pdf/PdfAssessmentRule.cs` (new)
- `src/CalibreLibraryCleaner.Application/Assessments/Pdf/PdfAssessmentRules.cs` (new)
- `src/CalibreLibraryCleaner.Application/Assessments/Pdf/PdfAssessmentEngine.cs` (new)
- `src/CalibreLibraryCleaner.Application/Assessments/Pdf/AssessPdfFormatsUseCase.cs` (new)
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryAnalysisOptions.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryErrorCode.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryScanPhase.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/ScanLibraryUseCase.cs`

### Infrastructure and worker

- `src/CalibreLibraryCleaner.Infrastructure/CalibreLibraryCleaner.Infrastructure.csproj`
- `src/CalibreLibraryCleaner.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Pdf/IsolatedPdfInspector.cs` (new)
- `src/CalibreLibraryCleaner.Infrastructure/Pdf/PdfWorkerProcessLauncher.cs` (new)
- `src/CalibreLibraryCleaner.Infrastructure/Pdf/PdfWorkerJobObject.cs` (new)
- `src/CalibreLibraryCleaner.Infrastructure/Pdf/PdfWorkerProtocol.cs` (new)
- `src/CalibreLibraryCleaner.Infrastructure/Pdf/PdfInspectionWorkerHost.cs` (new)
- `src/CalibreLibraryCleaner.Infrastructure/Pdf/PdfPigPdfInspectionEngine.cs` (new)
- `src/CalibreLibraryCleaner.Infrastructure/Pdf/PdfFileStateGuard.cs` (new)
- `src/CalibreLibraryCleaner.Infrastructure/Pdf/PdfStructuralPreflight.cs` (new)
- `src/CalibreLibraryCleaner.Infrastructure/Pdf/BoundedPdfFilterProvider.cs` (new)
- `src/CalibreLibraryCleaner.Infrastructure/Pdf/PdfTokenSafetyScanner.cs` (new)
- `src/CalibreLibraryCleaner.Infrastructure/Pdf/PdfPageFactCollector.cs` (new)
- `src/CalibreLibraryCleaner.Infrastructure/Pdf/PdfIsbnDetector.cs` (new)
- `src/CalibreLibraryCleaner.PdfWorker/CalibreLibraryCleaner.PdfWorker.csproj` (new)
- `src/CalibreLibraryCleaner.PdfWorker/Program.cs` (new)

### WPF

- `src/CalibreLibraryCleaner.Wpf/CalibreLibraryCleaner.Wpf.csproj`
- `src/CalibreLibraryCleaner.Wpf/App.xaml.cs`
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MainWindowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/AssessmentFindingFilterMode.cs` (new)
- `src/CalibreLibraryCleaner.Wpf/ViewModels/AssessmentFindingRowViewModel.cs` (new)
- `src/CalibreLibraryCleaner.Wpf/ViewModels/EpubAssessmentFindingRowViewModel.cs` (remove after generic replacement)
- `src/CalibreLibraryCleaner.Wpf/ViewModels/EpubFindingFilterMode.cs` (remove after generic replacement)
- `src/CalibreLibraryCleaner.Wpf/ViewModels/EpubAssessmentRowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/PdfAssessmentRowViewModel.cs` (new)

The WPF project will include an explicit build/copy target for the fixed worker
executable, `.deps.json`, `.runtimeconfig.json`, and required managed
dependencies. The worker path is resolved from the application installation
directory, never from the library or `PATH`.

### Tests

- `tests/CalibreLibraryCleaner.Domain.Tests/Assessments/AssessmentValueTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Assessments/PdfAssessmentTests.cs` (new)
- `tests/CalibreLibraryCleaner.Domain.Tests/Libraries/LibrarySnapshotTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Recommendations/ConsolidationRecommendationPolicyTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Assessments/EpubAssessmentTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Assessments/Pdf/PdfSamplingPolicyTests.cs` (new)
- `tests/CalibreLibraryCleaner.Application.Tests/Assessments/Pdf/PdfClassificationPolicyTests.cs` (new)
- `tests/CalibreLibraryCleaner.Application.Tests/Assessments/Pdf/PdfAssessmentRuleTests.cs` (new)
- `tests/CalibreLibraryCleaner.Application.Tests/Assessments/Pdf/AssessPdfFormatsUseCaseTests.cs` (new)
- `tests/CalibreLibraryCleaner.Application.Tests/Libraries/ScanLibraryUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Plans/CleanupPlanUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Recommendations/RecommendationUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/SyntheticCalibreLibrary.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/SyntheticPdfBuilder.cs` (new)
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/TestServices.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Pdf/PdfPigPdfInspectionEngineTests.cs` (new)
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Pdf/IsolatedPdfInspectorTests.cs` (new)
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Pdf/PdfWorkerProtocolTests.cs` (new)
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Pdf/PdfSecurityBoundaryTests.cs` (new)
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Recommendations/RecommendationJsonTests.cs`
- `tests/CalibreLibraryCleaner.Architecture.Tests/DependencyDirectionTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/MainWindowTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/EpubAssessmentRowViewModelTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/MainWindowViewModelTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/PdfAssessmentRowViewModelTests.cs` (new)

Small generated binary fixtures that cannot be constructed reliably in-test
may be added under
`tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/Pdf/` with a README
stating the generation command/tool version, license/provenance, expected
condition, SHA-256 digest, and why programmatic construction is insufficient.

## Package changes

Planned package change only:

```xml
<PackageVersion Include="PdfPig" Version="0.1.15" />
```

and:

```xml
<PackageReference Include="PdfPig" />
```

in Infrastructure only.

No package is added while this plan is being written. No renderer, image codec,
OCR, native PDF library, subprocess wrapper, JSON package, or test PDF package
is planned. BCL `System.Text.Json`, `Process`, cryptography, XML, and streams are
sufficient around PdfPig.

## Test plan

### Domain and shared-framework tests

- EPUB wrapper migration preserves every existing score/finding/fact/version.
- Shared result rejects invalid format, paths, observations, components,
  duplicate components, unassigned nonzero findings, inconsistent scores, and
  contradictory disqualification.
- PDF technical and metadata components are calculated and capped separately.
- PDF aggregate rejects text/raw-content-like evidence above bounds.
- Classification, confidence, sampling, metadata, identifier, and repeat
  summaries enforce bounds and deterministic order.
- `LibrarySnapshot` separately orders and deduplicates EPUB and PDF
  assessments.
- Existing recommendation digests/evidence/selections remain unchanged and PDF
  assessments cannot enter the policy.

### Valid document integration tests

Using generated synthetic files where practical:

- digitally generated text PDF;
- image-only scan-like PDF;
- scan-like PDF with an existing text layer;
- mixed text and image-only scanned pages;
- illustrated/image-heavy PDF that remains valid and is not penalized merely
  for images;
- PDF with and without outline;
- PDF with useful document information and bounded XMP;
- PDF without metadata;
- valid ISBN in document metadata;
- valid ISBN in bounded early-page text;
- invalid ISBN checksum ignored;
- accessible encrypted PDF using the empty password, if PdfPig supports the
  generated fixture;
- text, image, and vector content paths;
- no action/attachment execution or extraction while inert markers are
  reported.

### Invalid and unusual input tests

- zero-byte file;
- non-PDF renamed `.pdf`;
- truncated header/xref/trailer;
- malformed and cyclic object graph;
- password-required encrypted PDF;
- unsupported encryption;
- zero-page PDF;
- missing/inaccessible file;
- unsafe/rooted/traversing/reparse-aliased path;
- file changed before, during, and immediately after inspection;
- malformed metadata/XMP with DTD or excessive size;
- malformed font and missing font;
- non-embedded Standard 14 and non-Standard-14 font behavior without
  host-dependent high-confidence evidence;
- malformed/recursive outline;
- unsupported filter and structure;
- extreme page dimensions;
- excessive page/object/image/operation/outline counts;
- excessive image sample dimensions/pixel declaration;
- compressed-stream expansion limit;
- parser stack-depth limit;
- worker wall/CPU/heap/working-set timeout;
- worker crash, invalid JSON, duplicate/unknown properties, oversized message,
  wrong identity, and wrong page set.

Expected malformed or hostile inputs return structured findings and do not
crash, hang, allocate without bound, log content, or publish partial results.

### Classification tests

- deterministic `DigitalText`;
- deterministic `ScannedWithoutOcr`;
- deterministic `ScannedWithOcr`;
- deterministic `Mixed`;
- deterministic `EmptyOrNearEmpty`;
- `Encrypted` and `Unreadable` precedence;
- `Unknown` for insufficient/ambiguous facts;
- short image-based book is `Unknown/Low`, not defective;
- illustrated/comic/diagram-heavy facts do not imply a scan defect;
- scan classification has no score adjustment;
- OCR classification says inferred text layer and never claims OCR execution;
- exact threshold and just-below/just-above boundary cases;
- confidence margins and caps;
- large sampled document discloses sampling and cannot receive `High`;
- page input order and thread completion do not alter classification.

### Sampling tests

- all pages for 1, 2, 5, 199, and 200 pages;
- exactly 200 unique pages for 201, 1,000, and 100,000 pages;
- first five and last three always present;
- even distribution uses exact integer results;
- outline targets/adjacent pages included in traversal order within cap;
- malformed/duplicate/out-of-range outline destinations ignored safely;
- collision fill is deterministic;
- selection sorted and invariant across cultures/runs;
- over-limit page count disqualifies before page materialization.

### Rule, finding, and scoring tests

- every initial rule in the catalog has positive, negative/absent, boundary,
  and malformed-fact cases;
- every nonzero adjustment belongs to one finding/component;
- maximum catalog yields exactly technical 85, metadata 15, total 100;
- metadata cannot change the technical component;
- missing metadata forgoes positives but is not a technical defect;
- repeated page penalties retain raw count and cap at -12;
- blank, unreadable-page, dimension, resource, unsupported, and malformed-
  metadata caps apply exactly;
- exact text repeat, likely image repeat, and insufficient evidence remain
  distinct;
- headers/footers and sparse chapter templates do not cause duplicate findings;
- disqualifiers have zero adjustment and no numeric score;
- classification and confidence changes do not change score;
- text extraction absence alone does not change score;
- scan/image-heavy classification alone does not change score;
- deterministic score and finding order across shuffled facts/rules;
- analyzer, sampling, limit, and score versions recorded;
- no code compares incompatible scoring versions.

### Orchestration, progress, cancellation, and performance tests

- only PDF formats are selected and each is assessed independently;
- max active inspectors never exceeds requested concurrency;
- result order is canonical despite reverse completion order;
- 2,000 fake targets remain bounded and ordered;
- cancellation before start, during process launch, during header wait, during
  page parsing, and during final response propagates;
- hung worker is terminated and cancellation completes within one second;
- timeout/resource limit affects one file without leaking worker state;
- progress is serialized, monotonic, coalesced, and contains correct page/file
  totals;
- scan publishes no partial snapshot on cancellation/unexpected failure;
- PDF phase does not alter exact duplicate groups or EPUB recommendations;
- cleanup-plan generation/digests for an unchanged reviewed recommendation are
  identical and ignore PDF assessments;
- generated large PDF uses sampling, bounded retained facts, and no second
  parse;
- file streams are seekable and no production path calls `File.ReadAllBytes`;
- no full-file buffering, full-text retention, full-image decode, or unbounded
  WPF row detail materialization.

### Security and safety tests

- capture an entire synthetic library tree before/after every PDF scenario and
  assert byte-for-byte/path-for-path equality;
- capture `metadata.db` before/after and assert unchanged;
- assert PDF analysis never invokes Calibre tooling;
- source/behavior guards prohibit file create/write/delete/move/replace in PDF
  production folders;
- source/behavior guards prohibit `HttpClient`, `WebRequest`, sockets, URI
  loading, browser launch, shell execution, and external applications;
- action/JavaScript/URI markers are counted but never executed/opened;
- external file-stream/resource references are never resolved or opened;
- attachment markers are counted but no attachment API or bytes are accessed;
- image byte/PNG/render APIs are never called;
- parser logs are no-op and application logs contain no PDF text/metadata
  content, raw exceptions, or absolute paths;
- worker command line contains no library/book path and uses no shell;
- worker environment and protocol are bounded;
- cancellation/timeout kills only the read-only PDF worker and never a Calibre
  mutation process.
- execution and recovery full-state scanners never invoke the PDF analyzer or
  worker.

### Architecture tests

- Domain references no PdfPig, filesystem, process, SQLite, WPF, JSON, logging,
  or DI type;
- Application references no PdfPig, filesystem, process, JSON, SQLite, or WPF
  type;
- PdfPig package/type names occur only in Infrastructure and Infrastructure
  tests;
- `PdfWorker` references Infrastructure only as a composition root and contains
  no PdfPig source type;
- WPF view models reference no Infrastructure, filesystem, process, or parser
  type;
- the PDF worker process boundary and Calibre mutation process boundary are the
  only production `ProcessStartInfo` sites, each with separate strict tests;
- native interop in the PDF slice is confined to the reviewed Windows Job
  Object wrapper for process containment and includes no native PDF/parser/
  renderer/codec call;
- PdfPig path/byte-array opens, rendering, embedded-file extraction, image
  decoding, merge/writer production APIs, network, shell, and mutation APIs are
  prohibited;
- existing architecture and Milestone 7/8 safety tests remain enforced.

### WPF tests

- correct PDF row/status/score/components/classification/confidence/page/text/
  image/encryption/outline/versions;
- explicit disqualified/no-score presentation;
- sampling and OCR-inference limitations displayed;
- bounded metadata and identifier evidence displayed without content snippets;
- generic severity filter works for EPUB and PDF without changing source order;
- PDF details are lazy and bulk publication stays on the dispatcher;
- progress and cancellation stay responsive;
- selection resets/persists consistently with the current scan behavior;
- keyboard/accessibility names exist; and
- no recommendation, retention, cleanup, execution, recovery, or OCR action is
  added to the PDF tab.

## Fixture strategy

Use PdfPig's writer only in test fixture code for straightforward valid text
PDFs. Build image and mixed pages from tiny generated geometric/bitmap assets
whose bytes are created by the test. Construct malformed PDFs by bounded byte
transformations over generated fixtures when the malformed condition remains
deterministic.

Encryption, uncommon filters, cyclic graphs, and parser-hang regressions may
require small committed generated binaries. Each must be non-copyrighted,
minimal, explicitly generated/licensed, hashed, and documented. Tests copy
fixtures to temporary synthetic libraries and never inspect a real library.

The deliberately hung-worker test uses a test-only worker mode/protocol fixture,
not a potentially dangerous real PDF whose hang depends on parser version.
Separate parser regression fixtures exercise known malformed graphs and limits.

## Manual acceptance scenarios

Use a disposable synthetic library outside any real Calibre library:

1. Scan one digital text PDF and confirm completed score, `DigitalText`,
   metadata, useful-text percentage, findings, and versions.
2. Scan an image-only multi-page scan and confirm
   `ScannedWithoutOcr`, no OCR activity, no automatic scan penalty, and bounded
   image facts.
3. Scan a scan-like PDF with a text layer and confirm `ScannedWithOcr` with an
   inference warning, not an OCR-executed claim.
4. Scan mixed text/scan and illustrated/comic fixtures and confirm `Mixed` or
   conservative `Unknown`, with no “inferior” presentation.
5. Inspect outline/no-outline and metadata/no-metadata cases and verify only the
   documented components change.
6. Inspect valid/invalid ISBN cases and confirm sources/checksums and no online
   traffic or Calibre metadata change.
7. Inspect malformed, truncated, password-required, and zero-byte files and
   confirm structured no-score results without raw exceptions.
8. Inspect a >200-page generated file and confirm exact deterministic sampling,
   first/last/outline distribution, disclosure, and confidence cap.
9. Trigger changed-file detection in a disposable fixture and confirm partial
   facts are discarded.
10. Trigger a test worker timeout and cancellation and confirm prompt worker
    termination, responsive UI, and no partial snapshot.
11. Compare the entire synthetic library tree and `metadata.db` before/after.
12. Confirm EPUB scores/recommendations and Milestone 7/8 workspaces are
    unchanged.

No manual scenario uses a personal library or enables execution/recovery.

## Verification commands

Run after each coherent implementation step and again before completion:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```

Run focused suites while iterating:

```powershell
dotnet test tests/CalibreLibraryCleaner.Domain.Tests/CalibreLibraryCleaner.Domain.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Application.Tests/CalibreLibraryCleaner.Application.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Architecture.Tests/CalibreLibraryCleaner.Architecture.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Wpf.Tests/CalibreLibraryCleaner.Wpf.Tests.csproj
```

Also:

```powershell
dotnet list package --vulnerable --include-transitive
git diff --check
git status --short
rg -n "PdfPig|UglyToad" src tests
rg -n "File\\.ReadAllBytes|TryGetEmbeddedFiles|TryGetBytes|TryGetPng|HttpClient|WebRequest|UseShellExecute = true" src
```

Before claiming completion:

- review the complete diff;
- verify the package is pinned and referenced only by Infrastructure;
- verify the worker is copied and launched from the fixed installation path;
- run the dedicated PDF security/malformed/worker suites;
- inspect process lists after timeout/cancellation tests for orphan workers;
- compare synthetic library manifests before/after;
- manually exercise the acceptance scenarios;
- confirm every scoring catalog row and disqualifier has a test;
- confirm no PDF assessment reaches recommendation/cleanup/execution/recovery;
- record exact command results and test counts in this plan's final outcome.

Do not claim success unless the commands actually succeed.

## Incremental implementation steps

1. **Freeze the boundary and accept ADR 0009.** Review package 0.1.15,
   strict parser settings, worker isolation, protocol, sampling, limits,
   classification thresholds, scores, caps, and version strings. Update this
   plan before code if any decision changes.
2. **Generalize the shared assessment result.** Add score components, split
   shared result from format feature summaries, wrap EPUB explicitly, and prove
   byte-for-byte EPUB/recommendation compatibility before adding PDF types.
3. **Add PDF Domain values.** Implement bounded immutable assessment, feature,
   classification, confidence, sampling, metadata, identifier, page-summary,
   repeat, and score-breakdown values with focused invariant/ordering tests.
4. **Add Application contracts and pure policies.** Define `IPdfInspector`,
   requests/results/problems/progress/limits, sampling, classification, rule
   catalog, score components, and exhaustive pure tests.
5. **Add bounded PDF orchestration.** Implement target selection, canonical
   slots, problem translation, concurrency, cancellation, progress,
   association validation, and scale tests using fakes only.
6. **Add the worker protocol and process containment first.** Implement strict
   IPC, fixed executable resolution, minimal environment, heap/CPU/wall/
   working-set watchdogs, cancellation termination, crash translation, and
   adversarial protocol tests before parser integration.
7. **Add read-only PDF preflight and file-state guards.** Enforce canonical
   containment, reparse rejection, seekable read-only handles, signature/tail/
   size limits, and before/after observation with mutation-manifest tests.
8. **Integrate PdfPig behind Infrastructure.** Pin the package, use only the
   stream open, install strict options and bounded filters, translate
   encryption/parser faults, and prove third-party type confinement.
9. **Add bounded document/page fact collection.** Implement metadata/XMP,
   outline, action/attachment markers, page text/image/operation facts,
   resource counters, ISBN evidence, blank/repeat signals, and no-content-
   retention tests.
10. **Connect the one-parse sampling conversation.** Exchange header/page
    selection/final messages, verify deterministic sample identity, and exercise
    large/page-failure/cancellation cases.
11. **Connect the scan pipeline.** Add options/phase/DI, build PDF targets after
    hashing, store PDF assessments, preserve atomicity, and prove duplicate,
    recommendation, execution, and recovery behavior unchanged.
12. **Add WPF presentation.** Generalize finding presentation, add PDF rows/tab/
    details/filter/progress/cancel/accessibility, and preserve EPUB behavior.
13. **Complete security, malformed-input, and performance coverage.** Add
    generated/committed fixtures, worker containment, no-network/no-action/no-
    attachment/no-image-decode, scale, resource, and library-manifest tests.
14. **Update documentation and verify.** Update roadmap/domain/scoring/
    architecture/test docs, run all verification commands, perform manual
    synthetic acceptance, review the complete diff, and record actual outcomes.

Each step leaves the solution buildable and its relevant tests passing. If a
hard limit cannot constrain allocation before the PdfPig call returns, the
worker containment must cover it and a test must prove the resulting bounded
failure. If neither layer can enforce the limit, stop and revise the dependency
or design rather than weakening the requirement.

## Risks

- **Parser is not a hostile-input sandbox.** PdfPig has fixed recursion defects
  over time but exposes no global cancellation/memory quota. Mitigation:
  strict limits plus disposable worker containment; do not describe this as
  perfect sandboxing.
- **Filter allocation before quota observation.** A decoder may allocate before
  a wrapper sees output. Mitigation: encoded prechecks, heap/working-set/CPU/
  wall containment, hostile fixtures, and fail closed.
- **Strict parsing rejects recoverable real PDFs.** V1 prefers explainable
  rejection to undocumented repair. Characterize generated/open fixtures and
  version any future leniency.
- **Text extraction is not reading order or semantic correctness.** Glyphs may
  be hidden, scrambled, or encoded poorly. Use aggregate availability/density,
  disclose the limitation, and keep classification confidence conservative.
- **Host-font fallback can reduce cross-machine determinism.** PdfPig's font
  factory may consult system fonts for non-embedded fonts. Detect non-embedded
  fonts, never treat host-derived glyph geometry as strong classification
  evidence, characterize Standard 14 and missing-font behavior, and reconsider
  the dependency if filesystem font fallback cannot be bounded adequately.
- **OCR inference false positives.** Full-page images plus text may be captions,
  accessibility text, or born-digital composition. Cap confidence and say
  “scan-like with text layer.”
- **Illustrated-work false certainty.** Comics/facsimiles/diagrams can resemble
  scans. Require multi-page geometry consistency, use `Unknown` for short or
  ambiguous cases, and never penalize scan class.
- **Image coverage approximation.** Summed placement bounds can overlap. Cap at
  100 percent, disclose approximation, and avoid pixel decoding.
- **Repeated-page false positives.** Shared backgrounds/templates and repeated
  headers can recur. Require whole-page nontrivial text or stable full-page
  image identity/geometry, distinguish likely/insufficient, and cap penalties.
- **Process-per-file overhead.** Thousands of PDFs incur startups. Bound
  concurrency, measure, and accept security-first V1 behavior; do not add a
  pool without a later isolation decision.
- **Worker deployment complexity.** Missing/mismatched worker files could break
  production scans. Build/copy/version tests and a fixed sibling path fail
  clearly before scanning.
- **No OS-level network sandbox.** The worker code and PdfPig path contain no
  network behavior, and architecture/behavior tests prohibit it, but the
  process is not placed in a firewall sandbox. Do not overstate isolation.
- **File replacement race.** Observation checks and restrictive sharing cover
  ordinary changes but not every coordinated filesystem attack. Preserve the
  existing documented limitation.
- **Pre-1.0 dependency churn.** PdfPig may make breaking minor changes. Pin
  exactly, audit source/release/transitives, rerun the full adversarial suite,
  and bump analyzer version on upgrades.
- **Shared assessment refactor regression.** EPUB recommendations and artifacts
  depend on current types. Complete the compatibility step before PDF code and
  keep PDF out of recommendation inputs.
- **WPF scale.** Thousands of assessments and findings can pressure UI memory.
  Bulk-publish summaries and lazily materialize bounded details.
- **Policy precision.** Thresholds and weights are first-version policy choices,
  not objective truth. Display findings/components/versions and never turn the
  score into a retention decision.

## Unresolved questions

These are review points for implementation step 1. The defaults above are the
planned behavior; they are not permission to choose a materially different
behavior silently.

1. Confirm process-per-file isolation and the worker deployment/copy mechanism
   in ADR 0009. If the worker cannot be deployed reliably, reconsider the
   dependency rather than running unbounded parsing in the WPF process.
2. Characterize PdfPig 0.1.15 strict mode against a curated generated corpus.
   The plan keeps `UseLenientParsing = false`; any limited lenient path must
   expose repairs as facts and bump the analyzer.
3. Validate the 1 GiB file, 200-page sample, 100,000-page, 500,000-object,
   512 MiB decoded/heap, 60-second wall, and image/operation defaults on
   non-personal generated fixtures before freezing `pdf-limits/1.0.0`.
4. Confirm that an encrypted document accessible with the empty password is
   classified `Encrypted` but may receive a completed score; V1 still accepts
   no user password.
5. Confirm that a >20 percent sampled-page failure rate is the comparability
   disqualifier, while fewer failures remain scored with capped penalties and
   reduced confidence.
6. Confirm the +50 open baseline, 85/15 component split, and anomaly caps after
   table-driven boundary review. Do not copy EPUB weights to settle a dispute.
7. Verify that PdfPig exposes stable indirect image object identity without
   decoding. If it does not, omit likely image-repeat detection and emit
   insufficient evidence rather than inventing a fingerprint.
8. Decide whether XMP date fields can be described as publication-like only
   when their schema identifies that semantic. Generic creation/modification
   dates must not be mislabeled publication dates.
9. Confirm the worker's managed-heap and working-set enforcement behavior on
   the supported Windows/.NET 10 deployment. Tests must distinguish a clean
   resource-limit result from an unexplained crash.
10. Characterize PdfPig's system-font lookup for non-embedded non-Standard-14
    fonts. The V1 result must disclose the condition and exclude host-dependent
    glyph geometry from high-confidence evidence; if that cannot be done
    deterministically, those pages become incomplete or the dependency choice
    must be revised.

## Mandatory boundary review

This plan has been reviewed against `docs/roadmap.md` and the user-authorized
Milestone 9 objective:

- It implements PDF analysis only. It does not implement Milestone 10 content
  fingerprints, cross-document similarity, or comparison.
- It introduces no OCR engine and performs no OCR.
- Image-based and scan-like PDFs receive no automatic negative adjustment.
- PdfPig and every PDF parser/token/image/filter type remain inside
  Infrastructure.
- Every PDF open uses a canonical seekable read-only handle; no Calibre-managed
  file is written, moved, renamed, replaced, extracted, or deleted.
- `metadata.db` remains read-only and no Calibre mutation command is invoked.
- Milestone 5 recommendation semantics and Milestone 7/8 execution/recovery
  behavior remain unchanged.
- No PDF action, JavaScript, external link, attachment, or network resource is
  executed/opened/extracted.
- Pathological inputs are bounded by preflight, parser options, recursive
  limits, stream/object/page/image/operation quotas, bounded evidence, and a
  terminated per-file worker with heap/working-set/CPU/wall watchdogs.
- Classification, score, technical/metadata components, findings, and versions
  are distinct and explainable.
- Incompatible PDF scoring versions cannot be compared silently.

## Progress

### Post-review remediation (2026-07-31)

- [x] Keep PDF assessment out of Milestone 7 execution and Milestone 8 recovery scans.
- [x] Fail closed when Windows worker containment cannot be established.
- [x] Enforce the frozen V1 resource ceilings and parent-side total protocol budget.
- [x] Correct structural object/action evidence, page sampling, text evidence,
  repeat detection, ISBN/XMP validation, and content scoring thresholds.
- [x] Add non-embedded-font and hidden-text reliability disclosure and confidence limits.
- [x] Correct disqualified-score and classification-limitation presentation.
- [x] Restore the documented project dependency boundary.
- [x] Add the missing hostile-input, containment, cancellation, and regression tests.
- [x] Run the complete verification workflow and update the handoff.

- [x] Read root and all nested `AGENTS.md` files and `PLANS.md`.
- [x] Read all specifically requested product, functional, architecture,
  domain, quality, safety, test, roadmap, workflow, and accepted ADR documents.
- [x] Read the completed Milestone 0 through Milestone 8 plans.
- [x] Read the Milestone 7 and Milestone 8 handoffs.
- [x] Inspect the current Domain, Application, Infrastructure, WPF, project,
  package, test, recommendation, execution, and recovery implementation.
- [x] Inspect the implemented/remediated Milestone 4 EPUB assessment and its
  Domain, Application, Infrastructure, architecture, and WPF tests.
- [x] Evaluate PdfPig 0.1.15 using official package, release, source, and API
  information.
- [x] Draft the Milestone 9-only execution plan.
- [x] Review the plan against the roadmap and explicitly exclude Milestone 10
  content fingerprints.
- [x] Confirm no OCR engine, scan penalty, third-party type leakage, write-mode
  PDF access, execution/recovery behavior change, or unbounded pathological-file
  path is planned.
- [x] Accept ADR 0009 and freeze V1 dependency/options/limits/thresholds/weights.
- [x] Implement the shared assessment refactor with EPUB compatibility proof.
- [x] Implement and verify the PDF assessment vertical slice.
- [ ] Complete manual synthetic-library acceptance.

Implementation started on 2026-07-31. The pre-change baseline completed with
408 passed, zero failed, and two skipped caller-gated real-Calibre tests. ADR
0009 accepts the planned PdfPig 0.1.15, strict stream-only parser options,
process-per-file containment, V1 limits, deterministic sampling,
classification, and 85/15 findings-derived scoring model without deviation.

The shared result now has explicit score components and typed EPUB/PDF wrappers.
Existing EPUB and recommendation behavior remains green. The PDF Domain and
Application policies, isolated worker protocol, strict PdfPig integration,
quota filter provider, scan/snapshot association, WPF presentation, generated
fixtures, security boundaries, and read-only synthetic-library coverage are
implemented. Final verification completed with 503 passed, zero failed, two
skipped caller-gated real-Calibre tests, a zero-warning build, clean format
verification, and no `git diff --check` whitespace errors. Automated
synthetic-library acceptance is complete; the genuinely manual WPF walkthrough
remains unchecked and is documented in the handoff.

## Final outcome

Milestone 9 implementation completed on 2026-07-31. PdfPig 0.1.15 is pinned and
referenced only by Infrastructure behind a disposable bounded worker. The
delivered slice provides deterministic all-page/bounded sampling, independent
classification and confidence, bounded feature and identifier evidence,
findings-derived 85/15 PDF-only scoring, disqualifiers, verified identity,
version stamps, progress/cancellation, WPF presentation, and synthetic-fixture
coverage. It does not perform OCR, rendering, attachment extraction, network
access, PDF comparison, cross-file similarity, recommendation ranking, Calibre
mutation, cleanup execution, or recovery changes.

The frozen versions are `pdf-inspector/1.0.0`, `pdf-quality/1.0.0`,
`pdf-classification/1.0.0`, `pdf-sampling/1.0.0`, `pdf-limits/1.0.0`, and
`pdf-worker-protocol/1.0`. Every non-zero score contribution is a deterministic
finding; incompatible scoring versions are not compared silently. Hard limits
fail closed, image/text/scan properties are neutral by themselves, and
classification never changes the quality score.

One dependency limitation remains explicit: PdfPig exposes the hard parser
stack option but not observed stack depth, so depth 64 is enforced while the
planned advisory at 48 cannot be emitted. PdfPig also does not expose stable
indirect image/content identities through the supported high-level API;
therefore production repeat detection uses the approved complete bounded
normalized-text rule and otherwise reports insufficient evidence rather than
decoding content or relying on unstable internals. See
`docs/handoffs/milestone-9-handoff.md` for the complete file inventory, limits,
rules, commands, results, limitations, risks, and exact next step.
