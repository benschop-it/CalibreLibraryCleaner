# Windows Release Package and Acceptance

## Deployment decision

The release is framework-dependent `win-x64`. On 2026-08-16, independently published
and collision-free WPF/PDF-worker payloads measured:

| Deployment | Files | Uncompressed size |
| --- | ---: | ---: |
| Framework-dependent | 79 | 23.18 MiB |
| Self-contained | 505 | 230.36 MiB |

The one-time application already requires installed Calibre. Requiring the .NET 10
Desktop Runtime x64 keeps the ZIP practical and avoids about 207 MiB of duplicated
runtime. WPF files remain at package root; the complete PDF worker publish remains in
`pdf-worker/` because flat merging produced a real same-named dependency collision.

## Prerequisites

- Supported Windows x64 (Windows 10 or Windows 11).
- .NET 10 Desktop Runtime x64.
- Calibre `9.11.0` or newer within the supported 9.x range; Calibre 10 is not accepted.
- Calibre, calibre-server, viewers, editors, and synchronization writers closed during
  mutation.
- A caller-selected disposable representative library, never a personal production
  library.
- A complete external backup explicitly confirmed immediately before destructive
  packaged acceptance.
- Live provider requests disabled unless the caller separately approves them. Use
  compatible cached proposals or controlled fake-provider evidence for acceptance.

## Build and automated verification

```powershell
.\scripts\Build-ReleasePackage.ps1 -Version 0.1.0
.\scripts\Test-ReleasePackage.ps1 -ZipPath .\bin\releases\CalibreLibraryCleaner-0.1.0-win-x64-framework-dependent.zip
```

The builder restores and publishes WPF and PDF worker independently. It does not rely
on the WPF post-build worker copy. It rejects symbols, tests, caches, credentials,
logs, library state, temporary cover staging, temporary files, and source-machine
paths. `release-manifest.json` lists every payload file except itself with normalized
relative path, byte size, and lowercase SHA-256.

The verifier rejects duplicate/unsafe ZIP entries, checks every manifest size/hash,
required runtime files, PE headers, dependency graphs, exclusions, and both .NET 10
runtime declarations. It
then runs `CalibreLibraryCleaner.Wpf.exe --release-smoke` directly from an extracted
package. That no-UI path verifies the complete PDF-worker layout and fixed embedded
Calibre mutation worker without initializing logs, state, providers, or child workers.
The smoke timeout is 15 seconds, above the measured 8,031 ms verification
time while remaining bounded on a slow workstation.

## Current package evidence

| Field | Actual value |
| --- | --- |
| Version | `0.1.0` |
| ZIP | `CalibreLibraryCleaner-0.1.0-win-x64-framework-dependent.zip` |
| Compressed bytes | 9,308,644 |
| Payload files in manifest | 79 |
| ZIP SHA-256 | `fb11d863ef9dae39f698f84ac2e94ca712519bf639293cd2b5877a67c90c9f09` |
| Reproducibility | Two complete builds produced the identical ZIP hash |
| Direct packaged smoke | Passed in 8,031 ms |
| Smoke final state | 0 new processes; 0 new extraction directories |
| Live provider requests | None |
| Destructive library mutation | Exact cleanup completed; Candidate mutation not started |

## Interrupted acceptance attempt

The first packaged attempt used the caller-confirmed disposable library and current
external backup. Actual results before stopping were:

- Exact analysis: 27,952 books, 29,235 formats, 5,750 groups, 170,761 ms.
- Exact cleanup: 13,542 operations, 6,777 formats removed, 6,762 records removed,
  3 records merged, 1 skipped, 247,938 ms.
- Post-Exact catalog: 21,190 records; no worker/database lock; no cover staging residue.
- Local Candidate analysis: 21,190 records, 31 Exact metadata groups, 3,189 Expanded
  groups, 3,190 Unified groups, 129,882 ms.
- Deviation: the old package began Open Library enrichment before Unified cleanup.
  The user canceled; no Candidate or metadata mutation began, and no child process
  remained.

The workflow was corrected to `staged-cleanup/1.1.0`: Candidate preparation/load makes
zero edition-provider calls, Candidate cleanup advances to a post-cleanup checkpoint,
and online enrichment starts only through an explicit action for retained records.
The corrected package evidence above supersedes the old package hash.

During later visual review acceptance, cache reuse was confirmed but another defect
was observed: at 11,834 of 17,463 Open Library queries, 11,081 were cached and 752 were
online, then capped misses were rapidly counted as complete and the progress bar jumped
to 100%. The run was canceled with 12,319 cache entries retained. The resolver now
caches transient unavailable results for six hours, preserves immediate per-query
writes, stops at the true processed count on the request cap, and reports uncached
deferred queries so a later run can continue honestly from cache.

The next run completed online queries and retained 17,748 cache entries, then crashed
while displaying the new selected-proposal detail pane. Windows `.NET Runtime` event
1026 reported `InvalidOperationException`: a default TwoWay `TextBox.Text` binding
attempted to write the getter-only `Reasons` property. `Reasons` and `Provenance` are
now explicit OneWay bindings and the XAML architecture guard requires them. Exit-state
verification found no library change, cache temporary file, cover staging directory,
or child process.

## Destructive packaged acceptance gate

Do not continue until the caller supplies the exact disposable library path and
explicitly confirms a complete current external backup. Earlier API-spike permission
does not satisfy this gate.

After confirmation, record actual start/end timestamps and phase timings for:

1. packaged executable launch;
2. Exact analysis and reviewed Exact cleanup;
3. Candidate preparation and Unified review;
4. offline fake/cached online proposal review;
5. checked metadata plus Candidate cleanup;
6. packaged restart and persisted-state load;
7. Calibre opening the cleaned result; and
8. final catalog/file/process/lock/temp/credential/mutation-residue checks.

Record deviations and failures exactly. Stop on failed or ambiguous mutation and
require Rescan. Do not infer a successful prefix. Do not start filename/folder
normalization or any step 9 behavior.