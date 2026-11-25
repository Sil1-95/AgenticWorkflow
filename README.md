# AgenticWorkflow

**Status: Alpha — 0.1.0**

AgenticWorkflow runs coding agents through a **human-gated, adaptive workflow**. You describe a task; an agent (Claude Code or Pi) works on it in your repository inside a Herdr session; a
controller verifies the result itself and stops at the points where a person should decide. The agent never gets to declare its own work finished.

## The problem it solves

A coding agent left alone proposes its own requirements, judges its own tests and says "done". AgenticWorkflow separates the two jobs:

* **The agent brings the intelligence**: it asks questions, proposes requirements and a strategy, and changes code in its own native session.
* **The controller holds the authority**: it records your decisions, runs the checks, enforces what the agent may touch, and is the only thing that can complete a task.
* **You decide at gates**: answer a question, approve a proposed WHAT or HOW, accept the verified result. The stages after the first turn are optional and added when the work warrants them
  (an independent REVIEW, a formal PROVE), by you or by the agent, and rigor is only ever added.

See [docs/concepts.md](docs/concepts.md).

## How the pieces relate

```text
You → Herdr cockpit → aw ctl → local IPC → AgenticWorkflow controller → Herdr session running Claude Code or Pi
```

* **Herdr** is the terminal workspace. The AgenticWorkflow cockpit is a Herdr app mod, and each agent runs in a Herdr session you can open and watch.
* **AgenticWorkflow** is a .NET 10 application: an event-sourced workflow engine, a per-user controller, and the `aw` command line.
* **Native harnesses** (Claude Code, Pi) stay native. Their own prompts (permissions, "trust this folder") are answered by you in their own session; AgenticWorkflow never answers them.

Each role (implementer, reviewer, prover) is explicitly bound to a profile: harness, model and reasoning. Nothing is chosen for you and nothing is substituted. See
[docs/architecture.md](docs/architecture.md).

## Supported

| | |
|---|---|
| Harnesses | **Claude Code** and **Pi** (tested against Pi 1.6.0), mixed freely across roles |
| Platform | **Windows x64** (tested on Windows 11) |
| Toolchains | `dotnet`, `flutter`, and `custom` for any other environment (for example Unity); verification checks are always your own commands |
| Requires | Windows x64, Git, Herdr with `hc` on `PATH`, Claude Code and/or Pi. The release includes everything else: no .NET SDK, no Node.js, no repository clone. |

## Safety and security

* The agent cannot complete a task: only the controller's own verification of the final working tree, and your ACCEPT when you require one, can.
* Approvals are bound to the exact proposal you saw and are refused when stale. Typed text is never an approval.
* Agents may change only the writable scope; protected paths and Git history (no commit, no push) are off limits.
* No HTTP or TCP listener. The controller is reachable only through a per-user named pipe, by typed operations only.
* No secrets are stored; profiles carry no commands, arguments or environment.
* AgenticWorkflow never answers an agent's native permission or trust prompt and never auto-approves.

See [SECURITY.md](SECURITY.md).

## Get started

1. Download `AgenticWorkflow-0.1.0-win-x64.zip` and extract it.
2. From the extracted folder, run the installer. It installs for your user only (no administrator rights), puts `aw` on your `PATH` and installs the Herdr cockpit:

   ```powershell
   .\install.ps1
   ```

3. Open a **new** terminal and run `aw doctor`.
4. Open Herdr.
5. Press **Ctrl+Shift+P** and run **Open AgenticWorkflow**.

Re-running `install.ps1` from a newer release is the update; `uninstall.ps1` removes it and keeps your run history. The full walkthrough (prerequisites, initializing a project, profiles, your
first task) is in [docs/getting-started.md](docs/getting-started.md). Building from source is in [CONTRIBUTING.md](CONTRIBUTING.md).

## Documentation

| | |
|---|---|
| [Getting started](docs/getting-started.md) | From the downloaded release to a first task |
| [Concepts](docs/concepts.md) | Stages, gates, bindings, verification, recovery |
| [Architecture](docs/architecture.md) | Cockpit, controller, IPC, state, supervision |
| [Configuration](docs/configuration.md) | `agenticworkflow.json`, profiles and role bindings ([example](examples/agenticworkflow.json)) |
| [Herdr cockpit](docs/herdr.md) | Using the cockpit |
| [Claude Code](docs/claude-code.md) | Claude-specific profiles, resume, permissions |
| [Pi](docs/pi.md) | Pi-specific profiles, behaviour and limitations |
| [Command line](docs/cli.md) | `aw` reference and exit codes |
| [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md) | Build from source, package, test, report |

## License

AgenticWorkflow is licensed under the [Apache License 2.0](LICENSE) (`Apache-2.0`).

**Development note:** AgenticWorkflow was designed and implemented with AI-assisted development, with human review and final technical and product decisions.
