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

All repeated evidence remains visible after its scoring cap, with zero applied adjustment. Findings are ordered deterministically. Full completed assessments use `clamp(sum(applied adjustments), 0, 100)`. Definitive cannot-open/read outcomes are disqualified; incomplete technical inspection is unassessed unless bounded fallback proves renderable local content. A parsed empty spine remains scored with its severe penalty.

`epub-quality/1.0.3` adds fallback-readable scoring. The fallback reader considers only safe, unique, unencrypted entries with supported compression and bounded size/ratio, and requires at least one local XHTML/HTML/SVG candidate with non-empty alphanumeric text, an accepted local media reference, or renderable SVG content. Only established facets participate in ordinary scoring. Warning penalties are: invalid manifest path/name -3 each (cap -12); malformed/missing optional navigation -6 once; missing/malformed mandatory container/package -12 each (combined cap -18); unsupported parser -10; unsafe entries -4 each (cap -12); duplicate collision groups -5 each (cap -15); encrypted entries -10 each (cap -20); unsupported/suspicious/oversized entries -8 each (combined cap -16); unknown reading order -8; and other partial coverage -5 unless a specific warning already represents it. The stored fallback score is `min(clamp(sum(applied adjustments), 0, 100), 70)`. The ceiling is explicit, the uncapped score remains available, and a zero-point finding explains both values.

## Separate scores

Keep metadata quality separate from each format's quality. A record recommendation may select metadata from one record and EPUB/AZW3/PDF from others.

Milestone 5 may prefer one non-identical EPUB only when current matching assessments use the same analyzer/scoring-model versions, none is capped, and either a completed assessment is compared with a disqualified assessment or the completed score is at least 10 points above every competitor with a decisive structural/readability finding advantage of at least four applied points and no countervailing decisive error. Any capped candidate forces manual review for a non-identical comparison. Sole-copy retention and exact-binary selection remain allowed. Cover or embedded-metadata findings alone never authorize preference. Close, equal, contradictory, missing, stale, incompatible, or capped assessments remain unresolved. EPUB score never proves equivalent content or edition.

## Explainability

Prefer explicit reasoning over a single number. Example: keep EPUB A because it has navigation, a useful cover, complete resources, and equivalent text; reject EPUB B because it has broken references and a lower structural score.

## PDF V1 model

PDF scoring uses `pdf-quality/1.0.0` and is separate from EPUB scoring. Its 100 points comprise technical (maximum 85) and embedded-metadata (maximum 15) components. Every nonzero contribution is an `AssessmentFinding`; component sums are clamped independently. Analyzer `pdf-inspector/1.0.0`, classification `pdf-classification/1.0.0`, sampling `pdf-sampling/1.0.0`, and limits `pdf-limits/1.0.0` are retained.

Technical positives are strict open +50, valid page tree +10, page coverage +10/+8/+5, observable content +8/+4, resources within soft thresholds +5, and outline +2. Metadata positives are useful title +4, author +4, semantic date +2, checksum-valid bounded ISBN +3, and another useful field +2.

Penalties are unreadable sampled pages -2 each (cap -10), content not observable -8, conservative suspicious blanks -2 each (cap -8), exact repeated extra pages -4 each, likely repeated image pages -2 each (combined repeat cap -12), unusual permitted dimensions -3 each (cap -9), unusual resources -5 each (cap -15), partial unsupported font/filter facts -5 each (cap -15), and malformed/truncated metadata -1 per field (cap -3). More than 20% unreadable sampled pages and every hard file, structure, encryption, limit, timeout, memory, worker, protocol, or changed-file condition disqualify without a score.

Classification, sampling, text availability, image presence, action/link/attachment markers, outline absence, and scan/image-heavy presentation remain zero-adjustment evidence. File name, file size, and timestamps are not quality evidence. PDF scores never rank retained PDFs or change consolidation recommendations; incompatible scoring models are not compared silently.

## Special cases

Different editions, languages, translations, abridgements, and scanned historical editions must not be treated as interchangeable merely because one has a higher generic score.
