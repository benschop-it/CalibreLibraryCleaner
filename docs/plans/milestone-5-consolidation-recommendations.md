# Milestone 5 Consolidation Recommendations

This execution plan is a living document. During implementation, keep `Progress`, decisions, deviations, failed approaches, verification results, and `Final outcome` current. Follow `PLANS.md`, every applicable `AGENTS.md`, and all accepted ADRs.

This plan covers Milestone 5 only. It authorizes deterministic recommendations, in-memory user review state, and explicit export of a review artifact outside the Calibre library. It does not authorize a cleanup plan, approval for mutation, Calibre CLI use, backup, merge, replacement, deletion, or any other Calibre-library mutation.

## Objective

Add a deterministic, explainable, non-destructive consolidation-recommendation vertical slice for the exact normalized title/author metadata candidate groups produced in Milestone 3.

For each eligible group, propose:

- one existing Calibre record as the metadata source;
- at most one proposed source for each format retained in the conceptual consolidated result;
- formats supplied by different records when that produces the safest supported result;
- byte-identical alternatives that are proposed not to contribute another final-format copy;
- records that should remain separate because the available evidence suggests a different edition or makes consolidation unsafe;
- records that are potentially redundant only when no unique, unavailable, or unresolved format evidence is hidden;
- unresolved same-format conflicts that require review;
- ordered reasons, warnings, decision strength, and overall recommendation confidence.

The WPF workflow must preserve the generated recommendation, let the user review and override every proposed selection, distinguish generated values from reviewed values, and export the generated recommendation plus review state to deterministic, versioned JSON outside the Calibre library.

No recommendation or override modifies `metadata.db`, a Calibre-managed file, or Calibre metadata. A recommendation is a review artifact, not an instruction to mutate the library.

## Scope

### In scope

- Generate one consolidation recommendation for every eligible `ExactMetadataDuplicateGroup` in a completed scan.
- Reuse the existing `CalibreBook`, `BookFormat`, `FormatFileFingerprint`, `ExactBinaryDuplicateGroup`, and `FormatAssessment` results; never rehash or reassess an EPUB during recommendation generation.
- Extend the schema-27 read-only metadata projection with the smallest existing Calibre fields required for the requested metadata-quality and different-edition safeguards: publication date, publisher, series, series index, languages, and Calibre's stored cover-availability flag.
- Select metadata independently from format sources and explain the metadata-quality comparison.
- Select formats independently per canonical format type and combine sources from different records.
- Use exact binary evidence for byte-identical same-format alternatives.
- Use current, matching Milestone 4 EPUB assessments for non-identical EPUB alternatives.
- Leave non-identical AZW3, PDF, MOBI, and other unassessed same-format alternatives unresolved unless the user explicitly reviews them.
- Classify each metadata, format, and record decision as `Safe`, `Strong`, `Ambiguous`, or `Unsupported`.
- Derive an overall qualitative recommendation confidence independently of metadata-match confidence, EPUB score, assessment status, and per-decision strength.
- Model proposed retained formats, proposed excluded alternatives, proposed redundant records, retained-separate records, and unresolved conflicts without deletion or mutation language.
- Preserve an immutable generated recommendation while applying validated user overrides to a separate reviewed selection.
- Support review states `Unreviewed`, `Accepted`, `ManuallyAdjusted`, `Deferred`, `KeepSeparate`, and `NotDuplicates`.
- Keep overrides in memory for the current application session, except when the user explicitly exports the review artifact.
- Detect input-version changes on regeneration, keep stale overrides visible, and prevent stale overrides from becoming an effective final selection until reset or reapplied.
- Extend the existing metadata-candidate WPF workflow incrementally with comparison, selection, reasoning, warnings, review actions, reset, and JSON export.
- Export deterministic UTF-8 JSON with explicit schema and recommendation-model versions, sanitized source identity, input identity, generated and reviewed state, and staleness data.
- Add Domain, Application, Infrastructure, architecture, safety, serialization, WPF, cancellation, and scale tests using only synthetic fixtures.

### Eligibility

An existing Milestone 3 metadata group is eligible when:

- every member ID resolves to exactly one record in the same completed snapshot;
- at least two member records remain present;
- the group identity and ordered membership are internally valid;
- relevant file fingerprints and assessment associations are internally consistent.

A group with incomplete or inconsistent recommendation inputs still receives an `Unsupported` recommendation shell with blocking warnings so it remains visible and reviewable. It does not receive invented selections.

## Out of scope

- Cleanup-plan generation, cleanup-plan persistence, expected pre-operation states, plan approval, plan import, or cleanup-plan validation.
- Any claim that a proposed redundant record will be merged or deleted.
- Adding, replacing, removing, copying, moving, renaming, or deleting a Calibre record or format.
- Writing to `metadata.db`, changing Calibre metadata, or creating a Calibre-managed file.
- Calibre CLI discovery or invocation.
- Backups, rollback, execution locks, audit execution history, or post-mutation verification.
- PDF, AZW3, MOBI, or other new quality analyzers.
- EPUB content fingerprints, text equivalence, full-text similarity, or a claim that non-identical EPUBs contain the same text.
- Fuzzy duplicate detection, identifier-based duplicate grouping, semantic comparison, or edition inference from book content.
- Online metadata lookup, runtime network access, AI, or cloud services.
- Rewriting, correcting, normalizing, or merging stored metadata.
- A settings screen for recommendation thresholds.
- Persistent review databases, automatic background saves, or application-owned caches.
- Broad WPF navigation, styling, or shell redesign.
- Importing an exported review artifact as a cleanup plan or converting it to mutation instructions.
- Any Milestone 6 or later behavior.

## Relevant requirements and decisions

- `docs/roadmap.md`: Milestone 5 chooses metadata and format sources independently, warns about conflicts, supports override, and exports JSON. Immutable cleanup plans start in Milestone 6.
- `docs/product-vision.md`: safety and explainability take precedence; unique formats must never be silently discarded.
- `docs/functional-requirements.md`: metadata and format quality remain separate; non-identical same-format conflicts require review; recommendations may combine records.
- `docs/domain-model.md`: recommendations and invariants belong in Domain; scores remain findings-derived and separate from confidence.
- `docs/duplicate-detection.md`: exact binary and exact normalized metadata are independent evidence. Neither is a merge or deletion authorization.
- `docs/quality-scoring.md`: EPUB scores describe individual EPUB quality, not edition equivalence, and can be used only with their findings and compatible versions.
- `docs/safety-and-rollback.md`: analysis and application-owned reports outside the library are allowed; Calibre content mutation is forbidden in analysis mode.
- `docs/test-strategy.md`: recommendation, staleness, override, serialization, WPF, architecture, and safety behavior require synthetic automated coverage.
- ADR 0001: schema-27 reads stay read-only/query-only and no SQL mutation path is introduced.
- ADR 0002: preserve `Domain <- Application <- Infrastructure` and `Application <- Wpf`; Infrastructure remains confined to WPF composition.
- ADR 0003: future mutations use supported Calibre tooling, but this milestone adds no mutation/tooling boundary.
- ADR 0004: EPUB parser facts and versions remain behind provider-neutral assessment values. Milestone 5 consumes completed assessment values and never references parser types.

## Existing implementation inspected

The implementation and tests were inspected before this plan was drafted.

### Current production flow

- `ScanLibraryUseCase` validates a library, opens schema 27 read-only, resolves managed paths, hashes every safe format, assesses EPUB files, groups exact binary files, groups exact normalized title/author candidates, and publishes one immutable `LibrarySnapshot` only after the complete scan succeeds.
- `SqliteCalibreMetadataReader` uses `SqliteOpenMode.ReadOnly`, disabled pooling, `PRAGMA query_only = ON`, fixed `SELECT` statements, schema-shape validation, deterministic row order, cancellation checks, and provider-neutral records.
- The current catalog projection loads title, book and author sort values, ordered author names, identifiers, formats, and managed paths. It does not load publication date, publisher, series, series index, languages, or the stored cover flag.
- The installed schema source and existing schema-27 contract show these are existing Calibre fields/relations (`books.pubdate`, `books.series_index`, `books.has_cover`, `publishers`/`books_publishers_link`, `series`/`books_series_link`, and `languages`/`books_languages_link`). They can be added through the existing read-only boundary without changing the supported schema version.
- `LibrarySnapshot` currently owns ordered books, findings, exact binary groups, exact metadata groups, and EPUB assessments. It has no recommendation collection.
- `ExactBinaryDuplicateGroup` proves matching length and SHA-256 for managed file references. Its evidence is file-level and does not classify entire records.
- `ExactMetadataDuplicateGroup` has a deterministic identity derived from normalized title and author set, ordered record IDs, and reason `EXACT_NORMALIZED_TITLE_AUTHOR_SET`. It remains a candidate grouping, not content or edition proof.
- `FormatAssessment` records a canonical EPUB association, observed fingerprint, `Completed` or `Disqualified` status, nullable score, analyzer version, scoring-model version, bounded features, and deterministically ordered findings.
- Current analyzer/scoring versions are `epub-inspector/1.0.1` and `epub-quality/1.0.0`.
- `LibraryAnalysisOptions` bounds hashing and EPUB-assessment concurrency. There is no recommendation policy or progress phase.
- WPF currently exposes Library, Exact file duplicates, Metadata candidates, and EPUB assessments tabs. Metadata candidates already have text filtering, previous/next navigation, original record context, and session-only defer state.
- `MainWindowViewModel` builds presentation data off the dispatcher, publishes collections with a single reset, coalesces progress, retains the prior successful snapshot after failure/cancellation, and keeps Infrastructure references out of ViewModels.
- The existing metadata-member presentation shows IDs, original titles/authors, author sort, formats, and contextual identifiers, but has no recommendation, comparison, override, or export state.

### Current tests and fixtures

- Domain tests cover immutable values, exact binary grouping, conservative metadata normalization/grouping, 50,000-record metadata grouping, assessment invariants, deterministic findings, and score derivation.
- Application tests cover scan result mapping, independent duplicate evidence, hashing/assessment contracts, bounded EPUB concurrency, cancellation, progress, malformed contracts, and thousands of EPUB targets.
- Infrastructure tests cover schema-27 read-only loading, managed-path safety, streaming hashes, exact metadata integration, adversarial EPUB inspection, and recursive library non-mutation manifests.
- Architecture tests enforce project references, parser-package isolation, no WPF/ViewModel filesystem access, no mutable EPUB/hash implementation, and conservative metadata matching.
- WPF tests cover scan behavior, duplicate displays, metadata filtering/navigation/session defer, EPUB presentation, cancellation/retry, bulk collection changes, and real-Infrastructure synthetic flow.
- `SyntheticCalibreLibrary` currently creates only the reduced Milestone 1 schema projection and will need the additional schema-27 metadata tables/columns for recommendation fixtures.
- The working tree already contains user/IDE state in `.idea/workspace.xml` and `.ai/`; implementation must not modify, remove, or include those unrelated files in completion claims.

## Proposed design

### End-to-end flow

1. Preserve the current read-only scan, hashing, EPUB assessment, and duplicate grouping behavior.
2. Extend the read-only catalog projection with stored recommendation-relevant metadata and map it into immutable `CalibreBook` values without rewriting or normalizing the stored values.
3. Construct an analysis snapshot containing books, findings, binary groups, metadata groups, and EPUB assessments.
4. `GenerateConsolidationRecommendationsUseCase` indexes those inputs once and evaluates metadata groups in canonical order, reporting progress and cancellation per group.
5. Application maps each group into a validated Domain candidate set. Domain policies identify different-edition blockers, select a metadata source, select each format source, classify record dispositions, produce reasons/warnings, and derive confidence.
6. Publish the final `LibrarySnapshot` with an ordered recommendation collection only after all groups have been processed. A canceled or failed generation does not publish a partial replacement snapshot.
7. WPF creates lightweight recommendation rows and materializes selected-group comparison/format/reason details on demand.
8. User changes are validated through `ApplyRecommendationOverrideUseCase`. The immutable generated recommendation remains unchanged; a separate reviewed recommendation contains the override delta and effective selection.
9. A successful rescan regenerates recommendations. Session overrides with the same library UUID, group ID, recommendation-model version, and input version are reapplied. Mismatched overrides remain visible as stale but are not applied.
10. `ExportRecommendationsUseCase` snapshots current generated/reviewed state, builds a deterministic export document, and calls an Infrastructure exporter that rejects any destination inside the Calibre library.

Recommendation generation remains analysis. It does not create a cleanup plan or mutation vocabulary.

## Proposed Domain model

Use immutable records/value objects, defensive collection copies, strongly typed IDs where mix-ups are plausible, and explicit invariants. Exact names can be adjusted to match implementation conventions, but the following concepts must remain distinct.

### Version and status values

- `RecommendationModelVersion`: nonblank version value, initially `consolidation-recommendation/1.0.0`, advanced to `consolidation-recommendation/1.0.1` by the first post-completion safety remediation, and advanced to `consolidation-recommendation/1.0.2` by the second completed-implementation review remediation. Any change to metadata ranking, edition blockers, EPUB threshold/decisive rules, format-selection behavior, record-disposition rules, confidence classification, or input identity requires a version bump.
- `RecommendationInputVersion`: a versioned, canonical structured identity of every relevant input. Equality, not scan time, determines staleness.
- `RecommendationConfidence`: `Deterministic`, `High`, `Medium`, `Low`, `ManualReviewRequired`, `Unsupported`.
- `RecommendationDecisionStrength`: `Safe`, `Strong`, `Ambiguous`, `Unsupported`. This applies to individual metadata, format, and record decisions and is not the overall confidence.
- `RecommendationReviewStatus`: `Unreviewed`, `Accepted`, `ManuallyAdjusted`, `Deferred`, `KeepSeparate`, `NotDuplicates`.
- `RecommendationFreshness`: `Current` or `Stale`.
- `FormatResolutionStatus`: `Selected`, `UnresolvedConflict`, `Unavailable`, or `ExplicitlyExcludedByUser`.

These values are separate from:

- the exact normalized metadata match category/reason;
- exact binary equality;
- `QualityScore`;
- `AssessmentStatus`;
- analyzer/scoring-model versions.

### Recommendation aggregate

`ConsolidationRecommendation` contains:

- the exact metadata duplicate-group ID and ordered member IDs;
- `RecommendationModelVersion` and `RecommendationInputVersion`;
- one nullable `MetadataSourceSelection`;
- one ordered `FormatSourceSelection` per canonical format represented by the group;
- ordered proposed retained formats;
- ordered proposed excluded format alternatives and the evidence permitting each exclusion;
- ordered `ProposedRedundantRecord` values;
- ordered `RetainedSeparateRecord` values;
- ordered unresolved conflicts;
- ordered `RecommendationReason` and `RecommendationWarning` values;
- overall `RecommendationConfidence`.

Invariants:

- every selected record is a current group member;
- there is at most one proposed source for a final format;
- every format source actually owns the canonical format candidate represented by the selection;
- every selected source has at least one linked reason;
- every unresolved conflict has at least one linked manual-review warning;
- non-identical unassessed same-format candidates cannot appear in generated exclusions;
- a proposed redundant record cannot contain a unique, unavailable, retained-separate, or unresolved format and cannot be a selected metadata/format source;
- retained-separate record formats never enter the consolidation candidate set;
- no type/property represents deletion, merge execution, mutation order, or Calibre command arguments.

### Metadata source

`MetadataSourceSelection` contains the selected member ID, the ordered quality comparison facts, decision strength, and linked reason IDs. A null selection is valid only for an unsupported recommendation and requires a blocking warning.

Selecting metadata means retaining one record's stored metadata exactly as it exists. It never means synthesizing fields, combining metadata fields, normalizing stored values, or correcting a record.

### Format source

`FormatSourceSelection` contains:

- canonical format;
- all ordered candidate file references, including unavailable candidates;
- nullable proposed source;
- resolution status;
- ordered proposed excluded alternatives;
- exact-binary evidence when applicable;
- EPUB score/status/version and decisive-finding references when applicable;
- decision strength;
- linked reasons/warnings.

An unresolved selection retains all alternatives conceptually. It does not pick a winner using record ID, path, size, timestamp, or filename.

### Record disposition

- `ProposedRedundantRecord` means only that the recommendation found no selected metadata, selected unique format, unavailable file, unresolved format, or separate-edition reason on that record and every available format is byte-identical to an already selected same-format source. UI/export wording must include `proposed` or `potentially`.
- `RetainedSeparateRecord` means the recommendation does not combine that record's metadata or formats into the proposed consolidated result because conservative edition evidence requires separation/review.
- A record contributing the metadata source or any non-identical selected format is not generated as redundant.
- Empty-format records are not generated as redundant merely because they have no file contribution; incomplete evidence produces a warning and manual review.

### Reasons and warnings

`RecommendationReason` contains a stable code, subject kind, optional book/format subject, explanation, and a bounded ordinal evidence map. `RecommendationWarning` additionally has `Advisory`, `ManualReview`, or `Blocking` severity.

Reasons and warnings use stable policy-owned templates. They do not store raw exceptions, absolute paths, book prose, or parser objects. Example codes include:

- `METADATA.CORE_FIELDS_USABLE`
- `METADATA.MORE_COMPLETE`
- `METADATA.CONSISTENT_WITH_GROUP`
- `METADATA.TIE_BROKEN_BY_RECORD_ID`
- `FORMAT.ONLY_AVAILABLE_SOURCE`
- `FORMAT.EXACT_BINARY_EQUIVALENT`
- `EPUB.VALID_OVER_DISQUALIFIED`
- `EPUB.MATERIAL_QUALITY_ADVANTAGE`
- `FORMAT.COMBINED_FROM_DIFFERENT_RECORD`
- `RECORD.PROPOSED_REDUNDANT_EXACT_FORMATS`
- `IDENTIFIER.STRONG_CONFLICT`
- `METADATA.LANGUAGE_CONFLICT`
- `METADATA.PUBLICATION_YEAR_CONFLICT`
- `METADATA.SERIES_CONFLICT`
- `METADATA.EDITION_WORDING_CONFLICT`
- `FORMAT.UNASSESSED_NONIDENTICAL_CONFLICT`
- `EPUB.SCORES_TOO_CLOSE`
- `EPUB.FINDINGS_CONFLICT`
- `ASSESSMENT.STALE_OR_INCOMPARABLE`
- `FORMAT.FILE_UNAVAILABLE`
- `RECORD.RETAIN_SEPARATELY`
- `INPUT.INCOMPLETE`

Stable codes are serialized; user-facing explanations remain explicit and testable.

## Read-only metadata projection extension

The current implementation cannot evaluate requested language, year, series, publisher, or cover signals. Milestone 5 therefore extends only the existing read-only schema-27 projection.

Add immutable stored values to `CalibreBook`, preferably through a cohesive `BookPublicationMetadata` record:

- nullable publisher;
- nullable publication date and derived year for comparison;
- nullable series and nullable decimal series index;
- ordered stored language codes;
- Calibre's stored `has_cover` value.

Reader behavior:

- verify the required schema-27 columns/tables before querying;
- load `books.pubdate`, `books.series_index`, and `books.has_cover` with the existing book query;
- load publisher, series, and languages through fixed `SELECT` joins ordered by book/link order;
- fail closed on structurally ambiguous duplicate publisher/series links;
- convert malformed optional values into `CATALOG_VALUE_INVALID` findings and preserve the rest of the record when it can still be represented safely;
- preserve stored text and values; normalization exists only inside recommendation comparisons;
- do not inspect or validate the physical cover file in this milestone;
- issue no new write statement, schema migration, or mutable filesystem call.

The implementation step must verify the exact schema-27 relation/column shape against the same pinned Calibre source used by Milestone 1 before finalizing the schema contract and synthetic fixture.

## Recommendation algorithm

For each exact metadata group:

1. Resolve members and build ordered lookups for books, formats, exact-binary memberships, EPUB assessments, and relevant scan findings.
2. Build `RecommendationInputVersion` from canonical input facts.
3. Validate required associations. Contract failures yield an `Unsupported` shell, not partial invented choices.
4. Normalize only comparison values needed by documented policies; keep stored values untouched.
5. Detect blocking different-edition evidence and determine the conservative consolidation cohort plus retained-separate members.
6. If fewer than two compatible members remain, produce a manual-review/keep-separate recommendation with no cross-record format selection.
7. Rank metadata candidates in the compatible cohort using the metadata policy below.
8. For every canonical format represented in the cohort, build ordered candidates and apply the format policy below.
9. Classify proposed excluded alternatives, unresolved conflicts, retained-separate records, and potentially redundant records.
10. Generate subject-linked reasons and warnings.
11. Derive overall confidence only from the documented decision/warning rules.
12. Validate the aggregate invariants and place it in the deterministic result slot for the input group.

No stage compares ebook text or assumes non-identical files are the same edition.

## Different-edition and keep-separate policy

### Strong identifiers

Use only identifiers already stored in Calibre. V1 recognizes a frozen local registry:

- ISBN-10/ISBN-13 after separator removal and check-digit validation;
- DOI after trimming, invariant case normalization, and removal of a leading `doi:` or canonical DOI URL prefix;
- ASIN after invariant uppercase validation of the stored 10-character token;
- OCLC after trimming a recognized textual prefix and validating digits.

Unknown identifier types remain visible context but do not become strong evidence. No lookup occurs.

A conflict exists when two records have non-empty, valid, disjoint values for the same strong identifier type. Missing versus present is incompleteness, not a conflict. Invalid claimed strong identifiers produce warnings and no strength credit.

### Languages

- Compare trimmed stored language codes with ordinal invariant casing; do not translate or rewrite them.
- Disjoint non-empty language sets are blocking different-edition/translation evidence.
- Missing language lowers confidence and blocks `Deterministic` confidence but does not itself prove a different edition.

### Publication date

- Compare stored publication years only when both values parse safely.
- Any unequal known year produces a warning.
- A gap of at least two years is the configurable V1 `MaterialPublicationYearGap` and is blocking different-edition evidence.
- Timestamps and file modification dates are never treated as publication quality or edition evidence.

### Edition wording

- Inspect only stored title text for a small frozen, culture-invariant marker catalog such as ordinal/numbered edition, `REVISED`, `EXPANDED`, `ABRIDGED`, `UNABRIDGED`, `ILLUSTRATED`, and `ANNIVERSARY EDITION`.
- Different non-empty marker sets are blocking evidence. Matching or absent markers provide no equivalence proof.
- The detector never removes edition wording or changes Milestone 3 grouping.

### Series

- Conflicting non-empty normalized series names are blocking evidence.
- For the same non-empty series name, different known series indices are blocking evidence.
- Missing series information is incompleteness, not conflict.

### Format availability and unreadable inputs

- Disjoint non-empty format sets, or a symmetric difference of at least two canonical formats without exact-binary overlap, produces a `substantially different format availability` warning.
- Format-set difference alone lowers confidence; it recommends separation only when another blocking edition signal exists.
- Missing, inaccessible, changed, or invalid-path files block redundant-record classification and lower confidence.
- A disqualified EPUB is still an existing candidate. It can lose a supported quality comparison to a completed EPUB, but it is never evidence that the containing record as a whole is redundant.

### Conservative cohort rule

- Determine consensus only from at least two records sharing the same non-empty value/set.
- A record that conflicts with such consensus on a blocking signal is proposed retained separately.
- If a two-record group conflicts, or no unique consensus can isolate an outlier, retain all conflicting records separately and require manual review.
- Apply the union of blocking conditions, then re-evaluate the remaining cohort. Never select formats across the retained-separate boundary.

## Metadata-selection rules

### Usability and placeholders

- A usable title is nonblank after safe comparison normalization and is not a frozen placeholder token.
- A usable author set is non-empty, contains no unusable author, and contains no frozen placeholder-only author value.
- V1 placeholder tokens are deliberately small and explicit: `UNKNOWN`, `UNKNOWN AUTHOR`, `UNTITLED`, `N/A`, `NONE`, `-`, and `?` after the existing safe metadata normalization.
- Placeholder detection lowers source quality and emits a warning; it never rewrites the stored value or changes duplicate grouping.
- `ANONYMOUS` is not treated as a placeholder because it can be legitimate authorship.
- If no candidate has a usable title and usable author set, metadata selection is unsupported and all consolidation choices require manual review.

### Independently testable comparison vector

Compute and expose a named comparison vector for each compatible candidate:

1. core usability: usable stored title and complete usable author set;
2. catalog integrity: no incomplete-author or invalid core-value finding;
3. conflict count: fewer contradictions with unanimous known group metadata is better;
4. completeness count over the fixed fields author sort, at least one valid strong identifier, language, publication date, publisher, complete series name/index, and stored cover flag;
5. group-consistency count over non-conflicting known publisher/language/year/series values;
6. count of valid strong identifiers;
7. stable Calibre record ID ascending as the final fallback only.

Compare the tuple lexicographically in the order above, with boolean/count directions documented in the value type. Do not use an opaque weighted total.

The selected record receives reasons for every criterion that actually distinguished it. If only record ID breaks the tie, emit `METADATA.TIE_BROKEN_BY_RECORD_ID` and state explicitly that the ID is a deterministic fallback, not a quality signal.

Conflicting strong identifiers, languages, material years, or series are handled by the keep-separate policy before metadata ranking. They are never silently outweighed by completeness.

## Format-selection rules

Canonicalize Calibre format tokens with the existing invariant uppercase policy for comparison and ordering. File name, path, timestamp, and size are never quality signals. Size participates only in already-established exact binary equality with SHA-256.

### Common candidate handling

- Include every declared group format candidate, including missing/inaccessible/changed/invalid entries, in the recommendation evidence.
- A single safely present file for a format is proposed as the retained source, even when it comes from a different record than the metadata source.
- A sole present but disqualified EPUB is proposed retained because it is the only available copy, with `ManualReviewRequired` warning; it is not silently excluded.
- If no candidate is present, leave the source unavailable and warn. Never substitute a sibling file.
- Retained-separate record formats stay with their record conceptually and are not candidates for the consolidated result.

### Exact binary duplicates

When all safely present same-format alternatives are members of the same exact binary group:

- select the metadata-source record's copy when present; otherwise select the lowest record ID, then expected relative path ordinal;
- classify the decision as `Safe`;
- explain equal byte length and SHA-256;
- list other identical copies as proposed excluded alternatives for the final-format selection only;
- do not classify unrelated formats or entire records as redundant from this file-level fact.

If some candidates are identical and another same-format candidate is non-identical, collapse the identical subset for comparison but keep the non-identical candidate in the remaining conflict. Exact equality among a subset never removes the unresolved alternative.

### Non-identical EPUB conflicts

Only compare assessments when:

- each assessment association matches record, `EPUB`, relative path, and current file fingerprint;
- analyzer versions match;
- scoring-model versions match;
- neither assessment is stale relative to `RecommendationInputVersion`.

Rules:

1. A completed assessment is strongly preferred over a disqualified assessment when no blocking different-edition signal exists. Expose the disqualifying rules.
2. Between completed assessments, one EPUB is clearly preferable only when its score is at least the configurable `ClearlyPreferredEpubScoreDelta` above every competitor and the findings include decisive structural/readability support.
3. V1 sets `ClearlyPreferredEpubScoreDelta` to 10. Ten points is large enough to represent at least one meaningful V1 structural gap, but the numeric gap is never sufficient alone.
4. Decisive rules are frozen to open/package safety, navigation, non-empty spine, spine-resource completeness, internal-reference completeness, substantial readable text, near-empty chapters, and repeated structural references.
5. At least one decisive rule must favor the proposed source by four or more applied points, or the competitor must be disqualified by a decisive rule. The proposed source must not have a countervailing `Error` or `Disqualifying` decisive finding that the competitor lacks.
6. Cover and embedded metadata findings can explain a result but cannot by themselves authorize a clearly-preferred non-identical EPUB.
7. A score gap below 10, equal scores, different strengths across decisive rules, incompatible versions, stale assessments, or absent assessments produces an unresolved conflict and manual review.
8. Equal/close EPUB alternatives are ordered by record ID/path for display only. The fallback never selects a winner.
9. The reason exposes both scores, status, versions, score difference, and decisive rule IDs/explanations without asserting equal text or edition.

### Formats without a quality analyzer

For AZW3, PDF, MOBI, and every other unassessed format:

- select the only present candidate;
- select one deterministic source when every present alternative is byte-identical;
- for two or more non-identical present alternatives, select none, classify `Ambiguous`, retain every alternative conceptually, and emit `FORMAT.UNASSESSED_NONIDENTICAL_CONFLICT`;
- do not rank by size, timestamp, filename, record ID, metadata-source record, or format availability;
- never place a non-identical alternative in generated exclusions.

### User exclusions

The generated recommendation never excludes a unique or unresolved non-identical format. The user may explicitly exclude a final format through an override. That action:

- is stored as `ExplicitlyExcludedByUser`;
- records the full original candidate list and what changed;
- forces `ManuallyAdjusted` review status;
- emits a visible `USER.EXPLICIT_FORMAT_EXCLUSION` warning;
- cannot cause the generated recommendation or source evidence to be removed;
- remains a review choice, not a mutation instruction.

## Confidence model

Confidence is rule-derived, qualitative, and reproducible. It is not calculated from an opaque numeric formula.

### `Deterministic`

Requires all of:

- no strong identifier, language, material-year, edition-wording, or series conflict;
- no missing/inaccessible/changed/invalid format affecting the group;
- every same-format competition is byte-identical;
- no unresolved conflict;
- metadata candidates have usable core fields and either one clearly dominates the documented vector or are equivalent except for deterministic fallback;
- no retained-separate record.

### `High`

Requires all of:

- no blocking metadata/edition conflict;
- no unresolved unassessed format;
- every selected non-identical EPUB is supported by completed-versus-disqualified evidence or the 10-point-plus-decisive-finding rule;
- no stale/incomparable assessment;
- no unavailable file that could conceal a unique alternative.

### `Medium`

Applies when every proposed source is supported and no conflict is unresolved, but the recommendation relies on metadata completeness, sole-format availability, or complementary formats rather than exact/strong comparative evidence.

### `Low`

Applies when a coherent proposal exists but metadata or file evidence is incomplete, placeholder-heavy, warning-heavy, or substantially different in format availability. It is never used when a required conflict remains unresolved.

### `ManualReviewRequired`

Required for any:

- close/equal/conflicting non-identical EPUB comparison;
- strong identifier or language conflict;
- material publication-year, edition-wording, or series conflict;
- non-identical unassessed same-format conflict;
- retained-separate record;
- stale/incomparable assessment or stale override;
- unavailable file that prevents uniqueness/redundancy conclusions;
- explicit user format exclusion.

### `Unsupported`

Used when no usable metadata source exists, required associations are invalid, all relevant sources are unavailable without a coherent result, or the recommendation model cannot interpret the input versions. Unsupported recommendations remain visible with blocking warnings.

Overall confidence is the most conservative applicable level. Reasons and warnings must be sufficient to reproduce the classification in tests.

## User override model

`UserRecommendationOverride` is an immutable delta containing:

- optional replacement metadata-source record ID;
- ordered per-format actions: select another candidate, mark unresolved, or explicitly exclude the final format;
- ordered records to retain separately;
- requested review status;
- review timestamp supplied by `IClock`;
- the generated recommendation model/input versions against which the override was created.

`ReviewedConsolidationRecommendation` contains the immutable generated recommendation, nullable current override, effective final reviewed selection, review status, freshness, and an optional stale override retained only for display.

Validation rules:

- metadata/format IDs must be members/candidates of the current recommendation;
- a selected source must own a present candidate of the requested format;
- a retained-separate record cannot simultaneously supply consolidated metadata or a selected format;
- `Accepted` requires no effective change from the generated recommendation;
- any effective change produces `ManuallyAdjusted` unless the terminal status is `Deferred`, `KeepSeparate`, or `NotDuplicates`;
- `KeepSeparate` retains all group records separately and has no effective consolidated selections;
- `NotDuplicates` disables the effective consolidated selection while preserving the generated recommendation/evidence;
- `Deferred` may preserve a valid draft override but is not accepted;
- `mark unresolved` is allowed only for a represented format and preserves all candidates;
- explicit exclusion is recorded and warned, never silent;
- impossible selections return structured validation failures and are not saved;
- reset removes the current/stale override and restores the generated selection with `Unreviewed` status.

### Persistence decision

Current overrides live only in WPF/application memory, keyed by source library UUID, duplicate-group ID, recommendation-model version, and input version. This matches the established session-only Milestone 3 defer behavior and avoids designing durable review storage prematurely.

The only Milestone 5 persistence is explicit user-triggered JSON export outside the Calibre library. Export is not automatic and is not a cleanup plan.

## Staleness and input identity

`RecommendationInputVersion` is a canonical structured value, not a timestamp and not a mutable cache key. It includes, in deterministic order:

- source library UUID and supported schema version, but not the absolute library root;
- exact metadata group ID and ordered member IDs;
- relevant stored metadata for each member: title, author names/sorts, identifiers, publisher, publication date, series/index, language codes, cover flag;
- ordered format type, file status, expected relative path, file length, and SHA-256 when available;
- relevant exact-binary group identity/membership;
- EPUB association, fingerprint, assessment status, score, analyzer/scoring-model versions, and ordered decisive finding identities/adjustments;
- relevant catalog/file findings that affect eligibility or confidence;
- input-identity policy version.

It excludes `ScannedAt`, export time, UI selection, and review state so an unchanged rescan reproduces the same identity.

A recommendation becomes stale when group membership, metadata, file status/fingerprint, exact-binary membership, EPUB assessment/version/findings, source library identity, or recommendation model changes.

Behavior:

- successful scan/regeneration always creates a new generated recommendation from current inputs;
- an identical input version reuses a valid session override without recomputing the generated result;
- a changed input version keeps the prior override visible as `Stale`, does not apply it, clears the effective final selection derived from it, and prompts the user to review/reset/reapply;
- changing only a local override does not recompute hashes, assessments, duplicate groups, or generated recommendations;
- export recalculates the export document from the current in-memory snapshot and records freshness; it does not reopen/revalidate Calibre files, because cleanup-plan preconditions belong to Milestone 6;
- a round-tripped export can be compared to a current `RecommendationInputVersion` and classified stale without being imported as an action plan.

## Application use cases and policies

### `GenerateConsolidationRecommendationsUseCase`

- accepts one complete `LibrarySnapshot` without recommendations;
- validates/indexes books, metadata groups, exact binary groups, assessments, and findings once;
- processes groups in deterministic result slots;
- reports `GeneratingConsolidationRecommendations` progress by completed/total group;
- checks cancellation before/after indexing, every group/member/format, sorting/materialization, and final publication;
- returns a complete ordered recommendation list or propagates cancellation/fails the scan with a structured recommendation-generation error.

### `ApplyRecommendationOverrideUseCase`

- validates a proposed override against the immutable generated recommendation;
- calculates changes and effective reviewed selection;
- returns structured field/action validation errors rather than generic exceptions;
- does no I/O and does not mutate the snapshot/generated recommendation.

### `ExportRecommendationsUseCase`

- takes source snapshot identity, ordered generated recommendations, current reviewed state, destination selected by WPF, and injected time;
- validates internal freshness and constructs a provider-neutral `RecommendationReviewExportDocument`;
- calls one meaningful external boundary, `IRecommendationExporter`;
- returns structured path, serialization, cancellation, and write failures;
- never creates cleanup-plan removals, expected-operation states, approvals, or mutation instructions.

Do not add interfaces for pure metadata/format/confidence helper functions. Keep deterministic policies as cohesive Domain services/value comparers. Use an interface only for the external export boundary.

## JSON export design

### Root document

Initial versions:

- schema version: `recommendation-review/1.0`;
- recommendation-model version: `consolidation-recommendation/1.0.2` (the initial completed implementation used `1.0.0`; the first remediation used `1.0.1`).

The root contains properties in this fixed order:

1. `schemaVersion`;
2. `recommendationModelVersion`;
3. `sourceLibrary` with UUID and schema version only;
4. `exportedAtUtc` in invariant UTC round-trip format;
5. `groups` in canonical metadata-group order.

Each group contains, in a fixed order:

- duplicate-group ID and normalized match reason;
- recommendation input version/identity;
- ordered member metadata and formats using relative paths only;
- generated recommendation;
- reasons and warnings;
- confidence;
- user override, including stale override when present;
- effective final reviewed selection, nullable when stale/keep-separate/not-duplicates;
- review status and freshness;
- review timestamp when present.

### Serialization rules

- Infrastructure owns `System.Text.Json`, DTO property order, enum text mapping, schema-specific parsing, and file writing.
- Use explicit serialization DTOs or `Utf8JsonWriter`; Domain/Application types carry no JSON attributes.
- UTF-8 without BOM, fixed newline/indentation policy, invariant numbers/dates, and no environment-dependent converter behavior.
- Sort every collection before serialization; serialize evidence as ordered key/value objects rather than relying on dictionary enumeration.
- Do not serialize `LibraryIdentity.LibraryRoot`, canonical full paths, export destination, raw exceptions, EPUB parser types, book prose, or content snippets.
- Relative Calibre-managed paths are allowed because they identify reviewed format candidates without leaking an absolute library location.
- Include SHA-256/length where already available; do not calculate another file hash during export.
- Export no commands, operations, removals, target mutations, backup instructions, approvals, or execution ordering.
- Deserialization validates schema/model versions, required properties, enum values, association uniqueness, ordering-independent invariants, and bounded strings/collections.
- Unknown future schema versions return a controlled unsupported-version result; they are never interpreted as current cleanup data.

### Deterministic output and timestamps

For identical domain/review inputs and the same injected timestamps, serialized bytes must be identical. Tests use a fixed `IClock`. Real exports may have a new `exportedAtUtc`, which is appropriate provenance and the only intentional byte difference for otherwise identical separately timed exports.

### File-writing boundary

Infrastructure:

- canonicalizes the destination and selected library root;
- rejects the library root and every descendant, including unsafe reparse redirection;
- writes only to a user-selected existing directory outside the library;
- serializes to a temporary sibling file and atomically publishes/replaces only after successful serialization, subject to explicit Save dialog overwrite confirmation;
- cleans up only its own external temporary file after failure/cancellation;
- translates failures to structured Application outcomes;
- never creates a report/cache/temp file inside the Calibre library.

The serializer supports round-trip integration tests. Milestone 5 has no user-facing import workflow.

## WPF changes

Extend the Metadata candidates workflow rather than redesigning the window.

### Group navigation and summary

- Reuse current text filtering and previous/next navigation.
- Add recommendation confidence, review status, freshness, unresolved-warning count, and overridden indicator to lightweight group rows.
- Replace the old defer-only dictionary with a session review-state dictionary while preserving current defer behavior for unchanged inputs.
- Keep group lists virtualized and publish with one reset.

### Selected-group comparison

Add a detail area, using a compact nested tab or equivalent, containing:

- a virtualized record comparison grid with record ID, title, authors, author sort, identifiers, publisher, publication date, language, series/index, cover flag, available formats, file statuses, retained-separate state, and metadata-quality facts;
- a generated metadata-source column and a reviewed metadata-source selector containing only valid current member choices;
- a per-format grid with all source candidates, generated source, reviewed source selector, exact-binary indicator, EPUB status/score/version, decisive findings, resolution state, excluded alternatives, and warnings;
- reasons and warnings grids with stable code, subject, explanation, and bounded evidence;
- proposed redundant and retained-separate lists using explicitly non-destructive wording.

Record rows can expose commands/checkboxes to retain one or more records separately. Format rows expose select-source, mark-unresolved, and explicit-exclude actions. A null/unresolved choice must be textually distinct from an unavailable format.

### Review actions

Add commands/buttons for:

- accept generated recommendation;
- save a manually adjusted review;
- defer;
- keep all records separate;
- mark not duplicates;
- reset to generated recommendation;
- export JSON.

Use labels and text, not color alone, to distinguish `Generated`, `Overridden`, `Stale`, `Unresolved`, `Retained separately`, and `Explicitly excluded by user`. Invalid actions show actionable validation messages and do not update session state.

### Keyboard and accessibility

- Preserve `Ctrl+N`, `Ctrl+P`, and `Ctrl+D` group navigation/defer behavior.
- Add documented shortcuts for accept, keep separate, reset, and export only after checking conflicts with DataGrid/TextBox behavior.
- Preserve standard DataGrid arrow/Page/Home/End navigation and accessible button alternatives.
- Add access keys, automation names/help text, explicit tab order where needed, resizable columns/panes, high-DPI-friendly layout, and text wrapping.
- Explicitly enable row/column virtualization and recycling on large grids.
- Keep code-behind limited to initialization/view-only behavior.

### Export file selection

Add a WPF-only `IRecommendationExportFilePicker` implemented with the standard Save dialog. It supplies a user-selected `.json` path and handles overwrite confirmation. The ViewModel passes the path to the Application use case and never writes files directly.

## Deterministic ordering rules

- Duplicate groups: existing normalized title ordinal, normalized author sequence ordinal, then group ID ordinal.
- Group members and metadata candidates: record ID ascending for base order; policy comparison creates a separate explicit rank order.
- Metadata rank ties: record ID ascending only as the final non-quality fallback.
- Canonical formats: invariant uppercase, ordinal ascending.
- Format candidates: record ID, format, expected relative path ordinal.
- Exact-binary alternatives: metadata-source record first only for source choice, then record ID/path; evidence collections retain canonical candidate order.
- Equal/close non-identical EPUBs: no generated source; candidate display order by record ID/path.
- EPUB decisive findings: rule ID, evidence key, explanation ordinal after the assessment's validated association/version checks.
- Proposed exclusions: format, record ID, relative path, reason code.
- Proposed redundant/retained-separate records: record ID.
- Reasons/warnings: subject kind, format, nullable record ID, stable code, evidence key, explanation ordinal.
- Overrides: canonical format then action/source record ID; retained-separate records by ID.
- JSON properties: explicitly written fixed schema order.
- JSON groups and every nested collection: the canonical orders above.
- All string comparison used for policy identity/order is ordinal or explicitly invariant. Current culture, dictionary enumeration, task completion, and runtime hash codes never affect output.

Stable Calibre record ID ordering is never described as quality evidence.

## Performance considerations

- Build snapshot-wide dictionaries for books, assessments, fingerprints, exact-binary memberships, and relevant findings once: O(books + formats + assessments + groups).
- Process metadata groups incrementally with a fixed result array and cancellation/progress per group. Recommendation generation is CPU/memory policy work and performs no I/O.
- Avoid pairwise ebook comparisons. Different-edition consensus is built from normalized value indexes; a bounded per-group pass identifies outliers.
- Pathological very large groups must remain cancellation-aware and avoid O(member-squared) comparisons where a value-index/consensus pass is sufficient.
- Reuse Milestone 4 assessment objects and decisive findings. Do not reopen EPUBs or recalculate scores.
- Reuse SHA-256 and exact-group data. Do not rehash formats.
- Materialize lightweight WPF group rows in bulk. Create record comparison, format candidate, and finding rows only for the selected group.
- Explicit WPF virtualization/recycling prevents thousands of recommendation rows from creating controls eagerly.
- Cache only immutable in-memory presentation/index data for the current snapshot. No persistent cache is added.
- A local override validates and rebuilds only the selected group's effective reviewed selection. It does not rerun scan, hashing, assessment, grouping, or generated recommendation policy.
- Serialize each export once in canonical order and stream/write outside the library. Do not repeatedly sort already canonical immutable collections.
- Add a generated scale test with thousands of small groups and at least one large group; assert count/order/cancellation and bounded call concurrency without brittle wall-clock thresholds.

## Files expected to change

Names can be consolidated where the repository favors cohesive files over one type per trivial enum/record. Any material deviation must be recorded in this plan before implementation.

### Documentation

- `docs/plans/milestone-5-consolidation-recommendations.md`
- `docs/domain-model.md` — recommendation/review/input-version invariants and additional stored metadata.
- `docs/architecture.md` — recommendation-generation and JSON-export boundaries.
- `docs/quality-scoring.md` — clarify the allowed, version-compatible, findings-supported use of EPUB scores by recommendations.
- `docs/safety-and-rollback.md` — distinguish a Milestone 5 review export from a Milestone 6 cleanup plan.
- `docs/duplicate-detection.md` — clarify that recommendations combine independent evidence without changing group definitions.
- `docs/adr/0005-versioned-recommendation-review-artifact.md` — proposed ADR if accepted as described below.

### Domain

- `src/CalibreLibraryCleaner.Domain/Libraries/CalibreBook.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/LibrarySnapshot.cs`
- `src/CalibreLibraryCleaner.Domain/Libraries/BookPublicationMetadata.cs` — new cohesive stored metadata value.
- `src/CalibreLibraryCleaner.Domain/Recommendations/RecommendationValues.cs` — new versions/enums/strong values.
- `src/CalibreLibraryCleaner.Domain/Recommendations/RecommendationInputVersion.cs`
- `src/CalibreLibraryCleaner.Domain/Recommendations/RecommendationReason.cs`
- `src/CalibreLibraryCleaner.Domain/Recommendations/RecommendationWarning.cs`
- `src/CalibreLibraryCleaner.Domain/Recommendations/MetadataSourceSelection.cs`
- `src/CalibreLibraryCleaner.Domain/Recommendations/FormatSourceSelection.cs`
- `src/CalibreLibraryCleaner.Domain/Recommendations/RecordRecommendation.cs`
- `src/CalibreLibraryCleaner.Domain/Recommendations/ConsolidationRecommendation.cs`
- `src/CalibreLibraryCleaner.Domain/Recommendations/UserRecommendationOverride.cs`
- `src/CalibreLibraryCleaner.Domain/Recommendations/ReviewedConsolidationRecommendation.cs`
- `src/CalibreLibraryCleaner.Domain/Recommendations/ConsolidationRecommendationPolicy.cs`
- `src/CalibreLibraryCleaner.Domain/Recommendations/MetadataSourceSelectionPolicy.cs`
- `src/CalibreLibraryCleaner.Domain/Recommendations/FormatSourceSelectionPolicy.cs`
- `src/CalibreLibraryCleaner.Domain/Recommendations/RecommendationConfidencePolicy.cs`

### Application

- `src/CalibreLibraryCleaner.Application/Libraries/CalibreCatalogRecord.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryScanPhase.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/LibraryErrorCode.cs`
- `src/CalibreLibraryCleaner.Application/Libraries/ScanLibraryUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Abstractions/IRecommendationExporter.cs`
- `src/CalibreLibraryCleaner.Application/Recommendations/RecommendationGenerationProgress.cs`
- `src/CalibreLibraryCleaner.Application/Recommendations/GenerateConsolidationRecommendationsUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Recommendations/ApplyRecommendationOverrideUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Recommendations/RecommendationExportContracts.cs`
- `src/CalibreLibraryCleaner.Application/Recommendations/ExportRecommendationsUseCase.cs`
- `src/CalibreLibraryCleaner.Application/Recommendations/RecommendationExportStalenessEvaluator.cs`

### Infrastructure

- `src/CalibreLibraryCleaner.Infrastructure/Sqlite/CalibreSchemaContract.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Sqlite/SqliteCalibreMetadataReader.cs`
- `src/CalibreLibraryCleaner.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Recommendations/RecommendationJsonSerializer.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Recommendations/VersionedJsonRecommendationExporter.cs`
- `src/CalibreLibraryCleaner.Infrastructure/Recommendations/RecommendationExportPathGuard.cs`

No existing hashing or EPUB-inspector production file is expected to change.

### WPF

- `src/CalibreLibraryCleaner.Wpf/App.xaml.cs`
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MainWindowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MetadataDuplicateGroupRowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/MetadataDuplicateMemberRowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/RecommendationFormatRowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/RecommendationSourceOptionViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/RecommendationReasonRowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/ViewModels/RecommendationWarningRowViewModel.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/IRecommendationExportFilePicker.cs`
- `src/CalibreLibraryCleaner.Wpf/Services/SaveFileDialogRecommendationExportFilePicker.cs`

### Tests and fixtures

- `tests/CalibreLibraryCleaner.Domain.Tests/Libraries/LibrarySnapshotTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Libraries/LibraryValueTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Recommendations/MetadataSourceSelectionPolicyTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Recommendations/FormatSourceSelectionPolicyTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Recommendations/ConsolidationRecommendationTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Recommendations/RecommendationConfidencePolicyTests.cs`
- `tests/CalibreLibraryCleaner.Domain.Tests/Recommendations/UserRecommendationOverrideTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Libraries/ScanLibraryUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Recommendations/GenerateConsolidationRecommendationsUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Recommendations/ApplyRecommendationOverrideUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Application.Tests/Recommendations/ExportRecommendationsUseCaseTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/SyntheticCalibreLibrary.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Fixtures/TestServices.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Sqlite/SqliteCalibreMetadataReaderTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Recommendations/RecommendationJsonSerializerTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Recommendations/VersionedJsonRecommendationExporterTests.cs`
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/Safety/ReadOnlyLibraryScanSafetyTests.cs`
- `tests/CalibreLibraryCleaner.Architecture.Tests/DependencyDirectionTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/ViewModels/MainWindowViewModelTests.cs`
- `tests/CalibreLibraryCleaner.Wpf.Tests/MainWindowTests.cs`

Existing EPUB/hash/duplicate tests remain and may need only mechanical constructor/fixture updates for the additional immutable metadata/recommendation collection.

## Package changes

No new package is expected.

- Use existing .NET 10 BCL and `System.Text.Json` in Infrastructure.
- Use the existing WPF Save dialog API in the presentation project.
- Add no package to Domain or Application.
- Add no database-write, PDF, ebook-analysis, AI, networking, or Calibre CLI package.

## Possible ADRs

Propose ADR 0005, `Version deterministic recommendation review artifacts`, before implementing JSON. It should record:

- generated recommendation versus reviewed override separation;
- session-only override persistence plus explicit external JSON export;
- schema/model/input version separation;
- stale override behavior;
- deterministic serialization and sanitized path/content policy;
- review artifact versus Milestone 6 cleanup-plan boundary;
- exporter path guard forbidding writes inside the Calibre library.

The recommendation policy itself can remain in this plan, Domain tests, and updated domain/scoring documentation. Create a separate policy ADR only if review changes the 10-point EPUB threshold, edition-blocker semantics, or redundancy safety rule in a way that needs a long-lived cross-milestone decision.

Do not create a cleanup-plan, execution, backup, rollback, AI, PDF, or content-fingerprint ADR in Milestone 5.

## Safety considerations

- `metadata.db` remains strictly read-only/query-only. Added metadata fields use only fixed `SELECT` statements and schema inspection.
- Recommendation generation consumes immutable in-memory data and performs no filesystem, SQLite, parser, process, or network access.
- No recommendation calls Calibre CLI or any mutation service.
- No generated recommendation excludes a unique or unresolved non-identical format.
- A user exclusion is explicit, warned, serialized, reversible by reset, and still not a mutation instruction.
- Missing/inaccessible/changed/invalid formats block redundant-record classification.
- Exact binary evidence remains scoped to its file references; it cannot classify unrelated formats or a whole record by itself.
- A non-identical unassessed same-format conflict always remains unresolved until review.
- A non-identical EPUB preference never claims equivalent text/content and requires compatible current assessments plus decisive findings.
- Retained-separate records and all their formats remain outside the conceptual consolidation cohort.
- UI/export use `proposed redundant record`, `proposed retained format`, `unresolved same-format conflict`, and `manual review required`; do not use `will delete`, `safe to delete`, or mutation language.
- Export omits the absolute library root and all full format paths.
- Infrastructure rejects export writes inside the library and cleans up only its own external temporary file.
- Export contains no removals, command arguments, action ordering, approval, or expected pre-operation state.
- Synthetic fixtures remain in test-owned temporary directories; automated tests never use a real library.
- Existing recursive before/after library manifests must remain byte/name/path/attribute/timestamp identical across generation, override, export, failure, and cancellation scenarios.

## Tests

### Metadata selection

- One record with usable core fields and strictly greater fixed-field completeness is selected and every distinguishing reason is present.
- Equal metadata comparison vectors use lowest record ID deterministically and label it as a fallback, not quality.
- Blank/frozen-placeholder title, author, author sort, publisher, and identifiers are treated exactly as documented without rewriting stored values.
- Conflicting valid ISBNs and each supported strong identifier type produce blocking warnings and no silent completeness preference.
- Conflicting languages invoke the keep-separate/manual-review rule.
- Unequal publication years warn; a gap at the configured boundary blocks automatic consolidation.
- Conflicting series names and same-series/different-index values require manual review.
- Missing values lower completeness but do not become conflicts.
- No usable metadata candidate yields `Unsupported`, a null metadata source, and blocking reason/warning.
- Consistency facts cannot outweigh a blocking conflict.
- Shuffled candidate input produces identical ranks, source, reasons, warnings, and confidence.

### Format selection

- One present EPUB only is retained; a sole disqualified EPUB remains retained with warning.
- Two byte-identical EPUBs select the documented deterministic source and explain exact equality.
- Completed valid EPUB versus disqualified EPUB selects the completed source and exposes the disqualifier.
- A score difference exactly at and just below 10 exercises the configurable boundary.
- A materially higher score without a decisive structural/readability finding remains unresolved.
- A materially higher score with decisive support selects the stronger EPUB and exposes score difference/rules.
- Close scores require manual review.
- Equal scores with different findings remain unresolved and deterministically ordered.
- Conflicting decisive findings remain unresolved even when total scores differ.
- Missing EPUB file, stale fingerprint association, stale assessment, incompatible analyzer version, and incompatible scoring-model version warn and do not produce an unsupported winner.
- One unique AZW3 is retained.
- Two byte-identical AZW3 files select one safe source.
- Two non-identical AZW3 files remain unresolved with all candidates.
- One unique PDF is retained.
- Two non-identical PDFs remain unresolved with all candidates.
- MOBI/other unassessed formats follow the same policy.
- Multiple distinct formats are combined from different records.
- An exact-binary subset plus a third non-identical candidate preserves the third conflict.
- Size/timestamp/filename/record ID never selects between non-identical alternatives.

### Recommendation behavior

- Recommendations follow deterministic group/member/format/reason/warning ordering for shuffled inputs.
- Every selected metadata/format source has a linked reason.
- Every unresolved conflict has a manual-review warning.
- Decision strength and overall confidence classifications follow the documented evidence tables.
- Strong consensus can retain a conflicting outlier record separately; a two-record conflict keeps both separate.
- No record is proposed redundant when it contains a unique, unavailable, retained-separate, or unresolved format.
- No unassessed non-identical format appears in generated exclusions.
- Binary duplicate evidence for one format does not classify other unique formats or the whole record.
- A record contributing a selected non-identical format is not proposed redundant.
- Relevant metadata/file/assessment/group/model changes produce a different input version and recommendation or stale classification.
- `ScannedAt`/export timestamp and input enumeration order do not change input version.
- Cancellation during indexing, member/format policy, sorting, or final publication returns no partial successful snapshot.
- Thousands of groups and a large group preserve order/count and remain cancellation-aware.

### Overrides

- Replace metadata source with another valid member.
- Replace EPUB source with another present EPUB candidate.
- Replace another-format source, mark conflict unresolved, and explicitly exclude a format.
- Retain one/multiple records separately and reject a selected source on a retained-separate record.
- Accept unchanged recommendation.
- Effective changes become `ManuallyAdjusted`.
- Defer with and without a valid draft override.
- Mark `KeepSeparate` and `NotDuplicates` without losing the generated recommendation.
- Reject record IDs outside the group, absent formats, missing-file selections, incompatible source/format pairs, and contradictory states.
- Reset removes current/stale overrides and restores generated selections.
- Generated recommendation remains reference/value unchanged after every override.
- Identical rescans reapply session overrides.
- Changed input preserves stale override visibly but does not apply it.
- Changing only an override causes no scan/hash/assessment/recommendation-generation call.

### JSON serialization/export

- Fixed schema and recommendation-model versions are present.
- Source library UUID/schema are present and root is absent.
- Duplicate-group identity, members, input versions, generated recommendation, reasons, warnings, confidence, override, final reviewed selection, status, freshness, and timestamps round-trip.
- Same inputs and fixed clock produce byte-identical UTF-8 output.
- Shuffled dictionaries/source collections produce identical canonical output.
- Enum values, property order, collection order, numeric/date formatting, and newline/indent policy are exact.
- No absolute path, external path, chapter prose, raw exception, parser type, or book text content appears.
- No mutation/removal/Calibre command/cleanup-plan instruction appears.
- Current versus changed input version detects stale round-tripped artifacts.
- Unknown schema/model version and malformed JSON return controlled failures.
- Association/invariant violations are rejected on deserialize.
- Pre-cancellation and mid-write cancellation leave no published partial artifact.
- Serialization/write failures return structured outcomes and clean only the external temp file.
- Destination at/under the library, including reparse redirection, is rejected.
- Destination outside the library succeeds and does not change any library entry.
- User-confirmed overwrite affects only the selected external artifact.

### Infrastructure/read-only integration

- Schema-27 reader loads publisher, publication date, series/index, languages in link order, and cover flag with existing fields.
- Missing required new schema columns/tables fail closed as unsupported schema.
- Malformed optional values produce controlled findings where safe.
- Broken/ambiguous publisher, series, and language links follow the documented failure/finding policy.
- A synthetic end-to-end library produces cross-record metadata/EPUB/AZW3/PDF recommendations through real SQLite/path/hash/EPUB components without mutation.
- Existing schema, path, hash, EPUB, duplicate, and cancellation tests remain green.

### WPF tests

- Group rows display confidence, review status, freshness, warning counts, and override distinction.
- Selected record comparison exposes all planned stored metadata and file/assessment context.
- Metadata and per-format selectors offer only valid current candidates.
- Exact indicators, EPUB scores/status/versions/decisive findings, and unresolved warnings display separately.
- Accept, adjust, defer, keep separate, not duplicates, reset, and export commands update only session/review state.
- Invalid override errors are actionable and leave prior reviewed state unchanged.
- Generated and overridden values remain textually distinguishable without relying on color.
- Stale override banner disables applying/exporting a stale final selection while retaining stale details.
- Filtering/navigation remain keyboard-efficient and operate on the visible group list.
- Detail rows are materialized on demand and group/detail collections publish in bulk.
- DataGrid virtualization properties and XAML bindings activate in the STA window construction/show/close test.
- Save dialog cancellation makes no exporter call; successful selection passes the path to Application only.
- Failed/canceled scan or export leaves previous displayed snapshot/review state explicitly identified and retryable.

### Architecture and safety tests

- Production project-reference direction remains unchanged.
- Domain has no SQLite, filesystem, JSON, WPF, parser, logging, or DI reference/type.
- Application references only Domain and contains no concrete JSON/filesystem/WPF/parser type.
- `System.Text.Json` and external file writing are confined to Infrastructure.
- WPF ViewModels contain no filesystem, JSON, SQLite, parser, or Infrastructure reference.
- Infrastructure use in WPF remains confined to `App.xaml.cs`.
- No EPUB-library type leaks from Infrastructure.
- No metadata database or Calibre-managed file mutation API/statement is added.
- No cleanup-plan, deletion, merge, backup, rollback, Calibre CLI, PDF analyzer, content similarity, fuzzy matcher, AI, or network code is introduced.
- Strict recursive manifests prove generation, override, export, failure, and cancellation do not change the synthetic library or create SQLite/report/temp sidecars inside it.

## Incremental implementation steps

1. Resolve the proposed defaults in `Unresolved questions`, record decisions here, and write ADR 0005 if accepted before JSON implementation.
2. Verify the exact additional metadata shape against pinned schema-27 Calibre sources. Extend synthetic schema fixtures, schema contract, provider-neutral catalog records, reader queries, Domain stored metadata, and focused read-only tests.
3. Add recommendation version/status/reason/warning/selection/record-disposition aggregates and invariant tests in Domain.
4. Implement the canonical input-version builder and different-edition/consensus policy with direct strong-identifier/language/year/edition/series tests.
5. Implement metadata comparison and source-selection policy with named vectors, reasons, placeholders, deterministic tie fallback, and tests.
6. Implement common/exact/EPUB/unassessed format policies, the 10-point-plus-decisive rule, exclusions/unresolved invariants, and tests.
7. Implement record-disposition and qualitative confidence policies; add cross-signal tests proving unique/unresolved formats cannot be hidden or marked redundant.
8. Add `GenerateConsolidationRecommendationsUseCase`, progress/error contracts, cancellation, snapshot publication, and scale tests. Connect it after Milestone 4 assessments/grouping without rehash/reassessment.
9. Add immutable override/review models and `ApplyRecommendationOverrideUseCase`; replace WPF's defer-only session state with versioned review state while preserving unchanged-input defer behavior.
10. Add provider-neutral export contracts/use case and ADR-backed versioned DTO mapping.
11. Implement deterministic `System.Text.Json` round-trip serialization, guarded external atomic writing, path/reparse validation, structured failures, and serialization/safety integration tests.
12. Extend the metadata-candidate WPF tab with lightweight recommendation summary, selected-group comparison, per-format selection, reasons/warnings, review commands, reset, export picker, accessibility, virtualization, and focused UI tests.
13. Extend architecture/source guards and strict before/after safety manifests for recommendation generation, override, export success/failure, and cancellation.
14. Run focused suites while iterating, then the complete verification sequence, package audit, WPF startup/STA tests, optional interactive synthetic-library keyboard/high-DPI/accessibility review, and full diff/safety review.
15. Update this plan with deviations, failed approaches, exact results, remaining risks, and final outcome.

Each step must leave the relevant projects buildable/testable. Do not proceed from recommendation export into cleanup-plan generation.

## Verification commands

Focused commands during implementation:

```powershell
dotnet test tests/CalibreLibraryCleaner.Domain.Tests/CalibreLibraryCleaner.Domain.Tests.csproj --filter "FullyQualifiedName~Recommendation"
dotnet test tests/CalibreLibraryCleaner.Application.Tests/CalibreLibraryCleaner.Application.Tests.csproj --filter "FullyQualifiedName~Recommendation|FullyQualifiedName~ScanLibraryUseCase"
dotnet test tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj --filter "FullyQualifiedName~Recommendation|FullyQualifiedName~MetadataReader|FullyQualifiedName~Safety"
dotnet test tests/CalibreLibraryCleaner.Wpf.Tests/CalibreLibraryCleaner.Wpf.Tests.csproj
dotnet test tests/CalibreLibraryCleaner.Architecture.Tests/CalibreLibraryCleaner.Architecture.Tests.csproj
```

Required final sequence:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
dotnet list package --vulnerable --include-transitive
```

Repository/safety review:

```powershell
git status --short
git diff --check
git diff --stat HEAD
git diff HEAD
rg -n --glob 'src/**/*.cs' 'CommandText\s*=|SqliteOpenMode|query_only' src
rg -n --glob 'src/**/*.cs' -e 'File\.(Write|Delete|Move|Copy|Create|OpenWrite|Replace)' -e 'Directory\.(Create|Delete|Move)' -e 'FileMode\.(Create|Append|OpenOrCreate|Truncate)' -e '\b(INSERT|UPDATE|DELETE|DROP|ALTER|REPLACE|VACUUM|ATTACH|DETACH)\b' -e 'calibredb|CleanupPlan|merge|delete record|PdfPig|Levenshtein|Jaro|OpenAI|HttpClient' src
```

Review every mutable filesystem match. Only the guarded recommendation exporter may write, and only outside the selected library. Fixture builders may mutate only test-owned temporary directories.

Do not claim command success until it actually completes. Record exact test counts, warnings/errors, serialization/safety results, process/interactive limitations, and diff findings in `Progress` and `Final outcome`.

## Risks

- Exact metadata groups can contain different editions. Conservative blockers reduce risk but incomplete or incorrect stored metadata can still hide differences.
- The additional schema projection increases the supported schema contract surface. It must be verified against schema 27 and fail closed rather than infer missing relations.
- Identifier normalization can misclassify malformed vendor data. The V1 registry is frozen, local, validated, and advisory; unknown identifiers are not promoted.
- Language/year/series values may be missing or wrong. Missing data lowers confidence and prevents deterministic claims; it is not silently filled.
- Edition wording detection is intentionally narrow and can miss uncommon wording. It only adds warnings/separation and never rewrites/group-normalizes titles.
- The 10-point EPUB threshold is a policy choice. Requiring compatible versions and decisive structural/readability findings limits false precision, but generic EPUB quality still cannot prove edition/content equivalence.
- A valid but lower-scoring EPUB may contain a different or desired edition. Different-edition warnings and reviewability remain mandatory, and no content-equivalence claim is made.
- Proposed redundant-record classification can be misunderstood as deletion advice. Strict evidence, non-destructive wording, UI warnings, and the absence of mutation/cleanup models are safety controls.
- Session-only review state may surprise users. The UI must say it is not saved except by explicit JSON export.
- Staleness is detected when current inputs are supplied/regenerated, not by continuously monitoring the filesystem. Full cleanup precondition validation is intentionally deferred.
- Deterministic JSON with provenance timestamps is byte-identical only when timestamps are fixed/equal. Tests inject time and document the intentional real-export difference.
- External atomic replacement semantics and reparse-point handling vary by filesystem. Fail closed when destination safety cannot be established.
- Thousands of groups can increase memory for recommendations and WPF state. Canonical indexes, selected-detail materialization, virtualization, and scale tests mitigate this.
- Extending the already large `MainWindowViewModel` can reduce maintainability. Extract cohesive presentation/session helpers if needed without introducing business logic into WPF or redesigning the shell.
- The working tree contains unrelated IDE/agent state that must be preserved and excluded from implementation diff claims.

## Unresolved questions

These are proposed defaults for approval before implementation; none authorizes broader scope.

1. **Additional stored metadata projection:** Proposed answer: load publisher, publication date, series/index, languages, and Calibre's cover flag read-only because the requested Milestone 5 safety rules cannot be implemented from the current projection without them.
2. **Clear EPUB preference threshold:** Proposed answer: 10 points, plus compatible current versions and at least one decisive structural/readability finding with no countervailing decisive error. Keep it code-configurable/test-overridable, with no settings UI.
3. **Material publication-year difference:** Proposed answer: at least two years is blocking; any unequal known year warns.
4. **Strong identifier registry:** Proposed answer: validated ISBN, DOI, ASIN, and OCLC only for V1. Unknown types remain context.
5. **Placeholder policy:** Proposed answer: use only the small frozen token set documented above; treat `ANONYMOUS` and localized nonblank values as ordinary stored metadata.
6. **Different-edition consensus:** Proposed answer: require at least two agreeing known records before isolating an outlier. Ambiguous/two-record conflicts keep all affected records separate.
7. **Override persistence:** Proposed answer: session memory only, plus explicit user-triggered JSON export outside the library. No automatic persistence/database.
8. **Stale overrides:** Proposed answer: retain visibly but invalidate; never silently reapply to changed inputs.
9. **Export overwrite:** Proposed answer: standard Save dialog confirmation, then guarded atomic replacement outside the library only.
10. **Proposed redundant record:** Proposed answer: require at least one available format and exact-binary coverage of every available format, with no selected contribution, missing/unavailable file, unresolved conflict, retained-separate evidence, or metadata warning that could hide uniqueness.

If review changes any default that affects recommendation outcomes, update the model version decision and this plan before production edits.

## Milestone boundary and safety review

This plan has been reviewed against the Milestone 5 section of `docs/roadmap.md`.

- It chooses metadata and format sources independently.
- It supports formats from different records.
- It warns about exact, EPUB, unassessed, metadata, edition, and incomplete-input conflicts.
- It preserves generated recommendations while supporting validated overrides and all requested review statuses.
- It exports deterministic, versioned JSON as a review artifact.
- It introduces no cleanup plan, expected mutation state, plan approval/import, backup, Calibre command, mutation, verification, or rollback behavior from Milestone 6 or later.

No generated recommendation can silently discard a unique or unresolved format:

- unique present formats are proposed retained;
- exact duplicates can exclude only byte-identical same-format alternatives;
- non-identical unassessed alternatives remain unresolved;
- close/conflicting/incomparable EPUB alternatives remain unresolved;
- unavailable files block redundancy and produce warnings;
- explicit user exclusion is preserved as a visible override with warning and full source evidence.

All recommendations remain non-destructive and reviewable:

- generated and overridden selections are separate;
- every selection/conflict has reasons or warnings;
- stale overrides are not applied;
- JSON contains no mutation instructions;
- no Calibre database/file/CLI mutation boundary is added.

## Progress

### Completed-implementation review remediation (second pass)

- [x] Reopen the living plan after the completed Milestone 5 implementation review.
- [x] Prevent retained-separate records and their formats from disappearing from effective review state.
- [x] Validate effective reviewed selections before export and reject inconsistent or unsafe artifact graphs.
- [x] Complete canonical input identity, preserve raw stored publication metadata, and correct conflict semantics.
- [x] Correct empty-format confidence, deferred-draft behavior, and review presentation.
- [x] Export and display the documented metadata/record/EPUB decision evidence.
- [x] Add focused regressions for every reviewed defect and rerun the full verification sequence.

The remediation remains within Milestone 5 and adds no cleanup plan, mutation, backup, rollback, or Calibre CLI behavior. Because it changes input identity, conflict interpretation, record disposition, and confidence behavior, the recommendation model advances to `consolidation-recommendation/1.0.2` and the canonical input policy advances to `recommendation-input/1.0.1`. Existing `1.0.1` overrides must become stale rather than being silently reapplied.

Second-pass remediation outcome on 2026-07-18:

- Retained-separate records remain retained in every effective review state and cannot supply selected metadata or formats. Fully separated and formatless groups now fail closed with manual review and explicit warnings.
- Redundancy still requires exact-binary coverage of every present format. Unavailable, unique, unresolved, or retained-separate contributions prevent whole-record redundancy.
- Canonical input identity now includes observed assessment fingerprints and deterministically ordered evidence. Raw publication values are preserved for identity, while comparison normalization remains policy-owned. Strong identifiers conflict only when valid per-record sets are disjoint, and a missing series index is not treated as a contradiction.
- Current overrides are structurally re-derived before serialization. Forged effective selections, invalid associations, blank format keys, and unsafe paths are rejected; stale overrides remain visible but cannot apply.
- Generated metadata/record decisions and bounded EPUB comparison evidence are present in JSON, while the WPF review exposes metadata comparison facts and distinguishes unavailable formats from unresolved choices. Deferring preserves the current draft override.
- Recommendation model version advanced to `consolidation-recommendation/1.0.2` and input-policy version to `recommendation-input/1.0.1`, intentionally making prior session overrides stale.
- Final verification passed: restore; build with zero warnings/errors; 249 tests (Domain 88, Application 45, Infrastructure 87, Architecture 16, WPF 13); formatting verification; package vulnerability audit; diff and production mutation/scope scans.

### Post-completion review remediation

- [x] Reopen the plan after the read-only Milestone 5 review.
- [x] Fail closed when exact-binary evidence does not match current candidate fingerprints.
- [x] Require decisive disqualifying EPUB evidence and expose the evidence used for EPUB preferences.
- [x] Complete metadata conflict/warning inputs and structured override validation.
- [x] Resolve physical export paths for both the library and destination and validate the complete JSON artifact graph.
- [x] Keep stale override choices visibly separate from current effective selections.
- [x] Add focused regression coverage and rerun the required verification sequence.

This remediation remains within Milestone 5. It changes recommendation validation, explanation, review presentation, and analysis-artifact safety only; it adds no cleanup plan, mutation, backup, rollback, or Calibre CLI behavior.

Remediation outcome on 2026-07-18:

- Exact-binary exclusions and redundant-record conclusions now require matching current fingerprints and valid candidate associations. Inconsistent binary or assessment associations produce an `Unsupported` shell.
- Completed-versus-disqualified EPUB selection now requires a frozen decisive disqualifying rule. EPUB preference reasons carry bounded scores, statuses, analyzer/scoring versions, decisive rule IDs, adjustments, and explanations.
- Metadata conflict counts, invalid strong-identifier warnings, placeholder warnings, and substantially different format-set warnings now participate in the documented comparison/confidence policy.
- Undefined review statuses/actions return structured validation errors. Export accepts only the current generated recommendation instances and validates the complete member/candidate/override/effective-selection graph.
- Export containment compares the resolved physical library root against the destination and rejects library-root aliases. Rooted or traversing managed paths cannot be serialized.
- Stale override choices remain visible in a separate read-only summary while their effective selection remains null.
- Recommendation model version advanced to `consolidation-recommendation/1.0.1`, intentionally making `1.0.0` session overrides stale.
- Final verification passed: restore; build with zero warnings/errors; 236 tests (Domain 80, Application 44, Infrastructure 85, Architecture 16, WPF 11); formatting verification; package vulnerability audit; diff and production mutation/scope scans.

- [x] Read root `AGENTS.md`, `PLANS.md`, and every nested `AGENTS.md` under `src/` and `tests/`.
- [x] Read all requested product, functional, architecture, domain, duplicate, scoring, safety, test, roadmap, and workflow documents.
- [x] Read every accepted ADR under `docs/adr/`.
- [x] Read the completed Milestone 0, 1, 2, 3, and 4 plans, including recorded decisions, deviations, risks, and final outcomes.
- [x] Inspect current solution/package configuration, Domain/Application/Infrastructure/WPF code, XAML, representative unit/integration/architecture/safety/UI tests, fixtures, and working-tree state.
- [x] Identify the current metadata projection gap and plan the minimum read-only schema-27 extension needed for Milestone 5 safeguards.
- [x] Draft the Milestone 5-only execution plan.
- [x] Review the plan against `docs/roadmap.md` Milestone 5.
- [x] Confirm no Milestone 6 or later cleanup-plan/execution behavior is included.
- [x] Confirm no generated recommendation can silently discard a unique or unresolved format.
- [x] Confirm all recommendations/overrides remain non-destructive and reviewable.
- [x] Resolve proposed defaults before implementation. The user explicitly approved every proposed answer on 2026-07-18; the initial versions remain `recommendation-review/1.0` and `consolidation-recommendation/1.0.0`.
- [x] Implement Milestone 5. Implementation started on 2026-07-18 after re-reading the governing documents, accepted ADRs, nested instructions, prior milestone outcomes, and current working-tree state.
- [x] Run verification and record results.
- [ ] Complete interactive synthetic-library review where available. No interactive desktop session was used; the STA WPF construction/show/close test and focused ViewModel tests passed.
- [x] Review final diff and update final outcome.

Implementation notes:

- The exact additional schema shape was rechecked against the installed Calibre `metadata_sqlite.sql`: `books.pubdate`, `books.series_index`, `books.has_cover`; single publisher/series links; and language links ordered by `item_order` then link ID. Production access remains fixed-query/read-only.
- The first build checkpoint exposed analyzer-only return/parameter-shape issues in new pure helpers. Types were narrowed without suppressing analyzers.
- The first full test run after the schema extension found the pre-existing WPF-only synthetic schema was incomplete and the architecture file-hashing guard could not distinguish recommendation input-identity hashing from ebook file hashing. The fixture was extended and the guard was narrowed to continue forbidding file hashing outside Infrastructure while permitting canonical in-memory recommendation identity hashing.
- The first deterministic JSON test exposed platform newline behavior from `Utf8JsonWriter`; output is now normalized to LF and terminated by exactly one LF.
- The first required final sequence failed two newly added regressions: accepting an all-retained-separate result was rejected as contradictory, and the cancellation status test observed the transient `Canceling scan...` text. Effective consolidation is now null when every record is retained separately, generated dispositions remain intact, and cancellation text is immediately neutral. Both focused tests passed before the complete sequence was rerun.
- Complete diff/safety review found only the guarded exporter uses mutable production filesystem APIs (`CreateNew`, external temporary-file move, and cleanup of its own external temporary file). SQL review found only the existing read-only connection/query-only setup and fixed `SELECT`/read-only `PRAGMA` statements.

## Final outcome

Completed on 2026-07-18.

- Added deterministic Milestone 5 recommendations for every exact normalized metadata group in the completed snapshot. Metadata sources and per-format sources are independent and may come from different records. Generated selections carry stable reasons, warnings, decision strength, qualitative confidence, model version, and canonical input version.
- Reused Milestone 2 exact-binary groups, Milestone 3 metadata groups, and current Milestone 4 EPUB assessments without rehashing, reassessment, parser access, or content-equivalence claims. Exact byte identity is the only generated exclusion basis.
- Implemented the accepted 10-point EPUB preference threshold with compatible analyzer/scoring versions, matching current fingerprints, a decisive structural/readability advantage of at least four applied points, and no countervailing decisive error. Completed-versus-disqualified EPUB evidence is also supported. Close/equal/contradictory/stale/incomparable EPUBs remain unresolved.
- Non-identical AZW3, PDF, MOBI, and other unassessed same-format alternatives remain unresolved with every candidate preserved. Missing/inaccessible/changed/invalid candidates remain visible and block redundant-record conclusions. Potentially redundant records require at least one present format and exact-binary coverage of every available format, with no selected, unique, unavailable, unresolved, or retained-separate contribution.
- Added conservative strong-identifier, disjoint-language, material-year (two-year), edition-wording, and series/index safeguards; documented metadata comparison vectors; and record-ID-only final tie breaking explicitly labeled as non-quality fallback.
- Added immutable generated/reviewed separation, validated metadata/format/retain-separate overrides, explicit user-exclusion warnings, session review states (`Accepted`, `ManuallyAdjusted`, `Deferred`, `KeepSeparate`, `NotDuplicates`), reset, canonical staleness detection, and stale-override invalidation.
- Extended the schema-27 projection read-only with publication date, publisher, series/index, ordered languages, and stored cover flag. Added ADR 0005 for the versioned review-artifact boundary.
- Added deterministic UTF-8/LF JSON schema `recommendation-review/1.0` with current model `consolidation-recommendation/1.0.2`, sanitized library identity, relative paths, member inputs, generated/reviewed/effective/stale state, exact fingerprints already present in memory, and fixed ordering. Infrastructure rejects library/descendant/reparse destinations and publishes only through an external temporary sibling.
- Extended the metadata-candidate WPF workflow with recommendation summary, stored-metadata comparison, generated/reviewed metadata and format choices, retain-separate controls, reasons/warnings, all review actions, reset, JSON export, accessibility labels, and row/column virtualization. No business logic or filesystem/JSON integration was added to ViewModels.
- Added Domain, Application, Infrastructure, architecture, safety, serialization, scale, and WPF tests using only synthetic data. The final complete test run passed 249 tests: Domain 88, Application 45, Infrastructure 87, Architecture 16, and WPF 13; zero failed and zero skipped.
- Final `dotnet restore`, `dotnet build --no-restore` (zero warnings/errors), `dotnet test --no-build`, and `dotnet format --verify-no-changes` succeeded. `dotnet list package --vulnerable --include-transitive` reported no known vulnerable packages. `git diff --check` succeeded.
- No package was added. No Calibre CLI, cleanup plan, mutation instruction, backup/rollback execution, PDF analysis, content fingerprint/similarity, fuzzy matching, AI, network lookup, or stored-metadata rewrite was introduced.
- Deviations are implementation-shape only: cohesive recommendation/row files are used instead of one file per trivial value, and JSON deserialization is exposed as controlled schema/model/invariant inspection because Milestone 5 has no import workflow. Interactive high-DPI/screen-reader review remains manual follow-up; automated STA binding/window tests passed.
- The pre-existing `.idea/workspace.xml` modification and `.ai/` directory remain untouched and are excluded from implementation claims.
