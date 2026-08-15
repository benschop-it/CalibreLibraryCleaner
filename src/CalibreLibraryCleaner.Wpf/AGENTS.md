# WPF Project Instructions

These instructions extend the repository root `AGENTS.md`.

Use WPF with CommunityToolkit.Mvvm.

- Use MVVM.
- Keep code-behind limited to view-only behavior that cannot reasonably be expressed with bindings or behaviors.
- ViewModels must not directly access SQLite, the filesystem, ebook libraries, or external processes.
- Expose immutable or read-only collections.
- Keep the UI responsive and continuously report truthful progress/elapsed time.
	Cancellation is optional unless the touched workflow already exposes it.
- Display recommendation reasons and show confidence separately from quality score.
- Require explicit external-backup confirmation before every mutation run.
- Unified Candidate groups are the executable surface; preserve keeper override and
	Skip for high-throughput same-work/language review.
- Support keyboard-driven duplicate review.
- Present actionable errors and retain detailed logs.
- Provide keyboard access, clear labels, high-DPI support, and do not rely only on color.
