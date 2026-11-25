# Configuration

A project is configured by one file, `agenticworkflow.json`, at the repository root (or any path given with `--config`). It is strict, versioned JSON: no comments, no trailing commas, unknown
or duplicate members are errors, every problem is reported at once, and nothing is launched until the file is valid. AgenticWorkflow never guesses a toolchain, profile or check.

Create it from the cockpit (**Set up this project**), with `aw project init`, or by copying [`examples/agenticworkflow.json`](../examples/agenticworkflow.json). Validate it with
`aw show` (it starts nothing and writes nothing). Harness-specific details are in [claude-code.md](claude-code.md) and [pi.md](pi.md).

## Example

```json
{
  "version": 1,
  "role": "root",
  "profile": "claude-main",
  "reviewerProfile": "pi-review",
  "proverProfile": "claude-review",
  "profiles": {
    "claude-main":   { "harness": "claude-code", "model": "opus",   "reasoning": "high", "description": "implementer" },
    "claude-review": { "harness": "claude-code", "model": "sonnet", "reasoning": "low" },
    "pi-review":     { "harness": "pi", "description": "Pi's own model and thinking level" }
  },
  "toolchain": "dotnet",
  "acceptance": "human",
  "maxTurns": 3,
  "verification": [
    { "id": "build", "executable": "dotnet", "arguments": ["build", "--nologo"], "timeoutSeconds": 600 },
    { "id": "tests", "executable": "dotnet", "arguments": ["test", "--nologo", "--no-build"], "timeoutSeconds": 900 }
  ],
  "scope": {
    "writable": ["src/**", "tests/**"],
    "protected": [".git/**", "Directory.Build.props", "global.json", "AGENTS.md"]
  },
  "allowCommands": [],
  "operatingNotes": "Follow the existing code style. Do not add dependencies."
}
```

## Settings

| Member | Required | Meaning |
|---|---|---|
| `version` | yes | Must be `1`. |
| `role` | yes | Must be `root`. |
| `profile` | yes | The profile bound to the `root` (implementer) role. |
| `reviewerProfile`, `proverProfile` | no | Profiles bound to the `reviewer` and `prover` roles. Never implied: without one the role is unbound (see below). |
| `profiles` | no | Profile definitions (below). |
| `toolchain` | yes | `dotnet`, `flutter` or `custom`. Chosen explicitly, never detected; any other value is an error. See [Toolchains](#toolchains). |
| `acceptance` | no | `none` (default): the controller completes verified work itself. `human`: the task waits at an ACCEPT gate. Override per task with `--acceptance` or the cockpit's *ACCEPT* choice; the value is stored with the task when it starts. |
| `maxTurns` | no | 1–20, default 3. The initial turn plus repair turns after a failed verification. |
| `verification` | yes | One or more controller-run checks (below), whatever the toolchain. |
| `scope.writable` | yes | Paths the agent may change: `dir/**`, a file, or `**` for everywhere not protected. |
| `scope.protected` | no | Paths that must not change. Protected wins over writable; touching one fails verification. |
| `allowCommands` | no | Extra narrow command prefixes the agent may run without asking, e.g. `dart format`. Git, shells and other command launchers, and wildcards are rejected. |
| `operatingNotes` | no | Extra project guidance appended to the toolchain's defaults, if it has any (at most 4000 characters). |

The completion boundary is always the working tree. Completion needs at least one passing controller check.

### Toolchains

A toolchain is a small preset for what the agent may run without asking and a line of operating guidance. It never selects a profile, and it grants nothing the always-denied list (Git history,
remotes, network) refuses.

| `toolchain` | Commands the agent may run without asking | Operating notes |
|---|---|---|
| `dotnet` | `dotnet build`, `dotnet test`, read-only Git | how to run them (one plain command per call) |
| `flutter` | `flutter analyze`, `flutter test`, read-only Git; `flutter pub` and `dart pub` denied | how to run them |
| `custom` | read-only Git only | only the general note on how pre-approval works |

`custom` is for any environment without a built-in preset (a game engine such as Unity, a polyglot repository, an unusual build system). It adds **no** build or test command and guesses
none: what is verified is exactly the `verification` you list, what the agent may run is exactly `allowCommands`, and project guidance is exactly `operatingNotes`. Every scope, security and
verification rule applies as for any toolchain. For example:

```json
"toolchain": "custom",
"verification": [
  { "id": "tests", "executable": "make", "arguments": ["verify"], "timeoutSeconds": 1800 }
],
"allowCommands": ["make verify"],
"operatingNotes": "Run `make verify` once per turn. Do not edit anything under Library/."
```

Replace `make verify` with whatever command proves your project is right, for example the command line that runs your engine's tests.

### Verification checks

Each check is `id`, `executable`, `arguments` (a list), optional `timeoutSeconds` (1–7200, default 600) and `description`. Checks are started directly with an argument list; there is no shell, so
`&&`, pipes and redirection do not exist; `aw project init` and the cockpit also refuse a command interpreter (`bash`, `sh`, `cmd`, `powershell`, `pwsh`, `wsl`, `env`, `xargs`, `sudo`, `start`, …) as a check. The ids
`workspace_scope` and `workspace_stable` are reserved for the controller's own sensors. On Windows a `.cmd`/`.bat` toolchain script is supported, but its arguments may not contain `& | < > ^ % ! "`
(they are refused, not escaped).

## Profiles and role bindings

A **profile** is a reusable name for "this harness, this model, this reasoning":

| Member | Meaning |
|---|---|
| key (profile id) | Lowercase letters, digits and `_ . : -`. `claude-code-local` is built in (Claude Code with its own defaults) and cannot be redefined. At most 32 profiles. |
| `harness` | `claude-code` or `pi`. |
| `model` | Optional. One plain token, opaque to AgenticWorkflow; the harness adapter decides whether it is valid. Omitted means the harness's own setting. |
| `reasoning` | Optional. Likewise. Omitted means the harness's own setting. |
| `description` | Optional, at most 200 characters. Shown to people. |

Values may contain letters, digits and `. _ : / @ + [ ] -`, start with a letter or digit, and be at most 100 characters. There is **no** member for arguments, environment variables, commands
or credentials: a profile is data, and the adapter builds the launch from it. Both harnesses accept an explicit `model` and `reasoning`; each harness's mapping and validation are in
[claude-code.md](claude-code.md) and [pi.md](pi.md):

```json
"profiles": {
  "claude-main": { "harness": "claude-code", "model": "opus", "reasoning": "high" },
  "pi-main":     { "harness": "pi", "model": "provider/model-id", "reasoning": "high" }
}
```

The Claude `model` is an alias or model name Claude Code accepts. The Pi `model` is `<provider>/<id>` and must match an exact entry in Pi's own model list (`pi --offline --list-models`);
`provider/model-id` above is a placeholder for yours.

A **role binding** says "role X uses profile Y". `profile`, `reviewerProfile` and `proverProfile` set the project's bindings; `aw run --profile / --reviewer-profile / --prover-profile` and the
cockpit's New task form set them for one task. Rules:

* Only the roles a task needs are bound: `root` always; `reviewer` and `prover` when REVIEW or PROVE is required.
* A role is never given another role's profile, and nothing is substituted. An unknown profile, an unsupported harness, or a model or reasoning the harness cannot honour stops the start and
  names the profile and the value.
* The resolved configuration of every bound role (harness, model, reasoning, a fingerprint) is **recorded with the task** and is what is launched, on the first start and on every resume.
  Editing a profile later never changes a running task.
* If a stage becomes required mid-task and its role has no binding, the task stops (`missing_binding`) and lists the role, why it is needed, and the compatible profiles. Bind one, then continue:

```powershell
aw bind <run-id> --role reviewer --profile claude-review
aw continue <run-id>
```

Binding the same profile again is a no-op; binding a different one is refused (`already_bound`). A binding is never replaced.

## Settings outside the file

| Setting | Meaning |
|---|---|
| `AGENTICWORKFLOW_HOME` | Absolute path of the state root (default `%LOCALAPPDATA%\AgenticWorkflow`). A root inside the workspace is refused. |
| `AGENTICWORKFLOW_NOTIFY=toast` | Also show a best-effort Windows toast when an agent waits for you in its own session. Off by default. |
| `AGENTICWORKFLOW_TIMING=1` | Print phase timings to stderr after a run. |
