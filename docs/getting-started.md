# Getting started

This is the practical path from a downloaded release to a first task. For the ideas behind it, see [concepts.md](concepts.md); you do not need them to start. The supported platform is
Windows x64 (tested on Windows 11). To build from source instead, see [CONTRIBUTING.md](../CONTRIBUTING.md).

## 1. Prerequisites

| Need | Notes |
|---|---|
| Windows x64 | Tested on Windows 11. |
| Git | The workspace must be a Git repository root. AgenticWorkflow reads Git state and never commits, pushes or changes history. |
| Herdr app and `hc` on `PATH` | Hosts the agents. The cockpit is a Herdr app mod. |
| An agent harness | Claude Code (`claude` on `PATH`), [Pi](pi.md) (`pi` on `PATH`), or both. |

You do **not** need the .NET SDK or runtime, Node.js, or a clone of this repository: the release is self-contained.

Versions this release was tested with: Herdr `hc` 0.1.22, Claude Code 2.1.332, Pi 1.6.0.

Open the target repository once in the harness and trust it yourself. AgenticWorkflow never accepts a trust prompt for you; if a harness asks while starting, you answer it in the agent's own
session (step 8).

## 2. Install

1. Download `AgenticWorkflow-0.1.0-win-x64.zip` and extract it anywhere (for example your Downloads folder).
2. In PowerShell, from the extracted folder:

   ```powershell
   .\install.ps1
   ```

3. Open a **new** terminal (a terminal that was already open keeps its old `PATH`) and check the installation:

   ```powershell
   aw --version
   aw doctor
   ```

If Windows refuses to run the script (*running scripts is disabled*, or a warning about a downloaded file), do either of these once, then run `.\install.ps1` again:

```powershell
Get-ChildItem -Recurse . | Unblock-File                                    # mark the extracted files as trusted
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1           # or: run this one script with a one-time policy override
```

`install.ps1` checks the release against its manifest, stops an idle AgenticWorkflow controller (and refuses, changing nothing, if one is supervising running tasks), then reports exactly what it
installed and where:

| What | Where |
|---|---|
| Application (`aw`, the evidence hook, the runtime) | `%LOCALAPPDATA%\Programs\AgenticWorkflow\bin`, which is added to your user `PATH` |
| Run history and controller state | `%LOCALAPPDATA%\AgenticWorkflow` (never touched by install, update or a normal uninstall) |
| Herdr cockpit | `<Herdr home>\mods\agenticworkflow`, a copy, with its settings in `<Herdr home>\agenticworkflow`. Herdr home is `%HERDR_APP_HOME%` if set, otherwise `~\.config\herdr-app`. |

Options: `-InstallDir`, `-HerdrHome`, `-NoPath` (leave `PATH` alone), `-NoCockpit`, `-AllowDowngrade`.

**`aw doctor`** prints one line per check: `PASS`, `WARN` (an optional thing is missing or unusual) or `FAIL` (normal operation is not possible), with what to do about it. It checks the
installation, the state folder, the controller, Git, Herdr, Claude Code and Pi (you need at least one), the cockpit, and, inside a project, its profiles. It starts no agent and makes no model
call. Exit code 33 means a check failed.

### Update

Extract the newer release and run its `install.ps1`. Your run history and project configuration are kept; the application and the cockpit are replaced and no file of the old version is left
behind. An installed release is never silently replaced by an older one (`-AllowDowngrade` overrides that deliberately).

### Uninstall

```powershell
& "$env:LOCALAPPDATA\Programs\AgenticWorkflow\uninstall.ps1"
```

This removes the application, the `PATH` entry and the cockpit, and nothing else; other Herdr mods and settings are never touched. Your run history stays in
`%LOCALAPPDATA%\AgenticWorkflow`. `-PurgeState` deletes it too, after an explicit confirmation, and cannot be undone.

## 3. Open the cockpit

Open Herdr (with a space open), press **Ctrl+Shift+P** and run **Open AgenticWorkflow**. Details and troubleshooting: [herdr.md](herdr.md).

## 4. Initialize a project

In the cockpit, choose the project folder; if it has no configuration the cockpit offers **Set up this project**. Initialization writes `agenticworkflow.json` from choices you confirm: toolchain, a profile for each role,
the verification checks, the writable and protected paths, and whether a final human ACCEPT is required. Nothing is guessed on your behalf, and an existing file is never overwritten.

The toolchain is `dotnet`, `flutter` or `custom`. Use `custom` for any other environment (for example a Unity project): no build or test command is assumed, so the checks you enter are the
whole verification ([configuration.md](configuration.md#toolchains)).

Prefer files? Copy [`examples/agenticworkflow.json`](../examples/agenticworkflow.json) into the repository root and edit it. [configuration.md](configuration.md) describes every setting.

```powershell
aw show --workspace <repo>        # validates the configuration and prerequisites; starts nothing
aw project inspect --workspace <repo>
```

## 5. Create profiles

A profile names a harness, a model and a reasoning level; a role is bound to a profile explicitly. Define them under `profiles` (the cockpit's Initialize form does this for you):

```json
"profiles": {
  "claude-main": { "harness": "claude-code", "model": "opus", "reasoning": "high" },
  "pi-main":     { "harness": "pi", "model": "provider/model-id", "reasoning": "high" }
}
```

`provider/model-id` is a placeholder: use an exact entry from `pi --offline --list-models`. Leaving `model` or `reasoning` out means the harness's own setting, and the cockpit says so
(`native default`). See [configuration.md](configuration.md#profiles-and-role-bindings), [claude-code.md](claude-code.md) and [pi.md](pi.md).

## 6. Start a task

In the cockpit, **New task**: choose the project, describe the objective, and pick a profile for each role the task needs. Every profile is shown as
`profile id → harness → model | native default → reasoning | native default`. The task starts and the agent works in a Herdr session.

From a terminal the equivalent is `aw run "<objective>"` (see [cli.md](cli.md)).

## 7. Respond to workflow gates

When the workflow needs you, the task shows a **gate** in the cockpit. Read what is proposed, then decide:

| Gate | You can |
|---|---|
| CLARIFY (the agent asked a question) | answer |
| WHAT / HOW (a proposed requirement or strategy) | approve, request changes, abort |
| ACCEPT (verified work awaits your decision) | accept, request changes, abort |

An approval applies to exactly what you were shown; if the proposal changed meanwhile, the cockpit refuses the decision and refreshes. Typed text is never an approval.

## 8. Answer the agent's own prompts

Claude Code or Pi may itself need you: a permission, a "trust this folder", a login. That is a **native prompt**, not a workflow gate. The task card shows it, and **Open the agent's session** opens
the agent's own Herdr session where you answer it. AgenticWorkflow never answers it for you. The run continues when the agent does.

If something fails, the task shows why and offers only the recovery actions that are legal (Continue, Bind a profile, or Abort); see [herdr.md](herdr.md#recovery).

## Troubleshooting

| Symptom | Check |
|---|---|
| Anything unexpected | Run `aw doctor`: it names what is wrong and what to do. |
| `aw` is not found in a terminal | Open a new terminal (an open one keeps its old `PATH`), or re-run `install.ps1`. |
| Cockpit says it cannot find `aw`, or reports a different version | Re-run `install.ps1` from the release you want. |
| No "Open AgenticWorkflow" in the palette | Open a space in Herdr first, and check `aw doctor` reports the cockpit as installed. |
| Cockpit says the controller cannot start | Run `aw ctl health` in a terminal to see the reason. |
| `aw show` reports the project is not ready | It lists every problem in the configuration or prerequisites. |
| `install.ps1` refuses to continue | Something is still supervising tasks, or a program is using the installed files. Its message says which; finish or abort the tasks (`aw abort <run-id>`), close terminals running `aw`, and run it again. |
