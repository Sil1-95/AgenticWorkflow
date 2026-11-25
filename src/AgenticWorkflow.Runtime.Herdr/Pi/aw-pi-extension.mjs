// AgenticWorkflow's Pi extension. Loaded for ONE Pi session with `-e` (never installed, never discovered, nothing global or project-level is read or written).
// Its entire job:
//   1. host the workflow's MCP server (the same executable and tools that serve Claude Code) for this session only;
//   2. when the session calls one of those tools, hand the call to the AgenticWorkflow hook executable together with the session id of Pi's OWN session manager
//      (never an id the model supplied), so the record is attributed to this session, and stop the turn when the hook says to;
//   3. report, once, the session id / model / thinking level Pi actually started with, so the controller can refuse a launch that was silently clamped or substituted;
//   4. refuse a few shell commands (history, remotes, network) before they run: the Pi equivalent of the Claude launch's deny list. It never allows anything Pi would
//      not, and it never answers a Pi prompt or approves a permission.
// It has no network access of its own, writes only the one launch report beside it (outside the workspace) and runs only the hook executable named in pi-bridge.json.
import { spawn } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { basename, dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const bridge = JSON.parse(readFileSync(join(here, "pi-bridge.json"), "utf8"));
const TOOL_PREFIX = "mcp__aw__";
const HOOK_TIMEOUT_MS = 30000;
const recorded = new Set();

// Runs the hook exactly as Claude Code's hook is run: one JSON payload on stdin, the channel directory as the only argument, no shell. Resolves with its stdout.
function runHook(payload) {
  return new Promise((resolve) => {
    let out = "";
    let child;
    try {
      child = spawn(bridge.hook, [bridge.channel], { stdio: ["pipe", "pipe", "ignore"], windowsHide: true });
    } catch {
      resolve("");
      return;
    }
    const timer = setTimeout(() => child.kill(), HOOK_TIMEOUT_MS);
    child.stdout.on("data", (chunk) => (out += chunk));
    child.on("close", () => {
      clearTimeout(timer);
      resolve(out);
    });
    child.on("error", () => {
      clearTimeout(timer);
      resolve("");
    });
    child.stdin.on("error", () => {});
    child.stdin.end(JSON.stringify(payload));
  });
}

function stopsTurn(output) {
  try {
    return JSON.parse(output).continue === false;
  } catch {
    return false;
  }
}

// Words of one shell segment with leading assignments, wrappers and git's own global options removed, the program name reduced to its bare name.
function normalise(segment) {
  const words = segment.trim().split(/\s+/).filter(Boolean);
  while (words.length > 0 && /^[A-Za-z_][A-Za-z0-9_]*=/.test(words[0])) words.shift();
  while (words.length > 0 && ["sudo", "env", "command", "exec", "nohup", "time"].includes(words[0].toLowerCase())) words.shift();
  if (words.length === 0) return "";
  words[0] = basename(words[0].replace(/\\/g, "/")).replace(/\.exe$/i, "").toLowerCase();
  if (words[0] === "git") {
    let i = 1;
    while (i < words.length && words[i].startsWith("-")) i += ["-C", "-c", "--git-dir", "--work-tree", "--namespace"].includes(words[i]) ? 2 : 1;
    return ["git", ...words.slice(i)].join(" ").toLowerCase();
  }
  return words.join(" ").toLowerCase();
}

function deniedReason(command) {
  for (const segment of command.split(/[;&|\n`()]|\$\(/)) {
    const line = normalise(segment);
    if (line === "") continue;
    for (const prefix of bridge.deniedCommands) {
      if (line === prefix || line.startsWith(prefix + " ")) {
        return `'${prefix}' is not allowed in an AgenticWorkflow session: history, branches, remotes and the network are not changed from here.`;
      }
    }
  }
  return null;
}

export default function (pi) {
  if (bridge.mcp) {
    pi.registerMcpServer("aw", { command: bridge.hook, args: ["--mcp", bridge.channel], exposure: "direct" });
  }

  pi.on("session_start", async (_event, ctx) => {
    const report = {
      sessionId: ctx.sessionManager.getSessionId(),
      model: ctx.model ? `${ctx.model.provider}/${ctx.model.id}` : null,
      thinking: pi.getThinkingLevel(),
    };
    writeFileSync(join(here, "pi-launch.json"), JSON.stringify(report));
  });

  pi.on("tool_call", async (event) => {
    if (event.toolName !== "bash") return undefined;
    const reason = deniedReason(String(event.input?.command ?? ""));
    return reason ? { block: true, reason } : undefined;
  });

  pi.on("tool_result", async (event, ctx) => {
    if (!event.toolName.startsWith(TOOL_PREFIX) || event.isError) return;
    // One observation per tool call, whatever Pi does: the hook is never asked to record the same call twice.
    if (recorded.has(event.toolCallId)) return;
    recorded.add(event.toolCallId);

    const output = await runHook({
      session_id: ctx.sessionManager.getSessionId(),
      hook_event_name: "PostToolUse",
      tool_name: event.toolName,
      tool_input: event.input,
      tool_use_id: event.toolCallId,
      cwd: ctx.cwd,
    });

    if (stopsTurn(output)) ctx.abort();
  });
}
