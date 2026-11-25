// View models: what the cockpit shows, derived from the controller's typed read model. Pure.
//
// Nothing here reads a file or reproduces workflow logic. Every fact is taken from the read model as it is (state, stage, currentness, allowed actions); this module only chooses
// words and groups them. A value a newer controller adds that this code does not know is shown humanized, never hidden and never guessed.

import { ago, blockKindText, describeProfile, folderName, harnessLabel, missingReasonText, oneLine, roleLabel, shortId, stageLabel, stageReasonText, stateLabel } from "./text.js";

const PENDING_LABEL = {
  clarify_answer: "Answer a question",
  what_approval: "Decide the WHAT",
  how_approval: "Decide the HOW",
  accept_decision: "ACCEPT the result",
  native_agent_interaction: "Needs you in the agent's session",
};

export const pendingLabel = (kind) => PENDING_LABEL[kind] ?? String(kind ?? "").replace(/_/g, " ");

/** A role's binding as the person reads it. */
export function bindingText(binding) {
  const c = binding.configuration;
  return describeProfile({ profile: binding.profile?.id ?? binding.profile, harness: c?.harness, model: c?.model, reasoning: c?.reasoning });
}

/** The tone a row or header takes: what the person should notice first. */
export function toneOf(state, pendingRequest) {
  if (pendingRequest) return "attention";
  if (state === "blocked" || state === "failed") return "blocked";
  if (state === "running") return "working";
  if (state === "completed") return "done";
  return "idle";
}

/** One row of the task list, from a controller `TaskSummary`. */
export function rowOf(summary, now = Date.now()) {
  if (!summary.readable) {
    return { id: summary.taskId, readable: false, tone: "blocked", intent: "(unreadable task)", project: "", workspace: summary.workspace ?? "", status: "Unreadable", stage: "", pending: null, blocked: summary.problem ?? "its state cannot be read", agents: "", lastActivity: "", streamVersion: null };
  }
  const roles = (summary.bindings ?? []).map((b) => `${roleLabel(b.role)}: ${b.profile} · ${harnessLabel(b.harness)}`);
  return {
    id: summary.taskId,
    readable: true,
    tone: toneOf(summary.state, summary.pendingRequest),
    intent: oneLine(summary.intent, 110),
    project: folderName(summary.workspace),
    workspace: summary.workspace ?? "",
    status: stateLabel(summary.state),
    stage: stageLabel(summary.stage),
    pending: summary.pendingRequest ? pendingLabel(summary.pendingRequest) : null,
    blocked: summary.state === "blocked" ? blockKindText(summary.block ?? "other") : null,
    agents: roles.join("   "),
    lastActivity: ago(summary.lastEventAt, now),
    streamVersion: summary.streamVersion ?? null,
  };
}

const TONE_ORDER = { attention: 0, blocked: 1, working: 2, idle: 3, done: 4 };

/** Rows that need a person first, then blocked, then running, then the rest, each newest first. */
export function sortRows(rows) {
  return [...rows].sort((a, b) => (TONE_ORDER[a.tone] ?? 9) - (TONE_ORDER[b.tone] ?? 9));
}

// ---- detail -----------------------------------------------------------------------------------------------------------------------

const bullet = (lines) => lines.filter(Boolean).map((l) => `- ${l}`).join("\n");
const quote = (text) => String(text ?? "").split("\n").map((l) => `> ${l}`).join("\n");

/** Why the workflow moved backward or stopped, in plain words, newest concern first. */
export function movedBackNotices(task) {
  const notices = [];
  for (const r of task.reclassifications ?? []) {
    if (!r.isActive) continue;
    const invalidated = (r.invalidatedGateIds ?? []).length;
    notices.push({
      id: `reclassification:${r.id}`,
      title: `New evidence sent the task back to ${stageLabel(r.stage)}`,
      text: `The agent reported: ${r.reason}${r.evidence ? `\nEvidence: ${r.evidence}` : ""}\n${invalidated ? `${invalidated} earlier approval${invalidated === 1 ? "" : "s"} no longer apply, ` : ""}${stageLabel(r.stage)} must be approved again before anything is implemented.${r.provisionalWork ? " Work already done is provisional." : ""}`,
    });
  }
  const latestInvalid = {};
  for (const g of task.gates ?? []) if (g.status === "invalidated" && g.invalidation) latestInvalid[g.type] = g;
  for (const g of Object.values(latestInvalid)) {
    if ((task.reclassifications ?? []).some((r) => r.isActive && r.id === g.invalidation.reclassificationId)) continue;
    notices.push({ id: `invalidated:${g.id}`, title: `The ${stageLabel(g.type)} approval no longer applies`, text: g.invalidation.reason });
  }
  if (task.stageReason === "implementation_stale") notices.push({ id: "implementation_stale", title: "The implementation was superseded", text: stageReasonText("implementation_stale") });
  if (task.verification?.outcome === "failed" && task.verification?.currency === "current") notices.push({ id: "verification_failed", title: "Verification failed", text: stageReasonText("verification_failed") });
  const review = task.review;
  if (review?.status === "changes_required") notices.push({ id: "review_changes", title: "REVIEW found problems", text: "The independent reviewer asked for changes; the agent is revising before the work is verified again." });
  const proof = task.proof;
  if (proof?.status === "failed" || proof?.status === "inconclusive") notices.push({ id: "proof_not_passed", title: `PROVE ${proof.status === "failed" ? "failed" : "was inconclusive"}`, text: "The independent prover could not establish every claim; the agent is revising." });
  return notices;
}

function revisionLines(task) {
  const rev = task.semanticRevision;
  const lines = [`Semantic revision **${rev.ordinal}** (${String(rev.kind).replace(/_/g, " ")})${rev.isCurrent ? "" : " — superseded"}`];
  if (rev.explanation) lines.push(oneLine(rev.explanation, 300));
  const req = task.requirements;
  const asked = ["what", "how", "review", "prove"].filter((k) => req?.[k] && req[k].origin !== "none").map((k) => `${stageLabel(k)} ${req[k].satisfied ? "satisfied" : "required"} (${req[k].origin})`);
  if (asked.length) lines.push(`Requirements: ${asked.join(", ")}`);
  if (req?.acceptance) lines.push(`ACCEPT: ${req.acceptance === "human_required" ? "required (you decide)" : "not required"}`);
  return lines;
}

function implementationLines(task) {
  const impl = task.implementation;
  const v = task.verification;
  const lines = [];
  const latest = impl.latest;
  lines.push(latest ? `Implementation: **${impl.currency}** — revision for semantic ${latest.semanticOrdinal}${latest.targetsCurrentSemantics ? "" : " (older semantics)"}, fingerprint \`${shortId(latest.fingerprint)}\`` : "Implementation: none yet");
  if (impl.hasProvisionalWork) lines.push("There is provisional work in the working tree that is not yet approved.");
  lines.push(v.outcome ? `Verification: **${v.outcome}** (${v.currency})` : `Verification: not run (${v.currency})`);
  const run = (v.runs ?? []).find((r) => r.id === v.currentRunId) ?? (v.runs ?? []).at(-1);
  for (const c of run?.checks ?? []) lines.push(`  - ${c.checkId}: ${c.outcome}${c.source ? ` (${String(c.source).replace(/_/g, " ")})` : ""}${c.summary ? ` — ${oneLine(c.summary, 120)}` : ""}`);
  return lines;
}

function reviewLines(task) {
  const r = task.review;
  if (!r || (r.status === "none" && !r.isDue && !(r.requests ?? []).length)) return null;
  const lines = [`REVIEW: **${String(r.status).replace(/_/g, " ")}**${r.isDue ? " — due" : ""}${r.reviewerProfile ? ` — reviewer profile \`${r.reviewerProfile}\`` : ""}`];
  const round = (r.rounds ?? []).find((x) => x.isCurrent) ?? (r.rounds ?? []).at(-1);
  if (round) {
    lines.push(`Round ${round.round}: ${String(round.verdict).replace(/_/g, " ")}${round.isCurrent ? "" : " (not current)"} — ${oneLine(round.summary, 300)}`);
    for (const f of round.findings ?? []) lines.push(`  - **${f.title}**${f.location ? ` (${f.location})` : ""}: ${oneLine(f.impact, 200)} → ${oneLine(f.recommendation, 200)}`);
  }
  return lines;
}

function proofLines(task) {
  const p = task.proof;
  if (!p || (p.status === "none" && !p.isDue && !p.isContractMissing && !(p.contracts ?? []).length)) return null;
  const lines = [`PROVE: **${String(p.status).replace(/_/g, " ")}**${p.isDue ? " — due" : ""}${p.isContractMissing ? " — Proof Contract missing" : ""}${p.proverProfile ? ` — prover profile \`${p.proverProfile}\`` : ""}`];
  const contract = (p.contracts ?? []).find((c) => c.isCurrent) ?? (p.contracts ?? []).at(-1);
  const round = (p.rounds ?? []).find((x) => x.isCurrent) ?? (p.rounds ?? []).at(-1);
  for (const claim of contract?.claims ?? []) {
    const result = (round?.claims ?? []).find((c) => c.claimId === claim.id);
    lines.push(`  - ${claim.statement}${result ? ` — **${String(result.verdict).replace(/_/g, " ")}**: ${oneLine(result.summary, 200)}` : ""}`);
    for (const e of result?.evidence ?? []) lines.push(`      evidence (${e.kind}): ${oneLine(e.value, 160)}`);
  }
  if (round) lines.push(`Round ${round.round}: ${String(round.verdict).replace(/_/g, " ")} — ${oneLine(round.summary, 300)}`);
  return lines;
}

/** The read-only part of the detail: header facts, then named sections as markdown. */
export function detailOf(task, now = Date.now()) {
  const tone = toneOf(task.state, task.pendingRequest);
  const roles = (task.bindings ?? []).map((b) => `**${roleLabel(b.role)}** — ${bindingText(b)}`);
  const missing = (task.missingBindings ?? []).map((m) => `**${roleLabel(m.role)}** has no profile. ${missingReasonText(m.reason)}`);
  const sections = [
    { id: "revision", title: "Semantic revision", markdown: bullet(revisionLines(task)) },
    { id: "work", title: "Implementation & verification", markdown: bullet(implementationLines(task)) },
    { id: "roles", title: "Roles and profiles", markdown: bullet([...roles, ...missing]) || "No role is bound yet." },
  ];
  const review = reviewLines(task);
  if (review) sections.push({ id: "review", title: "REVIEW", markdown: bullet(review) });
  const proof = proofLines(task);
  if (proof) sections.push({ id: "prove", title: "PROVE", markdown: bullet(proof) });
  if ((task.completion?.blockingReasons ?? []).length) sections.push({ id: "completion", title: "Not yet eligible to complete", markdown: bullet(task.completion.blockingReasons) });
  return {
    id: task.taskId,
    intent: task.intent,
    tone,
    status: stateLabel(task.state),
    stage: stageLabel(task.stage),
    stageReason: stageReasonText(task.stageReason),
    stateReason: task.stateReason,
    streamVersion: task.streamVersion,
    project: folderName(task.operational?.project?.workspace),
    workspace: task.operational?.project?.workspace ?? "",
    lastActivity: ago(task.lastEventAt, now),
    notices: movedBackNotices(task),
    sections,
    block: task.block ? { kind: task.block.kind, text: blockKindText(task.block.kind), reason: task.block.reason } : null,
    unavailable: task.operational?.unavailable ?? [],
  };
}

export { quote };
