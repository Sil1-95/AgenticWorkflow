import test from "node:test";
import assert from "node:assert/strict";
import { detailOf, movedBackNotices, rowOf, sortRows } from "../lib/model.js";
import { bindingCards, buildFromPanel, decisionPanel, recoveryPanel } from "../lib/controls.js";
import { GATE, SEM, acceptTask, binding, blockedTask, clarifyTask, howTask, summary, task, whatTask } from "./fixtures.js";

const NOW = Date.parse("2026-01-01T10:06:00Z");

test("task list row: project, intent, state, stage, profile and harness, last activity", () => {
  const row = rowOf(summary({ bindings: [{ role: "root", profile: "pi-local", harness: "pi", model: "qwen", reasoning: null }, { role: "reviewer", profile: "claude-code-local", harness: "claude-code", model: null, reasoning: null }] }), NOW);
  assert.equal(row.project, "uploader");
  assert.equal(row.status, "Running");
  assert.equal(row.stage, "IMPLEMENT");
  assert.equal(row.pending, null);
  assert.match(row.agents, /Implementer: pi-local · Pi/);
  assert.match(row.agents, /Reviewer: claude-code-local · Claude Code/);
  assert.equal(row.lastActivity, "1 min ago");
  assert.equal(row.tone, "working");
});

test("a pending human request is flagged for each type", () => {
  const cases = {
    clarify_answer: "Answer a question",
    what_approval: "Decide the WHAT",
    how_approval: "Decide the HOW",
    accept_decision: "ACCEPT the result",
    native_agent_interaction: "Needs you in the agent's session",
  };
  for (const [kind, text] of Object.entries(cases)) {
    const row = rowOf(summary({ state: "awaiting_human", pendingRequest: kind }), NOW);
    assert.equal(row.pending, text, kind);
    assert.equal(row.tone, "attention");
  }
});

test("a BLOCKED task shows why in plain words", () => {
  const row = rowOf(summary({ state: "blocked", block: "reviewer_modified_workspace" }), NOW);
  assert.equal(row.tone, "blocked");
  assert.match(row.blocked, /reviewer changed files/i);
  assert.equal(row.status, "BLOCKED");
});

test("an unreadable task is shown as a problem, not hidden", () => {
  const row = rowOf({ taskId: "x", readable: false, problem: "state file is corrupt", workspace: null }, NOW);
  assert.equal(row.readable, false);
  assert.match(row.blocked, /corrupt/);
});

test("rows needing a person sort first, completed last", () => {
  const rows = [
    rowOf(summary({ taskId: "done", state: "completed" })),
    rowOf(summary({ taskId: "run", state: "running" })),
    rowOf(summary({ taskId: "need", state: "awaiting_human", pendingRequest: "what_approval" })),
    rowOf(summary({ taskId: "blk", state: "blocked" })),
  ];
  assert.deepEqual(sortRows(rows).map((r) => r.id), ["need", "blk", "run", "done"]);
});

test("unknown values from a newer controller are humanized, not dropped", () => {
  const row = rowOf(summary({ state: "paused_for_review", stage: "audit", pendingRequest: "new_kind" }), NOW);
  assert.equal(row.status, "paused for review");
  assert.equal(row.stage, "AUDIT");
  assert.equal(row.pending, "new kind");
});

test("detail shows profile → harness → model → reasoning with native defaults named", () => {
  const t = task({ bindings: [binding("root", "pi-local", "pi", "qwen3", null), binding("reviewer", "claude-code-local", "claude-code", null, "high")] });
  const d = detailOf(t, NOW);
  const roles = d.sections.find((s) => s.id === "roles").markdown;
  assert.match(roles, /pi-local → Pi → qwen3 → native default/);
  assert.match(roles, /claude-code-local → Claude Code → native default → high/);
});

test("detail shows semantic revision, implementation and verification currentness", () => {
  const d = detailOf(acceptTask(), NOW);
  const work = d.sections.find((s) => s.id === "work").markdown;
  assert.match(work, /Implementation: \*\*current\*\*/);
  assert.match(work, /Verification: \*\*passed\*\* \(current\)/);
  assert.match(work, /build: passed/);
  assert.match(d.sections[0].markdown, /Semantic revision \*\*1\*\*/);
  assert.equal(d.stage, "ACCEPT");
  assert.match(d.stageReason, /waiting for your ACCEPT/);
});

test("a reclassification explains why the workflow moved backward, with its evidence", () => {
  const t = task({
    reclassifications: [{ id: "r1", stage: "what", evidence: "the API is async", reason: "the WHAT assumed sync", turn: 2, session: null, priorSemanticOrdinal: 1, semanticOrdinal: 2, provisionalWork: true, workspaceFingerprint: "f", invalidatedGateIds: [GATE, "g2"], isActive: true }],
  });
  const [n] = movedBackNotices(t);
  assert.match(n.title, /back to WHAT/);
  assert.match(n.text, /the WHAT assumed sync/);
  assert.match(n.text, /the API is async/);
  assert.match(n.text, /2 earlier approvals no longer apply/);
  assert.match(n.text, /provisional/);
});

test("an invalidated approval is explained, once, without repeating its reclassification", () => {
  const t = task({
    gates: [{ id: GATE, type: "how", status: "invalidated", openedAt: "x", binding: { semanticRevisionId: SEM, semanticOrdinal: 1 }, isCurrent: false, staleReasons: [], invalidation: { cause: "depends_on_invalidated_what", reason: "its WHAT was invalidated", reclassificationId: null, supersededByGateId: null }, clarification: null, what: null, how: null, decision: null }],
  });
  const notes = movedBackNotices(t);
  assert.equal(notes.length, 1);
  assert.match(notes[0].text, /WHAT was invalidated/);
});

test("REVIEW findings are shown with title, impact and recommendation", () => {
  const t = task({
    review: {
      status: "changes_required", isDue: false, isSatisfied: false, reviewerProfile: "pi-reviewer", requests: [], currentReviewId: "r",
      rounds: [{ id: "r", round: 1, verdict: "changes_required", isCurrent: true, summary: "two problems", findings: [{ title: "Off by one", evidence: "e", impact: "drops last item", recommendation: "use <=", location: "Uploader.cs:42" }], notes: null }],
    },
  });
  const d = detailOf(t, NOW);
  const review = d.sections.find((s) => s.id === "review").markdown;
  assert.match(review, /Off by one/);
  assert.match(review, /Uploader\.cs:42/);
  assert.match(review, /reviewer profile `pi-reviewer`/);
  assert.ok(d.notices.some((n) => n.id === "review_changes"));
});

test("PROVE shows claims, verdicts and evidence; a failed proof is a notice", () => {
  const t = task({
    proof: {
      status: "failed", isDue: false, isContractMissing: false, isSatisfied: false, proverProfile: "claude-code-local", currentProofId: "p",
      contracts: [{ id: "c", contentHash: "h", semanticOrdinal: 1, isCurrent: true, rationale: "r", turn: 1, session: null, claims: [{ id: "c1", statement: "retries 3 times", acceptanceCriterion: "x", evidence: [] }] }],
      rounds: [{ id: "p", round: 1, contractId: "c", contractHash: "h", verdict: "fail", isCurrent: true, summary: "claim c1 not shown", claims: [{ claimId: "c1", verdict: "fail", summary: "only 2 retries", evidence: [{ kind: "command", value: "dotnet test" }] }] }],
    },
  });
  const d = detailOf(t, NOW);
  const prove = d.sections.find((s) => s.id === "prove").markdown;
  assert.match(prove, /retries 3 times/);
  assert.match(prove, /only 2 retries/);
  assert.match(prove, /evidence \(command\): dotnet test/);
  assert.ok(d.notices.some((n) => n.id === "proof_not_passed"));
});

test("a task with no REVIEW or PROVE shows neither section", () => {
  const ids = detailOf(task(), NOW).sections.map((s) => s.id);
  assert.ok(!ids.includes("review") && !ids.includes("prove"));
});

test("a missing role is listed in the roles section with why", () => {
  const t = blockedTask({ missingBindings: [{ role: "reviewer", reason: "review_required", requiredCapabilities: [] }] });
  assert.match(detailOf(t, NOW).sections.find((s) => s.id === "roles").markdown, /Reviewer\*\* has no profile.*independent REVIEW is required/);
});

// ---- controls ---------------------------------------------------------------------------------------------------------------------

test("CLARIFY panel: free-text answer and abort only", () => {
  const p = decisionPanel(clarifyTask());
  assert.deepEqual(p.actions.map((a) => a.id), ["answer", "abort"]);
  assert.equal(p.inputs[0].id, "answer");
  assert.match(p.content[0].text, /Which uploader/);
});

test("WHAT and HOW panels: approve, request changes, abort, with the proposal content", () => {
  const w = decisionPanel(whatTask());
  assert.deepEqual(w.actions.map((a) => a.id), ["approve", "request_changes", "abort"]);
  assert.ok(w.content.some((c) => c.heading === "Required behavior"));
  const h = decisionPanel(howTask());
  assert.ok(h.content.some((c) => c.heading === "Strategy"));
  assert.equal(h.title, "Decide the HOW");
});

test("an action the model does not offer is not shown", () => {
  const t = whatTask();
  t.pendingRequest.allowedActions = ["approve"];
  assert.deepEqual(decisionPanel(t).actions.map((a) => a.id), ["approve"]);
});

test("ACCEPT panel shows what is accepted with currentness, verification, review and proof", () => {
  const t = acceptTask({ review: { status: "passed_current", isDue: false, isSatisfied: true, reviewerProfile: "r", requests: [], rounds: [], currentReviewId: null }, proof: { status: "passed_current", isDue: false, isContractMissing: false, isSatisfied: true, proverProfile: "p", contracts: [], rounds: [], currentProofId: null } });
  const p = decisionPanel(t);
  assert.deepEqual(p.actions.map((a) => a.id), ["accept", "request_changes", "abort"]);
  const text = p.content.map((c) => `${c.heading}: ${c.text}`).join("\n");
  assert.match(text, /Implementation: current/);
  assert.match(text, /Verification: passed \(current\)/);
  assert.match(text, /REVIEW: passed current/);
  assert.match(text, /PROVE: passed current/);
});

test("a stale gate still offers send-back and abort, and says why accept is gone", () => {
  const t = acceptTask();
  t.gates[0].isCurrent = false;
  const p = decisionPanel(t);
  assert.match(p.staleNote, /no longer current/);
  assert.equal(buildFromPanel(t, p, "accept", {}).ok, false);
  assert.equal(buildFromPanel(t, p, "abort", {}).ok, true);
});

test("a native-interaction request is not a workflow panel: no buttons answer it", () => {
  const t = task({ state: "awaiting_human", pendingRequest: { kind: "native_agent_interaction", gateId: null, gateType: null, isCurrent: null, allowedActions: ["abort"] } });
  assert.equal(decisionPanel(t), null);
});

test("panel actions rebuild from the task passed in, so a refreshed model gives the new hash", () => {
  const first = whatTask();
  const panel = decisionPanel(first);
  const refreshed = whatTask();
  refreshed.gates[0].what.contentHash = "hash-what-2";
  assert.equal(buildFromPanel(refreshed, panel, "approve", {}).request.contentHash, "hash-what-2");
});

test("recovery panel lists only legal actions and explains the block", () => {
  const p = recoveryPanel(blockedTask());
  assert.deepEqual(p.actions.map((a) => a.id), ["continue", "abort"]);
  assert.match(p.reason, /ended before it finished/);
  const abortOnly = recoveryPanel(blockedTask({ recovery: { actions: ["abort"], pendingWork: null } }));
  assert.deepEqual(abortOnly.actions.map((a) => a.id), ["abort"]);
  assert.equal(recoveryPanel(task()), null);
  assert.equal(recoveryPanel(blockedTask({ recovery: null })).noActions, true);
});

test("Continue is disabled while a role still needs a profile", () => {
  const p = recoveryPanel(blockedTask({ missingBindings: [{ role: "reviewer", reason: "review_required", requiredCapabilities: [] }] }));
  const c = p.actions.find((a) => a.id === "continue");
  assert.equal(c.disabled, true);
  assert.match(c.reason, /profile/);
});

test("binding cards offer every usable profile, preselect none, and never reuse another role's", () => {
  const t = blockedTask({ missingBindings: [{ role: "reviewer", reason: "review_required", requiredCapabilities: [] }] });
  const cards = bindingCards(t, [
    { id: "claude-code-local", harness: "claude-code", model: null, reasoning: null, usable: true },
    { id: "pi-local", harness: "pi", model: "qwen", reasoning: null, usable: true },
    { id: "broken", harness: "pi", usable: false },
  ]);
  assert.equal(cards.length, 1);
  assert.equal(cards[0].roleLabel, "Reviewer");
  assert.deepEqual(cards[0].options.map((o) => o.id), ["claude-code-local", "pi-local"]);
  assert.equal("selected" in cards[0], false);
  assert.deepEqual(cards[0].build(t, "pi-local").request, { operation: "bind_role", taskId: t.taskId, role: "reviewer", profile: "pi-local" });
});
