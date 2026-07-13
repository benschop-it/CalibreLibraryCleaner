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

- Unicode normalization and invariant case folding.
- Trim and collapse whitespace.
- Normalize punctuation spacing.
- Remove zero-width characters.
- Stable ordering of multiple authors for comparison.

Do not initially remove subtitles, infer pen names, reverse comma-separated names, discard initials, translate titles, or infer editions.

## EPUB fingerprints

Traverse spine order, extract visible text, decode entities, normalize Unicode and whitespace, calculate strict and punctuation-insensitive hashes, and retain chapter count and text length. Use multiple signals rather than one hash.

## Automation policy

Only provably redundant byte-identical content is eligible for automatic action in early releases. All other matches require review.
