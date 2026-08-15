# Matching Corpus Sources

## Version 1

Most `matching-corpus-data/1.0` records are synthetic and were created for this
repository. Identifiers, paths, fingerprints, assessment results, and content
outcomes are test fixtures, not library exports or ebook content.

- Source IDs: `synthetic-calibration-v1`, `synthetic-holdout-v1`
- Provenance: deterministic, hand-reviewed scenario templates
- License: CC0-1.0
- Generation templates: `exact-variants/1.0`, `content-variants/1.0`,
  `identifier-contradictions/1.0`, `language-series/1.0`,
  `format-keeper-malformed/1.0`, and `candidate-cap/1.0`

Calibration and holdout use distinct opaque source-family and work IDs. They share
template algorithms only; no generated family crosses the split.

Two minimal independently sourced bibliographic families use Wikidata structured
main-namespace data, which Wikidata publishes under CC0-1.0:

- Calibration: `Q92640`, *Alice's Adventures in Wonderland*, Lewis Carroll.
- Holdout: `Q170583`, *Pride and Prejudice*, Jane Austen.
- Source pages: `https://www.wikidata.org/wiki/Q92640` and
    `https://www.wikidata.org/wiki/Q170583`, reviewed 2026-08-15.
- License policy: `https://www.wikidata.org/wiki/Wikidata:Licensing`, reviewed
    2026-08-15.

Only title, author, language, and stable source identity facts were retained. No
descriptions, quotations, book prose, images, or provider payloads are committed.
The edition-marker variants are synthetic and identified as such in scenario notes.