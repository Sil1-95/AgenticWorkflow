// Product vocabulary and small text helpers. Pure: no Herdr, no I/O. Unknown values from a newer controller are shown humanized, never dropped.

const humanize = (value) => String(value ?? "").replace(/_/g, " ").trim();

export const STAGE_LABEL = {
  clarify: "CLARIFY",
  what: "WHAT",
  how: "HOW",
  implement: "IMPLEMENT",
  verify: "VERIFY",
  review: "REVIEW",
  prove: "PROVE",
  accept: "ACCEPT",
  completion: "COMPLETION",
  none: "",
};

export const STATE_LABEL = {
  running: "Running",
  awaiting_human: "Needs you",
  blocked: "BLOCKED",
  completed: "Completed",
  cancelled: "Cancelled",
  failed: "Failed",
};

export const stageLabel = (stage) => (stage in STAGE_LABEL ? STAGE_LABEL[stage] : humanize(stage).toUpperCase());
export const stateLabel = (state) => (state in STATE_LABEL ? STATE_LABEL[state] : humanize(state));

const STAGE_REASON = {
  awaiting_human_answer: "The agent asked a question and is waiting for your answer.",
  awaiting_human_approval: "A proposal is waiting for your decision.",
  awaiting_human_acceptance: "The work is verified and waiting for your ACCEPT.",
  proposal_not_yet_made: "The agent has not proposed it yet.",
  proposal_revision_owed: "The agent owes a revised proposal.",
  approval_invalidated: "An earlier approval no longer applies, so it must be approved again.",
  human_input_being_acted_on: "Your decision is being acted on.",
  initial_implementation: "The agent is implementing.",
  implementation_stale: "The implementation no longer matches what was approved, so it must be redone.",
  awaiting_verification: "The controller is verifying the implementation.",
  verification_failed: "Verification failed; the agent is fixing it.",
  review_due: "An independent review is due.",
  review_findings_owed: "The reviewer's findings are owed.",
  proof_contract_owed: "The implementer owes a Proof Contract.",
  prove_due: "An independent proof is due.",
  proof_findings_owed: "The prover's findings are owed.",
  accept_gate_not_yet_opened: "The ACCEPT gate has not opened yet.",
  eligible_to_complete: "The work is eligible to complete.",
  terminal: "Finished.",
};

export const stageReasonText = (reason) => STAGE_REASON[reason] ?? humanize(reason);

const BLOCK_KIND = {
  recoverable_continuation: "The work you asked for was interrupted before it could finish. It can be retried.",
  runtime_failure: "The agent runtime failed.",
  unresolved_verification: "Verification still fails after the permitted turns.",
  what_stage_violated: "The WHAT stage was violated: the agent worked before the WHAT was approved, or never proposed a required WHAT.",
  how_stage_violated: "The HOW stage was violated: the agent worked before the HOW was approved, or never proposed a required HOW.",
  reviewer_modified_workspace: "The independent reviewer changed files it was only meant to read. The workspace was left untouched for you to inspect.",
  review_unresolved: "The independent REVIEW still requires changes after the last permitted turn.",
  prover_modified_workspace: "The independent prover changed files it was only meant to read. The workspace was left untouched for you to inspect.",
  proof_unresolved: "The PROVE stage still fails or is inconclusive after the last permitted turn.",
  native_interaction_unavailable: "The agent needed you in its own interface and its session ended before anyone could answer it.",
  other: "The task is blocked.",
};

export const blockKindText = (kind) => BLOCK_KIND[kind] ?? humanize(kind);

const ROLE = { root: "Implementer", reviewer: "Reviewer", prover: "Prover" };
export const roleLabel = (role) => ROLE[role] ?? humanize(role);

const MISSING_REASON = {
  work_requires_root: "Work cannot start: the implementer role has no profile.",
  review_required: "An independent REVIEW is required and the reviewer role has no profile.",
  prove_required: "An independent PROVE is required and the prover role has no profile.",
};
export const missingReasonText = (reason) => MISSING_REASON[reason] ?? humanize(reason);

export const HARNESS_LABEL = { "claude-code": "Claude Code", pi: "Pi" };
export const harnessLabel = (harness) => HARNESS_LABEL[harness] ?? (harness ? String(harness) : "unknown harness");

const NATIVE_DEFAULT = "native default";

/** How a profile is always shown: profile id → harness → model (or native default) → reasoning (or native default). */
export function describeProfile({ profile, harness, model, reasoning }) {
  return [profile, harnessLabel(harness), model ?? NATIVE_DEFAULT, reasoning ?? NATIVE_DEFAULT].join(" → ");
}

export const shortId = (id) => (typeof id === "string" ? id.slice(0, 8) : "");

/** "just now", "3 min ago", "2 h ago", or the date. `now` is injected so it is testable. */
export function ago(iso, now = Date.now()) {
  const then = Date.parse(iso ?? "");
  if (!Number.isFinite(then)) return "";
  const seconds = Math.max(0, Math.round((now - then) / 1000));
  if (seconds < 45) return "just now";
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.round(seconds / 3600)} h ago`;
  return new Date(then).toISOString().slice(0, 10);
}

/** The last path segment, for a compact project name. */
export function folderName(path) {
  const parts = String(path ?? "").split(/[\\/]+/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : String(path ?? "");
}

/** One line of free text, bounded, for a list row. */
export function oneLine(text, max = 90) {
  const flat = String(text ?? "").replace(/\s+/g, " ").trim();
  return flat.length <= max ? flat : `${flat.slice(0, max - 1)}…`;
}
