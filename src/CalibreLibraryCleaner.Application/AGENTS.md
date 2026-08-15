# Application Project Instructions

These instructions extend the repository root `AGENTS.md`.

- Depend only on Domain.
- Own use cases and interfaces implemented by Infrastructure.
- Do not reference SQLite, WPF, concrete filesystem APIs, ebook parser implementations, or process-launching implementations.
- Keep each use case focused on one user-visible outcome.
- Keep long operations asynchronous, bounded, and progress-reporting. Propagate an
	existing CancellationToken when the touched contract retains cancellation; do not
	introduce cancellation as a blanket product requirement.
- Own versioned cache, provider, and local-model ports; keep complete input/provenance
	identity explicit.
- Prefer indexed and incremental matching orchestration over unnecessary whole-
	library recomputation.
- Return structured findings for expected problems instead of throwing generic exceptions.
- Validate input at use-case boundaries.
- Preserve deterministic ordering where results are displayed or serialized.
- Do not implement UI concerns or external-process details.
