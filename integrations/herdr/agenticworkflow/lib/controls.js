// What the person can do with a task right now. Pure.
//
// Controls are derived only from the read model's own offer (pendingRequest.allowedActions, recovery.actions, missingBindings). A control the model does not offer is
// not shown; a control it offers but whose request cannot be built (a stale proposal, say) is shown disabled with the reason.

import { abortRequest, acceptRequest, answerRequest, approveRequest, bindRequest, continueRequest, requestChangesRequest } from "./commands.js";
import { harnessLabel, missingReasonText, roleLabel, stageLabel } from "./text.js";

/**
 * The decision panel for the task's open request, or null when nothing is being asked of the person at the workflow level.
 * `kind` is the read model's own request kind, so each type has its own panel.
 */
export function decisionPanel(task) {
  const pending = task?.pendingRequest;
  if (!pending) return null;
  if (pending.kind === "native_agent_interaction") return null;
  const gate = (task.gates ?? []).find((g) => g.id === pending.gateId) ?? null;
  const stale = gate && !gate.isCurrent;
  const staleNote = stale ? "This is no longer current: the task moved while you were reading. You can still send it back or abort." : null;
  const panel = { kind: pending.kind, gateId: pending.gateId, staleNote, content: [], inputs: [], actions: [] };

  switch (pending.kind) {
    case "clarify_answer":
      panel.title = "The agent has a question";
      panel.content = [{ heading: "Question", text: gate?.clarification?.question ?? "" }, { heading: "Why it asks", text: gate?.clarification?.reason ?? "" }];
      panel.inputs = [{ id: "answer", label: "Your answer", multiline: true }];
      panel.actions = [action("answer", "Send answer", (t, v) => answerRequest(t, v.answer), "primary", pending), action("abort", "Abort task", (t) => abortRequest(t), "danger", pending)];
      break;
    case "what_approval":
      panel.title = "Decide the WHAT";
      panel.content = whatContent(gate?.what);
      panel.inputs = [{ id: "feedback", label: "Feedback (needed to request changes)", multiline: true }];
      panel.actions = [
        action("approve", "Approve WHAT", (t) => approveRequest(t), "primary", pending),
        action("request_changes", "Request changes", (t, v) => requestChangesRequest(t, v.feedback), "secondary", pending),
        action("abort", "Abort task", (t) => abortRequest(t), "danger", pending),
      ];
      break;
    case "how_approval":
      panel.title = "Decide the HOW";
      panel.content = howContent(gate?.how);
      panel.inputs = [{ id: "feedback", label: "Feedback (needed to request changes)", multiline: true }];
      panel.actions = [
        action("approve", "Approve HOW", (t) => approveRequest(t), "primary", pending),
        action("request_changes", "Request changes", (t, v) => requestChangesRequest(t, v.feedback), "secondary", pending),
        action("abort", "Abort task", (t) => abortRequest(t), "danger", pending),
      ];
      break;
    case "accept_decision":
      panel.title = "ACCEPT the result";
      panel.content = acceptContent(task, gate);
      panel.inputs = [{ id: "feedback", label: "Feedback (needed to request changes)", multiline: true }];
      panel.actions = [
        action("accept", "Accept", (t) => acceptRequest(t), "primary", pending),
        action("request_changes", "Request changes", (t, v) => requestChangesRequest(t, v.feedback), "secondary", pending),
        action("abort", "Abort task", (t) => abortRequest(t), "danger", pending),
      ];
      break;
    default:
      panel.title = "A decision is waiting";
      panel.actions = [action("abort", "Abort task", (t) => abortRequest(t), "danger", pending)];
  }
  panel.actions = panel.actions.filter((a) => a.offered);
  return panel;
}

const ACTION_KEY = { answer: "answer", approve: "approve", request_changes: "request_changes", accept: "accept", abort: "abort" };

function action(id, label, build, style, pending) {
  return { id, label, style, build, offered: (pending.allowedActions ?? []).includes(ACTION_KEY[id]) };
}

const section = (heading, text) => (text ? { heading, text } : null);
const compact = (items) => items.filter(Boolean);

function whatContent(w) {
  if (!w) return [{ heading: "WHAT", text: "The proposal is not available." }];
  return compact([
    section("Objective", w.objective),
    section("Required behavior", w.requiredBehavior),
    section("Scope", w.scope),
    section("Constraints", w.constraints),
    section("Non-goals", w.nonGoals),
    section("Acceptance conditions", w.acceptanceConditions),
    section("Rationale", w.rationale),
  ]);
}

function howContent(h) {
  if (!h) return [{ heading: "HOW", text: "The proposal is not available." }];
  return compact([
    section("Strategy", h.strategy),
    section("Components", h.components),
    section("Data and control flow", h.dataAndControlFlow),
    section("Compatibility", h.compatibility),
    section("Risks and trade-offs", h.risksAndTradeoffs),
    section("Verification approach", h.verificationApproach),
    section("Rationale", h.rationale),
  ]);
}

/** What is being accepted: the content that was approved, and the evidence it rests on. */
function acceptContent(task, gate) {
  const out = [];
  const what = [...(task.gates ?? [])].reverse().find((g) => g.type === "what" && g.status === "approved");
  if (what?.what) out.push({ heading: "Approved WHAT", text: `${what.what.objective}\n\n${what.what.requiredBehavior}` });
  const how = [...(task.gates ?? [])].reverse().find((g) => g.type === "how" && g.status === "approved");
  if (how?.how) out.push({ heading: "Approved HOW", text: how.how.strategy });
  const v = task.verification;
  const run = (v.runs ?? []).find((r) => r.id === gate?.binding?.verificationRunId) ?? (v.runs ?? []).find((r) => r.id === v.currentRunId);
  out.push({ heading: "Implementation", text: `${task.implementation.currency} · semantic revision ${gate?.binding?.semanticOrdinal ?? task.semanticRevision.ordinal}` });
  out.push({ heading: "Verification", text: `${run?.outcome ?? "not run"} (${v.currency})\n${(run?.checks ?? []).map((c) => `${c.checkId}: ${c.outcome}`).join("\n")}`.trim() });
  if (task.review.status !== "none") out.push({ heading: "REVIEW", text: `${String(task.review.status).replace(/_/g, " ")}` });
  if (task.proof.status !== "none") out.push({ heading: "PROVE", text: `${String(task.proof.status).replace(/_/g, " ")}` });
  if (task.operational?.workingTree === "changed") out.push({ heading: "Warning", text: "The working tree changed after it was verified. It cannot be accepted as it stands." });
  return out;
}

/**
 * Applies a panel action to the task the person is looking at: the typed request, or the reason it cannot be sent.
 * Always rebuilt from the task passed in, so it carries exactly the gate, revision and hash currently shown.
 */
export function buildFromPanel(task, panel, actionId, values) {
  const found = (panel?.actions ?? []).find((a) => a.id === actionId);
  if (!found) return { ok: false, reason: "That action is not offered here." };
  return found.build(task, values ?? {});
}

/** Whether an action is worth enabling at all (its request can be built from what is shown). Inputs are not considered: those are checked on send. */
export function actionAvailability(task, panel) {
  return Object.fromEntries(
    (panel?.actions ?? []).map((a) => {
      const probe = a.build(task, { answer: "x", feedback: "x" });
      return [a.id, probe.ok ? { enabled: true } : { enabled: false, reason: probe.reason }];
    }),
  );
}

// ---- blocked / recovery ----------------------------------------------------------------------------------------------------------

/** The recovery card for a BLOCKED task: why, and only the legal actions. */
export function recoveryPanel(task) {
  if (task?.state !== "blocked") return null;
  const legal = task.recovery?.actions ?? [];
  const missing = task.missingBindings ?? [];
  const actions = [];
  if (legal.includes("continue")) actions.push({ id: "continue", label: missing.length ? "Continue (choose a profile first)" : "Continue", style: "primary", disabled: missing.length > 0, reason: missing.length ? "A role still needs a profile." : null, build: (t) => continueRequest(t) });
  if (legal.includes("abort")) actions.push({ id: "abort", label: "Abort task", style: "danger", disabled: false, reason: null, build: (t) => abortRequest(t) });
  return { reason: task.block?.reason ?? task.stateReason, kind: task.block?.kind ?? "other", pendingWork: task.recovery?.pendingWork ?? null, actions, noActions: actions.length === 0 };
}

// ---- missing bindings ------------------------------------------------------------------------------------------------------------

/**
 * One card per role that needs a profile. Compatible profiles are the project's profiles the controller can run for that role; each is shown as profile → harness → model →
 * reasoning. Nothing is preselected: the person picks.
 * `profiles` is the project inspection's profile list (id, harness, model, reasoning, usable).
 */
export function bindingCards(task, profiles) {
  return (task?.missingBindings ?? []).map((m) => ({
    role: m.role,
    roleLabel: roleLabel(m.role),
    why: missingReasonText(m.reason),
    options: (profiles ?? []).filter((p) => p.usable !== false).map((p) => ({ id: p.id, harness: harnessLabel(p.harness), harnessId: p.harness, model: p.model ?? null, reasoning: p.reasoning ?? null })),
    build: (t, profile) => bindRequest(t, m.role, profile),
  }));
}

// ---- native agent ----------------------------------------------------------------------------------------------------------------

export { stageLabel };
