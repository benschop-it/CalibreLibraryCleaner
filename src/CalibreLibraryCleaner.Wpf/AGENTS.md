# WPF Project Instructions

These instructions extend the repository root `AGENTS.md`.

Use WPF with CommunityToolkit.Mvvm.

- Use MVVM.
- Keep code-behind limited to view-only behavior that cannot reasonably be expressed with bindings or behaviors.
- ViewModels must not directly access SQLite, the filesystem, ebook libraries, or external processes.
- Expose immutable or read-only collections.
- Keep the UI responsive; report progress and support cancellation.
- Display recommendation reasons and show confidence separately from quality score.
- Require explicit confirmation before cleanup-plan execution.
- Support keyboard-driven duplicate review.
- Present actionable errors and retain detailed logs.
- Provide keyboard access, clear labels, high-DPI support, and do not rely only on color.
