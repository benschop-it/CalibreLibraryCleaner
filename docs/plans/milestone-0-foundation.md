# Milestone 0 Foundation

## Objective

Establish the documented .NET 10 solution foundation for Calibre Library Cleaner without implementing product functionality.

The milestone must create the four production projects and four test projects described in `docs/architecture.md`, enforce dependency direction, centralize package and build settings, enable nullable reference types and analyzers, add formatting configuration, add initial test infrastructure, and verify restore, build, format, and tests.

## Scope

- Replace the current single root console project shape with the documented `src/` and `tests/` project topology.
- Target .NET 10.
- Add central package management.
- Enable nullable reference types, implicit usings where appropriate, deterministic build settings, analyzers, and formatting rules.
- Add dependency injection and structured logging packages where appropriate for Application, Infrastructure, and WPF composition.
- Add WPF with CommunityToolkit.Mvvm for future UI work, but only with a minimal non-functional shell.
- Add xUnit, FakeItEasy, FluentAssertions, and architecture test dependencies.
- Add architecture tests that enforce documented project boundaries.
- Run the required verification commands and record results in this plan.

## Out of scope

- Calibre database access.
- Duplicate detection.
- Hashing.
- EPUB or PDF analysis.
- Cleanup plans.
- Cleanup execution.
- Rollback.
- Functional WPF screens.
- Real Calibre library fixtures.
- AI integration.
- Any direct mutation of Calibre databases or Calibre-managed files.

## Relevant requirements

- `AGENTS.md`: preserve safety rules, clean architecture direction, nullable reference types, DI, structured logging, xUnit, FakeItEasy, FluentAssertions, and required verification commands.
- `PLANS.md`: keep this execution plan current and use the required plan sections.
- `docs/roadmap.md`: Milestone 0 is foundation only.
- `docs/architecture.md`: create `Domain`, `Application`, `Infrastructure`, and `Wpf` projects plus matching test projects and enforce dependency direction.
- `docs/test-strategy.md`: establish xUnit/FakeItEasy/FluentAssertions and architecture tests without using a real user library.
- `docs/adr/0001-read-only-calibre-access.md`: no database write capability is introduced in this milestone.
- `docs/adr/0002-clean-architecture.md`: project references and tests must enforce the clean architecture boundaries.
- `docs/adr/0003-calibre-cli-for-writes.md`: no Calibre mutation path is introduced in this milestone.
- Nested `AGENTS.md` files: each project must follow its local constraints.

## Existing implementation inspected

- `CalibreLibraryCleaner.sln`: currently contains a single root `CalibreLibraryCleaner` project.
- `CalibreLibraryCleaner.csproj`: currently a .NET 10 console app with nullable enabled.
- `Program.cs`: currently prints `Hello, World!`.
- `src/CalibreLibraryCleaner.Domain/AGENTS.md`: contains Domain-specific constraints, but no project file yet.
- `src/CalibreLibraryCleaner.Application/AGENTS.md`: contains Application-specific constraints, but no project file yet.
- `src/CalibreLibraryCleaner.Infrastructure/AGENTS.md`: contains Infrastructure-specific constraints, but no project file yet.
- `src/CalibreLibraryCleaner.Wpf/AGENTS.md`: contains WPF-specific constraints, but no project file yet.
- `tests/AGENTS.md`: contains test constraints, but no test projects yet.
- `.NET SDK 10.0.301` is installed.

## Proposed design

Create a conventional multi-project .NET 10 solution:

```text
src/
  CalibreLibraryCleaner.Domain/
  CalibreLibraryCleaner.Application/
  CalibreLibraryCleaner.Infrastructure/
  CalibreLibraryCleaner.Wpf/

tests/
  CalibreLibraryCleaner.Domain.Tests/
  CalibreLibraryCleaner.Application.Tests/
  CalibreLibraryCleaner.Infrastructure.Tests/
  CalibreLibraryCleaner.Architecture.Tests/
```

Project references:

- `Application` references `Domain`.
- `Infrastructure` references `Application`.
- `Wpf` references `Application` and `Infrastructure`, with Infrastructure usage limited to composition.
- `Domain.Tests` references `Domain`.
- `Application.Tests` references `Application` and `Domain`.
- `Infrastructure.Tests` references `Infrastructure`, `Application`, and `Domain`.
- `Architecture.Tests` references all production assemblies for dependency assertions only.

Build configuration:

- Use `Directory.Packages.props` for package versions.
- Use `Directory.Build.props` for shared settings such as nullable, analyzers, warnings, deterministic builds, and central package management expectations.
- Use `.editorconfig` for formatting and analyzer rules.
- Use `global.json` to pin the .NET 10 SDK roll-forward policy to installed compatible SDKs.

Initial code:

- Production projects should contain only minimal assembly marker types or placeholders needed for compilation and architecture tests.
- WPF should contain only a minimal app shell required to build, with no functional screens.
- No Domain entities, Application use cases, Infrastructure Calibre access, or cleanup behavior should be implemented.

Testing:

- Add smoke tests proving each project assembly is loadable.
- Add architecture tests proving dependency direction and banned references.
- Keep all tests deterministic and offline.

## Files expected to change

- `docs/plans/milestone-0-foundation.md`: this execution plan.
- `global.json`: pin .NET SDK behavior for repeatable .NET 10 builds.
- `Directory.Build.props`: central shared MSBuild settings.
- `Directory.Packages.props`: central package versions.
- `.editorconfig`: formatting and analyzer configuration for `dotnet format --verify-no-changes`.
- `CalibreLibraryCleaner.sln`: update solution membership to the documented projects.
- `CalibreLibraryCleaner.csproj`: remove during implementation because the root console project is not part of the documented architecture.
- `Program.cs`: remove during implementation with the root console project.
- `src/CalibreLibraryCleaner.Domain/CalibreLibraryCleaner.Domain.csproj`: Domain class library.
- `src/CalibreLibraryCleaner.Domain/AssemblyMarker.cs`: minimal type to make the assembly explicit for tests.
- `src/CalibreLibraryCleaner.Application/CalibreLibraryCleaner.Application.csproj`: Application class library referencing Domain.
- `src/CalibreLibraryCleaner.Application/AssemblyMarker.cs`: minimal type to make the assembly explicit for tests.
- `src/CalibreLibraryCleaner.Infrastructure/CalibreLibraryCleaner.Infrastructure.csproj`: Infrastructure class library referencing Application and carrying integration packages appropriate for future implementation.
- `src/CalibreLibraryCleaner.Infrastructure/AssemblyMarker.cs`: minimal type to make the assembly explicit for tests.
- `src/CalibreLibraryCleaner.Wpf/CalibreLibraryCleaner.Wpf.csproj`: WPF executable referencing Application and Infrastructure.
- `src/CalibreLibraryCleaner.Wpf/App.xaml`: minimal WPF application declaration.
- `src/CalibreLibraryCleaner.Wpf/App.xaml.cs`: composition-root placeholder.
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml`: minimal empty shell window.
- `src/CalibreLibraryCleaner.Wpf/MainWindow.xaml.cs`: view-only window initialization.
- `tests/CalibreLibraryCleaner.Domain.Tests/CalibreLibraryCleaner.Domain.Tests.csproj`: Domain test project.
- `tests/CalibreLibraryCleaner.Domain.Tests/AssemblySmokeTests.cs`: verifies Domain assembly availability.
- `tests/CalibreLibraryCleaner.Application.Tests/CalibreLibraryCleaner.Application.Tests.csproj`: Application test project.
- `tests/CalibreLibraryCleaner.Application.Tests/AssemblySmokeTests.cs`: verifies Application assembly availability.
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/CalibreLibraryCleaner.Infrastructure.Tests.csproj`: Infrastructure test project.
- `tests/CalibreLibraryCleaner.Infrastructure.Tests/AssemblySmokeTests.cs`: verifies Infrastructure assembly availability.
- `tests/CalibreLibraryCleaner.Architecture.Tests/CalibreLibraryCleaner.Architecture.Tests.csproj`: architecture test project.
- `tests/CalibreLibraryCleaner.Architecture.Tests/DependencyDirectionTests.cs`: verifies project dependency rules and prohibited references.

## Safety considerations

- This milestone does not access Calibre libraries, `metadata.db`, ebook files, or Calibre CLI.
- No database, filesystem cleanup, backup, or mutation behavior is introduced.
- Architecture tests should make future unsafe dependency drift visible early.
- Infrastructure may receive package references for logging and dependency injection, but no Calibre integration code is added.
- WPF may reference Infrastructure only because `docs/architecture.md` permits it for composition; architecture tests must prevent broader leakage where practical.

## Implementation steps

1. Create shared build files: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, and `.editorconfig`.
2. Remove the root console project from the solution and delete its default scaffold files.
3. Create the four production projects under `src/`.
4. Add allowed project references that match the documented dependency direction.
5. Add minimal compile-only marker or shell files.
6. Create the four test projects under `tests/`.
7. Add test package references through central package management.
8. Add architecture tests for dependency direction and prohibited references.
9. Run `dotnet restore`.
10. Run `dotnet build --no-restore`.
11. Run `dotnet test --no-build`.
12. Run `dotnet format --verify-no-changes`.
13. Review the complete diff against `AGENTS.md`, `PLANS.md`, `docs/architecture.md`, and accepted ADRs.
14. Update this plan's progress and final outcome.

## Tests

- Domain smoke test confirms the Domain assembly is present.
- Application smoke test confirms the Application assembly is present.
- Infrastructure smoke test confirms the Infrastructure assembly is present.
- Architecture tests confirm:
  - Domain references no other solution project.
  - Application references Domain and not Infrastructure or WPF.
  - Infrastructure references Application and not WPF.
  - WPF references Application and Infrastructure.
  - Domain has no references to integration technologies such as SQLite, WPF, filesystem-specific packages, ebook parser packages, logging, or dependency injection.
  - Application has no references to SQLite, WPF, ebook parser implementations, or process-launching implementation packages.

## Verification commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```

Do not claim these succeeded until they are actually run during implementation.

## Risks

- WPF on .NET 10 requires Windows desktop targeting and may need careful SDK settings to build cleanly.
- Architecture tests can become brittle if they assert implementation details instead of assembly dependency boundaries.
- `dotnet format --verify-no-changes` can fail if generated WPF files or IDE settings are unintentionally included; configuration should target source files and standard analyzer rules.
- Removing the root console project is a structural change, but it aligns the repo with the documented architecture.

## Resolved questions

- The old root project files in the repository can be deleted.
- The WPF project can use a minimal shell so the project builds as a desktop app without product behavior.
- Regarding the exact architecture test library to use: prefer a lightweight, actively maintained library if available through NuGet; otherwise, reflection-based assembly reference tests are sufficient for Milestone 0.

## Progress

- [x] Read root `AGENTS.md`.
- [x] Read `PLANS.md`.
- [x] Read `docs/roadmap.md`.
- [x] Read `docs/architecture.md`.
- [x] Read `docs/test-strategy.md`.
- [x] Read all accepted ADRs.
- [x] Read relevant nested `AGENTS.md` files.
- [x] Inspect existing solution, project, and default program files.
- [x] Create Milestone 0 execution plan.
- [x] Implement Milestone 0 foundation.
- [x] Run restore, build, tests, and format verification.
- [x] Review complete diff.

Implementation notes:

- The first `dotnet build --no-restore` failed because the test sources did not explicitly import xUnit and the WPF `Application` base type conflicted with the solution's Application namespace. The scaffold was corrected with explicit imports and fully qualified WPF base types.
- The next build reached all projects and failed on analyzer rule `CA1859` in the architecture test helper. Its private return type was narrowed from `IReadOnlyCollection<string>` to `string[]`; no analyzer rule was suppressed.
- The first `dotnet format --verify-no-changes` reported LF line endings in the newly added C# files while `.editorconfig` requires CRLF. `dotnet format` normalized the files before the final verification sequence.
- Diff review found that the architecture-test XML helper treated dotted package IDs as filenames. It was corrected to remove extensions only from project-reference paths before final verification.
- The first build after that review correction failed nullable analysis because LINQ did not infer non-null values after the explicit whitespace guard. A null-forgiving annotation now documents the guarded invariant.

## Final outcome

Completed on 2026-07-13.

- Adapted the existing solution to contain the four production projects and four test projects under the documented `src/` and `tests/` folders.
- Added .NET 10 SDK selection, central package management, nullable reference types, deterministic builds, analyzers-as-errors, and formatting configuration.
- Added the documented project references, DI and structured-logging abstractions in Infrastructure, WPF hosting and MVVM packages, and xUnit/FakeItEasy/FluentAssertions in all test projects.
- Added three assembly smoke tests and seven architecture tests covering project-reference direction and prohibited core dependencies.
- Added only assembly markers and a minimal compile-only WPF shell. No Calibre access, duplicate detection, ebook analysis, cleanup, or mutation behavior was introduced.
- Final `dotnet restore` succeeded.
- Final `dotnet build --no-restore` succeeded with zero warnings and zero errors.
- Final `dotnet test --no-build` succeeded: 10 passed, 0 failed, 0 skipped.
- Final `dotnet format --verify-no-changes` succeeded.
- Complete diff review found no conflict with the architecture or accepted ADRs.
