# Functional Requirements

## Library analysis

- Select and validate a Calibre library directory.
- Open `metadata.db` read-only.
- Load book IDs, titles, authors, author-sort values, identifiers, series, languages, formats, and managed paths.
- Resolve referenced format files and report missing or anomalous paths.
- Calculate SHA-256 using streaming I/O, cancellation, progress, and bounded concurrency.

## Duplicate detection

Support progressively:

1. byte-identical files;
2. strong identifier matches;
3. exact normalized title/author matches;
4. normalized content fingerprints;
5. fuzzy metadata matches.

Every group must expose confidence and reasons. Exact title/author matches are candidates, not proof of identical content.

## EPUB analysis

Inspect readability, package metadata, identifiers, language, cover, navigation, spine, resources, internal references, text presence, chapter structure, and encryption indicators. Malformed files become findings rather than application crashes.

## PDF analysis

Later milestone: readability, encryption, page count, metadata, text extraction, scanned/digital classification, OCR evidence, repeated/blank pages, and identifier detection.

## Scoring and recommendations

- Score formats and metadata separately.
- Every rule returns an adjustment and explanation.
- A recommendation can combine metadata and formats from different records.
- Conflicting non-identical files of the same format require review.

## Review workflow

The user can navigate groups, compare records, inspect findings and covers, open files externally, accept or override recommendations, defer groups, and filter by confidence or issue.

## Cleanup plan and execution

Generate an immutable JSON plan containing record IDs, chosen metadata source, chosen format sources, removals, expected hashes, backup requirements, warnings, and approval details.

Execution must revalidate the plan, back up content and metadata, use supported Calibre tooling, capture command output, reload the library, verify results, and retain audit history.

## AI

Optional AI may assist with ambiguous metadata or edition comparison. It must include provenance and may not directly authorize destructive operations.
