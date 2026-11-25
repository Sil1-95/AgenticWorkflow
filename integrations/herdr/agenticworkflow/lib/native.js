// The agent's native session, as the cockpit presents it. Pure.
//
// Two things stay visibly apart. A WORKFLOW decision (WHAT, HOW, ACCEPT, an answer) is made with the cockpit's buttons. A NATIVE interaction (the agent's own permission prompt,
// its project-trust question, an extension dialog) is answered in the agent's own session, which the cockpit only opens: it never answers one, and never offers a button that would.

import { harnessLabel } from "./text.js";

/** Herdr's agent states (hc status) as words. A state this code does not know is shown as reported. */
const AGENT_STATE = { working: "working", idle: "idle", done: "idle", blocked: "waiting for you" };

export function agentStateText(state) {
  if (!state) return "unknown";
  return AGENT_STATE[state] ?? String(state);
}

/** The harness that runs the task's current work: the implementer's, unless the open request is about another role. Reads the bindings only. */
export function activeHarness(task) {
  const root = (task?.bindings ?? []).find((b) => b.role === "root");
  return root?.configuration?.harness ?? null;
}

/**
 * The native-session card.
 * @param task  the task read model
 * @param described  the result of `aw attach <task> --describe` (or null when it has not been asked yet)
 */
export function nativeCard(task, described) {
  const interaction = task?.operational?.nativeInteraction ?? null;
  const live = task?.operational?.liveness === "live";
  const harness = harnessLabel(activeHarness(task));
  const needsYou = !!interaction || described?.agentState === "blocked" || task?.pendingRequest?.kind === "native_agent_interaction";

  const card = {
    harness,
    live,
    available: !!described?.available,
    status: described?.available ? agentStateText(described.agentState) : live ? "starting or not visible yet" : "not running",
    message: described?.agentMessage ?? null,
    needsYou,
    needsYouText: null,
    canOpen: !!described?.available,
    unavailableReason: described?.available ? null : (described?.reason ?? (live ? "The agent session is not visible yet." : "No agent is running for this task.")),
    note: "Workflow decisions are made here. Questions the agent itself asks (permissions, trust, dialogs) are answered in its own session.",
  };

  if (needsYou) {
    const what = interaction?.description ?? described?.agentMessage ?? "the agent is waiting for you";
    card.needsYouText = `The agent is waiting for you in its own session: ${what}. Open the session to answer it. The cockpit never answers it for you.`;
  }
  return card;
}

/**
 * A short, honest hint for a harness whose startup question is not reported as a status (Pi's project-trust prompt): if the agent is live, not yet visible as working, and has been
 * for a while, the person should look at its session.
 */
export function startupHint(task, described, secondsSinceStart) {
  if (described?.agentState === "working" || described?.agentState === "blocked") return null;
  if (!described?.available) return null;
  if (secondsSinceStart < 20) return null;
  if (activeHarness(task) !== "pi") return null;
  return "Pi may be asking whether to trust this project, which it does not report as a waiting state. Open the session and answer it there.";
}

/** The argument list that opens the session in a Herdr tab. A task id is validated as a GUID before it is placed in an argument. */
export function attachArgv(awPath, taskId) {
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(String(taskId ?? ""))) throw new Error("not a task id");
  return [awPath, "attach", taskId];
}
