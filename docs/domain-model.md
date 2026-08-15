# Domain Model

Domain contains immutable provider-neutral values and deterministic policies. It
contains no SQLite, filesystem, process, JSON, WPF, EPUB-library, PdfPig, HTTP, or
model-runtime types.

## Library and formats

`LibrarySnapshot` is one immutable analysis view containing:

- `LibraryIdentity` and scan time;
- ordered `CalibreBook` records and `LibraryFinding` values;
- Exact binary and Exact Metadata groups;
- EPUB and PDF assessments;
- Expanded work-language evidence and matching summary; and
- disjoint `UnifiedCandidateGroup` values.

`CalibreBook` contains Calibre ID, title, author-sort, ordered authors, identifiers,
relative directory, publication metadata, and formats. `BookFormat` contains
canonical format, stored name/relative path, physical status, fingerprint, and file
observation where available.

`FormatFileFingerprint` is byte length plus SHA-256. `FormatFileObservation` records
technical file identity used for change/cache checks. Non-physical statuses never
carry invented fingerprint/observation facts.

## Workflow state

Current implementation uses `LibraryState` with generation, revision,
authoritative/uncertain status, immutable snapshot, workflow checkpoint, mutation
intent, and typed deltas. Workflow phases are:

- `RequiresExactAnalysis`;
- `ExactReady`;
- `CandidatePreparationReady`;
- `CandidateAnalysisReady`; and
- `Completed`.

This detailed projected model is implemented baseline. ADR 0021 targets simpler
durable workflow/run status while preserving stop-and-Rescan after failed or
ambiguous mutation.

## Exact evidence

`ExactBinaryDuplicateGroup` groups distinct managed file references with identical
length and SHA-256. Its ID derives from that fingerprint.

`ExactMetadataDuplicateGroup` groups distinct records with the same safely normalized
title and complete order-independent normalized author set. It is Candidate evidence,
not byte/content/edition proof.

The two Exact group collections are independent.

## Assessment values

`EpubAssessment` and `PdfAssessment` share `FormatAssessment` identity/status/score
semantics and retain provider-neutral feature summaries, ordered findings, verified
fingerprint/observation, and all analyzer/scoring/resource versions.

Status is Completed, Unassessed, or Disqualified. Completed scores are reproducible
from findings and may have an explicit ceiling. Unknown facts are not treated as
success or absence.

Assessment quality helps keeper/source ranking. It does not establish a match.

## Matching values

### Implemented

`BookMatchingProfile` is transient bounded metadata/assessment evidence for one
record. `BookCandidatePair` has canonical record ordering, cheap score, evidence,
contradictions, and content-demand flag.

`EpubContentSignature` contains fingerprint, token/coverage counts, 12 hash
landmarks, and a bounded hash sketch. It contains no prose.

`WorkLanguageCandidateGroup` stores members/anchors, normalized language, evidence,
contradictions, confidence, content comparison summary, policy version, and advisory
eligibility.

`UnifiedCandidateGroup` merges Exact Metadata and Expanded evidence into one
disjoint executable group. It contains canonical members, evidence provenance,
review findings, advisory classification, generated keeper, retention facts, and
policy version. Every current record belongs to at most one Unified group.

A Unified group means same work and language. It may span editions/revisions or
formatting differences. Both advisory classifications are executable unless skipped.

### Target evidence

Future provider-neutral Domain values should represent:

- PDF content signatures/comparisons with coverage semantics;
- cover/visual and structural comparisons;
- content-language classification;
- bibliographic provider work/edition evidence with provenance; and
- local model/embedding evidence with model/input versions.

These values are evidence inputs. They cannot reference HTTP/model runtimes or call
mutation.

## Keeper and cleanup values

`UnifiedCandidateRetentionPolicy` ranks a generated keeper from completed compatible
assessments, format coverage, metadata completeness, valid identifiers, cover, and
record-ID tie-breaking.

User choices are session-scoped keeper override and Skip. Cleanup selections bind
expected generation/revision/group members and selected keeper.

Application planner output consists of deterministic complementary transfers,
non-keeper format removals, empty-record removals, and skipped-group count. Domain
simulation must prove transfers survive, keeper formats remain, and removed records
are empty.

General cleanup-plan, execution-history, backup-bundle, rollback, and recovery
aggregates are not current product concepts.

## Core invariants

- Exact groups have at least two distinct file references/records as appropriate.
- Exact Metadata never degrades to title-only matching.
- Exact Metadata groups contain no disjoint known normalized-language or validated
  same-type strong-identifier pair; ambiguous tied conflict buckets are not
  executable.
- Matching/group IDs are canonical and deterministic for their policy version.
- Weak evidence alone cannot bridge components; decisive contradictions block union.
- Final Unified groups are disjoint and contain current record IDs only.
- Matching signatures/evidence contain bounded hashes/counts/provenance, not prose.
- Assessment scores derive from findings and never prove duplicate identity.
- Keeper ranking is deterministic and independent from group identity.
- Executable formats are physical, present, verified, and safely contained.
- Transfers precede removals; a record removal is valid only when final inventory is
  empty.
- AI/provider/model confidence remains distinct from deterministic evidence and
  never authorizes mutation directly.
- Uncertain or failed mutation state cannot enable another mutation before Rescan.
