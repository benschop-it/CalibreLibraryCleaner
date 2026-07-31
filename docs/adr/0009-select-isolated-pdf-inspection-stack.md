# ADR 0009: Select an Isolated Bounded PDF Inspection Stack

- Status: Accepted
- Date: 2026-07-31

## Context

Milestone 9 must inspect PDF files as untrusted input while keeping PDF parser
types out of Domain, Application, WPF, and serialized assessment values. PDF
parsers can perform synchronous, allocation-heavy work and do not expose a
complete cancellation or memory-quota boundary. Analysis must remain read-only,
must not execute active content, and must fail closed when required limits are
exceeded.

## Decision

Use exactly `PdfPig` 0.1.15, pinned centrally and referenced only by
`CalibreLibraryCleaner.Infrastructure`. Open only a canonical seekable
read-only `FileStream` and call the stream-based parser API. Never use a path or
byte-array parser overload.

The production inspector runs one disposable worker process per PDF. The
Application process selects the deterministic page sample after receiving
bounded header facts from the already-open worker session. The parent enforces
the `pdf-worker-protocol/1.0` message bounds, wall-clock, CPU, managed-heap, and
working-set limits and terminates only this read-only worker on cancellation,
timeout, resource breach, crash, or protocol violation. On Windows the worker
is assigned to a one-process kill-on-close Job Object with a process-memory
limit before the request is released.

Use strict parser behavior with `UseLenientParsing = false`,
`SkipMissingFonts = false`, `ClipPaths = false`, `UseActualText = true`, and
`MaxStackDepth = 64`. V1 accepts no user-supplied password. An empty-password
accessible encrypted file may be assessed but remains classified `Encrypted`;
password-required and unsupported encryption are disqualified.

The frozen profiles are:

- analyzer: `pdf-inspector/1.0.0`;
- scoring model: `pdf-quality/1.0.0`;
- classification policy: `pdf-classification/1.0.0`;
- sampling policy: `pdf-sampling/1.0.0`;
- resource profile: `pdf-limits/1.0.0`; and
- worker protocol: `pdf-worker-protocol/1.0`.

The hard and soft limits, deterministic 200-page sampling policy,
classification thresholds, 85/15 score components, rule weights, caps, and
disqualifiers are those frozen in
`docs/plans/milestone-9-pdf-assessment.md`. A limit or fact-semantics change
requires analyzer/profile review and versioning; a weight, cap, component,
formula, or disqualifier change requires scoring-model versioning.

## Forbidden behavior

- No PDF JavaScript, launch action, URI, form action, or other action is
  executed.
- No external resource is resolved or opened and no network API is used.
- Embedded-file markers may be counted, but attachment bytes, names, and
  extraction APIs are not accessed.
- Images are not rendered or decoded; only bounded placement and declared
  dimension facts are inspected.
- OCR is not performed and no OCR, renderer, native PDF codec, PDFium, MuPDF,
  Ghostscript, Skia, or image-codec package is added.
- Complete page text, XMP XML, image bytes, parser tokens, raw exceptions,
  absolute paths, links, actions, or attachment content are not retained or
  logged.
- The worker never writes into the Calibre library, invokes Calibre tooling, or
  receives cleanup/recovery authority.

## Consequences and guardrails

Process-per-file startup is accepted for V1 because it contains parser crashes
and allocations and allows prompt cancellation of a non-cooperative parser.
This is defense in depth, not a complete operating-system sandbox; the worker
does not have an OS firewall boundary. Strict parsing can reject a recoverable
PDF, which is preferred to silent undocumented repair.

Infrastructure performs canonical path and reparse validation, bounded PDF
header/tail preflight, read-only restrictive sharing, before/after observation
checks, parser/resource quotas, and bounded result validation. Changed-file
results discard all partial facts. PdfPig exceptions become closed
provider-neutral problem codes without raw messages.

Only Infrastructure source and Infrastructure tests may name PdfPig types.
The worker composition root references Infrastructure and contains no PdfPig
type. Dependency upgrades require license, release, source, transitive-package,
vulnerability, malformed-input, resource-containment, and architecture review.

PdfPig 0.1.15 does not expose the observed parser recursion depth or stable
indirect identities for page-content and image XObjects through the supported
high-level inspection APIs. V1 therefore enforces the hard parser depth of 64
but cannot emit the planned advisory at depth 48. It also reports insufficient
repeated-page evidence unless the complete bounded normalized-text rule can be
used; it does not decode/hash images or depend on unstable parser internals to
manufacture shared-object evidence. These limitations are disclosed in the
assessment/handoff and are preferable to weaker or non-deterministic evidence.

## Alternatives considered

PDFsharp does not provide the required extraction/page-fact surface. iText's
licensing is unsuitable for this project. Native/rendering stacks add a larger
execution and codec surface that Milestone 9 does not need. In-process PdfPig
inspection was rejected because hard cancellation and allocation containment
could not be enforced around every synchronous parser call.
