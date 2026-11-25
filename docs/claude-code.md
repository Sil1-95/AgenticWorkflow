# Claude Code

Claude Code is a first-class harness beside Pi. A role bound to a `claude-code` profile runs the same workflow, with the same signal meaning and the same controller supervision
([concepts.md](concepts.md), [architecture.md](architecture.md)). This page covers only what is specific to Claude Code. The shared schema is in [configuration.md](configuration.md).

**Supported and tested: Claude Code 2.1.332.** Another version works only if it still offers the options AgenticWorkflow passes (`--model`, `--effort`, `--session-id`, `--resume`,
`--permission-mode`, `--allowedTools`, `--disallowedTools`, `--settings`); a profile that sets a model or reasoning is refused rather than guessed when Claude Code cannot be asked.

## Profiles

```json
"profiles": {
  "claude-main":   { "harness": "claude-code", "model": "opus",   "reasoning": "high" },
  "claude-review": { "harness": "claude-code", "model": "sonnet", "reasoning": "low" },
  "claude-native": { "harness": "claude-code" }
}
```

* `model` is passed as `--model <model>`: an alias such as `opus` or `sonnet`, or a full model name. Omitted means Claude Code's own setting.
* `reasoning` is passed as `--effort <level>`. The level must be one the installed `claude --help` lists (for example `low`, `medium`, `high`, `xhigh`, `max`); the list is read from the
  installed program, never assumed, so a level your version does not offer is refused with the levels it does offer. Omitted means Claude Code's own setting.
* `claude-code-local` is built in: Claude Code with its own defaults. It cannot be redefined.
* There is no argument, environment or command member. The permission mode, the allow and deny rules and the evidence hooks never depend on the profile.

A recorded model and reasoning are the **main native session configuration AgenticWorkflow requested**. They do not guarantee that Claude Code's own internal helper requests use only that
model.

## Conversations and resume

Each task's conversation id is a UUID AgenticWorkflow generates and passes with `--session-id`. Every continuation (approve, answer, request changes, recovery) relaunches Claude Code with
`--resume <uuid>`, which re-enters the **same** conversation. Only canonical UUIDs are ever passed (Claude Code treats anything else as a search term and opens a picker). A conversation Claude
Code reports as missing is a recoverable failure, never a silent fresh start. A resumed session replays the *recorded* model and reasoning, not the current project file.

## Workflow signals and evidence

The workflow's typed signals reach Claude Code as an MCP server and its tool use is observed through hooks, both supplied per session through `--settings` with a settings file AgenticWorkflow
writes for that session only. The hooks are observational: they record what Claude Code ran, and the evidence host (`AgenticWorkflow.ClaudeEvidenceHook.exe`, shipped beside `aw.exe`) accepts
a record only for the matching run, session and turn window; a repeated call is recorded once. Run and session come from that channel, never from tool input. A check the agent ran itself
with the exact command, against exactly the final working tree, can be reused as evidence; otherwise the controller runs the check. See
[concepts.md](concepts.md#verification-and-evidence).

## Permissions, trust and native prompts

The session starts in `acceptEdits` mode: reading and editing inside the working directory need no prompt, and so do the commands the toolchain and `allowCommands` list plus read-only Git
inspection. Git history, branches, remotes and network commands are always denied, and a deny rule wins over every allow rule.

Anything else that needs permission is a **native prompt**: Claude Code asks it in its own session and a person answers it there. The same holds for "trust this folder" and a login.
Claude Code reports such a wait to Herdr as *blocked*, so the cockpit shows it as a native prompt, distinct from a workflow gate, and **Open the agent's session** opens the session where you
answer it ([herdr.md](herdr.md#the-agents-own-session)). AgenticWorkflow never answers, allows or denies it, and no permission-bypass flag is ever used. Open the repository in Claude Code once
and trust it yourself before the first task.

## Reviewer and prover

A reviewer or prover is a **fresh** Claude Code session with its own profile. It has the implementer's read-only inspection and toolchain commands, every file-editing tool (`Edit`, `Write`,
`MultiEdit`, `NotebookEdit`) denied, and `default` permission mode instead of `acceptEdits`. The controller also compares the working tree before and after: a change is recorded as a block,
not absorbed.

## Global configuration

AgenticWorkflow does not read for writing, create or modify Claude Code's global or user settings, or the project's `.claude` files. Everything it adds is session-local: command-line
options and the per-session settings file. Uninstalling leaves Claude Code exactly as it was.
