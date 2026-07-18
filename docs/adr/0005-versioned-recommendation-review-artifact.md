# ADR 0005: Version Deterministic Recommendation Review Artifacts

- Status: Accepted
- Date: 2026-07-18

## Context

Milestone 5 needs explainable recommendations and user review state without creating a cleanup plan or authorizing Calibre-library mutation. Generated evidence must remain distinguishable from user choices, and changed scan inputs must not silently reuse an obsolete review.

## Decision

Keep each immutable generated recommendation separate from its optional user override and effective reviewed selection. Overrides are session-only unless the user explicitly exports a review artifact. Use independent schema, recommendation-model, and canonical input-identity versions. A changed model or input identity retains the old override visibly as stale but does not apply it.

Export `recommendation-review/1.0` as deterministic UTF-8 JSON with sanitized library identity, relative managed paths, generated evidence, review state, and freshness. The Infrastructure exporter must reject the selected library root and all descendants, including reparse redirection, and publish through a temporary sibling file outside the library.

## Guardrails

- A review artifact is not a Milestone 6 cleanup plan and contains no removal, mutation, command, backup, approval, or execution ordering.
- No absolute library path, raw exception, parser object, book prose, or content snippet is serialized.
- Generated selections and evidence remain unchanged after review.
- Stale overrides have no effective final selection until reset or reapplied.
- Deterministic ordering and fixed timestamps produce byte-identical output.
- Export never creates a file inside the Calibre library.

## Consequences

Review work can be inspected and shared without enabling mutation. Session state is intentionally not durable except through explicit export, and changed inputs require renewed review.
