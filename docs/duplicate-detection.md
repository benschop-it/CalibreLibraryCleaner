# Duplicate Detection

## Confidence levels

- **Exact binary:** same SHA-256 for compared files.
- **Strong identifier:** matching normalized ISBN or another strong identifier.
- **Exact normalized metadata:** title and author set match after safe deterministic normalization.
- **Equivalent normalized text:** extracted text fingerprints are equal or extremely similar.
- **Fuzzy metadata:** similar title/author/series/year; manual review only initially.

## MVP

Group records when normalized title and normalized author set are equal. Record that reason and do not auto-merge.

## Safe normalization

- Normalize to Unicode NFC, apply whole-string `ToUpperInvariant()`, then normalize to NFC again.
- Remove Unicode `Format` scalars, including zero-width and directional formatting controls.
- Convert Unicode whitespace runs to one ASCII space and trim leading/trailing space.
- Remove that canonical space immediately before or after Unicode punctuation while preserving every punctuation scalar and all subtitle/edition text.
- Normalize titles from the stored title and authors from stored author names only. Never use author-sort values for identity.
- Deduplicate normalized author names and sort them with ordinal comparison to create an order-independent author set.
- Require a usable title and a non-empty author set. If any stored author normalizes to empty, exclude the record rather than silently weakening its identity.
- Exclude records carrying an `AUTHOR_REFERENCE_MISSING` catalog finding because their complete stored author set is unknown.

Do not initially remove subtitles, infer pen names, reverse comma-separated names, discard initials, translate titles, or infer editions.

Author sets must be exactly equal. A record listing only a main author does not match a record listing that author plus additional authors. Literal non-empty values such as `Unknown` are treated as stored text; the detector does not infer localized placeholder semantics.

## Exact normalized metadata groups

A group key is exactly `(NormalizedTitle, NormalizedAuthorSet)`. Identifiers, author IDs/order, author-sort values, formats, hashes, paths, series, and edition inference do not affect it. A group requires at least two distinct Calibre record IDs.

Member IDs are ordered ascending. Groups are ordered by normalized title ordinal, then lexicographically by the ordinally sorted normalized author-name sequence, then by canonical group ID. Group IDs use the versioned, UTF-8-byte-length-prefixed normalized identity and never depend on record order or runtime dictionary hashes.

Every group records reason code `EXACT_NORMALIZED_TITLE_AUTHOR_SET`, category `Exact normalized metadata candidate`, and the explanation that normalized title and order-independent normalized author set are exactly equal. These are candidate duplicate records, not proof of identical files, content, or editions.

Exact metadata groups remain separate from exact binary file groups. A pair can appear in either collection, both collections, or neither; grouping itself does not combine the signals or imply a recommendation.

Milestone 5 consumes these existing collections without changing either group definition. Recommendations may use exact-binary membership to choose among byte-identical same-format alternatives and exact-metadata groups as their review scope. Exact equality remains file-level evidence only; it cannot hide a unique/unavailable/unresolved format or establish content/edition equivalence for a non-identical file.

## Expanded work-language candidates

An explicit scan first builds conservative canonical author identities from catalog and OPF creator variants. Punctuation, spacing, comma order, initials, and compatible full given-name expansions are normalized: for example `J. K. Rowling`, `J.K.Rowling`, `Joanne K. Rowling`, `Joanne Kathleen Rowling`, and `Rowling, J.K.` share `ROWLING|JK`. Expanded given names must not conflict, so `Joanne Kathleen Rowling` and `John Kevin Rowling` remain distinct even though both abbreviate to `J.K. Rowling`.

Work candidates are searched only inside compatible author identities. Author similarity alone is insufficient; a pair also requires title overlap, a validated/embedded identifier, compatible series/index, or exact binary evidence. Inverted indexes suppress broad author buckets, retain at most 20 mutual ordinary candidates per record, and stop inferred discovery if unique pairs exceed `min(200,000, 10 * record count)`. Exact detectors and IDs are unchanged.

Only retained ambiguous pairs request EPUB content evidence. The inspector reuses the existing read-only archive/path/observation boundary, removes script/style/navigation content, and creates 12 distributed windows of at most 64 normalized tokens plus a 64-value bottom-k shingle sketch. Cache entries are keyed by file fingerprint and all algorithm/resource versions, contain hashes/counts only, and live outside the library. Added front matter can match through the sketch without retaining prose.

Pair decisions retain explicit evidence and contradiction codes. Known language, author-expansion, series-index, and different-content contradictions reject edges. Every non-binary inferred relation requires equivalent/high-similarity EPUB content evidence; unavailable or ambiguous content never forms a final group. Anchor edges seed components, complete-component author/language contradictions are checked before union, and final `WorkLanguageCandidateGroup` values are partitioned by known catalog/OPF language (otherwise `und`). They are always `ReviewOnly`.

The Expanded candidates tab exposes confidence, language, anchors, reason codes, content counts, and viewer opening. It has no cleanup command and inferred group IDs are not accepted by mutation contracts.

## Content fingerprints (Milestone 10)

EPUB candidate content uses the bounded hash-only signatures above. Content-language detection and PDF cross-document fingerprints remain future work.

PDF fingerprints must disclose whether all pages or a deterministic bounded
sample contributed. Sampled evidence cannot establish whole-document equality.
Cross-format comparisons must use explicitly compatible, versioned
normalization semantics and retain no book prose. Content comparison is
evidence only until a separately reviewed recommendation policy defines safe
use.

## Automation policy

Only same-format files contributing provably byte-identical content are eligible for this cleanup path without metadata equality. One retained copy is generated automatically from record format count, metadata completeness, validated identifiers, cover presence, and a final record-ID tie-breaker. Other copies are removed as formats; records with remaining formats are preserved. All non-identical matches require separate review.

## Exact binary file groups

Milestone 2 hashes every safely readable declared format with streaming SHA-256. File size is a comparison pre-filter, not a reason to skip hashing. A group requires both equal byte length and equal SHA-256 and at least two distinct managed file references.

Group identity is derived from the length and digest. Groups are ordered by size descending and digest; members are ordered by record ID, format, and managed relative path. These are file-level groups: even when identical files span records, the result does not assert that the book records are metadata duplicates, equivalent editions, or safe to merge or delete.

An exact-binary cleanup decision removes duplicate copies through typed `calibredb remove_format`. It removes a Calibre record through non-permanent `calibredb remove` only after the authoritative projected state contains no formats. Successful commands apply typed deltas; no cleanup scan occurs. Additional formats and metadata remain on their records.
