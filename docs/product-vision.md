# Product Vision

## Product

Calibre Library Cleaner is a local-first Windows application for one focused job: clean
a messy disposable copy of a Calibre library, review the result, and replace the
original library when the copy is accepted.

The application finds records for the same work and language, explains the evidence,
selects a practical keeper, improves retained bibliographic metadata from trusted
online sources, and applies approved changes through supported Calibre tooling.

## Primary goal

Complete one safe, understandable cleanup from analysis through duplicate
consolidation, metadata improvement, release acceptance, and final Calibre-managed
name normalization.

An executable Candidate group means **the same work and language**. It may include
different editions, revisions, illustrations, or formatting. The application makes
that uncertainty visible, proposes a keeper, and lets the user change the keeper or
Skip the group. Published groups are processed unless skipped.

## Priorities

1. Preserve the proven Exact and Unified Candidate duplicate-cleanup behavior.
2. Produce one coherent, attributed online-edition proposal for every expected
  retained record without blocking cleanup when providers fail.
3. Make proposal confidence, default selection, keeper override, and Skip efficient
  to review at large-library scale.
4. Apply only approved changes through one supported Calibre worker.
5. Package and accept the complete Windows workflow before normalizing managed names.

Cancellation, resumable partial analysis, automated rollback, and automated recovery
are not primary product goals.

## Operating assumptions

- The user confirms a complete external backup before every mutation run.
- Calibre and other library writers remain closed while the cleaner mutates a
  library.
- Analysis never writes directly to `metadata.db` or Calibre-managed files.
- Mutation uses only the fixed persistent `calibre-debug` worker.
- A failed or ambiguous mutation stops; the user restores externally if necessary
  and runs an explicit Rescan before further cleanup.

## Metadata strategy

- Preserve the existing matching decision independently from metadata enrichment.
- Query Open Library and Google Books independently after disclosing transmitted
  bibliographic field categories; Google Books requires an optional protected key.
- Select one coherent edition rather than freely mixing conflicting records.
- Show provenance, confidence, disagreement, and a user-controlled Apply checkbox.
- Preserve local tags, ratings, comments, custom columns, and unrelated identifiers.
- Leave metadata unchanged when a proposal is unchecked or unavailable.

Provider failure never blocks duplicate cleanup. Online results are proposals and
never mutation authority.

## Implemented baseline

- Exact-only scan and exact binary grouping.
- Staged Exact cleanup followed by trusted Candidate preparation.
- Local deterministic same-work/language discovery using metadata, identifiers,
  series/language constraints, exact binary relations, and candidate-only EPUB
  content signatures.
- EPUB and isolated PDF quality assessment.
- Unified Candidate groups with evidence, generated keeper, keeper override, and
  Skip.
- Persistent assessment/signature caches and measured progress.
- Fixed persistent Calibre mutation worker for Exact and Candidate cleanup.
- External-backup confirmation and no automated recovery.
- Stable-observation SHA-256 reuse with forced verification at the Application
  boundary.
- Enabled-by-default Open Library same-work evidence with reduced caching.
- Optional configured Ollama observations that remain non-authoritative and do not
  affect grouping or cleanup after failed threshold calibration.

## Success measures

- Every expected retained record receives one coherent proposal or an explicit
  unavailable outcome.
- High/Medium proposals default checked; Low proposals default unchecked and remain
  easy to review.
- Provider failure leaves metadata unchanged without blocking duplicate cleanup.
- Checked metadata updates preserve local-only fields and verify through Calibre.
- The packaged Windows build includes the PDF worker and completes the workflow on a
  disposable representative library.
- Final name normalization uses supported Calibre behavior and no direct filesystem
  mutation.
