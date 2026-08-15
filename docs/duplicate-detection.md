# Duplicate Detection and Matching

## Matching objective

Find records that contain the same work and language. Different editions, revisions,
illustrations, or formatting may belong to one executable Candidate group.

Matching confidence is not a promise of file interchangeability. Evidence,
contradictions, advisory classification, generated keeper, keeper override, and Skip
make the tradeoff visible. Published groups are processed unless skipped.

## Evidence layers

### Exact binary

Equal byte length and SHA-256 for at least two managed file references. This proves
byte equality for those files only.

### Exact normalized metadata

Equal safely normalized title and complete order-independent author set for at least
two records. It is strong Candidate evidence, not content/edition proof.

Normalization uses Unicode NFC and invariant casing, removes format controls,
normalizes whitespace around retained punctuation, and excludes incomplete/empty
author identities. It does not silently discard subtitles or infer aliases.

Before publishing an executable Exact Metadata group, policy
`exact-metadata/1.1.0` checks catalog languages and validated ISBN/DOI/ASIN/OCLC
values. Missing/invalid values remain neutral. Disjoint known values exclude an
outlier only when one repeated consensus exists; an ambiguous or tied conflict
suppresses the identity bucket. This preserves one deterministic identity-based
group ID and prevents known translations or identifier conflicts from reaching
Exact-first cleanup.

### Expanded local evidence

Implemented profiles combine:

- canonical author aliases (family name, positional initials, comma order, compatible
  full given names);
- title keys/tokens;
- validated catalog and embedded identifiers;
- series/index, year, language, and edition markers;
- exact binary relations;
- format/assessment metadata; and
- candidate-only EPUB content comparison.

Cheap title-token similarity ignores only the fixed edition markers `abridged`,
`annotated`, `expanded`, `illustrated`, `revised`, and `unabridged`. Original title
evidence remains visible, thresholds are unchanged, and marker overlap alone cannot
propose a work. This lets connector-damaged revised/illustrated variants reach
content comparison without introducing aliases or fuzzy string matching.

Author similarity alone cannot propose a work. Known author expansion, language,
series/index, identifier, edition-marker, and different-content contradictions can
reject a pair or component.

## Bounded candidate generation

The current matcher indexes compatible author identities instead of comparing all
records. It suppresses broad ordinary buckets, ranks cheap evidence, retains at most
20 mutual ordinary candidates per record, preserves decisive anchor relations, and
fails closed when the global pair ceiling is exceeded.

Exact Metadata candidates bypass ordinary caps and always reach unified merging.

## EPUB content evidence

Only retained ambiguous pairs with two usable EPUBs request signatures. Each
signature is keyed by file fingerprint and all policy/resource versions and stores:

- token/coverage counts;
- 12 distributed landmarks of at most 64 normalized tokens;
- a bounded bottom-k shingle sketch; and
- safe problem codes.

No prose is retained. Comparison is symmetric. Equivalent/high-similarity content
can support union; unavailable/weak/different content does not.

## Open Library evidence

Candidate pairs still unresolved after local content may query Open Library. Queries
are cache-first and disclose/transmit validated ISBN only when available, otherwise
bounded title, author, and optional language. A deterministic policy requires one
compatible or clearly dominant work result. Two records resolving to the same
provider work ID gain provenance-bearing Anchor evidence; different, ambiguous,
missing, failed, or rate-limited results remain neutral. Existing decisive
contradictions still reject the pair.

## Component and group construction

Anchor/strong compatible edges seed components. Before each union, the matcher checks
complete-component contradictions rather than applying blind connected components.
Groups are partitioned by normalized known language, otherwise `und`.

The unified merge policy combines Exact Metadata and Expanded evidence, resolves
safe overlaps, blocks contradictory bridges, and emits disjoint Candidate groups.

Advisory classifications:

- `Cleanup eligible`: strongest uncontradicted evidence;
- `To be reviewed`: weaker, incomplete, older-policy, or ambiguous evidence.

Both start included and are executable unless skipped.

## Keeper policy

Group identity and keeper selection are separate. The generated keeper ranks:

1. completed compatible assessment evidence;
2. assessment score total;
3. present format coverage;
4. metadata completeness;
5. validated strong identifiers;
6. cover evidence; and
7. lowest Calibre record ID.

For complementary formats absent from the keeper, the planner selects the best
quality-ranked physical source. The keeper's existing same-format file wins.

## Current limitations

- No PDF cross-document matching.
- No cover/visual comparison.
- Content language relies on catalog/OPF evidence rather than calibrated detection.
- No online bibliographic enrichment.
- No local embeddings/models.
- Candidate neighborhoods are recomputed as a complete residual run rather than
  incrementally.
- Exact Scan currently rereads every resolvable file for SHA-256.

## Matching evaluation baseline

`matching-corpus/1.0` now evaluates the complete current Domain matching pipeline on
strict embedded calibration and holdout data. The corpus expands to 62 scenarios and
includes 474 records, 232 positive pairs, 2,600 definitive negative pairs, synthetic
hard cases, and two minimal CC0 Wikidata public-domain work families. It contains no
ebook prose or personal-library data.

The reviewed `matching-evaluation/1.0` baseline for current policies records:

- pair precision: 222/222, 100%;
- pair recall: 222/232, 95.6897%;
- F1: 444/454, 97.7974%;
- candidate-route recall: 232/232, 100%;
- exact final components: 202/212, 95.2830%;
- keeper coverage: 202/212 expected groups, 95.2830%; and
- keeper accuracy among those complete predicted groups: 202/202, 100%.

Contradiction-aware Exact Metadata removed all 20 prior false positives. Edition-
neutral title similarity then recovered all ten previously unproposed positive pairs
without changing precision. Open Library evidence recovered the two independently
sourced public-domain work families. The ten remaining false negatives are synthetic
cases that reached candidate generation but had requested-yet-unavailable/weak
content outcomes. These are corpus
measurements, not universal library accuracy claims. Later policy changes must report
calibration and frozen-holdout deltas against the committed semantic baseline.

## Target matching program

1. Improve normalization/evidence against the committed calibration and frozen
  holdout baseline without weakening decisive contradictions.
2. Add PDF fingerprints with all-page versus sampled coverage.
3. Add calibrated language, cover/visual, and structural evidence.
4. Add configured online provider evidence with provenance and local fallback.
5. Evaluate local embeddings/models against deterministic baselines.
6. Calibrate evidence fusion and confidence on held-out data.
7. Add incremental candidate/evidence invalidation and cold/warm performance budgets.

A new evidence source is accepted only when it improves measured quality or
performance without unbounded resource use or hidden library-specific rules.

## Cache requirements

All signatures, provider results, model outputs, and derived evidence are keyed by
stable input identity and all relevant versions. Corruption or incompatibility is a
miss. Cache entries retain no recoverable book prose unless a future explicit privacy
decision allows it.

Future hash reuse additionally validates canonical path, size, timestamps,
attributes/file identity, and provenance, with selective/periodic byte checks and a
forced verification mode.
