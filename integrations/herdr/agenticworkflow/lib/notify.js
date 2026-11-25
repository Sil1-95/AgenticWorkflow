// Sparse notifications. Pure: compares what was last seen with what is seen now and returns the few things worth interrupting the person for.
//
// Only a change worth a look is announced, and only once: a human decision is needed, the agent needs the person in its own session, a task is BLOCKED, a task completed,
// or the controller has a problem. Routine progress is never announced.

import { pendingLabel } from "./model.js";
import { folderName, oneLine } from "./text.js";

/** The signal a task row currently gives, or null. Each distinct signal is announced once per task. */
export function signalOf(row) {
  if (!row.readable) return null;
  if (row.pendingRequest === "native_agent_interaction") return "native";
  if (row.pendingRequest) return `decision:${row.pendingRequest}:${row.stage}`;
  if (row.state === "blocked") return `blocked:${row.block ?? "other"}`;
  if (row.state === "completed") return "completed";
  return null;
}

/**
 * @param {Map<string,string|null>} seen  signals already announced (task id → signal); mutated
 * @param {object[]} rows  the controller's task summaries
 * @param {boolean} first  true on the first poll after startup: what is already waiting is recorded without an announcement, except decisions and blocks, which are still worth a look
 */
export function newNotifications(seen, rows, first = false) {
  const out = [];
  for (const row of rows) {
    const signal = signalOf(row);
    const before = seen.get(row.taskId);
    if (signal === (before ?? null)) continue;
    seen.set(row.taskId, signal);
    if (!signal) continue;
    if (first && signal === "completed") continue;
    out.push({ taskId: row.taskId, signal, title: titleFor(signal), body: `${folderName(row.workspace)}: ${oneLine(row.intent, 80)}`, level: signal.startsWith("completed") ? "info" : "attention" });
  }
  return out;
}

function titleFor(signal) {
  if (signal === "native") return "The agent needs you in its session";
  if (signal.startsWith("decision:")) return pendingLabel(signal.split(":")[1]);
  if (signal.startsWith("blocked")) return "A task is BLOCKED";
  if (signal === "completed") return "Task completed";
  return "AgenticWorkflow";
}

/** A controller problem is announced when it starts and when it ends, not on every failed poll. */
export function controllerNotice(previous, error) {
  const kind = error ? error.kind : null;
  if (kind === (previous ?? null)) return null;
  if (!error) return { title: "Controller reachable again", body: "AgenticWorkflow is back.", level: "info", kind: null };
  return { title: "Controller problem", body: oneLine(error.message || error.kind, 120), level: "attention", kind };
}
