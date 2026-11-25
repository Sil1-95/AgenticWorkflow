import test from "node:test";
import assert from "node:assert/strict";
import { abortRequest, acceptRequest, answerRequest, approveRequest, bindRequest, continueRequest, requestChangesRequest, startRequest } from "../lib/commands.js";
import { GATE, IMPL, RUN, SEM, TASK_ID, acceptTask, blockedTask, clarifyTask, howTask, task, whatTask } from "./fixtures.js";

test("approve WHAT carries the exact gate, revision and content hash from the model", () => {
  const r = approveRequest(whatTask());
  assert.equal(r.ok, true);
  assert.deepEqual(r.request, { operation: "approve", proposal: "what", taskId: TASK_ID, gateId: GATE, semanticRevisionId: SEM, contentHash: "hash-what-1", presence: "attended" });
});

test("approve HOW names the HOW hash", () => {
  const r = approveRequest(howTask());
  assert.equal(r.request.proposal, "how");
  assert.equal(r.request.contentHash, "hash-how-1");
});

test("approval is refused when nothing is pending, when stale, or when not offered", () => {
  assert.equal(approveRequest(task()).ok, false);
  const stale = whatTask();
  stale.gates[0].isCurrent = false;
  assert.match(approveRequest(stale).reason, /no longer current/);
  const notOffered = whatTask();
  notOffered.pendingRequest.allowedActions = ["abort"];
  assert.match(approveRequest(notOffered).reason, /not legal/);
  assert.equal(approveRequest(acceptTask()).ok, false, "an ACCEPT gate is never approved as a proposal");
});

test("approval is refused when the proposal has no content hash", () => {
  const t = whatTask();
  t.gates[0].what.contentHash = "";
  assert.equal(approveRequest(t).ok, false);
});

test("request changes needs feedback and carries it as data, never as an approval", () => {
  assert.equal(requestChangesRequest(whatTask(), "  ").ok, false);
  const r = requestChangesRequest(whatTask(), "narrow the scope");
  assert.equal(r.request.operation, "request_changes");
  assert.equal(r.request.gate, "what");
  assert.equal(r.request.feedback, "narrow the scope");
  assert.equal("contentHash" in r.request, false);
});

test("request changes at ACCEPT names the implementation and verification shown", () => {
  const r = requestChangesRequest(acceptTask(), "needs docs");
  assert.equal(r.request.gate, "accept");
  assert.equal(r.request.implementationRevisionId, IMPL);
  assert.equal(r.request.verificationRunId, RUN);
});

test("free text that looks like approval is just feedback", () => {
  const r = requestChangesRequest(whatTask(), "approve");
  assert.equal(r.request.operation, "request_changes");
  const a = answerRequest(clarifyTask(), "yes, approve it");
  assert.equal(a.request.operation, "answer_clarification");
});

test("accept binds to the shown implementation and verification and refuses a changed working tree", () => {
  const r = acceptRequest(acceptTask());
  assert.deepEqual(r.request, { operation: "accept", taskId: TASK_ID, gateId: GATE, semanticRevisionId: SEM, implementationRevisionId: IMPL, verificationRunId: RUN });
  const changed = acceptTask();
  changed.operational.workingTree = "changed";
  assert.match(acceptRequest(changed).reason, /working tree changed/);
  const stale = acceptTask();
  stale.gates[0].isCurrent = false;
  assert.equal(acceptRequest(stale).ok, false);
});

test("answer requires text and an open clarify gate", () => {
  assert.equal(answerRequest(clarifyTask(), "").ok, false);
  assert.equal(answerRequest(whatTask(), "x").ok, false);
  const r = answerRequest(clarifyTask(), "The S3 uploader");
  assert.equal(r.request.gateId, GATE);
  assert.equal(r.request.answer, "The S3 uploader");
});

test("abort names the open gate, or is legal for a blocked task, otherwise refused", () => {
  assert.deepEqual(abortRequest(whatTask()).request, { operation: "abort", taskId: TASK_ID, gateId: GATE });
  assert.deepEqual(abortRequest(blockedTask()).request, { operation: "abort", taskId: TASK_ID });
  assert.equal(abortRequest(task()).ok, false);
});

test("continue is only for a recoverable blocked task with no missing profile", () => {
  assert.equal(continueRequest(blockedTask()).request.operation, "continue_task");
  assert.equal(continueRequest(task()).ok, false);
  const missing = blockedTask({ missingBindings: [{ role: "reviewer", reason: "review_required", requiredCapabilities: [] }] });
  assert.match(continueRequest(missing).reason, /profile/);
});

test("bind only for a role the model lists as missing", () => {
  const t = blockedTask({ missingBindings: [{ role: "reviewer", reason: "review_required", requiredCapabilities: [] }] });
  assert.deepEqual(bindRequest(t, "reviewer", "pi-reviewer").request, { operation: "bind_role", taskId: TASK_ID, role: "reviewer", profile: "pi-reviewer" });
  assert.equal(bindRequest(t, "prover", "x").ok, false);
  assert.equal(bindRequest(t, "reviewer", "").ok, false);
});

test("start builds a typed request with only the chosen bindings and explicit acceptance", () => {
  const r = startRequest({ objective: "do it", workspace: "C:/p", bindings: { root: "claude-code-local", prover: "pi-prover" }, acceptance: "human", requireReview: true });
  assert.deepEqual(r.request, { operation: "start_task", objective: "do it", workingDirectory: "C:/p", workspace: "C:/p", bindings: { root: "claude-code-local", prover: "pi-prover" }, presence: "attended", acceptance: "human_required", requireReview: true });
  assert.equal(startRequest({ objective: "x", workspace: "C:/p", bindings: {} }).ok, false, "the implementer profile is never defaulted");
  assert.equal(startRequest({ objective: "x", workspace: "relative/path", bindings: { root: "a" } }).ok, false);
  assert.equal(startRequest({ objective: " ", workspace: "C:/p", bindings: { root: "a" } }).ok, false);
});

test("task text is carried as data, untouched, even when it looks like a shell command", () => {
  const nasty = "\"; rm -rf / #  $(whoami) `id` & calc";
  const r = startRequest({ objective: nasty, workspace: "C:/p", bindings: { root: "a" } });
  assert.equal(r.request.objective, nasty);
  assert.equal(requestChangesRequest(whatTask(), nasty).request.feedback, nasty);
});
