# Domain Project Instructions

These instructions extend the repository root `AGENTS.md`.

- Do not reference SQLite, WPF, filesystem APIs, Calibre CLI, EPUB/PDF libraries, logging frameworks, or dependency-injection frameworks.
- Prefer immutable records and readonly collections.
- Model invariants explicitly.
- Use strongly typed IDs where accidental mixing is plausible.
- Avoid public setters and mutable collection exposure.
- Do not add persistence attributes.
- Keep scoring and confidence calculations deterministic and explainable.
- Keep matching evidence, contradictions, coverage, provider/model versions, and
	provenance explicit and provider-neutral.
- Keep matching identity separate from quality/keeper scoring and mutation authority.
- Do not access system time directly when an application-provided clock is appropriate.
