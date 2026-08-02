# Functional Requirements

## Library analysis

- Select and validate a Calibre library directory.
- Open `metadata.db` read-only.
- Load book IDs, titles, authors, author-sort values, identifiers, series, languages, formats, and managed paths.
- Resolve referenced format files and report missing or anomalous paths.
- Calculate SHA-256 using streaming I/O, cancellation, progress, and bounded concurrency.
- Persist the latest complete successful scan per canonical library folder outside the Calibre library.
- List persisted library folders and explicitly load a prior result instead of rescanning, while identifying it as potentially stale.
- Invalidate the persisted result before an approved library mutation; only a later complete normal scan may repopulate it.

## Duplicate detection

Support progressively:

1. byte-identical files;
2. strong identifier matches;
3. exact normalized title/author matches;
4. normalized content fingerprints;
5. fuzzy metadata matches.

Every group must expose confidence and reasons. Exact title/author matches are candidates, not proof of identical content.

For each byte-identical same-format file group, automatically retain the copy on the record with the most formats, then the best metadata/validated identifiers/cover evidence, using the lowest Calibre ID only as a tie-breaker. Remove the other format copies, not their records. A record becomes a deletion target only when no formats remain after deduplication. Mixed-format-label groups are anomalous and skipped. Complete affected-record metadata, covers, formats, and managed state must first be backed up externally.

## EPUB analysis

Inspect readability, package metadata, identifiers, language, cover, navigation, spine, resources, internal references, text presence, chapter structure, and encryption indicators. Malformed files become findings rather than application crashes. EPUBs are disqualified only by definitive file open/read failures. Incomplete technical inspection is `Unassessed` unless bounded fallback inspection proves at least one safe, unique, unencrypted local XHTML/HTML/SVG resource is renderable. A fallback-readable EPUB is `Completed`, receives explainable warning penalties, and has an explicit maximum score of 70. Unknown facets earn neither positive points nor absence penalties. Capped EPUBs require manual review when compared with non-identical EPUB alternatives.

## PDF analysis

Milestone 9 implements safe read-only PDF assessment for verified PDF formats in
the library snapshot. It reports strict readability, encryption/password
status, page count, bounded metadata, deterministic all-page or sampled-page
text/image evidence, scan-like/digital/mixed classification with confidence,
outline and inert active-content facts, conservative repeated/blank-page
evidence, checksum-valid bounded ISBN evidence, resource-limit outcomes, and a
PDF-specific explainable quality score.

PDF parsing runs in one disposable bounded worker per file. It does not render,
perform OCR, decode images for presentation, extract attachments, execute PDF
actions, follow links, retain complete text, access the network, or mutate the
library. Classification is evidence rather than score, and PDF assessments do
not rank retained PDFs or affect recommendations, cleanup, execution, or
recovery.

Cross-document EPUB/PDF content fingerprints, equality/similarity comparisons,
and any resulting recommendation-policy changes begin in Milestone 10 or later.

## Scoring and recommendations

- Score formats and metadata separately.
- Every rule returns an adjustment and explanation.
- A recommendation can combine metadata and formats from different records.
- Conflicting non-identical files of the same format require review.

## Review workflow

The user can navigate groups, compare records, inspect findings and covers, open files externally, accept or override recommendations, defer groups, and filter by confidence or issue.

## Cleanup plan and execution

Generate an immutable JSON plan containing record IDs, chosen metadata source, chosen format sources, removals, expected hashes, backup requirements, warnings, and approval details.

Exact-binary cleanup uses a separate plan body containing the automatically retained exact-file association, every duplicate-format removal, only the record IDs derived to become empty, exact-binary evidence, complete involved-record state, backup requirements, and explicit approval.

Execution must revalidate the plan against the current authoritative revision, back up content and metadata, use supported Calibre tooling, capture command output, durably apply the corresponding typed state delta, and retain audit history. Cleanup and recovery never scan or reload the library; only explicit Scan replaces projected state with observed state.

## AI

Optional AI may assist with ambiguous metadata or edition comparison. It must include provenance and may not directly authorize destructive operations.
