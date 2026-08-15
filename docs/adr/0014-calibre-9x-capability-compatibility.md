# ADR 0014: Accept Capability-Compatible Calibre 9.x Releases

- Status: Accepted version range; capability mechanism amended by ADRs 0015 and 0017
- Date: 2026-08-02
- Amends: ADR 0007, ADR 0013

> Current interpretation: the bounded Calibre 9.x version range and executable
> identity checks remain current. The fixed worker handshake, not the removed
> CLI/recovery command list below, proves mutation capabilities.

## Context

The mutation boundary accepted only Calibre 9.11.0 even though it already probed every command and option used by the application. Calibre 9.12 introduced no documented breaking changes to `calibredb` cleanup commands and exposes the same required help surface.

Exact patch-version matching unnecessarily prevents users from applying routine Calibre updates.

## Decision

Accept installed Calibre versions in the bounded range `9.11.0 <= version < 10.0.0` under compatibility profile `calibredb/windows/9.x`.

Discovery must still successfully probe every command and option used by cleanup:

- global `--with-library`;
- `add_format` and `--dont-replace`;
- `remove_format`;
- non-permanent `remove` capability;
- `export`, `--dont-save-extra-files`, `--dont-update-metadata`, `--to-dir`, and `--single-dir`.

When recovery is enabled, its additional `add --empty --title --authors` and `set_metadata --field` probes remain mandatory.

Tool identity records the actual installed product version and executable hash. Every command invocation revalidates the executable path, hash, compatible version range, capability profile, and required typed capability.

Calibre versions below 9.11 and Calibre 10 or later remain blocked. Supporting a new major release requires reviewing its release notes and updating the bounded profile.

## Consequences

- Calibre 9.11 and 9.12 work without application changes or configuration overrides.
- Future Calibre 9.x patch/minor releases can work when their required command probes pass.
- A command or option removed within Calibre 9.x still fails closed during discovery.
- Major-version changes remain explicit rather than silently accepted.

## Rejected alternatives

- Continue exact 9.11.0 matching.
- Add a second exact 9.12.0 profile with duplicated policy.
- Accept all future Calibre versions based only on process version.
- Skip runtime command/option probes.