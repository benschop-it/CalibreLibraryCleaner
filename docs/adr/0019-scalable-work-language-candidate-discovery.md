# ADR 0019: Use Bounded Work-Language Candidate Discovery

- Status: Accepted
- Date: 2026-08-08
- Amends: ADR 0012, ADR 0016, ADR 0018

## Context

Exact normalized title and author grouping is deterministic and safe, but it misses records for the same work when catalog metadata differs through translation, conversion prefixes, volume notation, author punctuation/order, missing language, incomplete identifiers, or poor metadata. Libraries can exceed 20,000 records, so all-pairs metadata or content comparison is not acceptable.

EPUB package metadata is already inspected for every EPUB, but sampled content evidence is more expensive and must not run for records with no plausible duplicate candidate. Same work and language also does not prove identical edition, abridgement, revision, illustrations, or formatting.

## Decision

Keep exact-binary and exact-normalized-metadata groups unchanged as independent evidence and cleanup authority. Add a separate versioned work-language candidate discovery pipeline that runs before inferred groups are published.

The first delivery is local, deterministic, offline, explainable, and review-only. Inferred groups cannot enter cleanup request contracts.

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

Existing snapshots load with inferred evidence unavailable. Only explicit Scan creates or refreshes inferred evidence. State projection may remove or mark inferred evidence stale but never opens files or reruns discovery.

Online work lookup, multilingual embeddings, and local LLM adjudication are deferred to separate opt-in decisions after local deterministic precision, recall, performance, and cache behavior are measured.

## Consequences

- Metadata-poor records can be discovered before user-visible inferred groups are formed.
- Dutch and English translations can share a future work relation while remaining separate language groups.
- Expensive content reads scale with bounded ambiguous candidates, not total library size squared.
- Exact grouping IDs and cleanup behavior remain stable.
- Inferred groups require review and cannot authorize deletion in this milestone.
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
