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

Exact metadata groups remain separate from exact binary file groups. A pair can appear in either collection, both collections, or neither; the application does not combine the signals or calculate a recommendation.

## EPUB fingerprints

Traverse spine order, extract visible text, decode entities, normalize Unicode and whitespace, calculate strict and punctuation-insensitive hashes, and retain chapter count and text length. Use multiple signals rather than one hash.

## Automation policy

Only provably redundant byte-identical content is eligible for automatic action in early releases. All other matches require review.

## Exact binary file groups

Milestone 2 hashes every safely readable declared format with streaming SHA-256. File size is a comparison pre-filter, not a reason to skip hashing. A group requires both equal byte length and equal SHA-256 and at least two distinct managed file references.

Group identity is derived from the length and digest. Groups are ordered by size descending and digest; members are ordered by record ID, format, and managed relative path. These are file-level groups: even when identical files span records, the result does not assert that the book records are metadata duplicates, equivalent editions, or safe to merge or delete.
