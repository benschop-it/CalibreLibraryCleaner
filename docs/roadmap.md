# Roadmap

Calibre Library Cleaner has one remaining product finish line: complete a one-time
cleanup of a messy disposable Calibre library copy, accept the result, and use that
copy to replace the original library. The active
[execution plan](plans/finish-one-time-library-cleanup.md) governs implementation.

## Proven baseline

- Read-only Exact analysis with stable-observation SHA-256 reuse.
- Exact keeper review and supported worker-only cleanup.
- Residual reconciliation, bounded Expanded discovery, and disjoint Unified
  same-work/language Candidate groups.
- Unified keeper override and Skip followed by supported worker-only cleanup.
- External-backup confirmation, stop-on-ambiguity, and explicit Rescan after failure.
- Large-library acceptance on a 27,952-record disposable copy.
- Enabled-by-default Open Library same-work evidence with local fallback.
- Optional Ollama observations that do not affect grouping or cleanup.

Matching calibration is frozen. Metadata enrichment must not change the existing
grouping authority.

## Remaining sequence

1. Remove review ambiguity and prevent analysis/cleanup operation overlap.
2. Add provider-neutral rich edition contracts and cached Open Library metadata.
3. Add independent Google Books metadata and protected per-user API-key settings.
4. Select one coherent proposal per Unified group or singleton with deterministic
   confidence and default-selection policy.
5. Build scalable proposal review with filters, provenance, cover preview, and Apply
   overrides.
6. Apply only checked metadata through the fixed Calibre worker and verify read-back.
7. Publish a deterministic complete Windows release including the isolated PDF worker,
   then accept the packaged workflow on an explicitly backed-up disposable
   representative library. Package construction and smoke verification are complete;
   destructive workflow acceptance remains pending.
8. After explicit approval, normalize Calibre-managed names as a separate final
   action through supported Calibre behavior.

Implement and verify this sequence one step at a time. Provider failure leaves
metadata unchanged and never blocks duplicate cleanup.

## Explicitly not planned

- Further matching calibration, heuristic tuning, embeddings, local models, or new
  matching providers.
- Periodic verification, generalized cache architecture, incremental neighborhoods,
  background maintenance, or synchronization.
- Application-managed backup, undo, rollback, recovery, or resume.
- A second mutation engine, direct SQLite writes, or direct managed-file mutation.
- Cloud AI, OCR, book-content upload, plugins, multi-library comparison, or
  cross-platform work.
- Installer work beyond a practical Windows release ZIP unless requested.
