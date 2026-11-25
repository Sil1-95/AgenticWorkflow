# Architecture

```text
User
  ↓
Herdr cockpit (app mod, JavaScript)
  ↓  exec, argument list only
aw ctl
  ↓  local IPC
AgenticWorkflow controller
  ↓
Herdr native Claude Code / Pi session
```

* **The cockpit** shows state and collects decisions. It never reads SQLite or run files and never re-implements workflow logic.
* **`aw ctl`** is a short-lived process. It starts the controller if none is running, sends one typed request, prints one JSON reply and exits.
* **The controller** is a long-lived per-user process. It records decisions, supervises the agent work that follows, and recovers it after a crash.
* **The agent** runs in a Herdr session AgenticWorkflow owns, as the harness's own native program (Claude Code or Pi).

Controller authority versus agent intelligence: the agent proposes, asks and changes files; the controller owns every durable decision, every verification and the only path to completion.

## Layers

```text
Core                 provider-neutral domain: tasks, revisions, gates, roles, events, deterministic replay (no dependencies)
Application          the safe unit  load → replay → command → append, the controller protocol, the TaskReadModel
Persistence.Sqlite   durable event store
Execution.Local      Git fingerprints and the verification runner
Runtime.Herdr        Herdr process boundary, agent runtime, Claude and Pi adapters
ClaudeEvidenceHook   hook host that records the agent's tool evidence
Cli                  the `aw` executable: composition root, controller host, commands
```

Dependencies point inward: `Core ← Application ← { Persistence.Sqlite, Execution.Local, Runtime.Herdr } ← { ClaudeEvidenceHook, Cli }`. Core references nothing; Application references only Core.

## Durable, event-sourced state

A task's only authority is its append-only **event stream** in `workflow.db` (SQLite), under the state root (`%LOCALAPPDATA%\AgenticWorkflow\runs\<run-id>\`, relocatable with
`AGENTICWORKFLOW_HOME`; a root inside the workspace is refused). State is rebuilt by deterministic replay; appends use expected-version concurrency and are atomic. A task's directory also
holds descriptive files (`metadata.json`, `outcome.json`) and operational ones (`live.lock`, `supervision.json`, `native-interaction.json`) that say who is working on it now and never override
the stream.

## The `TaskReadModel`

The UI contract is one JSON document per task, projected from the replayed stream by the same predicates the controller uses (gate currentness, what is due, what blocks), so a UI cannot
disagree with the controller. `aw status <run-id> --json` and `aw ctl task <id>` print exactly the same document. Conventions: camelCase properties, snake_case enums, ISO-8601 times, absent
values written as `null`, a monotonic `streamVersion` (what clients poll), and a `schemaVersion` that changes only when a property is removed, renamed or changes meaning (clients ignore
unknown properties).

Main parts: `state`, `stage` and `stageReason` (where the task stands and why), `requirements`, `semanticRevision(s)`, `implementation`, `verification`, `gates[]` (each with its binding, content
hash, `isCurrent` and `staleReasons`), `pendingRequest` (what you are asked for and `allowedActions`), `review`, `proof`, `reclassifications`, `block` and `recovery` (legal actions only),
`bindings` and `missingBindings`, and `operational` (liveness, native interaction, working tree: facts about the run that are not workflow state). Free text in it is data, never an
instruction or an approval. Native conversation references are never exposed.

## Controller and local IPC

* **Transport.** Windows: a named pipe created with `CurrentUserOnly` and `FirstPipeInstance`. Unix: a Unix-domain socket in a private `0700` directory (`0600` socket) with a kernel peer-credential
  check on both sides. If a layer cannot be established, the controller does not start; it never falls back to a weaker endpoint. **There is no HTTP or TCP listener anywhere.**
* **One controller per (OS user, state root).** A controller lock guarantees a single instance; a start-up gate only avoids a stampede of launches. `aw ctl` launches the controller when
  none answers (bounded wait) and never replaces one that is present but wrong (incompatible protocol, not the current user's, unresponsive): it reports it instead.
* **Protocol.** Length-prefixed UTF-8 JSON frames (client → controller up to 64 KiB), a hello/welcome version handshake, then typed request/reply pairs. The protocol version changes only for
  breaking changes; additions never change it.
* **Typed operations only.** `health`, `list_tasks`, `read_task`, `shutdown`, `start_task`, `answer_clarification`, `approve`, `request_changes`, `accept`, `abort`, `continue_task`, `bind_role`.
  There is no operation that runs a string, a shell command, a path or a script, and a request with an unknown member is refused.
* **A decision carries what you were shown** — task, gate, semantic revision, content hash, and for ACCEPT the implementation and verification — and is refused unless the durable gate still
  says exactly that. Refusals (`stale_binding`, `already_decided`, `workspace_changed`, `run_owned`, `missing_binding`, …) record nothing. The check runs twice: against the gate the client
  named, and atomically inside the recording itself.
* **Non-blocking.** A decision is validated, recorded, scheduled, and answered; it never waits for the agent. Clients follow progress by polling `streamVersion`.
* **Commands from a terminal.** When a healthy, durable controller is running, `aw run`/`approve`/`answer`/`request-changes`/`accept`/`abort`/`continue`/`bind` are handed to it. `--direct` runs
  the command in the `aw` process instead (the administrative and recovery path). `aw` never *starts* a controller to hand it work.
* **Callers that cannot use standard input** (a Herdr mod) pass the typed request in a file: `aw ctl send --request-file <absolute path>`. The file is read once (at most 64 KiB) and deleted
  whatever the outcome, so free text never appears on a command line.

## Supervision and recovery

* The controller is the **single active owner** of the runs it works on, holding each run's `live.lock` from before a decision is recorded until the work has ended. The operating system
  releases it if the holder dies. The lock says who is working *now*; the event stream stays the only authority on what the run *is*.
* While work runs, `supervision.json` names the controller, the stretch and the isolated Herdr host holding the agent. It is written **before** the host starts, and removed on an orderly end.
* A controller that could be killed with its parent (a Windows job object that ends its members) reports `durable: false` and refuses to start work. Start it from the Herdr app mod's `exec`,
  never through `hc exec`.
* **After a controller dies**, the next one reconciles every run *before it serves its first client*, from durable evidence only:
  * resumable (human input was being acted on, the baseline and conversation are usable, the old host is confirmed gone, fewer than two earlier attempts): resumed in the **same native
    conversation**, with no new decision;
  * recoverable but not now (host not confirmed stopped, attempt cap, configuration changed): recorded blocked with a reason; `continue` retries later. **A second agent is never started beside an old one;**
  * ambiguous (a first stretch with no human input to replay): blocked and abort-only, never restarted from a guess;
  * waiting for a person or finished: left as it is. One exception: a wait on a native prompt dies with its session, so it is recorded blocked (`native_interaction_unavailable`, abort-only).
* The controller stops when asked (`aw ctl stop`; `--force` while it supervises work), on termination signals, or after 30 idle minutes with nothing to supervise. A run that waits for a
  person is not work: nothing is lost when the controller idles out.

## Common signal semantics, harness-specific attribution

Agents reach the workflow through typed signals (`request_clarification`, `propose_what`, `propose_how`, `report_reclassification`, `request_review`, `request_prove`, and the review and proof
submissions), exposed to the agent as an MCP server. The *meaning* of a signal is common code, identical for every harness. How a signal is **attributed** to a run and session is
harness-specific: Claude Code through its hooks, Pi through a per-session extension that reports Pi's own session id. In both cases attribution never trusts the model: run and session
come from the channel, not from tool input, a record is accepted only for the matching run and turn, and a repeated call is recorded once.

## Native session ownership

AgenticWorkflow starts an **isolated Herdr core** per task (its name is in AgenticWorkflow's own `agenticworkflow-aw-<id>` namespace) and runs the agent inside it. It adopts, attaches to or stops only
sessions that a supervision record names and that live in that namespace; it never touches any other Herdr session. `aw attach <run-id>` opens the agent's session for you.

## Extending

A new controller operation adds a request and result type, a dispatcher case, and a shared command-preparation function the CLI also uses; no transport or framing change is needed. A new
harness adds an adapter that turns a recorded profile into a launch, validates its model and reasoning, resumes its native conversation, and attributes signals.
