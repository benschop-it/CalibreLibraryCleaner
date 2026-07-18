# Quality Scoring

## Model

Each independent rule returns a finding containing rule ID, severity, score adjustment, explanation, and safe evidence. The total score is reproducible from findings and versioned with analyzer and weight versions.

## Initial EPUB checks

Positive checks may include:

- opens successfully;
- readable package metadata;
- title, author, language, date, and strong identifier;
- embedded cover and acceptable resolution;
- navigation/TOC;
- non-empty spine;
- all spine resources and references valid;
- substantial text and sensible chapter structure.

Negative or disqualifying checks may include:

- cannot open;
- encryption prevents analysis;
- missing spine resources;
- broken images or links;
- repeated content;
- excessive empty chapters;
- suspiciously tiny text content.

## EPUB V1 model

`epub-quality/1.0.0` uses a visible +50 baseline and the following maximum positive contributions: open +4, package +4, title +3, author +3, language +2, date +1, valid ISBN +1, cover +4, useful cover dimensions +2, navigation +4, non-empty spine +5, complete spine resources +4, complete internal resources +4, substantial text +5, no near-empty chapters +2, and no repeated structural references +2. The maximum is 100.

Negative weights are: missing title/author -4 each, language -2, malformed date -1, missing cover -6, small cover -3, malformed supported cover header -2, missing navigation -6, empty spine -20, missing spine resources -5 each capped at -20, broken internal references -2 each capped at -10, 500–4,999 readable characters -8, fewer than 500 -15, near-empty chapters -2 each capped at -10, and repeated structural references -4 each capped at -12. Cover usefulness requires a 600-pixel short side and 800-pixel long side. Chapters below 100 normalized letters/digits are near-empty. ISBN-10/13 check digits are validated locally without lookup.

All repeated evidence remains visible after its scoring cap, with zero applied adjustment. Findings are ordered deterministically and a completed score is `clamp(sum(applied adjustments), 0, 100)`. Missing, unreadable, invalid, changed, unsafe, malformed-package, limit-exceeding, unsupported-encrypted, or mandatory-analysis-truncated EPUBs are disqualified and display no numeric score. A parsed empty spine remains scored with its severe penalty.

## Separate scores

Keep metadata quality separate from each format's quality. A record recommendation may select metadata from one record and EPUB/AZW3/PDF from others.

Milestone 5 may prefer one non-identical EPUB only when current matching assessments use the same analyzer/scoring-model versions and either a completed assessment is compared with a disqualified assessment or the completed score is at least 10 points above every competitor with a decisive structural/readability finding advantage of at least four applied points and no countervailing decisive error. Cover or embedded-metadata findings alone never authorize that preference. Close, equal, contradictory, missing, stale, or incompatible assessments remain unresolved. EPUB score never proves equivalent content or edition.

## Explainability

Prefer explicit reasoning over a single number. Example: keep EPUB A because it has navigation, a useful cover, complete resources, and equivalent text; reject EPUB B because it has broken references and a lower structural score.

## Special cases

Different editions, languages, translations, abridgements, and scanned historical editions must not be treated as interchangeable merely because one has a higher generic score.
