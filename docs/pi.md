# Pi

Pi is a first-class harness beside Claude Code. A role bound to a `pi` profile runs the same workflow, with the same signal meaning and the same controller supervision ([concepts.md](concepts.md),
[architecture.md](architecture.md)). This page covers only what is specific to Pi.

**Supported and tested: Pi 1.6.0** (`@earendil-works/pi-coding-agent`). Another version works only if it still offers every option listed under [Options used](#options-used); otherwise the
profile is refused with `unsupported_configuration`.

## Profiles: model and reasoning

```json
"profiles": {
  "pi-main":   { "harness": "pi", "model": "provider/model-id", "reasoning": "high" },
  "pi-native": { "harness": "pi" }
}
```

`provider/model-id` is a placeholder: use an exact entry from Pi's own model list (`pi --offline --list-models`). `pi-native` sets neither, so Pi uses its own configured model and thinking
level.

* `model` is the value Pi takes for `--model`: **`<provider>/<id>`**. There is no separate provider field. Omitted means Pi's own setting.
* `reasoning` is a Pi thinking level (`off`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`, as the installed Pi lists them). A model that does not support reasoning allows only `off`.
  Omitted means Pi's own setting.
* There is no argument, environment or command member.

Pi itself only warns about an unknown model or level and silently substitutes or clamps it. AgenticWorkflow therefore **validates first** (the model must be listed by `pi --offline
--list-models`, the level by `pi --help`) and **verifies after launch** (Pi reports the model and level it started with; a mismatch stops the session). A value is honoured or refused, never
replaced. If Pi cannot be found or asked, a profile that sets a model or reasoning is refused.

## Launch and resume

On Windows the npm shim `pi.cmd` is not launched (a command shell would reinterpret arguments). AgenticWorkflow reads the shim, finds the bundled `cli.js` and a `node`, and runs
`node cli.js …` with an argument list; a shim it cannot resolve is not launched. A reviewer or prover gets `--tools read,grep,find,ls`.

Each task's conversation id is a UUID AgenticWorkflow generates and passes with `--session-id`. Every continuation (approve, answer, request changes, recovery) relaunches Pi with
`--session <uuid>`, which re-enters the **same** conversation. A malformed reference is refused before anything starts, and a conversation Pi reports as missing is a recoverable failure, never a
silent fresh start. Resumed sessions replay the *recorded* model and reasoning, not the current project file.

## Workflow signals

Pi reaches the workflow's typed signals through a per-session **extension** loaded with `-e`; nothing global is written and Pi's own configuration is never modified. The extension exposes the
same MCP server Claude uses, records Pi's own model and thinking level at start for the controller to check, and reports each successful signal together with **Pi's own session id**. The channel
accepts a record only for the matching run, session and turn window, and a repeated call id is recorded once. Because Pi's own tool executions are not observed (unlike Claude's), the
controller runs **every** configured check itself for a Pi session.

## Permissions, trust and native prompts

Pi 1.6.0 has no native tool-permission prompts, and AgenticWorkflow auto-approves nothing. Pi can still ask a person something:

* **Project trust.** Shown only when the project has protected `.pi` resources and Pi's `defaultProjectTrust` is `ask`. The setting belongs to you; AgenticWorkflow does not change it. This
  prompt emits **no Herdr status**, so it cannot be identified. When someone is attending and Pi has not reported ready after the startup budget, the cockpit says Pi *may* be waiting for you,
  keeps the session, and the task continues when Pi becomes ready. Open the agent's session ([herdr.md](herdr.md#the-agents-own-session)) and answer it there. Unattended, the start fails with a reason.
* **Extension dialogs** raised mid-task are visible and actionable in the agent's session, and are shown as "waiting for input" only if Pi reports them.

## Limitations

* The **command deny-list is best-effort.** The extension blocks shell commands whose program matches the project's denied commands (`git commit`, `git push`, `curl`, …), after environment
  assignments, `sudo`/`env`/`time`/`nohup` and git global options. It is weaker than Claude's own command parser and does not stop a determined shell trick. Defence in depth is the same as
  for Claude: AgenticWorkflow never commits, and the controller verifies the working tree itself.
* A **Pi reviewer or prover cannot run commands.** Pi has no per-command read-only shell, so those roles get read-only file tools only.
* A Pi **trust prompt has no status**; it can only be flagged after a quiet period, not identified.
* A prompt Herdr pasted but Pi never started (observed once in live use) is detected: after 60 seconds idle with no turn the attempt is reported failed ("the prompt was not taken"), nothing is
  typed again, and the task is blocked and recoverable.
* Only Pi sessions AgenticWorkflow created, or can identify by their recorded id, are managed.

## Options used

`--model`, `--thinking`, `--session`, `--session-id`, `-e/--extension`, `--tools`, `--offline --list-models`, and the extension API (`registerMcpServer`, `session_start`, `tool_call`,
`tool_result`, `ctx.sessionManager.getSessionId`, `ctx.abort`). If an option is missing, the profile is refused.
