# Quality Scoring and Keeper Ranking

## Separation of concerns

Matching decides whether records likely contain the same work and language. Quality
scoring decides which record/format is the most useful keeper or complementary
source. A high score never proves a match.

Every nonzero score contribution is an ordered, explainable finding with rule ID,
severity, adjustment, explanation, safe evidence, and scoring-model version.

## Assessment status

- **Completed:** sufficient facts; score is reproducible from findings.
- **Unassessed:** facts are insufficient; no invented score.
- **Disqualified:** definitive read/open/resource failure; no numeric score.

A Completed score may have an explicit ceiling. Unknown facts earn neither success
points nor absence penalties.

## EPUB model

The implemented EPUB model rewards strict/readable open, package metadata,
identifiers, cover, navigation, spine/resources/references, text, and chapter
structure. It penalizes missing/malformed metadata, cover/navigation problems,
empty/broken resources, low text, near-empty chapters, repeated references, and
bounded resource defects.

Definitive cannot-open/read outcomes are disqualified. Bounded fallback-readable
EPUBs remain Completed with warning penalties and a score ceiling of 70.

Analyzer, scoring, and resource versions are stored independently. Incompatible
scores are not silently compared.

## PDF model

The implemented PDF score has separate technical and embedded-metadata components.
It rewards strict open, valid page tree, sampled/all-page parse coverage, observable
content, resources within soft limits, outline, useful metadata, and valid ISBN
evidence. It penalizes unreadable sampled pages, unavailable content, suspicious
blank/repeated pages, unusual dimensions/resources, unsupported optional facts, and
malformed/truncated metadata.

PDF classification (`DigitalText`, scan-like, mixed, encrypted, empty, unreadable,
unknown) is evidence, not a score adjustment. Sampling coverage is explicit and does
not claim whole-document certainty.

## Metadata and record quality

Keeper ranking also uses record-level facts independent of ebook score:

- number of present formats;
- metadata completeness;
- validated strong identifiers;
- cover flag; and
- deterministic record-ID tie-breaking.

## Implemented keeper ranking

For Unified Candidate groups, rank in this order:

1. number of completed compatible assessments;
2. sum of compatible assessment scores;
3. present format count;
4. metadata completeness count;
5. valid strong identifier count;
6. cover presence; and
7. lowest Calibre ID.

The same facts rank complementary format sources where the selected keeper lacks a
format. The keeper's existing same-format file always wins.

The user may override the generated keeper or Skip the group.

## Future evidence and models

Online bibliographic evidence and local embeddings may improve matching and
metadata confidence, but they remain separate from generic format quality. Provider
or model confidence must not be folded into a format score without a versioned,
calibrated rule.

Future scoring work should prioritize:

- richer cover/visual usefulness;
- PDF comparison coverage and structure;
- edition/revision indicators for explanation;
- calibration against keeper-review outcomes; and
- performance/caching of model and provider evidence.

## Invariants

- Scores derive from findings.
- Matching confidence, format quality, metadata quality, and provider/model
  confidence remain distinct.
- A sole format is retained regardless of score unless the selected same-work group
  cleanup removes its non-keeper record under the documented policy.
- Missing/incompatible assessment evidence does not become zero quality.
- File name, path, timestamp, and size alone are not quality evidence.
- No score or model output directly starts mutation.
