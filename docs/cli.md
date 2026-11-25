# Command line

**Herdr is the normal way to use AgenticWorkflow** ([herdr.md](herdr.md)). The `aw` command line is for development, administration and recovery, and for scripting the same operations the
cockpit performs. `aw --help` and `aw <command> --help` are authoritative for syntax.

`aw --version` prints the product version (`aw 0.1.0`); the controller, the evidence hook and the cockpit of a release all carry the same one.

## Everyday commands

| Command | Does |
|---|---|
| `aw show [--workspace <dir>] [--config <file>]` | Validates the project configuration and prerequisites exactly as `run` would, and prints what a run would use. Starts nothing. Exit 2 if not ready. |
| `aw run [options] "<task>"` | Starts a task: the agent works, the controller verifies, then the task completes (acceptance `none`) or opens the ACCEPT gate (`human`). |
| `aw runs [--limit n]` | Lists recent runs (id, state, start, workspace, first line of the task) to find an id again. Reads durable state only. |
| `aw status <run-id> [--json]` | Replays the run's event stream and shows its state, pending gate and recovery options. `--json` prints the [read model](architecture.md#the-taskreadmodel). |
| `aw answer <run-id> --question <id> --text "…"` | Answers the agent's CLARIFY question. Approves nothing. |
| `aw approve <run-id> --gate <id>` | Approves a WHAT or HOW proposal; the agent continues in its own conversation. `--gate` is required: an approval always names what it approves. |
| `aw accept <run-id> [--gate <id>]` | Accepts the verified work at the ACCEPT gate; the task completes. No agent or model is involved. |
| `aw request-changes <run-id> --feedback "…" [--gate <id>]` | Declines a gate and sends feedback; the agent's native conversation resumes. |
| `aw continue <run-id>` | Retries interrupted work you already asked for. Records no new decision. |
| `aw bind <run-id> --role <root\|reviewer\|prover> --profile <id>` | Binds a role the task needs to a project profile. Starts nothing; then `aw continue`. |
| `aw abort <run-id>` | Cancels a blocked task, or stops one waiting at a gate without deciding it. |

Options for `aw run`: `--workspace <dir>` (default: current directory), `--config <file>` (default: `<workspace>\agenticworkflow.json`), `--acceptance none|human`, `--require
what,how,review,prove`, `--profile / --reviewer-profile / --prover-profile <id>`, `--task-file <file>`, and `--interactive` / `--non-interactive` (whether someone attends native prompts;
by default attended when input and output are a terminal).

Mutating commands are handed to a running controller when one is healthy; `--direct` runs them in the `aw` process to their outcome instead (the administrative and recovery path). A routed command
returns once the decision is recorded and scheduled; follow with `aw status`.

## Controller, project and session commands

| Command | Does |
|---|---|
| `aw ctl health` | Controller identity, version, transport and protocol versions. Starts the controller if needed (`--no-start` to forbid). |
| `aw ctl tasks [--limit n]` / `aw ctl task <task-id>` | The task list, or one task's read model (the same document as `aw status --json`). |
| `aw ctl send [--request-file <absolute path>]` | Sends one typed request (JSON on standard input, or from a file that is read once and deleted). This is how the cockpit drives workflow operations. |
| `aw ctl stop [--force]` | Asks the controller to stop. Refused while it supervises work, unless `--force`. |
| `aw ctl serve` | Runs the controller in the foreground, for debugging. |
| `aw project inspect [--workspace <dir>]` | Describes a workspace as JSON: configuration missing, invalid (with the problems) or ready (profiles and checks), plus conventional checks the files suggest. |
| `aw project init --workspace <dir> --request-file <path>` | Creates `agenticworkflow.json` from confirmed choices, validated by the strict parser. Never overwrites a file. |
| `aw doctor [--workspace <dir>] [--json]` | Diagnoses the installation and the machine: version, files, state folder, controller, Git, Herdr, Claude Code and Pi, the cockpit and (inside a project) its profiles. One `PASS`, `WARN` or `FAIL` line per check. Starts no agent, makes no model call; it may start the controller, as any client does. Exit 33 if a check failed. |
| `aw attach <run-id> [--describe]` | Opens the agent's own Herdr session (where native prompts are answered). `--describe` prints one JSON document and always exits 0; the reason is inside it. |

## Exit codes

Exit codes are a stable contract. Every outcome that is not a completion prints a message naming the exact next step.

| Exit | Meaning |
|---|---|
| 0 | Completed (verified with no acceptance required, or accepted); also success for read-only commands. |
| 1 | Internal error. |
| 2 | Usage or configuration error, or unmet prerequisite. Nothing was launched. |
| 3 / 4 | Run not found / run state unreadable. |
| 11 | Agent startup blocked (typically an untrusted folder). `aw` never answers trust prompts. |
| 12 | Verification still failing after `maxTurns`; recorded blocked, abort-only. |
| 13 | Workflow state moved on under the command; nothing further recorded. |
| 14 | Runtime failure (agent or Herdr); recorded blocked. |
| 15 | A native prompt needed a person and nobody was attending; recorded blocked, abort-only. |
| 16 | Aborted. |
| 17 | Verified, awaiting your ACCEPT. |
| 18 | A decision was refused (stale, unknown or already decided gate; no open gate; working tree changed; not recoverable). Nothing was recorded. |
| 19 / 20 / 22 | Awaiting your CLARIFY answer / WHAT approval / HOW approval. |
| 21 / 23 | WHAT / HOW stage violated (workspace changed before approval, or never proposed); blocked. |
| 24 / 27 | REVIEW / PROVE required but no reviewer / prover profile is bound. Bind one with `aw bind`, then `aw continue`. |
| 25 / 28 | The read-only reviewer / prover modified the workspace; blocked. |
| 26 / 29 | Review / proof still failing after the last permitted turn. |
| 30 | PROVE required but no valid Proof Contract was submitted; blocked. |
| 31 / 32 | `aw ctl`: no healthy controller could be reached or started / incompatible protocol. |
| 33 | `aw doctor`: at least one check failed. |
| 130 | Interrupted with Ctrl+C. The task is recorded blocked; only `aw abort` cancels it. |

## Recovery

`aw status <run-id>` states what is legal. A recoverable block offers `aw continue`; a block that cannot be resumed safely offers `aw abort` only; a missing binding offers `aw bind` then
`aw continue`. Both are described in [concepts.md](concepts.md#recovery-at-a-high-level). If the controller died, run any `aw ctl` command: the next controller reconciles interrupted
runs before serving. `aw continue` and `aw abort` also work with no controller, after proving nobody owns the run.

## State and safety

Run state lives outside the repository ([architecture.md](architecture.md#durable-event-sourced-state)). Nothing in the target repository's Git history, index, stash, branches or remotes is ever
modified by `aw`, and the agent is denied commit, push and similar commands. A task's text is stored in its event stream: do not put secrets in a task.
