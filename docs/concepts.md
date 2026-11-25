# Concepts

AgenticWorkflow runs a coding agent (Claude Code or Pi) through a **human-gated, adaptive workflow**. The agent supplies the intelligence; the **controller** supplies the authority: it
records decisions, runs the verification itself, and is the only thing that can complete a task. This page explains the ideas you need to use and maintain it.

## Stages

A task moves through some or all of these stages. Only IMPLEMENT and VERIFY always happen.

| Stage | What happens | Who decides |
|---|---|---|
| **CLARIFY** | The agent asks one question it cannot resolve from the repository. The task waits. | You answer. Answering approves nothing. |
| **WHAT** | The agent proposes the requirements: objective, required behaviour, scope, constraints, non-goals, acceptance conditions. | You approve, request changes, or abort. |
| **HOW** | The agent proposes the implementation strategy: components, data and control flow, compatibility, risks, verification approach. | You approve, request changes, or abort. |
| **IMPLEMENT** | The agent changes the workspace in its own native session, inside the writable scope. | The agent, bounded by scope and tool policy. |
| **VERIFY** | The controller runs the configured checks (build, tests, …) against the working tree. A failure sends the agent a repair turn, up to `maxTurns`. | The controller. The agent's own claim of success is never evidence. |
| **REVIEW** | A *fresh*, read-only reviewer session reads the implementation and submits one structured report: pass, or changes required with findings. | A reviewer bound to its own profile. |
| **PROVE** | The implementer writes a *Proof Contract* (1–8 claims, each tied to evidence: a verification check, a current review pass, or a stated observation). A fresh read-only prover judges every claim. | A prover bound to its own profile. |
| **ACCEPT** | Verified work waits for your decision. Without this stage the controller completes verified work itself. | You accept, request changes, or abort. |

A reviewer or prover is given authoritative state and the diff-relevant facts, never a transcript, and cannot change the workspace: a change is recorded as a block, not absorbed.
A review or proof pass is never an ACCEPT.

## Adaptive composition

The stages after CLARIFY are not a fixed pipeline. A stage becomes **required** in one of two ways:

* **You require it**, when starting the task (`--require what,how,review,prove` on `aw run`, the *Require* options in the cockpit) or through the project's `acceptance` setting for ACCEPT.
* **The agent asks for it**, with a typed signal: `propose_what`, `propose_how`, `request_review`, `request_prove`, or `report_reclassification`.

Rigor is only ever added. A stage that became required stays required for that task; nothing de-escalates automatically. Requiring one stage does not imply another.

## Workflow gates versus native prompts

Two kinds of "waiting for a person" exist, and they are always shown separately:

* A **workflow gate** belongs to AgenticWorkflow: a CLARIFY question, a WHAT or HOW proposal, the ACCEPT decision. It is durable (it survives restarts), bound to exact content, and decided in the
  cockpit or with `aw` commands. It needs no agent session.
* A **native prompt** belongs to the harness: a tool permission, a "trust this folder" question, a login. You answer it in the agent's own Herdr session. AgenticWorkflow observes that the
  agent is waiting, can open that session for you, and **never answers, allows or denies it**.

## Semantic revisions and currentness

Every decision that changes what the task *means* (a clarification answer, an approved WHAT or HOW, requested changes, a reclassification) creates a new **semantic revision**. Each gate
and each approval is bound to the revision, and to the exact content hash, it was made against. An approval is therefore *current* only while the thing it approved still stands.

* A decision against an older revision, or content that changed since you saw it, is refused ("stale"); the cockpit refreshes.
* **Reclassification:** when the agent finds concrete new evidence that an approved stage is wrong, it reports it. The approval becomes *invalidated* (still visible in the history, never
  rewritten), the dependent HOW, implementation and verification become stale, the workspace is kept as provisional work, and the same conversation proposes a revised stage. A stale verification
  can never complete a task.
* The task view always says why work moved backward or is blocked: a failed check, review findings that must be addressed, a proof claim that failed, or a reclassification.

## Roles, profiles and bindings

Three roles exist: `root` (the implementer), `reviewer` and `prover`. Each is **explicitly bound** to a *profile*, and a profile names a harness, a model and a reasoning level:

```text
role → profile → harness → model → reasoning
```

Nothing chooses a profile for a role. There is no default, no fallback, and a reviewer never silently reuses the implementer's profile. A model or reasoning a profile leaves unset means the
harness's own setting; it is never filled in. The resolved binding is recorded with the task and is what is launched, including on every resume, so later edits to the project file do not change a
running task. If a task needs a role it has no binding for (the agent asked for a review and no reviewer is bound), it stops, says which role and why, and waits for you to bind a profile.
See [configuration.md](configuration.md#profiles-and-role-bindings).

## Verification and evidence

Completion is derived from controller evidence, never from the agent's words:

* The controller runs the configured checks itself (an executable plus an argument list; no shell). A check can be satisfied by the agent's own *authoritative* run of the same command
  (observed through Claude Code's hooks) when its recorded state matches the working tree exactly; otherwise the controller executes it. For Pi sessions the controller runs every check.
* Every check records who produced the evidence and why it was reused or re-run.
* The writable and protected **scope** is checked against the working tree. A protected path touched, or a change outside the writable scope, fails verification.
* Completion needs at least one passing controller check and a current verification of the final working tree.

## Recovery at a high level

State is durable, so a task can always be inspected. A task is in one of a few states: running, awaiting a person, blocked, completed or cancelled.

* **Blocked, recoverable.** The agent or its host failed after you had already decided something (for example during a resumed turn). **Continue** retries the same work in the same native
  conversation, with no new decision.
* **Blocked, not recoverable.** The verification stayed unresolved, a stage rule was violated, or nothing durable says what an interrupted agent was doing. Only **Abort** is offered; AgenticWorkflow
  never restarts an agent from a guess.
* **Controller restarted.** The next controller reconciles interrupted work before serving anyone: it resumes what is provably resumable, never starts a second agent beside an old one, and blocks
  the rest with a reason.
* **Missing binding.** Bind a profile to the role, then Continue.

See [architecture.md](architecture.md#supervision-and-recovery) for how this is enforced.
