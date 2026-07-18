# ADR 0004: Select a Bounded EPUB Inspection Stack

- Status: Accepted
- Date: 2026-07-16

## Context

Milestone 4 must inspect EPUB 2 and EPUB 3 files as untrusted ZIP-based input without extraction, network access, unbounded allocation, or third-party types crossing the Infrastructure boundary. Expected file and content failures must become deterministic findings, while cancellation must propagate.

## Decision

Use exactly `VersOne.Epub` 3.3.6 behind the Application-owned `IEpubInspector` boundary, with `HtmlAgilityPack` 1.12.4 for bounded, local HTML/XHTML inspection. Both packages are referenced only by Infrastructure.

Infrastructure performs a complete BCL `ZipArchive` safety preflight before invoking the EPUB parser. It enforces entry/path/count/declared-size/compression-ratio limits, reads only bounded resources, prohibits DTDs and external XML resolution, never extracts, and never dereferences content links. The VersOne eager whole-book API is forbidden; only lazy, stream-based access is allowed. Content downloading is disabled. Package/parser types, archive entries, streams, XML/HTML objects, and image types never leave Infrastructure.

After strict bounded preflight has validated the mandatory container and package XML, VersOne uses its `RELAXED` preset with content downloading explicitly disabled. The adapter additionally suppresses only missing EPUB 3 navigation manifest/file errors and missing spine manifest/content errors, because those exact conditions are collected independently as scored findings. `STRICT` is rejected for this assessment use case because it aborts on those scoreable defects before they can become findings; `IGNORE_ALL_ERRORS` remains forbidden. Unsupported encryption is classified during preflight before VersOne is invoked.

The initial V1 analyzer was `epub-inspector/1.0.0`. Its limits are those frozen in the Milestone 4 execution plan: 1 GiB file, 10,000 entries, 512 MiB declared aggregate, 64 MiB ordinary entry, 4 MiB XML, 8 MiB chapter, 2 MiB CSS, 32 MiB cover with a 64 KiB header read, 10,000 spine items, 50,000 local references, 100 retained evidence examples per rule, 20 million readable characters, and the documented compression-ratio limits.

The V1 scoring model is `epub-quality/1.0.0` with the rule weights, caps, 600-by-800 useful-cover threshold, 500/5,000 readable-character thresholds, 100-character near-empty chapter threshold, null-score disqualification semantics, and ISBN check-digit validation specified in the plan. A parsed empty spine remains scored with a severe penalty. Recognized IDPF and Adobe font obfuscation is informational only when it does not block analysis; other protection that prevents mandatory inspection disqualifies the EPUB. The shared finding severity vocabulary is extended with `Positive` and `Disqualifying`.

## Alternatives considered

- A BCL-only EPUB implementation was rejected because it would make this project own the full EPUB 2/3 compatibility model.
- Eager whole-book loading was rejected because it conflicts with bounded memory and responsive cancellation.
- General image decoding was rejected; bounded header parsing is sufficient for V1 cover dimensions.

## Consequences and guardrails

VersOne is not treated as a security boundary. Every archive is preflighted before parser use, actual reads remain bounded and cancellation-aware, and file identity is checked before and after inspection. The adapter catches expected parser, ZIP, XML, HTML, image, I/O, authorization, and unsupported-input failures and returns stable provider-neutral problem codes without raw exception messages or book content.

There is no runtime networking or extraction path. Dependency upgrades require review of release notes, license, transitive dependencies, and the full malformed/security fixture suite. Changes that can alter extracted facts or safety-limit behavior require an analyzer-version bump; scoring policy changes require a scoring-model-version bump.

## 2026-07-18 hardening amendment

Post-completion security review advanced the analyzer to `epub-inspector/1.0.1` without changing `epub-quality/1.0.0`. The adapter now rejects excessive central-directory counts before `ZipArchive` materializes entries, preflights bounded navigation/NCX XML before VersOne reads it, rejects raw archive traversal and reparse-point roots, caps HTML at 25,000 nodes and 256 nested levels, bounds every retained evidence collection, and redacts external-reference credentials and paths. These changes affect safety-limit and extracted-fact behavior and therefore require the analyzer-only version bump mandated above.
