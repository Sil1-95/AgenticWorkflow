# Contributing

## Build and test

```powershell
dotnet build
dotnet test                                                         # offline: no Herdr, no model; live tests are skipped
npm test --prefix integrations/herdr/agenticworkflow              # cockpit logic, no Herdr needed
```

Live tests (real Herdr, real Claude Code or Pi) are opt-in, skipped by default, and make real model calls. Run them only against a disposable, trusted fixture repository, never your own work. The direct-path live tests need `AGENTICWORKFLOW_LIVE_DIRECT=1` and `AGENTICWORKFLOW_LIVE_DIRECT_WORKSPACE=<repo>` (a repository you created, trusted and marked as disposable);
`AGENTICWORKFLOW_LIVE_PROTECTED_LOCATIONS` adds directories they must refuse to touch.

A running controller holds the build output's DLLs. Run `aw ctl stop` before rebuilding.

## Run from a source checkout

Prerequisites: the .NET 10 SDK and Node.js (for the cockpit's tests and its development installer), plus what a user needs (Git, Herdr with `hc`, Claude Code and/or Pi).

```powershell
dotnet build src/AgenticWorkflow.Cli -c Release
node integrations/herdr/agenticworkflow/install.mjs              # copies the cockpit into Herdr's mods folder, pointing at the build above
node integrations/herdr/agenticworkflow/install.mjs --link       # development only: link the folder so edits apply on save (no command-palette entry)
node integrations/herdr/agenticworkflow/install.mjs --uninstall
```

`aw doctor` reports a cockpit installed this way as an unstamped development copy; that is expected.

## Build the release package

```powershell
.\scripts\package.ps1
```

This publishes `aw` and the evidence hook self-contained for `win-x64` (the first run restores the runtime pack, so it needs network access), assembles the release from known locations only,
audits it (no source, tests, documentation history, developer paths, local state or debug binaries), and writes `artifacts\AgenticWorkflow-<version>-win-x64.zip`. The same input gives the
same ZIP, byte for byte. The package tests (`tests/AgenticWorkflow.Packaging.Tests`) build it and then run `install.ps1` and `uninstall.ps1` against isolated folders and a private registry key,
never your real `PATH`, Herdr home or run history.

The version lives in one place: `<Version>` in `Directory.Build.props`. `aw --version`, the controller, the package manifests and the cockpit's version stamp all derive from it; change it there
only.

## Platform

The release and its installer target Windows x64, tested on Windows 11. The controller also contains Unix-domain-socket code that has not been verified on Linux or
macOS; the installer, the package and `aw doctor` are Windows-only.

## Structure

```text
src/AgenticWorkflow.Core/                 domain kernel: tasks, revisions, gates, events, replay (no dependencies)
src/AgenticWorkflow.Application/          transaction boundary, controller protocol, TaskReadModel (Core only)
src/AgenticWorkflow.Persistence.Sqlite/   event store
src/AgenticWorkflow.Execution.Local/      Git fingerprints, verification runner
src/AgenticWorkflow.Runtime.Herdr/        Herdr process boundary, agent runtime, Claude and Pi adapters
src/AgenticWorkflow.ClaudeEvidenceHook/   hook host recording the agent's tool evidence
src/AgenticWorkflow.Cli/                  the aw executable and controller host
tests/                                    one test project per source project
integrations/herdr/agenticworkflow/       the Herdr cockpit (JavaScript; logic in lib/, tests in test/)
docs/ examples/                           documentation and an example configuration
```

Dependencies point inward: `Core ← Application ← adapters ← executables`. Core and Application never reference a transport, process or file. [docs/architecture.md](docs/architecture.md)
explains the design.

## Expectations

* Keep the layering. The cockpit never reads SQLite or run files; it only calls `aw ctl`. The controller has no operation that runs a string.
* Keep the safety properties in [SECURITY.md](SECURITY.md): no network listener, no natural-language approval, no auto-answering of native prompts, no substitution of profiles, no secrets in state.
* Match the surrounding code. C#: private fields are `_lowerCamelCase`, an `if` body goes on its own line, code is small, clear and SOLID. Prefer a focused fix over a redesign.
* Add a test with every behaviour change, offline where possible. Keep cockpit logic in `lib/` (unit-testable) and `main.js` thin.
* Documentation is current-state only: no milestone codes, no history. Update the one document that owns a concept instead of repeating it.
* Do not commit generated state, logs, databases or build output (see `.gitignore`).

## Working with AI agents

Agents working in this repository must not run `git commit` or `git push` unless the maintainer explicitly asks for that specific commit, and must not add AI attribution or session links to
commits, source or documentation.

## Cockpit acceptance testing

`install.mjs --driver` installs a development-only driver that presses the cockpit's own handlers through files, so a test needn't synthesize pointer input. Use it only against an isolated
Herdr home (`HERDR_APP_HOME`) and `AGENTICWORKFLOW_HOME`, never your real ones.
