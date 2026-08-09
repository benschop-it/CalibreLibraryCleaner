# ADR 0019: Use Bounded Work-Language Candidate Discovery

- Status: Amended
- Date: 2026-08-08
- Amends: ADR 0012, ADR 0016, ADR 0018

## Context

Exact normalized title and author grouping is deterministic and safe, but it misses records for the same work when catalog metadata differs through translation, conversion prefixes, volume notation, author punctuation/order, missing language, incomplete identifiers, or poor metadata. Libraries can exceed 20,000 records, so all-pairs metadata or content comparison is not acceptable.

EPUB package metadata is already inspected for every EPUB, but sampled content evidence is more expensive and must not run for records with no plausible duplicate candidate. Same work and language also does not prove identical edition, abridgement, revision, illustrations, or formatting.

## Decision

Keep exact-binary and exact-normalized-metadata groups unchanged as independent evidence and cleanup authority. Add a separate versioned work-language candidate discovery pipeline that runs before inferred groups are published.

The first delivery is local, deterministic, offline, and explainable. Initially inferred groups were review-only. Product acceptance on 2026-08-09 first added a high-confidence cleanup path and then clarified the intended high-throughput behavior: every published Expanded candidate group is included in cleanup by default. Groups meeting the strongest evidence criteria display `Cleanup eligible`; all others display `To be reviewed` as an advisory indication that manual inspection would be wise. Both types use the deterministic generated keeper, allow keeper override and Skip, and otherwise follow the same cleanup path. Cleanup still requires authoritative state and confirmation of a complete external backup.

The pipeline:

1. builds bounded record profiles from catalog metadata, existing OPF/assessment evidence, identifiers, exact-binary relations, series, language, and format fingerprints;
2. generates candidate pairs through inverted indexes rather than all-pairs comparison;
3. retains at most 20 ordinary candidates per record with deterministic ranking and a global pair ceiling;
4. scores cheap evidence and contradictions;
5. requests sampled EPUB content signatures only for retained ambiguous candidate pairs;
6. caches signatures by file fingerprint and all analyzer/policy/resource versions;
7. compares 12 deterministic landmarks of at most 64 normalized tokens symmetrically;
8. forms constrained work components without blind transitive closure; and
9. partitions accepted components by inferred normalized language.

Weak fuzzy edges may rank or display possible relations but never merge components. Every joined member requires an anchor or strong edge compatible with the complete component. Known language, series/index, identifier, edition-marker, and content contradictions can block union.

Content signatures store only hashes, compact similarity signatures, token counts, language/coverage facts, and safe problem codes. They never store or log sentences or recoverable prose.

Existing snapshots load with inferred evidence unavailable. Only explicit Scan creates or refreshes inferred evidence. State projection removes inferred cleanup eligibility after any mutation and never opens files or reruns discovery.

Expanded cleanup is separate from exact-metadata cleanup. A deterministic retention policy proposes one keeper from assessed format quality, present-format coverage, metadata quality, validated identifiers, cover evidence, and record-ID tie-breaking. The user may override that keeper. The fixed persistent Calibre worker transfers complementary formats, removes every format from non-keepers, and removes the empty non-keeper records. Existing same-format content on the selected keeper wins; automatic cross-record metadata rewriting is not part of this amendment.

Title/author normalization of the surviving record requires a separate future decision and worker protocol extension. Metadata quality and format quality may select different source records, so cleanup must not silently copy metadata from the retained format record.

Online work lookup, multilingual embeddings, and local LLM adjudication are deferred to separate opt-in decisions after local deterministic precision, recall, performance, and cache behavior are measured.

## Consequences

- Metadata-poor records can be discovered before user-visible inferred groups are formed.
- Dutch and English translations can share a future work relation while remaining separate language groups.
- Expensive content reads scale with bounded ambiguous candidates, not total library size squared.
- Exact grouping IDs and cleanup behavior remain stable.
- Published final groups are processed by default with a generated keeper; the user can override the keeper or Skip. Per-run external-backup confirmation, authoritative projected state, and the existing fail-closed worker boundary remain mandatory.
- Cold scans may perform additional bounded EPUB reads; warm scans reuse a no-prose cache.
- Snapshot and state schemas gain optional inferred evidence and matching-run summaries.
- Algorithm, normalization, landmark, cache, and resource-profile versions become part of persisted provenance.

## Rejected alternatives

- Make exact title/author normalization aggressive enough to merge inferred aliases.
- Compare every book pair.
- Extract content signatures for every book unconditionally.
- Use connected components over weak fuzzy edges.
- Mix known different languages in one candidate group.
- Hard-code title, author, series, publisher, language, or work aliases.
- Introduce online lookup, embeddings, or an LLM before deterministic local evaluation.
- Permit inferred groups to use existing metadata cleanup immediately.
