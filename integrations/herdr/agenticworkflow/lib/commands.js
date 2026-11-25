// Typed controller requests, built from the task read model the person is looking at. Pure.
//
// A decision is never inferred from text. Each builder reads the exact gate, semantic revision, content hash and (for ACCEPT) implementation and verification the read
// model currently shows, and refuses (fails closed, with a reason) when the model does not offer that decision. The controller checks the same bindings again and refuses
// a stale one; nothing here is a substitute for that check, it only means the cockpit never sends a decision the model does not offer.

const refuse = (reason) => ({ ok: false, reason });
const accept = (request) => ({ ok: true, request });

export const MAX_FEEDBACK = 4000;
export const MAX_ANSWER = 4000;
export const MAX_OBJECTIVE = 16000;

const gateOf = (task, id) => (task?.gates ?? []).find((g) => g.id === id) ?? null;

/** The open gate the person is asked about, with its kind, or a reason there is none. */
function openGate(task, kinds) {
  const pending = task?.pendingRequest;
  if (!pending || !pending.gateId) return { reason: "Nothing is waiting for your decision." };
  const gate = gateOf(task, pending.gateId);
  if (!gate) return { reason: "The gate this decision is about is not in the task any more. Refresh." };
  if (!kinds.includes(gate.type)) return { reason: "The open gate is not of the kind this decision is for." };
  return { pending, gate };
}

const allows = (pending, action) => (pending.allowedActions ?? []).includes(action);

export function approveRequest(task) {
  const found = openGate(task, ["what", "how"]);
  if (found.reason) return refuse(found.reason);
  const { pending, gate } = found;
  if (!allows(pending, "approve")) return refuse("Approving is not legal for this gate any more.");
  if (!gate.isCurrent) return refuse("This proposal is no longer current and cannot be approved.");
  const proposal = gate.type === "what" ? gate.what : gate.how;
  if (!proposal?.contentHash) return refuse("The proposal's content is not available to approve.");
  return accept({
    operation: "approve",
    proposal: gate.type,
    taskId: task.taskId,
    gateId: gate.id,
    semanticRevisionId: gate.binding.semanticRevisionId,
    contentHash: proposal.contentHash,
    presence: "attended",
  });
}

export function requestChangesRequest(task, feedback) {
  const found = openGate(task, ["what", "how", "accept"]);
  if (found.reason) return refuse(found.reason);
  const { pending, gate } = found;
  const text = String(feedback ?? "").trim();
  if (!text) return refuse("Say what to change; sending a gate back without feedback is not a request.");
  if (text.length > MAX_FEEDBACK) return refuse(`Feedback is limited to ${MAX_FEEDBACK} characters.`);
  if (!allows(pending, "request_changes")) return refuse("Requesting changes is not legal for this gate any more.");
  if (!gate.isCurrent) return refuse("This gate is no longer current and cannot be sent back.");
  const request = {
    operation: "request_changes",
    gate: gate.type,
    taskId: task.taskId,
    gateId: gate.id,
    semanticRevisionId: gate.binding.semanticRevisionId,
    feedback: text,
    presence: "attended",
  };
  if (gate.type === "accept") {
    request.implementationRevisionId = gate.binding.implementationRevisionId;
    request.verificationRunId = gate.binding.verificationRunId;
  }
  return accept(request);
}

export function acceptRequest(task) {
  const found = openGate(task, ["accept"]);
  if (found.reason) return refuse(found.reason);
  const { pending, gate } = found;
  if (!allows(pending, "accept")) return refuse("Accepting is not legal for this gate any more.");
  if (!gate.isCurrent) return refuse("This result is no longer current and cannot be accepted.");
  if (task.operational?.workingTree === "changed") return refuse("The working tree changed after it was verified, so it cannot be accepted. Send it back, or abort.");
  const { semanticRevisionId, implementationRevisionId, verificationRunId } = gate.binding;
  if (!implementationRevisionId || !verificationRunId) return refuse("The gate does not name the implementation and verification to accept.");
  return accept({ operation: "accept", taskId: task.taskId, gateId: gate.id, semanticRevisionId, implementationRevisionId, verificationRunId });
}

export function answerRequest(task, answer) {
  const found = openGate(task, ["clarify"]);
  if (found.reason) return refuse(found.reason);
  const { pending, gate } = found;
  const text = String(answer ?? "").trim();
  if (!text) return refuse("Write an answer first.");
  if (text.length > MAX_ANSWER) return refuse(`An answer is limited to ${MAX_ANSWER} characters.`);
  if (!allows(pending, "answer")) return refuse("Answering is not legal for this question any more.");
  if (!gate.isCurrent) return refuse("This question is no longer current.");
  return accept({
    operation: "answer_clarification",
    taskId: task.taskId,
    gateId: gate.id,
    semanticRevisionId: gate.binding.semanticRevisionId,
    answer: text,
    presence: "attended",
  });
}

/** Abort is legal at a gate (a stale one too) and when blocked; the gate is named when one is open, so the controller can check it is still the one meant. */
export function abortRequest(task) {
  const pending = task?.pendingRequest;
  if (pending?.gateId) {
    if (!allows(pending, "abort")) return refuse("Aborting is not legal here.");
    return accept({ operation: "abort", taskId: task.taskId, gateId: pending.gateId });
  }
  if (task?.state === "blocked" && (task.recovery?.actions ?? []).includes("abort")) return accept({ operation: "abort", taskId: task.taskId });
  return refuse("There is nothing to abort right now.");
}

export function continueRequest(task) {
  if (task?.state !== "blocked" || !(task.recovery?.actions ?? []).includes("continue")) return refuse("This task has no recoverable work to continue.");
  if ((task.missingBindings ?? []).length > 0) return refuse("A role still needs a profile. Choose one first.");
  return accept({ operation: "continue_task", taskId: task.taskId, presence: "attended" });
}

export function bindRequest(task, role, profile) {
  const missing = (task?.missingBindings ?? []).find((m) => m.role === role);
  if (!missing) return refuse("That role does not need a profile.");
  if (!profile) return refuse("Choose a profile for the role.");
  return accept({ operation: "bind_role", taskId: task.taskId, role, profile });
}

/**
 * A new task. `bindings` is `{root, reviewer?, prover?}`: exactly the profiles the person chose, passed through unchanged. Nothing is defaulted here: a missing implementer profile
 * is refused, and an absent reviewer or prover is simply not bound (the controller refuses the start if a required stage needs one).
 */
export function startRequest({ objective, workspace, bindings, acceptance, requireWhat, requireHow, requireReview, requireProve }) {
  const text = String(objective ?? "").trim();
  if (!text) return refuse("Describe what you want done.");
  if (text.length > MAX_OBJECTIVE) return refuse(`The objective is limited to ${MAX_OBJECTIVE} characters.`);
  if (!workspace || !/^([a-zA-Z]:[\\/]|\/)/.test(workspace)) return refuse("Choose the project folder (an absolute path).");
  if (!bindings?.root) return refuse("Choose the profile the implementer runs under.");
  const chosen = {};
  for (const role of ["root", "reviewer", "prover"]) if (bindings[role]) chosen[role] = bindings[role];
  const request = { operation: "start_task", objective: text, workingDirectory: workspace, workspace, bindings: chosen, presence: "attended" };
  if (acceptance === "none" || acceptance === "human") request.acceptance = acceptance === "human" ? "human_required" : "not_required";
  if (requireWhat) request.requireWhat = true;
  if (requireHow) request.requireHow = true;
  if (requireReview) request.requireReview = true;
  if (requireProve) request.requireProve = true;
  return accept(request);
}
