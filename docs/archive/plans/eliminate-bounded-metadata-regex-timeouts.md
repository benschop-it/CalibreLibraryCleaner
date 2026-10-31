# Eliminate bounded metadata regex timeouts

## Objective

Prevent Domain metadata checks from interrupting review with
`RegexMatchTimeoutException`, preserving existing matching and author-write rules.

## Scope

The seven explicitly timed generated regexes in pseudonym detection/removal,
provider pseudonym withholding and record identity triage. Keep their existing
bounded inputs and match decisions, using non-backtracking linear-time execution.

## Out of scope

Changing bibliographic matching, names, cache identities, provider requests,
mutation, user data, unrelated infrastructure regexes or debugger settings.

## Relevant requirements

- [Architecture](../../architecture.md): shared author preparation and evidence rules.
- [Test strategy](../../test-strategy.md): malformed/resource bounds and frozen matching.
- [ADR 0021](../../adr/0021-matching-first-performance-oriented-product.md): bounded
  resources and responsive operations.

## Existing implementation inspected

The reported debug output identifies the Domain generated-regex document, but not
the particular expression or call stack. Seven Domain checks use 50-100 ms timers
despite bounded fields. Other Domain generated typography regexes have no explicit
timer. Exit code zero indicates normal program closure, not the timeout's origin.

## Proposed design

Use `RegexOptions.NonBacktracking` without a wall-clock timeout for the seven
bounded expressions. Replace lookahead-only marker boundaries with equivalent
consumed boundaries where callers only inspect success. The bare suffix's
lookahead is already enforced by its whitespace/end-anchored remainder and can
be removed without changing match start or length. Keep invariant ignore-case
behavior, field bounds, versions and public decisions unchanged.

## Files expected to change

Three Domain metadata policies, generated parity/resource regression tests and
directly related current documentation.

## Safety considerations

Never turn timeout into "no marker" or clean identity evidence. Do not catch and
suppress errors, raise timeouts, expand inputs or permit unsafe author writes.
Linear-time execution plus existing field bounds replaces timer-based control.

## Implementation steps

1. Add regressions for actual matcher engine/resource configuration and differential
   legacy behavior, using generated inputs only.
2. Change all seven explicitly timed Domain expressions without changing semantics.
3. Validate public maximum-length/over-limit decisions and all matching holdouts.
4. Build normal Debug outputs and run standard verification.

## Tests

Compare all seven actual matchers against their original expressions across
generated markers, near misses, whitespace, punctuation, Unicode and maximum-length
fields. Assert suffix match spans, linear-time engine/no timer and public
pseudonym-withholding/triage bounds. Run existing Domain and full solution suites.

## Verification commands

Focused Domain tests, full Domain suite, `dotnet format --verify-no-changes
--no-restore`, normal `dotnet build --no-restore`, full `dotnet test --no-build
--maxcpucount:1`, `git diff --check`, dependency vulnerability report.

## Risks

Non-backtracking regexes do not support lookahead. Boundary rewrites must preserve
success and bare-suffix spans. No claim that a particular pattern or input caused
the reported timeout is possible from the provided debugger output.

## Unresolved questions

The exact timed-out matcher is unknown. Cover all seven explicit Domain timeout
sites rather than assume which one ran. No user book data is needed.

## Progress

- [x] Inspected Domain timeout sites and existing public bounds.
- [x] The engine/timer contract failed before changes; generated legacy comparison
  and public boundary cases passed. The reported timeout itself was not reproduced
  without its call stack/input.
- [x] All seven matchers now use bounded linear-time execution without timers.
- [x] All 487 Domain tests passed, including matching holdouts and new differential
  resource/semantics checks. Policy/cache versions remain unchanged.
- [x] Normal Debug application rebuilt: zero warnings and errors.
- [x] Full solution: Domain 487, Application 325, Infrastructure 384, Architecture 34,
  WPF 93; all 1,323 passed, zero skipped.
- [x] Full format verification and dependency vulnerability report succeeded.
- [x] Reconciled current documentation and archived the completed plan.

## Final outcome

Implemented linear-time matching for every explicitly timed Domain metadata regex,
preserving field bounds, invariant case handling, marker decisions and suffix spans.
No timeout catch/default, longer timer, cache invalidation or personal-library
verification was introduced. Existing generated file-cache reuse and matching
holdout tests pass unchanged. Normal Visual Studio F5 outputs are current.

The supplied debugger output did not identify the individual failing pattern, so
the exact production exception/input was not reproduced. The baseline resource
contract regression failed as expected; generated semantic comparisons passed
before and after the engine change. The verified change removes the short-timer
failure mode from all seven candidate Domain sites rather than claiming a specific
one was proven responsible.
