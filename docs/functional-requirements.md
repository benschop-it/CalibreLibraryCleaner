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
- When Load is invoked, immediately show an indeterminate loading-saved-analysis phase before deserialization or state replay begins. After scanning or loading completes, show an indeterminate preparing-results phase while large presentation collections are materialized, then publish the collections and finish progress at 100%.

The staged workflow begins with explicit exact-only analysis. It reads the complete
catalog, resolves every current format path, hashes every resolvable format with
streaming SHA-256 and bounded concurrency, and publishes Exact binary groups. It
does not assess EPUB/PDF files, generate recommendations, detect exact-metadata
groups, inspect candidate content, or run Expanded discovery.

After Exact cleanup succeeds, Candidate preparation performs a dedicated read-only
catalog refresh. It reconciles current records, metadata, format labels, and
associations against authoritative projected state and completed Exact deltas.
Known fingerprints may be reused only for explained associations. Unexplained
catalog or file changes fail closed and require a new exact analysis. Candidate
preparation is not a normal full scan and never occurs implicitly during mutation.
An unchanged catalog association already classified `InvalidPath` by Exact analysis
may remain as a non-executable finding only while the same association remains
unresolvable. A newly resolvable or otherwise changed association fails closed.

Candidate preparation reports its active phase with truthful phase-local units.
Transfer verification uses bytes/files; EPUB/PDF assessment uses completed/total
files plus bounded technical substages; content matching uses fingerprints; and
result materialization uses exact item totals. A one-second elapsed heartbeat keeps
the current phase visible when semantic counters do not advance. It never invents
completed work. Cancel remains available and immediately reports that cancellation
is pending while the current bounded operation stops.

Candidate preparation reports its active phase with truthful phase-local units.
Transfer verification uses bytes/files; EPUB/PDF assessment uses completed/total
files plus bounded technical substages; content matching uses fingerprints; and
result materialization uses exact item totals. A one-second elapsed heartbeat keeps
the current phase visible when semantic counters do not advance. It never invents
completed work. Cancel remains available and immediately reports that cancellation
is pending while the current bounded operation stops.

## Duplicate detection

Support progressively:

1. byte-identical files;
2. strong identifier matches;
3. exact normalized title/author matches;
4. normalized content fingerprints;
5. fuzzy metadata matches.

Every group must expose confidence and reasons. Exact title/author matches are candidates, not proof of identical content.

Residual candidate discovery runs only after successful Exact cleanup and trusted
post-exact refresh. It retains exact normalized title/author detection as mandatory
candidate evidence and combines that evidence with Expanded discovery into one
disjoint candidate model. Exact-metadata candidates remain present when EPUB
content is absent and bypass ordinary candidate caps. Expanded discovery first
canonicalizes compatible author variants using family name, positional initials,
comma order, and non-conflicting full given-name expansions. It searches for
duplicate works only inside those author identities, requires independent
title/identifier/series/binary evidence, and partitions candidates by language.
Every non-binary final inferred relation uses EPUB content evidence from 12 by
64-token hash landmarks and a bounded shingle sketch when available. Signatures
are cached by fingerprint/version outside the library and retain no prose. Groups
whose evidence meets the strongest policy criteria display `Cleanup eligible`;
groups with weaker, incomplete, ambiguous, older-policy, or contradictory evidence
display `To be reviewed`. This distinction is advisory: both types start
unskipped, use the generated keeper, permit keeper changes and Skip, and are
processed unless the user explicitly skips them.

For each byte-identical same-format file group, automatically retain the copy on the record with the most formats, then the best metadata/validated identifiers/cover evidence, using the lowest Calibre ID only as a tie-breaker. Remove the other format copies, not their records. A record becomes a deletion target only when no formats remain after deduplication. Mixed-format-label groups are anomalous and skipped. The user is responsible for a complete library backup; complementary formats are fingerprint-verified in automatic temporary staging before transfer and source removal.

The Exact file duplicates UI lets the user override each generated keeper and provides one `Remove duplicates` command for all eligible groups. It transfers complementary formats to one unambiguous keeper record, removes duplicate and transferred source formats, and deletes empty records. Non-identical same-format conflicts and multi-target sources remain unchanged.

## EPUB analysis

Inspect readability, package metadata, identifiers, language, cover, navigation, spine, resources, internal references, text presence, chapter structure, and encryption indicators. Malformed files become findings rather than application crashes. EPUBs are disqualified only by definitive file open/read failures. Incomplete technical inspection is `Unassessed` unless bounded fallback inspection proves at least one safe, unique, unencrypted local XHTML/HTML/SVG resource is renderable. A fallback-readable EPUB is `Completed`, receives explainable warning penalties, and has an explicit maximum score of 70. Unknown facets earn neither positive points nor absence penalties. Capped EPUBs require manual review when compared with non-identical EPUB alternatives.

Decoded HTML and DOM structure are bounded before expensive traversal. Monolithic dictionary/reference chapters that exceed the assessment resource profile return controlled incomplete/limit evidence instead of blocking the scan. The UI reports the current EPUB substage and chapter/candidate units, and technical diagnostics identify slow records without logging titles, paths, or book content.

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

Candidate-only EPUB content fingerprints and equality/similarity evidence are available for Expanded discovery and policy-gated cleanup eligibility. PDF cross-document fingerprints and recommendation-policy changes remain future work.

## Scoring and recommendations

- Score formats and metadata separately.
- Every rule returns an adjustment and explanation.
- A recommendation can combine metadata and formats from different records.
- Conflicting non-identical files of the same format require review.

## Review workflow

The user can navigate duplicate groups and compare records. Exact and metadata candidate groups present one generated keeper that can be changed by selecting another member. Metadata groups also expose a session-scoped Skip choice.

Double-clicking a member in Exact file duplicates opens that exact format in Calibre ebook viewer. Double-clicking a Metadata candidate opens its preferred present format (EPUB, AZW3, MOBI, PDF, then another available format). Viewer launch is explicit, read-only, and reports unavailable files or viewer installation errors without changing review or cleanup state.

The Expanded candidates tab shows content-confirmed work-language groups, confidence, reason/contradiction codes, bounded content evidence, member formats, a generated sole keeper, Keep/Remove actions, and a session-scoped Skip choice. Selecting another member changes the keeper. Double-clicking opens its preferred present format.

Metadata and Expanded tabs are read-only evidence views. Unified Candidate groups
combine these evidence sources into disjoint executable groups. Before Candidate
cleanup the user confirms a complete external backup and acknowledges that matching
may not prove identical work, edition, revision, illustrations, or formatting.
`To be reviewed` is advisory rather than a cleanup gate. Complementary formats are
transferred to the keeper; every non-keeper format and proven-empty record is
removed through the fixed worker. The keeper's same-format file wins.

## Retired legacy cleanup

`Cleanup all` and standalone Metadata/Expanded mutation commands are retired.
Shadow parity and intentional differences are recorded in
`docs/workflows/unified-candidate-shadow-parity.md`.

## Exact duplicate cleanup

The Exact file duplicates workflow presents one generated keeper per group, allows explicit keeper overrides, and removes all eligible exact duplicates in one command. Before each run, the user confirms that a complete external library backup exists; the application does not create or verify backups.

Cleanup uses one trusted persistent `calibre-debug` worker with typed chunks of at most 100 operations. It transfers complementary formats only to one unambiguous non-conflicting target, removes exact duplicate and transferred source formats, and removes records that become empty. Successful complete chunks durably update projected state without rescanning.

Worker startup or preflight failure logs and stops before mutation. Any failed, ambiguous, interrupted, unpersistable, or unprojectable mutation logs structured technical context, marks state uncertain, stops without retry or continuation, and blocks later mutation until explicit Rescan. There is no direct-command fallback or automated recovery.

Persisted analysis loading remains available during development. Startup lists only small state manifests; explicit Load restores the saved analysis without scanning. Legacy snapshot files migrate into state on explicit Load. New scans and checkpoints retain only the active state generation.

In staged mode, Exact cleanup is enabled only for a compatible authoritative
`ExactReady` checkpoint and eligible unskipped Exact selections. It invokes the
existing Exact cleanup algorithm unchanged. Completed and nothing-to-do outcomes
advance the durable phase to `CandidatePreparationReady`; failed or ambiguous
mutation leaves state uncertain and Candidate cleanup disabled.

## AI

Optional AI may assist with ambiguous metadata or edition comparison. It must include provenance and may not directly authorize destructive operations.
