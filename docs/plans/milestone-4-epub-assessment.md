# Milestone 4: EPUB Assessment

This execution plan is a living document. Keep `Progress`, `Risks`, `Unresolved questions`, and `Final outcome` current while implementing the milestone. Follow `PLANS.md`, the repository `AGENTS.md` files, and accepted ADRs. The plan covers Milestone 4 only; it does not authorize cleanup or later duplicate-recommendation work.

## Objective

Add a safe, explainable vertical slice that finds EPUB formats already present in a completed Calibre library scan, inspects each EPUB independently as untrusted ZIP-based input, assigns a deterministic EPUB-format quality score when the file is assessable, and displays the assessment and every contributing finding in WPF.

For every discovered EPUB format, the resulting snapshot must preserve its Calibre book identifier, format, expected relative path, and observed file identity, and report:

- whether the file could be opened and its package parsed;
- a numeric score from 0 through 100, or an explicit disqualified/not-scored state;
- every positive, informational, warning, error, and disqualifying finding;
- embedded EPUB metadata and a bounded structural summary;
- the analyzer and scoring-model versions used.

The milestone is successful when one read-only scan can assess valid and invalid EPUBs without modifying the database or library files, continue past per-file failures, remain cancellable and responsive, and present reproducible results whose score can be recomputed solely from their findings.

## Scope

### In scope

- Select formats whose normalized Calibre format is exactly `EPUB` from the existing `LibrarySnapshot` scan pipeline.
- Assess every discovered EPUB independently, including entries whose file is missing, inaccessible, path-invalid, zero length, changed, malformed, encrypted, unsupported, or unsafe.
- Reuse the established canonical Calibre path validation and file-state safeguards before opening any EPUB.
- Add a bounded, asynchronous EPUB assessment phase after file discovery/hashing and before the immutable final snapshot is published.
- Preflight the ZIP container with explicit archive limits before handing it to an EPUB library.
- Read the package, embedded EPUB metadata, cover, navigation, spine, local references, and bounded readable chapter text without extraction or network access.
- Add an extensible, deterministic rule engine with one independently testable rule for each finding type in the initial catalog.
- Add versioned analyzer and scoring models, capped repeated penalties, and disqualification semantics.
- Store assessments in `LibrarySnapshot` and expose them through WPF with progress, cancellation, selection, severity filtering, evidence, and a clear not-scored state.
- Add synthetic unit, integration, architecture, safety, cancellation, concurrency, and bounded-performance tests.
- Document the accepted EPUB-reading stack in an ADR during implementation, before taking the new dependency.

### Out of scope

- PDF inspection or scoring.
- Comparing content between books, EPUB full-text fingerprints, fuzzy content similarity, or semantic similarity.
- Comparing or reconciling embedded EPUB metadata with Calibre record metadata. This milestone reports and scores only EPUB-format quality.
- General Calibre metadata correction, network metadata lookup, or AI integration.
- Recommending which duplicate, book, or format to keep; ranking duplicate candidates by score; automatic decisions of any kind.
- Milestone 5 review queues or consolidation recommendations.
- Cleanup plans, immutable action plans, backups, rollback execution, Calibre CLI writes, merging, moving, renaming, deletion, or any other library mutation.
- Persistent assessment caching. The design must leave a safe cache key available, but this milestone always computes current results.
- Extracting an EPUB into the Calibre library or any other directory. Tests may create isolated synthetic fixture files, but production inspection is stream-only.
- Correcting malformed EPUBs or rewriting EPUB packages.

## Relevant requirements and decisions

This plan is governed by:

- `docs/product-vision.md`: analysis must be local-first, safe, transparent, explainable, and non-destructive.
- `docs/functional-requirements.md`: inspect EPUB readability, package metadata, identifiers, language, cover, navigation, spine, resources, readable text, chapter anomalies, and encryption; keep format quality separate from record metadata quality.
- `docs/architecture.md` and ADR 0002: preserve `Domain <- Application <- Infrastructure` and `Application <- Wpf`; third-party, ZIP, XML, HTML, image, filesystem, and WPF types cannot leak into Domain.
- `docs/domain-model.md`: snapshots and analysis results are immutable and findings carry stable identifiers and evidence.
- `docs/quality-scoring.md`: scores must be reproducible, versioned, composed of explainable rule findings, and expose both positive and negative factors.
- `docs/safety-and-rollback.md`, ADR 0001, and ADR 0003: `metadata.db` remains read-only, analysis never mutates Calibre-managed files, and this milestone introduces no mutation path.
- `docs/duplicate-detection.md`: EPUB content fingerprints belong to a later milestone and must not be introduced here.
- `docs/test-strategy.md`: synthetic fixtures, safety invariants, cancellation, malformed input, architecture boundaries, and deterministic algorithms require automated coverage.
- `docs/roadmap.md`: Milestone 4 is limited to EPUB parser integration, cover/TOC/spine/resource/text checks, reproducible scores, and explainable findings.

## Existing implementation inspected

The implementation and tests were inspected before proposing this design. The working tree contains the completed Milestone 3 work and unrelated Rider workspace state; implementation must preserve those changes.

### Current production flow

- `ScanLibraryUseCase` validates the library, reads schema version 27 through `ICalibreMetadataReader`, resolves every format through `ILibraryPathResolver`, hashes present files through `IFormatFileHasher`, and builds exact-binary and exact-title/author groups before publishing one `LibrarySnapshot`.
- `SqliteCalibreMetadataReader` opens `metadata.db` read-only/query-only, while `LibraryPathResolver` canonicalizes paths and rejects traversal and reparse-point escapes.
- `StreamingSha256FormatFileHasher` streams with `FileShare.Read`, bounds parallel hashing, checks file state before and after hashing, and translates missing, inaccessible, and changed files into application results.
- `BookFormat` records a format, stored file name, expected relative path, file status, and optional SHA-256 fingerprint. There is no EPUB assessment model.
- `FindingSeverity` currently contains `Information`, `Warning`, and `Error`. `LibraryFinding` is scan-level; it is not suitable as the scored per-format finding without extension or a dedicated type.
- `LibrarySnapshot` currently stores books, scan findings, exact-binary groups, and exact-metadata groups. Assessment collections do not yet exist.
- `LibraryAnalysisOptions` currently controls only maximum hash concurrency. `LibraryScanPhase` has no EPUB phase.
- Infrastructure has no EPUB, archive, HTML, XML, or image inspection dependency.
- `MainWindowViewModel` manages one cancellation source, coalesces progress, constructs presentation rows off the UI thread, and publishes collections in bulk. `MainWindow` currently exposes Library, Exact file duplicates, and Metadata candidates tabs.

### Current test support

- Domain, Application, Infrastructure, Architecture, and WPF test projects already use xUnit, FakeItEasy, and FluentAssertions.
- `SyntheticCalibreLibrary` creates a schema-27 temporary Calibre-shaped library, but its current `.epub` bytes are not valid EPUB packages.
- `LibraryStateCapture` records names, attributes, lengths, timestamps, and streaming hashes and is the correct basis for database and library non-mutation assertions.
- Application tests already exercise result ordering, cancellation, hashing failures, exact-binary groups, and exact metadata groups with fakes.
- Architecture tests enforce project references, prohibit parser/filesystem/WPF packages and types from core projects, and confine hashing implementation to Infrastructure.
- WPF tests cover progress, cancellation, presentation construction, selection, filtering, and scan retry behavior.

### Completed plans and ADRs reviewed

- Completed Milestones 0, 1, 2, and 3 plans establish the foundation, read-only ingestion, exact binary duplicate detection, and exact normalized title/author candidate discovery.
- Accepted ADRs 0001 through 0003 establish direct read-only database access, clean dependency direction, and supported Calibre tooling for future mutations.
- No accepted ADR currently selects an EPUB-reading stack or defines archive inspection limits.

## Proposed design

The scan remains a single atomic application operation. After format paths have been resolved and present files have been hashed, the application derives EPUB targets from the prepared formats, obtains a stable file observation from the hashing phase, and assesses them with bounded concurrency. Per-file failures become `FormatAssessment` values; only cancellation, an application contract violation, or an orchestration defect fails the overall scan. The final snapshot is published only after all requested EPUB assessments have completed.

No parser object, open stream, HTML DOM, decoded image, ZIP entry, or mutable rule context crosses the Infrastructure boundary.

### Proposed domain model

Add immutable Domain types under an `Assessments` feature folder:

- `FormatAssessment`: Calibre book ID, format, expected relative path, observed fingerprint when available, status, nullable score, analyzer version, scoring-model version, `EpubFeatureSummary`, and ordered findings.
- `AssessmentFinding`: stable rule ID, severity, signed applied score adjustment, explanation, and bounded evidence. Evidence is presentation-safe data such as a normalized archive-relative path, count, dimension, or threshold; it must not contain book content.
- `AssessmentStatus`: `Completed` or `Disqualified`. A completed assessment has a `QualityScore`; a disqualified assessment has no score.
- `FindingSeverity`: extend the existing shared enum with `Positive` and `Disqualifying`, retaining `Information`, `Warning`, and `Error`. `LibraryFinding` may use only its existing severities, while `AssessmentFinding` can use the full set.
- `QualityScore`: a validated integer value object in the inclusive range 0 through 100.
- `AnalyzerVersion`: a nonblank validated value object, initially `epub-inspector/1.0.0`.
- `ScoringModelVersion`: a nonblank validated value object, initially `epub-quality/1.0.0`.
- `EpubFeatureSummary`: bounded facts required by the UI and rules, including EPUB/package version where available; package-opened and package-parsed flags; trimmed embedded title, authors, languages, dates, and strong identifiers; cover presence and optional dimensions; navigation presence; manifest, spine, chapter, local-resource, and broken-reference counts; bounded readable-character count; encryption state; and whether configured limits truncated optional analysis. Rule-local normalization must not replace the bounded source values shown to the user.

Domain invariants:

- `FormatAssessment.Format` must be `EPUB` using ordinal case-insensitive validation and is stored canonically as `EPUB`.
- The stable association is `(CalibreBookId, Format, ExpectedRelativePath, ObservedFingerprint)`. The path must remain relative and presentation-safe; Domain does not resolve it.
- `Completed` requires a score and no disqualifying finding. `Disqualified` requires at least one disqualifying finding and requires a null score.
- A score cannot exist without a non-empty findings collection. The collection includes the baseline finding and all scored or zero-adjustment findings.
- The score must equal the scoring algorithm applied to the findings for the recorded scoring-model version.
- Evidence collections and string lengths are bounded before Domain construction.
- Assessments sort by Calibre book ID, then format and expected relative path using ordinal comparison.
- Findings sort deterministically by display severity (`Disqualifying`, `Error`, `Warning`, `Information`, `Positive`), then rule ID, normalized evidence key, and explanation using ordinal comparison. Rules use a separate deterministic evidence order before applying caps so UI order cannot affect scoring.

`LibrarySnapshot` gains `IReadOnlyList<FormatAssessment> EpubAssessments`, validates unique assessment associations, and stores the canonical assessment order. It does not embed or derive duplicate-retention recommendations.

### Application use cases and interfaces

Add these provider-neutral Application concepts:

- `IEpubInspector.InspectAsync(EpubInspectionRequest request, IProgress<EpubInspectionProgress>? progress, CancellationToken cancellationToken)`: inspect exactly one already validated EPUB and return an immutable `EpubInspectionResult`. It never throws for an expected file or content problem.
- `EpubInspectionRequest`: trusted root, canonical full path, expected relative path, Calibre book ID, expected post-hash file observation and fingerprint, and immutable security limits. These are Application primitives and records, not filesystem or VersOne types.
- `EpubInspectionResult`: safe facts and provider-neutral problem codes produced by Infrastructure. It contains no weighted score and no third-party exceptions or objects.
- `IFormatAssessmentRule`: stable rule ID and an evaluation method that maps an immutable inspection result to unweighted finding candidates. Each initial finding type has a separate rule class and can be unit tested without opening an EPUB.
- `EpubAssessmentEngine`: runs a frozen ordered rule catalog, normalizes and caps contributions, applies the versioned scoring model, and constructs a valid `FormatAssessment`.
- `AssessEpubFormatsUseCase`: selects all EPUB formats, creates structured disqualified assessments directly for path/file statuses that cannot be inspected, runs inspectable files with bounded concurrency, preserves output slots, reports progress, and returns the ordered assessment list.

`ScanLibraryUseCase` will invoke `AssessEpubFormatsUseCase` after resolution and hashing have established file identity. It will then construct duplicate groups as it does today and publish the snapshot. EPUB assessment does not influence either duplicate group.

Expected orchestration rules:

1. Derive one target for every discovered EPUB format. Invalid path, missing, inaccessible, zero-byte, or hashing-time changed entries remain targets and receive explicit disqualifying findings without invoking the parser.
2. For a present hashed EPUB, require the post-hash observation and fingerprint in the request.
3. Allocate result slots in canonical target order and fill each slot once. Concurrency completion order never determines result order.
4. Translate expected inspector problem results into findings and continue with other EPUBs. Infrastructure must catch expected I/O/parser failures, log only safe identifiers, and return stable problem codes. Cancellation is never translated and propagates promptly; any other exception violates the inspector contract and fails the atomic scan rather than publishing a partial result.
5. Treat duplicate/missing result slots, wrong book/path association, or invalid inspector contracts as application errors; do not publish a partial snapshot.
6. Report phase, completed count, total count, current safe relative path, and optional per-book substage. Progress is monotonic even though files finish out of order.

Extend `FormatHashResult` with a provider-neutral post-read `FormatFileObservation` containing length, last-write UTC, creation UTC where reliable, and attributes. The hasher supplies this observation after its existing verification. EPUB inspection compares it immediately before opening, after opening, and after parsing. This avoids a second full SHA-256 pass while detecting ordinary replacements between hashing and inspection. The assessment records the already computed fingerprint. The residual limitation that a hostile same-length/same-timestamp rewrite can evade metadata checks must be documented; the process is local read-only analysis rather than an adversarial filesystem transaction.

### Infrastructure design

Infrastructure owns all file, archive, parser, HTML, XML, and image details:

- `VersOneEpubInspector` implements `IEpubInspector`, coordinates safe preflight, opens the exact canonical file read-only, creates a lazy VersOne book reference over the guarded stream, gathers bounded facts once, and disposes every handle deterministically.
- `EpubArchivePreflight` uses `System.IO.Compression.ZipArchive` in read mode before VersOne. It validates entry names, declared sizes, compression ratios, entry counts, duplicate canonical names, required EPUB container entries, and aggregate limits. It does not extract.
- `EpubArchivePathResolver` normalizes forward-slash archive paths, resolves package-relative references, rejects traversal/absolute/drive/UNC/NUL/backslash ambiguity, and compares paths ordinally according to ZIP entry naming.
- `EpubFileStateGuard` repeats trusted-root, parent-chain, reparse-point, expected file-state, and regular-file checks immediately before open and after inspection.
- `CancellationCheckingStream` checks the token between bounded reads so long entry reads can stop even though VersOne APIs do not accept a `CancellationToken`.
- `SafeXmlReaderFactory` uses `DtdProcessing.Prohibit`, `XmlResolver = null`, zero entity expansion, and explicit document-character limits for XML inspected outside the library. Malformed XML is translated into a problem code.
- `HtmlChapterInspector` uses Html Agility Pack on one size-capped XHTML/HTML resource at a time, removes non-readable elements, counts normalized Unicode letters/digits and local references, and discards the DOM and text before moving to the next chapter. It does not retain full-book text.
- `SafeImageHeaderInspector` reads only a bounded header for supported PNG, JPEG, GIF, WebP, and SVG cover resources. It returns optional dimensions without fully decoding or rendering an image. Unsupported or malformed image headers become findings, not failures.

The inspector must use the exact stream-based, lazy API rather than `EpubReader.ReadBook`, because the latter reads all content into memory. It explicitly disables content downloading and supplies a downloader that throws if invoked. Missing-content handling is configured deliberately and characterized by integration tests; permissive “ignore all errors” presets are forbidden because they would hide evidence needed by rules. The wrapper catches and maps VersOne, ZIP, XML, HTML, image-header, I/O, unauthorized-access, and unsupported-feature exceptions to stable Application problem codes. Exception text and stack traces are not stored as finding evidence.

One inspection builds one immutable provider-neutral fact set. Rules never reopen or reparse the file. Content resources are streamed in canonical order; only counts, bounded evidence samples, normalized reference keys needed for repeated-reference checks, and capped metadata values are retained. No resource bytes or readable chapter text are logged.

### EPUB dependency decision

Use `VersOne.Epub` 3.3.6 as the default EPUB package and pin the exact stable version through central package management. The project is actively maintained, supports EPUB 2 and EPUB 3, targets .NET Standard and is compatible with .NET 10, exposes lazy stream-based access through `EpubBookRef`, and as of 3.3.6 exposes compressed and uncompressed entry lengths useful for defense-in-depth. Sources: [VersOne.Epub NuGet package](https://www.nuget.org/packages/VersOne.Epub/), [project and support matrix](https://github.com/vers-one/EpubReader), [3.3.6 release](https://github.com/vers-one/EpubReader/releases/tag/v3.3.6), and [stream/lazy reader API](https://os.vers.one/EpubReader/reference/VersOne.Epub.EpubReader.html).

The principal alternative is a BCL-only ZIP/XML implementation. Reject it for V1 because it would make this project responsible for the complete EPUB 2/3 package, navigation, metadata, and compatibility model. Also reject an eager “read the entire book” integration regardless of package because it conflicts with bounded memory and responsive cancellation. VersOne is selected for its current maintenance, supported format range, lazy reference model, and inspectable source/API; the wrapper remains free to replace it later because Application owns `IEpubInspector`.

Add `HtmlAgilityPack` 1.12.4, also pinned centrally, for tolerant, bounded HTML/XHTML text and reference inspection. VersOne's own documentation demonstrates Html Agility Pack for plain-text extraction, and the package supports the target through its current .NET targets. Source: [Html Agility Pack NuGet package](https://www.nuget.org/packages/htmlagilitypack/).

Do not add a general image-decoding package in this milestone. A narrow header reader is sufficient for safely obtainable cover dimensions and avoids decoding attacker-controlled pixel buffers. If implementation evidence shows the header reader cannot meet the stated formats safely, stop and amend the plan/ADR before taking another dependency.

Limitations to document and test:

- VersOne is a parser, not a complete security boundary. The application must preflight the ZIP and independently constrain paths, sizes, compression, XML, HTML, resources, image headers, network behavior, cancellation, and file state.
- Its top-level convenience read loads all content and must not be used; lazy references retain file handles and must be disposed.
- Reader methods do not consistently expose cancellation tokens, requiring the guarded stream and checks between every resource/rule stage. The wrapper explicitly sets the library's [content downloading option](https://github.com/vers-one/EpubReader/blob/master/Source/VersOne.Epub/Options/ContentDownloaderOptions.cs) to false as defense in depth.
- Relaxed options can tolerate common publisher defects but could conceal missing resources. Use an explicit option set and surface tolerated defects as findings.
- EPUB encryption support is limited. The application detects `META-INF/encryption.xml`, permits only recognized font obfuscation that does not prevent structural/text analysis, and disqualifies unsupported protection that blocks required analysis.
- Supported EPUB structures in the library do not remove the need to handle undocumented parser exceptions as untrusted-input outcomes.

Create proposed ADR `docs/adr/0004-select-versone-epub-inspection-stack.md` during implementation, before adding packages. It should record the selection, alternatives considered, lazy-only usage, Html Agility Pack role, security wrapper, cancellation limitation, upgrade policy, and consequences. Repository workflow does not require writing that ADR during this planning change, so it is not created now.

Upgrade policy:

- Pin exact package versions in `Directory.Packages.props`; do not use ranges or floating versions.
- Review release notes, license, transitive dependencies, and known vulnerabilities before initial merge and every upgrade.
- Run the complete malformed/security fixture suite on upgrades.
- Bump `AnalyzerVersion` when a dependency upgrade or parsing/limit behavior can change extracted facts or findings. Bump `ScoringModelVersion` only when weights, caps, thresholds, formula, or disqualifying semantics change.

## Rule engine design

Rules consume only `EpubInspectionResult`; they do not do I/O, mutate shared state, or know about VersOne. Each rule returns immutable finding candidates containing rule ID, severity, nominal adjustment, explanation template, and normalized evidence. A frozen V1 catalog evaluates rules in ordinal rule-ID order.

The engine performs these deterministic stages:

1. Validate the inspection association and analyzer version.
2. Evaluate every applicable rule once. A rule that cannot apply because an earlier disqualifying condition removed the necessary facts emits a zero-point informational or disqualifying finding as defined by its contract; it is never silently omitted if it explains the absent score.
3. Normalize candidate evidence and order candidates by rule ID and evidence key.
4. Apply per-rule penalty caps in that order. Findings beyond a cap remain visible with an applied adjustment of zero and an explanation that the cap was reached.
5. Order findings for display using the Domain ordering rule.
6. If any disqualifying finding exists, create a `Disqualified` assessment with no numeric score.
7. Otherwise sum every applied adjustment, clamp to 0 through 100, and create a `Completed` assessment.

Unexpected rule exceptions represent programming defects and fail the operation rather than mislabeling a book. Rule implementations must be pure and extensively unit tested.

### Initial rule catalog and proposed weights

`docs/quality-scoring.md` requires positive and negative explainable factors but intentionally does not freeze weights. The proposed model uses a visible +50 baseline finding and up to +50 in positive evidence. This makes an entirely healthy EPUB score 100 while missing or bad evidence reduces the result through explicit findings. It also avoids an unexplained implicit starting score.

The initial thresholds and weights are proposals to be accepted in the ADR/documentation before implementation. They prioritize readability and structure over optional bibliographic enrichment, critically separating format quality from Calibre record metadata quality.

| Rule ID | Independently tested finding | Positive result | Negative result | Cap / notes |
| --- | --- | ---: | ---: | --- |
| `EPUB.SCORE.BASELINE` | Visible scoring baseline | +50 | n/a | Exactly once; proves the score is findings-derived. |
| `EPUB.OPEN` | File and ZIP can be opened | +4 | disqualifying | Missing, inaccessible, zero-byte, invalid ZIP, unsupported ZIP encryption/compression, or changed file has no valid score. |
| `EPUB.ARCHIVE_SAFETY` | Archive passes safety preflight | 0 information | disqualifying | Unsafe paths, duplicate canonical entries, or any configured resource/archive limit breach invalidates scoring. |
| `EPUB.PACKAGE` | Container and package metadata can be parsed | +4 | disqualifying | Missing/malformed container or package document prevents reliable structural assessment. |
| `EPUB.METADATA.TITLE` | Nonblank embedded title exists | +3 | -4 warning | Embedded EPUB metadata only; never the Calibre title. |
| `EPUB.METADATA.AUTHOR` | At least one nonblank creator/author exists | +3 | -4 warning | Multiple authors do not add more points. |
| `EPUB.METADATA.LANGUAGE` | At least one usable language tag exists | +2 | -2 warning | Preserve raw bounded values as evidence; do not network-validate. |
| `EPUB.METADATA.DATE` | A parseable publication date is present | +1 | 0 information, or -1 for a malformed declared value | Absence is common and optional; malformed asserted data is weaker evidence. |
| `EPUB.METADATA.STRONG_IDENTIFIER` | A syntactically plausible ISBN-10/ISBN-13 exists | +1 | 0 information | No lookup and no record comparison. Other identifiers are summarized but do not earn V1 points. |
| `EPUB.COVER.PRESENT` | A local declared/inferred cover resource exists | +4 | -6 warning | Broken declared cover is reported as error and takes the same -6 once. |
| `EPUB.COVER.DIMENSIONS` | Safely read dimensions are useful | +2 | -3 warning if too small; -2 if a supported header is malformed; 0 information if safely unknown | Useful means short side at least 600 px and long side at least 800 px. No full image decode. |
| `EPUB.NAVIGATION` | EPUB 3 navigation document or EPUB 2 NCX/TOC is usable | +4 | -6 warning | Empty/broken navigation counts as absent and supplies evidence. |
| `EPUB.SPINE.NON_EMPTY` | Spine exists and contains an item | +5 | -20 error | Not disqualifying if the package parsed; the low score accurately reflects unreadability. |
| `EPUB.SPINE.RESOURCE_EXISTS` | Every spine reference resolves locally | +4 once when none are missing | -5 per missing item | Repeated negative cap -20; all missing paths remain visible with zero adjustment after cap. |
| `EPUB.RESOURCE.INTERNAL_EXISTS` | All inspected internal image/resource references resolve locally | +4 once when none are broken | -2 per broken reference | Repeated negative cap -10. Remote references are separate zero-point information and are never fetched. |
| `EPUB.TEXT.SUBSTANTIAL` | Substantial normalized readable text exists | +5 | -8 for 500–4,999 characters; -15 below 500 | Count Unicode letters/digits after excluding markup, scripts, styles, nav, and the cover. Thresholds are model-versioned. |
| `EPUB.CHAPTER.EMPTY` | No content chapter is empty or near-empty | +2 once | -2 per chapter below 100 normalized letters/digits | Repeated negative cap -10; cover/nav pages are excluded. |
| `EPUB.STRUCTURE.REPEATED_REFERENCE` | No obvious repeated chapter/reference structure | +2 once | -4 per duplicate spine `idref`, resolved spine href, or repeated navigation target | Repeated negative cap -12. Reusing the same resolved content resource is the V1 “obviously repeated content” signal; V1 does not compare or fingerprint chapter text. |
| `EPUB.ENCRYPTION` | Encryption state does not prevent assessment | 0 information | disqualifying when DRM/encryption blocks package, navigation, spine, or readable-content inspection | Recognized font obfuscation alone is informational if analysis remains possible. |

Maximum positive adjustments are 50, so baseline plus all positive checks equals 100. The larger V1 penalties focus on an absent/nonfunctional spine, missing content, and insufficient text. Cover and navigation remain important but cannot outweigh core readability. Optional date and identifier absence do not materially penalize older or hand-produced EPUBs. This is a deliberate adjustment from treating every metadata gap as equally important.

Rules can emit multiple findings to preserve evidence, but only the explicitly stated contribution earns or loses points. For example, `EPUB.SPINE.RESOURCE_EXISTS` emits a single +4 finding only when all resources exist; otherwise it emits one negative finding per missing canonical path and no positive finding. This prevents contradictory contributions.

### Scoring and disqualification algorithm

For scoring model `epub-quality/1.0.0`:

```text
orderedCandidates = order(rule findings by RuleId, EvidenceKey)
appliedFindings = apply each rule's positive/negative cap to orderedCandidates

if appliedFindings contains FindingSeverity.Disqualifying:
    status = Disqualified
    score = null
else:
    status = Completed
    score = clamp(sum(appliedFindings.ScoreAdjustment), 0, 100)
```

Every non-disqualified assessment includes the +50 baseline finding, so the numeric result is derived entirely from stored findings. Recomputing the sum and clamp must reproduce the stored `QualityScore`. A domain test rejects a score that does not match its findings/model. A disqualifying finding overrides every contribution and invalidates rather than forcing a misleading zero. WPF renders `Not scored — disqualified`, never `0`, in that case.

Cap application uses normalized evidence ordering, not discovery or task completion order. Negative contributions are capped independently per rule. All suppressed repeated findings remain explainable and display an applied `0` adjustment plus the cap explanation. Positive rules are naturally capped at one success finding unless the table states otherwise.

The initial versions are:

- Analyzer: `epub-inspector/1.0.1` after the 2026-07-18 security hardening pass (`1.0.0` was the initial implementation).
- Scoring model: `epub-quality/1.0.0`.

Changes to archive parsing, content extraction, reference resolution, image support, security limits, or dependency behavior that can change facts require a new analyzer version. Changes to baseline, weights, caps, thresholds, formula, rule applicability, or disqualification behavior require a new scoring-model version. Persisted or later cached assessments must key by file fingerprint plus both versions. The UI and future recommendation code must not compare or rank scores whose scoring-model versions differ without explicit migration/reassessment. Caching and cross-score ranking are not implemented in Milestone 4.

## Security limits and disqualification algorithm

Centralize immutable defaults in `EpubInspectionLimits` and record the analyzer version that owns them. Proposed V1 limits are deliberately conservative and must be exercised at boundary values:

| Limit | Proposed V1 value | Outcome when exceeded |
| --- | ---: | --- |
| EPUB file length | 1 GiB | Disqualify before parser invocation. |
| ZIP entry count | 10,000 | Disqualify. |
| Sum of declared uncompressed entry lengths | 512 MiB | Disqualify. |
| One ordinary entry | 64 MiB | Disqualify, except no higher override is permitted. |
| Container/package/navigation/encryption XML document | 4 MiB each | Disqualify if required for analysis; otherwise emit an error and omit that optional feature. |
| One XHTML/HTML chapter | 8 MiB | Disqualify because readable-content analysis is no longer trustworthy within bounds. |
| One CSS resource | 2 MiB | Stop optional CSS reference inspection and emit an error; do not allocate beyond limit. |
| One cover resource read | 32 MiB declared, 64 KiB header actually read | Disqualify on declared limit breach; malformed/unsupported bounded header becomes a scored finding. |
| Spine item count | 10,000 | Disqualify. |
| Resolved local reference count | 50,000 | Disqualify. |
| Retained evidence examples per rule | 100, with an additional omitted count | Continue safely; scoring counts all occurrences subject to caps. |
| Aggregate readable characters counted | 20 million | Stop content processing, record truncation, and disqualify because the full rule result is not comparable. |
| HTML nodes / nested levels | 25,000 nodes / 256 levels | Disqualify before or during DOM construction. |
| Per-entry compression ratio | greater than 200:1 for entries over 1 MiB | Disqualify as suspicious. |
| Aggregate compression ratio | greater than 100:1 after 10 MiB uncompressed | Disqualify as suspicious. |

Zero-compressed-length entries with substantial declared output, negative/overflowing lengths, inconsistent central-directory data, or arithmetic overflow are disqualifying. All sums use checked 64-bit arithmetic. Limits apply before allocation and again during actual reads because declared ZIP metadata is untrusted.

Disqualify, without a numeric score, when any of these prevents safe and comparable inspection:

- missing, inaccessible, non-regular, zero-byte, path-invalid, changed, or unreadable file;
- invalid/unsupported ZIP container or encrypted ZIP entry required for analysis;
- unsafe entry path, canonical duplicate, archive bomb signal, count/size/ratio breach, or observed bytes beyond a declared/configured bound;
- missing or malformed `META-INF/container.xml` or package document;
- DTD/external-entity use or XML processing that cannot be completed with external resolution disabled;
- DRM or encryption preventing package, navigation, spine, or readable content inspection;
- file-state mismatch against the hash observation before/during/after inspection;
- mandatory analysis truncated by a security limit;
- an unexpected parser failure that cannot be safely classified into a more specific condition.

Do not disqualify merely for missing title/author/language/date/identifier/cover/navigation, an empty spine, missing resources, too little text, near-empty chapters, or repeated references when the bounded inspection completed. Those are quality findings whose penalties make the result useful and comparable.

## Security safeguards

- Treat the EPUB as untrusted from the first path comparison. Revalidate canonical root containment, expected Calibre path, regular-file status, and every parent reparse-point condition immediately before opening.
- Open read-only with sharing that does not enable application writes. Never create a write-capable stream for a Calibre-managed file.
- Never call `ZipArchiveEntry.ExtractToFile`, create an extraction directory, or map archive paths onto filesystem paths. Archive paths are identifiers only.
- Reject absolute paths, drive-qualified paths, UNC forms, NULs, `..` traversal after normalization, ambiguous backslashes, and canonical-name collisions even though extraction is forbidden.
- Preflight counts, sizes, ratios, and checked sums before parser invocation. Wrap actual entry streams with byte limits and cancellation checks so lying metadata cannot evade bounds.
- Configure XML with DTD processing prohibited, no resolver, no entity expansion, and maximum character limits. Never use an API that resolves external schemas/entities.
- Parse HTML locally and tolerate malformed markup only within size/node/count limits. Treat `http`, `https`, protocol-relative, `file`, `data`, and other non-archive references as evidence; never dereference them.
- Set VersOne content downloading to false and provide a fail-closed downloader. Add a test that an EPUB containing external URLs makes no outbound request.
- Read supported image headers only. Validate dimensions and multiplication with checked arithmetic; do not allocate `width * height` buffers and do not render images.
- Stream one content resource at a time, bound buffers, avoid whole-book content objects, dispose DOMs/streams/book references promptly, and retain no book prose in results or logs.
- Sanitize exception translation. Evidence may name a bounded archive-relative resource but never stores OS paths outside the trusted root, content snippets, credentials, stack traces, or raw exception messages.
- Check the file observation before open, after opening, between resource stages where practical, and after completion. A mismatch discards partial facts and returns a single changed-file disqualification plus safe context.
- Propagate `CancellationToken` through orchestration and every custom async read. Check before ZIP preflight, parser open, each manifest/spine/resource, each rule, and before publishing results.

## Cancellation, concurrency, progress, and performance

### Bounded concurrency

- Add `MaxEpubAssessmentConcurrency` to `LibraryAnalysisOptions`, defaulting to 2 and validated as positive. Hash concurrency remains independently configured.
- `AssessEpubFormatsUseCase` uses `Parallel.ForEachAsync` or an equivalent fixed worker/channel design with that exact bound. It must not start one task per EPUB.
- Each worker owns its file stream and parser state. Rules are stateless immutable objects; no mutable global cache is allowed.
- Concurrency tests use gates/counters rather than timing sleeps and prove both that the configured maximum is never exceeded and that more than one file can run when the limit permits.

### Cancellation

- Cancellation before selection, while waiting for a worker, during ZIP preflight, during a large chapter read, between resources, during rule evaluation, and before UI publication must throw `OperationCanceledException` and leave no published partial snapshot.
- Guarded streams limit individual read sizes and check the token, targeting sub-second cancellation latency between bounded reads under normal local storage. No exact wall-clock guarantee is embedded in Domain.
- WPF reuses the current scan cancellation command and shows the EPUB phase. A cancelled run returns the view model to a retryable state and does not retain results from the cancelled scan.

### Progress

- Add `AssessingEpubFormats` and `ScoringEpubFormats` phases, or a single `AssessingEpubFormats` phase with substages if existing progress contracts favor one phase. The implementation plan should prefer two phases only if rules are materially observable; otherwise one phase prevents noisy UI transitions.
- Report total EPUB count once, monotonically increasing completed-file count, current safe relative path, and bounded substages such as `Preflight`, `Package`, `Content`, and `Rules`.
- Aggregate per-resource progress inside Infrastructure at a throttled/coalesced boundary. Do not dispatch a WPF update for every ZIP entry or read buffer.
- EPUBs that are immediately disqualified from existing file status still increment completed progress.

### Thousands of EPUBs

- Select and order targets once; use a fixed result array rather than repeatedly sorting growing collections.
- Reuse the post-hash file observation and fingerprint; do not hash or parse the same EPUB twice during one scan.
- Use lazy VersOne access, stream one chapter/resource at a time, and retain only bounded summaries/evidence.
- Avoid populating WPF detail rows for every finding up front if measurement shows this dominates memory. Construct the assessment table in bulk and materialize selected-assessment detail/filter views on demand while preserving immutable source data.
- Add an Application scale test with thousands of fake inspector results to detect accidental unbounded task creation, quadratic ordering, or result loss. Add a smaller real-parser fixture batch for integration coverage. Record diagnostics rather than a flaky absolute time threshold.
- Design the future cache identity as `(library identity, CalibreBookId, format, expected relative path, SHA-256, AnalyzerVersion, ScoringModelVersion)`, but do not implement storage or cache invalidation in this milestone.

## WPF changes

Add an `EPUB assessments` tab without adding a recommendation or retention decision:

- Summary grid columns: Calibre book ID, Calibre display title (context only), expected EPUB path, assessment status, score or `Not scored`, opened, package parsed, EPUB/package version, analyzer version, and scoring-model version.
- Selecting one assessment shows a bounded feature summary: embedded EPUB title/authors/languages/dates/identifiers, cover state/dimensions, navigation state, spine/chapter/resource counts, readable character band/count, encryption state, and any analysis truncation.
- A findings grid shows severity, rule ID, signed applied adjustment, explanation, and evidence. It supports `All`, `Positive`, `Information`, `Warning`, `Error`, and `Disqualifying` filters while retaining deterministic source ordering.
- A prominent disqualification banner explains why no score exists. It must not display a disqualified assessment as zero.
- Existing Library format rows may add assessment status and EPUB score columns by joining on the stable association. Non-EPUB rows display an em dash.
- Progress text includes EPUB completed/total and current substage; the existing Cancel command stays enabled through assessment.
- Build presentation rows off the UI thread, publish collections in bulk, preserve selection/filtering behavior, and provide automation names/keyboard access for the new tab and controls.
- Never label a score as “best”, “keep”, “delete”, “winner”, or otherwise imply duplicate consolidation advice.

## Files expected to change

Exact names may be adjusted only if the implementation discovers an existing naming convention that makes a listed name misleading; record any adjustment in this plan before editing.

### Documentation and package configuration

- `Directory.Packages.props` — pin `VersOne.Epub` 3.3.6 and `HtmlAgilityPack` 1.12.4.
- `docs/adr/0004-select-versone-epub-inspection-stack.md` — new accepted/proposed ADR written during implementation before package integration.
- `docs/domain-model.md` — document format assessments, findings, feature summaries, score/status invariants, association, and versioning.
- `docs/quality-scoring.md` — record V1 formula, weights, caps, thresholds, disqualifiers, and compatibility rules.
- `docs/architecture.md` — add only the new Application/Infrastructure inspection boundary and third-party isolation if the ADR alone is insufficient.
- `docs/plans/milestone-4-epub-assessment.md` — maintain progress, decisions, verification, and outcome.

### Domain

- `src/CalibreLibraryCleaner.Domain/Findings/FindingSeverity.cs` — add positive and disqualifying severities.
- `src/CalibreLibraryCleaner.Domain/Assessments/AssessmentStatus.cs` — new status enum.
- `src/CalibreLibraryCleaner.Domain/Assessments/QualityScore.cs` — new validated score value.
- `src/CalibreLibraryCleaner.Domain/Assessments/AnalyzerVersion.cs` — new validated version value.
- `src/CalibreLibraryCleaner.Domain/Assessments/ScoringModelVersion.cs` — new validated version value.
- `src/CalibreLibraryCleaner.Domain/Assessments/AssessmentFinding.cs` — new scored finding record.
- `src/CalibreLibraryCleaner.Domain/Assessments/EpubFeatureSummary.cs` — new bounded format facts.
- `src/CalibreLibraryCleaner.Domain/Assessments/FormatAssessment.cs` — new invariant-owning assessment aggregate.
- `src/CalibreLibraryCleaner.Domain/Libraries/LibrarySnapshot.cs` — add ordered EPUB assessments and uniqueness validation.

### Application

- `src/CalibreLibraryCleaner.Application/Abstractions/IEpubInspector.cs` — new Infrastructure port.
- `src/CalibreLibraryCleaner.Application/Assessments/EpubInspectionContracts.cs` — request, limits, progress, result facts, and stable problem codes.
- `src/CalibreLibraryCleaner.Application/Assessments/IFormatAssessmentRule.cs` — pure rule contract and candidate model.
- `src/CalibreLibraryCleaner.Application/Assessments/EpubAssessmentEngine.cs` — deterministic rule execution and assessment construction.
- `src/CalibreLibraryCleaner.Application/Assessments/EpubScoringModelV1.cs` — version, weights, caps, formula, and validation.
- `src/CalibreLibraryCleaner.Application/Assessments/EpubAssessmentRuleCatalog.cs` — frozen ordered V1 catalog.
- `src/CalibreLibraryCleaner.Application/Assessments/AssessEpubFormatsUseCase.cs` — selection, concurrency, progress, cancellation, association, and per-file failure handling.
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubOpenRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubArchiveSafetyRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubPackageRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubTitleRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubAuthorRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubLanguageRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubPublicationDateRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubStrongIdentifierRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubCoverRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubCoverDimensionsRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubNavigationRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubSpineRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubSpineResourceRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubInternalResourceRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubReadableTextRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubEmptyChapterRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubRepeatedReferenceRule.cs`
- `src/CalibreLibraryCleaner.Application/Assessments/Rules/EpubEncryptionRule.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/FormatFileObservation.cs` — provider-neutral verified file state shared by hashing and inspection.
- `src/CalibreLibraryCleaner.Application/Libraries/FormatHashContracts.cs` — carry the verified post-hash file observation.
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryAnalysisOptions.cs` — add EPUB concurrency and inspection limits.
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryScanPhase.cs` — add EPUB progress phase(s).
- `src/CalibreLibraryCleaner.Application/Libraries/ScanLibraryUseCase.cs` — invoke assessment and publish it in the snapshot.

### Infrastructure

- `src/CalibreLibraryCleaner.Infrastructure/CalibreLibraryCleaner.Infrastructure.csproj` — reference both centrally pinned parser packages.
- `src/CalibreLibraryCleaner.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` — register `IEpubInspector` only; rule/use-case composition remains outside Infrastructure.
- `src/CalibreLibraryCleaner.Infrastructure/Hashing/StreamingSha256FormatFileHasher.cs` — return verified file observations without weakening current change checks.
- `src/CalibreLibraryCleaner.Infrastructure/Epub/VersOneEpubInspector.cs` — third-party adapter and fact collection.
- `src/CalibreLibraryCleaner.Infrastructure/Epub/EpubArchivePreflight.cs` — ZIP validation and limits.
- `src/CalibreLibraryCleaner.Infrastructure/Epub/EpubArchivePathResolver.cs` — safe archive-relative normalization/resolution.
- `src/CalibreLibraryCleaner.Infrastructure/Epub/EpubFileStateGuard.cs` — trusted root and file observation checks.
- `src/CalibreLibraryCleaner.Infrastructure/Epub/CancellationCheckingStream.cs` — bounded cancellable reads.
- `src/CalibreLibraryCleaner.Infrastructure/Epub/SafeXmlReaderFactory.cs` — fail-closed XML settings.
- `src/CalibreLibraryCleaner.Infrastructure/Epub/HtmlChapterInspector.cs` — bounded text/reference analysis.
- `src/CalibreLibraryCleaner.Infrastructure/Epub/SafeImageHeaderInspector.cs` — bounded cover dimension detection.

### WPF

- `src/CalibreLibraryCleaner.Wpf/App.xaml.cs` — compose the V1 rule catalog, assessment engine/use case, and scan use case.
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml` — add the assessment summary, detail, filtering, progress, and accessible labels.
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MainWindowViewModel.cs` — expose assessment state, selection, filtering, background presentation, and cancellation.
- `src/CalibreLibraryCleaner.Wpf/ViewModels/BookRowViewModel.cs` — provide format-assessment context if the existing library tab displays aggregate state.
- `src/CalibreLibraryCleaner.Wpf/ViewModels/FormatRowViewModel.cs` — expose EPUB status/score for the matching format.
- `src/CalibreLibraryCleaner.Wpf/ViewModels/EpubAssessmentRowViewModel.cs` — new summary row.
- `src/CalibreLibraryCleaner.Wpf/ViewModels/EpubAssessmentFindingRowViewModel.cs` — new finding/evidence row.
- `src/CalibreLibraryCleaner.Wpf/ViewModels/EpubFeatureSummaryViewModel.cs` — new selected EPUB detail.
- `src/CalibreLibraryCleaner.Wpf/ViewModels/EpubFindingFilterMode.cs` — new severity filter values.

### Tests and fixtures

- `tests/CalibreLibraryCleaner.Domain.Tests/Assessments/QualityScoreTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Assessments/AssessmentFindingTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Assessments/FormatAssessmentTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Libraries/LibrarySnapshotTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Assessments/EpubAssessmentEngineTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Assessments/EpubScoringModelV1Tests.cs`
- one test file per rule under `tests/CalibreLibraryCleaner.Application.Tests/Assessments/Rules/`, mirroring the rule filenames above.
- `tests/CalibreLibraryCleaner.Application.Tests/Assessments/AssessEpubFormatsUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Libraries/ScanLibraryUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/SyntheticEpubBuilder.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Epub/EpubArchivePreflightTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Epub/EpubArchivePathResolverTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Epub/VersOneEpubInspectorTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Epub/EpubInspectionSecurityTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Hashing/StreamingSha256FormatFileHasherTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/SyntheticCalibreLibrary.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/TestServices.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Safety/ReadOnlyLibraryScanSafetyTests.cs`
- `tests/CalibreLibraryCleaner.Architecture.Tests/DependencyDirectionTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/MainWindowViewModelTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/EpubAssessmentRowViewModelTests.cs`

## Package changes

- Add centrally pinned `VersOne.Epub` version `3.3.6` and reference it only from Infrastructure.
- Add centrally pinned `HtmlAgilityPack` version `1.12.4` and reference it only from Infrastructure.
- Add no package to Domain or Application.
- Add no image, networking, database-write, or AI package.
- Confirm restore resolves no unexpected networking-at-runtime component and architecture tests reject parser assemblies/references outside Infrastructure.

## Possible ADRs

- Required during implementation: ADR 0004 selecting VersOne.Epub plus Html Agility Pack behind `IEpubInspector`, with the security wrapper and exact-version upgrade policy.
- No separate scoring ADR is initially necessary because `docs/quality-scoring.md`, this plan, and versioned code can own V1. If review reveals that disqualification/null-score semantics affect later architecture broadly, amend ADR 0004 or propose a focused scoring ADR before coding that decision.
- Do not write a cleanup, recommendation, cache, or content-fingerprint ADR in this milestone.

## Tests

### Synthetic fixture strategy

Implement `SyntheticEpubBuilder` with `ZipArchive` and small generated strings/bytes. It must write `mimetype` first and uncompressed where the scenario needs a standards-conforming EPUB, generate `META-INF/container.xml`, OPF, XHTML, navigation/NCX, and tiny header-valid images, and allow targeted omission/corruption. Fixtures contain no copyrighted book text and live only in per-test temporary directories. Malformed ZIP/XML and archive-safety cases may patch generated bytes or use hand-authored minimal byte arrays when `ZipArchive` cannot produce the invalid structure.

Do not use the user's library or download example books. Test evidence strings should be synthetic and recognizable.

### Domain and rule unit tests

- Valid and invalid `QualityScore`, analyzer version, and scoring-model version values.
- `FormatAssessment` association, status/score/disqualifier invariants, required findings, immutable collections, and deterministic ordering.
- Snapshot uniqueness and ordering across book IDs, paths, and formats.
- Every catalog rule in isolation for positive, absent, malformed, boundary, and non-applicable inputs.
- Date and ISBN syntax boundaries without network lookup.
- Cover dimension threshold boundaries and safely unknown dimensions.
- Text threshold boundaries at 499/500/4,999/5,000 and chapter threshold at 99/100 normalized characters.
- One, many, and over-cap missing spine resources, broken internal resources, empty chapters, and repeated references.
- Findings beyond a repeated penalty cap remain present with zero applied adjustment.
- Baseline plus all positives equals 100; clamp behavior is deterministic; stored score can be recomputed solely from findings.
- Any disqualifying finding yields null score regardless of positive findings.
- Same facts in different input/enumeration orders produce byte-for-byte equivalent ordered findings and score.
- Analyzer and scoring-model versions are recorded and incompatible scoring versions are not silently treated as comparable.
- Embedded metadata rules never receive or inspect Calibre record title/author values.

### Application tests

- Select only EPUB formats, case-insensitively, while preserving the correct Calibre record, format, expected path, fingerprint, and observation.
- Return one assessment for every EPUB, including missing, inaccessible, invalid-path, zero-byte, and changed statuses.
- Never call `IEpubInspector` for an already uninspectable status.
- Continue after a malformed/encrypted/unreadable per-file result and preserve canonical assessment order independent of completion order.
- Reject mismatched inspector book/path associations or duplicate/missing result slots.
- Progress is monotonic and counts immediate disqualifications; zero-EPUB libraries report a valid empty assessment collection.
- Cancellation before start, while queued, during inspector work, during rule execution, and before snapshot publication propagates.
- Bounded concurrency never exceeds the configured maximum and actually allows parallel work when configured above one.
- Thousands of fake EPUBs do not create unbounded simultaneous calls, lose results, or exhibit nondeterministic ordering.
- `ScanLibraryUseCase` publishes assessments without changing exact-binary or metadata candidate grouping and does not publish a partial snapshot after cancellation/failure.

### Infrastructure integration and robustness tests

Programmatically cover at least:

- valid EPUB with complete metadata, cover, useful dimensions, navigation, non-empty spine, valid references, and substantial text;
- valid EPUB without a cover;
- valid EPUB without navigation;
- valid EPUB without useful title/author/language/date/identifier metadata;
- EPUB with a missing spine resource;
- EPUB with a broken image and another broken internal resource reference;
- EPUB with empty and near-empty chapters;
- repeated spine `idref`, repeated resolved href, and repeated navigation target without content fingerprinting;
- malformed ZIP, truncated ZIP, and zero-byte file;
- malformed/missing container and malformed/missing package document;
- encrypted ZIP entry, unsupported DRM/encryption, and recognized font obfuscation that does not block analysis;
- missing file, inaccessible file where the platform permits a deterministic fixture, and non-regular/reparse escape;
- file replaced or modified before inspection and while inspection is gated;
- cancellation during archive preflight, parser/resource reading, and a large synthetic chapter;
- oversized entry, too many entries, total-size overflow, per-entry and aggregate compression-ratio boundary, path traversal, absolute path, backslash ambiguity, NUL if constructible, and canonical duplicate names;
- malformed XML with DTD/external entity and entity-expansion attempt;
- malformed HTML, excessive HTML/resource/reference counts, and external `http`, `https`, protocol-relative, `file`, and `data` references;
- valid, too-small, unsupported, malformed, and absurd-dimension cover headers without full image allocation;
- output contains bounded metadata/evidence and no book text or raw exception details;
- exact VersOne reader option behavior, lazy access, disposal, and translation of expected parser exceptions;
- an external URL plus fail-closed downloader/local request counter proves no network access occurs;
- no production extraction API is invoked and no files appear outside the fixture directory.

Platform-sensitive inaccessible-file tests should use existing repository conventions and explicitly skip only when the OS cannot establish the condition; missing and injected unauthorized outcomes still require deterministic coverage.

### Safety tests

- Capture the entire synthetic library and `metadata.db` before and after a successful multi-EPUB scan and assert names, paths, attributes, lengths, timestamps, and SHA-256 values are identical.
- Repeat the full manifest assertion after malformed, unsafe, encrypted, missing, inaccessible, changed, cancelled, and faulted inspections.
- Verify the SQLite connection remains read-only/query-only and no new database command path is introduced.
- Verify no temporary/extracted files are created inside the Calibre root. If a dependency internally requests temporary storage, fail the test and configure/replace that behavior rather than permitting it.
- Verify an EPUB reference cannot escape to another library file and no external URL is fetched.
- Verify logs/findings do not contain synthetic chapter prose or absolute paths outside the safe presentation contract.

### Architecture tests

- Domain references no Application, Infrastructure, WPF, VersOne, Html Agility Pack, filesystem, ZIP, XML, HTML, image, logging, or DI types/assemblies.
- Application references only Domain and contains no VersOne, Html Agility Pack, filesystem, ZIP, WPF, or concrete parser types.
- Infrastructure is the only production project that references `VersOne.Epub` and `HtmlAgilityPack` and the only project containing archive/parser/image-header implementations.
- WPF accesses EPUB inspection only through Application use cases/models; no parser, filesystem, or Infrastructure type appears in view models or code-behind. Infrastructure remains referenced only in the composition root.
- Rules contain no I/O or parser dependencies, and business/scoring logic is absent from WPF code-behind.
- Existing architecture tests for SQLite and hashing continue to pass.

### WPF tests

- Display score, completed/disqualified status, safe path, versions, and structural summary for the correct record/format.
- Display `Not scored — disqualified` and the disqualifying explanation, never a zero score.
- Display every finding and filter correctly across Positive, Information, Warning, Error, and Disqualifying without changing source order.
- Preserve selection and reset behavior across scans; clear stale results after failure/cancellation.
- Progress remains coalesced, cancellation stays responsive, and collection publication occurs on the UI dispatcher in bulk.
- Non-EPUB formats show no fabricated assessment.
- No text, command, sort default, or visual marker recommends which duplicate to keep.
- Accessibility names and keyboard navigation exist for the new tab, grids, filters, and cancel control.

## Verification commands

Run from the repository root after each coherent implementation step and again before completion:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```

Also run focused suites while iterating:

```powershell
dotnet test tests/CalibreLibraryCleaner.Domain.Tests/CalibreLibraryCleaner.Domain.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Application.Tests/CalibreLibraryCleaner.Application.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Architecture.Tests/CalibreLibraryCleaner.Architecture.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Wpf.Tests/CalibreLibraryCleaner.Wpf.Tests.csproj
```

Before reporting completion:

- review `git status --short` and the complete diff, preserving pre-existing Milestone 3 and Rider changes;
- search production code for extraction and network APIs and justify any match;
- confirm package references occur only in Infrastructure;
- run the safety manifest suite separately and record its result;
- manually exercise one valid, one warning-heavy, and one disqualified synthetic library in WPF, including cancellation and severity filters;
- update this plan's `Progress` and `Final outcome` with exact command/test results and any deviations.

Do not claim success unless the relevant commands actually succeed.

## Incremental implementation steps

1. **Freeze decisions and write ADR 0004.** Confirm V1 dependency versions, limits, thresholds, weights, disqualification semantics, parser options, and version strings. Update this plan if review changes them. Write the ADR before adding package references.
2. **Add Domain assessment values and invariants.** Implement versions, score, severity extension, status, finding, feature summary, assessment, snapshot collection, ordering, and focused Domain tests. No parser or filesystem concepts enter Domain.
3. **Add provider-neutral Application contracts.** Define file observation, inspection request/result/problem/progress/limits and `IEpubInspector`. Extend the hash contract to return its verified observation and update current hasher/application tests without changing behavior.
4. **Implement the pure V1 rule engine.** Add one rule class per catalog row, frozen catalog, cap handling, formula, versions, deterministic ordering, and exhaustive unit tests. Keep all rules independent of I/O.
5. **Implement bounded assessment orchestration.** Add `AssessEpubFormatsUseCase`, selection/status translation, result slots, progress, cancellation, concurrency enforcement, and association validation. Exercise it entirely with fakes, including thousands-of-results and gated concurrency tests.
6. **Add dependencies and secure archive primitives.** Pin packages, reference them only in Infrastructure, implement archive path resolution, preflight limits, guarded streams, safe XML, file-state checks, and their adversarial integration tests before parser integration.
7. **Integrate VersOne lazily.** Implement explicit safe reader options, fail-closed network behavior, provider exception translation, package/metadata/navigation/spine/resource collection, and disposal. Do not use the eager whole-book API.
8. **Add bounded content and cover inspection.** Implement sequential HTML text/reference analysis and header-only cover dimensions. Add malformed, excessive, cancellation, reference, and memory-bound fixture cases.
9. **Connect the scan pipeline.** Add options/phases, invoke assessment after hashing, store assessments in `LibrarySnapshot`, register services in composition roots/test services, and prove existing duplicate groups and read-only guarantees remain unchanged.
10. **Add WPF presentation.** Build the assessment tab, rows, detail/filter state, status/score/version display, progress/cancellation, accessibility, and tests. Keep all scoring and inspection outside view models/code-behind.
11. **Complete safety and performance coverage.** Expand synthetic library manifests, no-network/no-extraction tests, changed-file races, bounded concurrency, cancellation latency, scale diagnostics, and architecture enforcement.
12. **Document and verify.** Update domain/scoring/architecture documentation, run formatting/build/all tests, manually inspect the WPF slice with synthetic data, review the entire diff, and record exact results here.

Each step must leave the solution buildable and its relevant tests passing. If a security primitive cannot enforce a proposed limit before VersOne allocates or parses, stop and revise the adapter/design rather than accepting the gap.

## Risks

- **Parser trust and eager allocation:** VersOne may parse required metadata eagerly or allocate before custom code sees an entry. Mitigate with BCL ZIP preflight, capped outer/entry streams, lazy-only API use, adversarial fixtures, and a stop/review condition for limits that cannot be enforced.
- **Cancellation granularity:** Third-party calls lack cancellation tokens. Mitigate with cancellation-checking streams and checks between resources; document any unavoidable non-interruptible metadata window measured in tests.
- **Tolerant-versus-strict parsing:** Strict mode may reject common EPUB defects, while relaxed mode may hide score evidence. Use explicit options and characterization tests, recording tolerated defects as facts/findings.
- **Archive metadata lies:** Declared sizes and ratios are not sufficient. Enforce actual byte limits while reading and checked aggregate counters.
- **File replacement race:** Post-hash metadata checks cannot prove immutability against a deliberately forged same-size/same-time replacement. Preserve the hash association and revalidate ordinary state changes; do not claim transactional protection.
- **Memory use on thousands of books:** Parser objects, HTML DOMs, evidence, and WPF rows can accumulate. Dispose per file/resource, bound evidence, keep only immutable summaries, fix worker count, and materialize details on demand if profiling requires it.
- **HTML and cover edge cases:** Real EPUB markup and image formats are diverse. Unsupported but safe cases should yield transparent zero/negative findings; they must not trigger full decode or silent success.
- **False quality precision:** Initial weights and thresholds are policy choices. Keep contributions visible, version the model, avoid cross-version comparisons, and do not turn scores into retention recommendations.
- **Severity extension impact:** Adding `Positive` and `Disqualifying` to the shared enum may expose unhandled switches in existing UI/tests. Audit all switches and serialization/display mappings; if this causes inappropriate coupling, use a dedicated `AssessmentFindingSeverity` and update the file list before implementation.
- **Dependency churn/licensing:** Package releases and transitive dependencies may change. Pin exact versions, record the decision, audit at implementation time, and require the full security suite on upgrades.
- **Dirty working tree:** Milestone 3 changes are not all committed. Review diffs by path and never reset, overwrite, or reformat unrelated work.

## Unresolved questions

These are proposed defaults, not permission to silently choose a materially different behavior during implementation:

1. Confirm whether extending shared `FindingSeverity` is preferable to a dedicated `AssessmentFindingSeverity`. The plan prefers extension for one UI vocabulary, subject to an exhaustive switch audit.
2. Confirm the V1 archive/resource limits against representative non-personal synthetic large EPUBs. Raising a limit changes analyzer behavior and must be recorded before code is merged.
3. Confirm the cover usefulness threshold of 600 by 800 pixels and readable-text thresholds of 500/5,000 normalized letters/digits. Any change requires a scoring-model version decision.
4. Confirm that a parsed package with an empty spine remains numerically scored with a severe penalty, while inability to parse the package is disqualifying. The plan favors this distinction because it preserves explainable structural assessment.
5. Confirm whether syntactically plausible ISBN validation should include check-digit validation in V1. The plan proposes check digits with normalization and no external lookup.
6. Characterize exactly which VersOne relaxed-reader callbacks/options are necessary for malformed-but-assessable real-world EPUBs. The accepted option set belongs in ADR 0004 and tests, not in undocumented defaults.
7. Confirm recognized font-obfuscation schemes that remain assessable. Anything beyond known font-only obfuscation stays disqualifying when it blocks required content analysis.

## Milestone boundary review

This plan has been reviewed against the Milestone 4 section of `docs/roadmap.md`. It includes the required EPUB parser integration, cover and navigation checks, spine/resource/text checks, reproducible scoring, explainable findings, UI presentation, and safety/performance support.

It deliberately excludes Milestone 5 and later functionality: no format recommendation engine, no duplicate retention choice, no comparison/ranking of candidates, no review/approval queue, no cleanup plan, no execution/backup/rollback, no PDF analysis, no EPUB content fingerprints, no fuzzy/semantic duplicate detection, and no AI or network metadata services. The score describes one EPUB file only and is not used to decide what should be retained.

## Progress

- [x] Read root and all nested `AGENTS.md` files and `PLANS.md`.
- [x] Read all requested product, functional, architecture, domain, duplicate, scoring, safety, test, roadmap, and accepted ADR documents.
- [x] Read the completed Milestone 0, 1, 2, and 3 plans.
- [x] Inspect the current Domain, Application, Infrastructure, WPF implementation, project/package configuration, test topology, safety fixtures, architecture enforcement, and working-tree state.
- [x] Review the maintained VersOne.Epub candidate and relevant official API/options/release information; review Html Agility Pack as the bounded HTML helper.
- [x] Draft the Milestone 4-only execution plan.
- [x] Review the planned scope against `docs/roadmap.md` and explicitly exclude Milestone 5 and later work.
- [x] Resolve the listed V1 decisions during implementation review and accept the proposed defaults in ADR 0004.
- [x] Write accepted ADR 0004 before adding the EPUB dependencies.
- [x] Add Domain assessment values, provider-neutral Application contracts, and the deterministic V1 rule engine.
- [x] Add bounded assessment orchestration and connect it to the atomic scan pipeline.
- [x] Add the read-only, no-network, no-extraction Infrastructure inspector and synthetic security fixtures.
- [x] Add WPF assessment presentation and severity filtering.
- [x] Recover and audit the interrupted implementation on 2026-07-17. The core vertical slice is present, but the earlier completion note overstated coverage: the dedicated Infrastructure EPUB suite has only three tests, the Application assessment suite has four tests, the Domain assessment suite has three tests, and no Milestone 4 WPF presentation tests were added.
- [x] Narrow the Infrastructure exception boundary so unexpected implementation faults fail the atomic scan instead of being mislabeled as unsupported input.
- [x] Characterize VersOne options and record the bounded `RELAXED` configuration plus four explicit missing-navigation/spine tolerances in ADR 0004; keep `IGNORE_ALL_ERRORS` forbidden.
- [x] Complete scoring/cap, orchestration/scale, adversarial archive/XML/EPUB, encryption, navigation/reference, cover-header, safety-manifest, cancellation, stale-file, architecture, and WPF presentation coverage.
- [x] Require every successful hash result to carry a matching verified file observation and forward per-file inspection substages through scan progress.
- [x] Run the final default-output restore, build, test, and formatting sequence after the user-owned WPF process exited normally.
- [x] Review the complete diff and repeat mutation, extraction, network, eager-reader, package-boundary, and parser-type leakage searches.
- [x] Complete Milestone 4.
- [ ] Perform an interactive visual/high-DPI/screen-reader exercise against valid, warning-heavy, and disqualified synthetic libraries. No interactive desktop review was available; focused WPF construction, binding, score/disqualification/filter, and real-Infrastructure flow tests passed.
- [x] Remediate the post-completion review findings with the smallest Milestone 4-only corrections: reject oversized central directories before `ZipArchive` materialization; preflight every XML resource eagerly read by VersOne; enforce archive-entry and managed-root traversal rules; bound and sanitize retained evidence; bound HTML structure and cancellation; validate scoring contracts; serialize progress; and materialize WPF finding details on demand.
- [x] Add focused regressions for navigation limits/DTDs, embedded traversal, root reparse points, HTML structure/cancellation, evidence bounds/redaction, scoring-contract disqualification, monotonic progress, lazy WPF details, and complete selected-feature display.
- [x] Re-run restore, build, all tests, format verification, safety tests, and complete diff review after remediation.

## Final outcome

Completed the Milestone 4 EPUB assessment vertical slice on 2026-07-17 after recovering and auditing the interrupted 2026-07-16 work. The immutable snapshot carries deterministic, versioned EPUB assessments; Application performs bounded orchestration and findings-derived scoring; Infrastructure preflights and lazily inspects untrusted EPUBs read-only without extraction or network access; and WPF displays assessment summaries, explicit not-scored state, bounded feature details, and severity-filtered findings. ADR 0004 records the accepted dependency, parser-option, and safety decisions.

Packages added only to Infrastructure and pinned centrally: `VersOne.Epub` 3.3.6 and `HtmlAgilityPack` 1.12.4. `dotnet list package --vulnerable --include-transitive` reported no known vulnerable packages from the configured sources.

Verification actually performed:

- `dotnet restore` succeeded.
- Default-output `dotnet build --no-restore` succeeded with zero warnings and zero errors.
- Default-output `dotnet test --no-build` succeeded: Domain 59, Application 33, Infrastructure 68, Architecture 14, and WPF 8; 182 total passed, zero failed, zero skipped.
- `dotnet format --verify-no-changes` succeeded.
- The separately filtered Infrastructure safety suite passed: 13 passed, zero failed, zero skipped.
- `dotnet list package --vulnerable --include-transitive` found no known vulnerable packages from the configured sources.
- `git diff --check`, parser-boundary searches, mutation/extraction/network/eager-reader searches, package-reference review, and the complete changed/new file review found no Calibre mutation path, extraction API, runtime network client, eager `ReadBook` use, or EPUB-library type outside Infrastructure.

The V1 inspector enforces the frozen 1 GiB file, 10,000-entry, 512 MiB declared and actual aggregate-read, 64 MiB entry, 4 MiB XML/navigation, 8 MiB chapter, 2 MiB CSS, 32 MiB cover/64 KiB cover-header, 10,000-spine-item, 50,000-local-reference, 100-evidence-item, 20-million-readable-character, and per-entry/aggregate compression-ratio limits. It rejects unsafe or duplicate canonical archive paths, DTD/external XML, unsupported DRM, stale file observations, and parent reparse points. It opens files read-only with restrictive sharing, never extracts, disables VersOne downloading, installs a fail-closed downloader, bounds reads, and checks cancellation between bounded operations.

The frozen `epub-quality/1.0.0` catalog uses the documented +50 baseline and positive/negative weights for open/package, embedded metadata, validated ISBN, cover presence/header/dimensions, usable navigation, spine/resources, internal references, readable text, near-empty chapters, repeated spine/href/navigation references, and encryption state. Repeated penalties remain visible after their independent caps. Completed scores equal `clamp(sum(finding adjustments), 0, 100)`; missing, unreadable, invalid, changed, unsafe, malformed-package, limit-exceeding, or unsupported-encrypted EPUBs are disqualified and have no numeric score. A parsed empty spine remains scored with its severe penalty.

Assessment concurrency defaults to two and is explicitly bounded; result slots preserve canonical book/path order regardless of completion order. Cancellation propagates rather than becoming a finding, cancellation-checking streams cap individual reads, and the scan publishes no partial replacement snapshot. A 2,000-target orchestration test, gated concurrency test, large/limited content tests, and mid-inspection cancellation and stale-file tests pass.

Implementation-shape deviations do not change the approved behavior or milestone boundary: cohesive private inspector helpers remain in and around `VersOneEpubInspector` rather than every planned helper being a separate source file; V1 rule evaluations are consolidated in the pure `EpubAssessmentEngine` rather than one source file per rule; and selected feature detail is presented by an immutable assessment row rather than a separate feature-summary view-model. Failed/canceled scans retain the prior atomic UI snapshot, consistent with the established Milestone 3 behavior, rather than clearing already-published results. No PDF, content fingerprint/similarity, recommendation, retention choice, cleanup, mutation, backup/rollback, AI, or online lookup work was introduced.

Post-completion remediation on 2026-07-18 hardened archive central-directory preflight, eager navigation XML validation, archive and managed-root path handling, HTML structure limits, evidence retention/redaction, score derivation, progress serialization, and selected-row WPF materialization. The behavior change is recorded as analyzer `epub-inspector/1.0.1`; the unchanged scoring catalog remains `epub-quality/1.0.0`. The remediation verification succeeded with zero build warnings or errors and 195 passing tests: Domain 59, Application 35, Infrastructure 78, Architecture 14, and WPF 9. The focused EPUB/safety filter passed 71 tests across Application, Infrastructure, Architecture, and WPF; `dotnet format --verify-no-changes` and `git diff --check` also succeeded.

Remaining risks are the documented non-transactional same-size/same-timestamp hostile replacement race, parser rejection of malformed-but-potentially-salvageable publications outside the four explicitly tolerated conditions, simple bounded HTML/CSS/image-header heuristics, and the policy nature of the initial scoring weights. Interactive high-DPI/screen-reader review remains manual; automated WPF construction and behavior tests pass.
