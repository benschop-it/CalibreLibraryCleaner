# Contain read-only regex timeouts

## Objective

Keep read-only parser timeouts from escaping UI commands and unexpectedly closing
the application. Preserve the prior bounded-matcher fix and report failure clearly.

## Scope

Regex timeout boundaries for scan, saved-state loading, Candidate/metadata/final
review preparation and read-only identity repair. Preserve displayed results,
release busy/operation resources and avoid success-shaped review publication.

## Out of scope

Global exception suppression, debugger configuration, arbitrary retries,
mutation/recovery changes, cache invalidation and personal-library verification.

## Relevant requirements

- [Architecture](../../architecture.md): presentation owns user-visible failures.
- [Safety](../../safety-and-rollback.md): analysis does not mutate; mutation uncertainty
  must not be disguised as a read-only failure.
- [Test strategy](../../test-strategy.md): progress, resource cleanup and privacy.

## Existing implementation inspected

The Domain's seven short timers have already been removed. Independently, WPF
workflow commands catch cancellation and selected argument/state exceptions, not
`RegexMatchTimeoutException`. CommunityToolkit async commands can forward an
uncaught task exception to WPF. The supplied output does not prove the precise
original call stack, but this is a concrete exception escape path.

## Proposed design

Catch only regex timeouts at read-only UI boundaries, report a static technical
error/action through the existing user-visible logging mechanism, and retain the
existing finally/operation-gate cleanup. Track mutation entry within the combined
workflow command so the new read-only catch never masks a mutation failure.
Do not add a global dispatcher handler, fabricated evidence or successful fallback.
If failed loading already replaced application-owned authoritative state, require
the currently displayed snapshot to match before enabling cleanup.

## Files expected to change

Main window view-model partials, focused WPF command tests and current documentation.

## Safety considerations

No exception input, pattern, payload or metadata in new errors/logs. No automatic
retry or continuation after failure. No mutation exception is reclassified as an
unchanged read-only operation. No user application process is terminated.

## Implementation steps

1. Inject regex timeouts through actual read-only command dependencies and reproduce
   the escaping task.
2. Add narrow reporting boundaries and preserve mutation exception behavior.
3. Verify retained results, gate/busy release, safe logging and explicit retry.
4. Build normal Debug outputs and run standard checks.

## Tests

Injected scan-reader, saved-state-load, Candidate preparation and online metadata
timeouts. Assert no faulted command, visible failure rather than readiness, retained
displayed rows/state, released operation gate, no mutation and no input/pattern
logging. Explicit metadata retry must succeed after the injected failure is removed.
Also cover a failing load that replaces authoritative state: retained display must
not enable cleanup until an explicit successful load presents the matching state.

## Verification commands

Focused WPF tests, full format verification, normal Debug build, full solution
tests, whitespace check and dependency vulnerability report.

## Risks

The combined Candidate command also performs mutation; a catch without an explicit
pre-mutation boundary could incorrectly enable continuation. Exception text may
contain sensitive data, so report only a static technical code and explanation.

## Unresolved questions

None for this boundary fix. The exact original production call stack remains
unavailable; do not claim the injected failure proves that precise call stack.

## Progress

- [x] Identified the concrete unhandled regex-timeout command path.
- [x] All four initial injected command cases reproduced unhandled regex timeouts.
- [x] Added narrow boundaries and stopped queued scan progress before failure
  reporting, so terminal errors are not overwritten by an earlier progress message.
- [x] Added displayed/authoritative snapshot gating for failed state replacement.
- [x] All 98 WPF tests passed, including five failure cases, safe logging, released
  resources and successful explicit metadata retry.
- [x] Normal Debug application rebuilt with zero warnings/errors.
- [x] Full solution passed: Domain 487, Application 325, Infrastructure 384,
  Architecture 34, WPF 98; total 1,328, zero skipped.
- [x] Format verification, whitespace check and dependency vulnerability report
  succeeded. Current documentation reconciled and execution plan archived.

## Final outcome

Completed narrow regex-timeout containment for read-only UI analysis. The new
failure paths report `ANALYSIS.REGEX_TIMEOUT`, do not fault the UI command or publish
review readiness, and release their busy/operation resources. Real `ICommand`
invocation is covered, including a successful explicit metadata retry. A failed
load that already advanced application-owned state cannot enable cleanup against
the earlier displayed snapshot.

The prior linear-time Domain matcher fix remains intact; matching/cache versions,
provider request policy and Calibre mutation behavior are unchanged. No global
exception suppression or mutation-error continuation was added. The tests
reproduce an actual command exception escape using injected regex timeouts; they
do not claim to identify the unavailable original production call stack.

Normal Visual Studio F5 outputs are current. No personal library, external provider,
alternate build output or process termination was used for verification.
