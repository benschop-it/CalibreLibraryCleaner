# Matching Corpus 1.0

`matching-corpus/1.0` is a closed-world JSON corpus for same-work/language
evaluation. Each document declares one `Calibration` or `Holdout` split and contains
ordered scenarios. Unknown JSON properties and mixed schema versions are rejected.

Every scenario contains opaque IDs, fixed tags, complete synthetic record facts,
optional content-comparison oracles, expected multi-record groups, and acceptable
keeper record IDs. Records with the same `workKey` and `expectedLanguage` are
positive pairs. Every other pair in that scenario is negative.

Optional `bibliographicResolutions` are per-record provider-result oracles containing
provider/version, disclosed query-field flags, retrieval time, status, and a bounded
work ID only for `Matched`. The evaluator derives and verifies the real query identity
from record facts; raw provider payloads are prohibited.

Content oracles map a canonical record-key pair to the exact public
`CandidateContentComparison` fields. Missing oracle data means unavailable evidence;
it never creates a candidate that the production generator did not retain.

Generated scenarios may set `repeat` from 1 through 100 and a positive
`calibreIdStride`. The loader expands each reviewed complete fixture into distinct
scenario, source-family, work, and Calibre IDs before validation and digesting. A
repeated scenario must declare `generationTemplateId` and
`generationTemplateVersion`; expansion never changes titles, authors, or labels.

Limits are 32 MiB/document, JSON depth 32, 10,000 expanded scenarios, 100,000 total
records, 100 records/scenario, 512 UTF-16 characters/string, 64 values per bounded
record list, 32 tags/scenario, and 4,950 content comparisons/scenario. Paths must be
relative, slash-separated, and free of traversal. Validation reports only stable
error codes and valid opaque scenario/record IDs.

Incompatible semantics require a new major schema version. Additive fields require a
minor version with explicit absence/default behavior. Baselines bind the canonical
expanded calibration and holdout digests.