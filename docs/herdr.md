# The Herdr cockpit

The cockpit is a Herdr app mod (`integrations/herdr/agenticworkflow/`). It lets you use AgenticWorkflow without a terminal: pick a project, start a task with explicit profile bindings,
watch Claude Code or Pi work in Herdr, decide workflow gates, answer the agent's own prompts in its visible session, recover, complete or abort. The [`aw` command line](cli.md) stays for
debugging, administration and recovery.

## Install

The cockpit is installed by the release's `install.ps1` ([getting-started.md](getting-started.md#2-install)): a **copy** of the mod in `<Herdr home>\mods\agenticworkflow` (Herdr home is
`%HERDR_APP_HOME%` if set, otherwise `~\.config\herdr-app`) and one small settings file, `<Herdr home>\agenticworkflow\config.json`, holding the absolute path of the installed `aw`. It never
stores a secret and never touches AgenticWorkflow's own state or any other Herdr mod. Updating re-runs `install.ps1`, which replaces the copy exactly (no file of the old version is left);
`uninstall.ps1` removes the copy and the settings file.

The cockpit, `aw` and the controller are one release with one version. The cockpit notices when it is not: if the controller it reaches reports another version, the cockpit says so at the top of
its view, and `aw doctor` reports a cockpit that is older or newer than `aw`. The cure is always to re-run `install.ps1` from the release you want.

Herdr lists a mod's command-palette entry only for a real folder under `mods/`, which is why the cockpit is copied, never linked.

`install.mjs` in the mod's folder is development tooling for contributors (see [CONTRIBUTING.md](../CONTRIBUTING.md)); it is not part of the release.

## Open it

* **Command palette:** press **Ctrl+Shift+P** and run **Open AgenticWorkflow** (search `agenticworkflow`, `workflow` or `aw`). This opens a normal Herdr tab.
* **From a script or terminal** (an alternative, and useful for debugging):
  `herdr-app do tab.new --name "AgenticWorkflow" --mod agenticworkflow` opens a tab, and `herdr-app do mod.open --name agenticworkflow` opens the cockpit as an overlay.
* The palette entry is declared in the mod's `mod.json`. If it is missing, check that the mod is installed as a copy rather than a link, that a space is open, and run `aw doctor`.

## What you can do

* **Projects.** Choose a workspace. The cockpit shows whether it has a valid configuration, what is wrong if not, and which profiles and checks it defines.
* **Set up a project.** A typed form (toolchain: `dotnet`, `flutter` or `custom`; a profile for each role; checks; writable and protected paths; whether you ACCEPT) is sent to `aw project init`, which validates it with the
  same strict parser `aw run` uses and never overwrites an existing `agenticworkflow.json`. Suggested checks come from the project's files and are shown with their evidence; they only become
  values when you confirm them. There are no fields for a shell command, environment or extra arguments.
* **New task.** An objective, the stages you want to require, whether you ACCEPT, and a profile for **each role**. Every profile is shown as
  `profile id → harness → model | native default → reasoning | native default`. No profile is chosen for you and none is reused from another role.
* **Task list and detail.** Each task shows its state, current stage and why, what it waits for, its agents and bindings. The list refreshes by polling the task's `streamVersion`;
  notifications are sparse and only for tasks that need you.
* **Why work moved.** REVIEW findings, a failed PROVE claim, a reclassification and a failed verification are shown with the reason the task went backward or is blocked.

## Gates

| Gate | Controls |
|---|---|
| CLARIFY | Answer |
| WHAT, HOW | Approve, Request changes, Abort task |
| ACCEPT | Accept, Request changes, Abort task |

An approval carries the exact task, gate, semantic revision, binding and content hash that the cockpit showed. If anything changed meanwhile, the controller refuses the decision and the
cockpit refreshes. Free text is never an approval. Every button shows a pending state, waits for the controller's reply and then refreshes; nothing is shown optimistically.

## The agent's own session

Workflow gates are decided in the cockpit. A prompt the **agent** raises (a tool permission, "trust this folder", a login) is a *native prompt*: the task card says so, shows what the agent
reports, and **Open the agent's session** opens the agent's Herdr session (the same as `aw attach <run-id>`). You answer there. AgenticWorkflow never answers, allows or denies it, and the two
kinds of waiting are visibly distinct.

An agent can need you while it is still starting. When someone is attending, the session is kept and the task waits, then continues when the agent reports it is ready; unattended, the start
fails with a clear reason. Claude Code reports such a prompt as blocked. Pi's project-trust prompt emits no status, so after a quiet period the cockpit says the agent may be waiting for you
(see [pi.md](pi.md)).

## Missing bindings

If a task needs a role that has no binding (the agent asked for a review and no reviewer is bound), it stops and the card lists the role, why it is needed, and the compatible profiles. Choose
one explicitly with **Use this**, then **Continue**. Nothing is selected automatically and the implementer's profile is never reused.

## Recovery

The cockpit offers only the actions the controller reports as legal:

* **Continue** retries interrupted work in the same conversation (no new decision).
* **Abort task** cancels a task that cannot continue, or stops one at a gate without deciding it.
* If the controller restarted, interrupted tasks are reconciled before it serves the cockpit. A task whose agent was waiting on a native prompt when the controller died cannot be resumed
  (that session ended with it) and offers **Abort** only.

## The controller starts by itself

Every cockpit action is a short-lived `aw ctl …` call, which starts the controller if none is running. When it cannot, the cockpit explains in words: the controller could not start, speaks an
incompatible protocol, cannot supervise durably, or `aw` was not found. It never runs anything itself as a fallback. An update (`install.ps1`) stops an idle controller itself; contributors
rebuilding from source run `aw ctl stop` first, because a running controller locks the DLLs.

## Structure (for contributors)

Calls are `exec` of `aw` with an argument list: no shell, no standard input, no HTTP or TCP. Text a person typed travels in a request file passed as `--request-file <absolute path>`, never on a
command line. The mod never reads SQLite or run files and never re-implements workflow logic.

```text
main.js    the view and its handlers (the only file that talks to Herdr)
ui.js      widgets
lib/       Herdr-independent logic: aw.js (process client), model, controls, forms, commands, native, notify, poll, text
test/      node --test, no Herdr needed
```
