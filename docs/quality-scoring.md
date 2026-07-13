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

## Separate scores

Keep metadata quality separate from each format's quality. A record recommendation may select metadata from one record and EPUB/AZW3/PDF from others.

## Explainability

Prefer explicit reasoning over a single number. Example: keep EPUB A because it has navigation, a useful cover, complete resources, and equivalent text; reject EPUB B because it has broken references and a lower structural score.

## Special cases

Different editions, languages, translations, abridgements, and scanned historical editions must not be treated as interchangeable merely because one has a higher generic score.
