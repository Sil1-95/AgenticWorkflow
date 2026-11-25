# Security

AgenticWorkflow is a **local, single-user** tool. This page states what it protects, what it does not, and how to report a problem.

## Local IPC design

* The controller is reachable through one local endpoint, private to the current operating-system user: a named pipe created with `CurrentUserOnly` and `FirstPipeInstance` on Windows, or a
  Unix-domain socket in a `0700` directory (`0600` socket) with a kernel peer-credential check on both sides on Unix. If a protection layer cannot be established the controller does not
  start; it never falls back to a weaker endpoint.
* **There is no HTTP or TCP listener**, in the controller or in the Herdr cockpit.
* Requests are a closed set of typed operations in bounded, versioned frames. There is no operation that executes a string, shell command, path or script, a request with an unknown member is
  refused, and malformed, oversized or incompatible messages change nothing.
* The Herdr cockpit calls `aw` with an argument list (no shell). Text a person typed travels in a request file that is read once, bounded to 64 KiB and deleted, never on a command line.

## What the workflow enforces

* **No natural-language approval.** A decision is a typed operation naming the gate and the exact revision and content hash it was shown; stale or duplicate decisions are refused.
* **No auto-approval.** AgenticWorkflow never answers, allows or denies an agent's native permission, trust or login prompt.
* **Controller authority.** Completion comes from the controller's own verification of the working tree, not from anything an agent says. Agents are confined to a writable scope, cannot
  change protected paths, and are denied Git commit, push and similar commands. Reviewers and provers are read-only; a change is recorded as a block.
* **Own sessions only.** The controller adopts and ends only agent sessions in AgenticWorkflow's own isolated Herdr namespace that a supervision record names. It never attaches to or stops
  any other session.
* **Fail closed.** Ambiguity (an unconfirmed agent host, an unusable baseline, a configuration that changed) blocks a task instead of guessing; a second agent is never started beside an old one.

## Threat boundary

AgenticWorkflow protects against other users and remote machines, a confused or over-eager agent, and stale or forged UI actions. It is **not a boundary against code running as the same
operating-system user**: such code can already read that user's run files, repositories and credentials. The configured agent runs with the privileges of the user who started it; its tool
permissions are enforced by the harness (Claude Code, Pi) and by the deny rules AgenticWorkflow passes it, which for Pi are best-effort (see [docs/pi.md](docs/pi.md)). Run agents only on
repositories you trust, and treat a repository's own agent configuration files as code.

## Secrets policy

* Configuration and profiles carry no commands, arguments, environment variables or credentials, and the loader rejects any such member.
* Events, the read model and logs carry harness, model, reasoning and hashes only. Native conversation references are never exposed, and the controller's log contains operation names, ids and
  error type names, never an objective, answer, feedback, agent output or path content.
* Task text is stored in the task's event stream. Do not put secrets in a task, an answer or feedback.

## Reporting a vulnerability

Do not open a public issue for a vulnerability. Use GitHub's private vulnerability reporting: on the repository's page, choose *Security → Report a vulnerability*. Include the version
(`aw --version`), your platform, and steps to reproduce.
