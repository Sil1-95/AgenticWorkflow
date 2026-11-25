import test from "node:test";
import assert from "node:assert/strict";
import { initChoices, initDraft, newTaskContext, newTaskRequest, newTaskWarnings, splitArguments } from "../lib/forms.js";
import { PendingCommand, Poller, listFingerprint } from "../lib/poll.js";
import { attachArgv, nativeCard, startupHint } from "../lib/native.js";
import { controllerNotice, newNotifications, signalOf } from "../lib/notify.js";
import { TASK_ID, summary, task, whatTask } from "./fixtures.js";

const inspection = (o = {}) => ({
  workspace: "C:/proj",
  workspaceExists: true,
  configPath: "C:/proj/agenticworkflow.json",
  configState: "ready",
  problems: [],
  project: { toolchain: "dotnet", acceptance: "human", maxTurns: 6, rootProfile: "claude-code-local", reviewerProfile: null, proverProfile: null, checks: [], writable: ["src"], protected: [], allowCommands: [], hasOperatingNotes: false },
  profiles: [
    { id: "claude-code-local", harness: "claude-code", model: null, reasoning: null, builtIn: true, usable: true, problems: [] },
    { id: "pi-local", harness: "pi", model: "qwen", reasoning: "low", builtIn: false, usable: true, problems: [] },
    { id: "pi-bad", harness: "pi", model: null, reasoning: null, builtIn: false, usable: false, problems: ["pi is not installed"] },
  ],
  suggestions: { toolchain: "dotnet", evidence: ["App.sln"], checks: [{ id: "build", executable: "dotnet", arguments: ["build", "App.sln"], timeoutSeconds: 900 }], protected: ["agenticworkflow.json"] },
  prerequisites: [],
  harnesses: ["claude-code", "pi"],
  toolchains: ["dotnet", "flutter", "custom"],
  acceptance: ["none", "human"],
  ...o,
});

// ---- New Task ---------------------------------------------------------------------------------------------------------------------

test("New Task context: profiles are shown profile → harness → model → reasoning with native defaults", () => {
  const ctx = newTaskContext(inspection());
  assert.equal(ctx.ready, true);
  assert.equal(ctx.profiles[0].label, "claude-code-local → Claude Code → native default → native default");
  assert.equal(ctx.profiles[1].label, "pi-local → Pi → qwen → low");
});

test("a project with no config or an invalid one sends the person to initialize, not to a form", () => {
  assert.equal(newTaskContext(inspection({ configState: "missing" })).state, "needs_init");
  const invalid = newTaskContext(inspection({ configState: "invalid", problems: ["bad json"] }));
  assert.equal(invalid.state, "invalid");
  assert.deepEqual(invalid.problems, ["bad json"]);
  assert.equal(newTaskContext(inspection({ workspaceExists: false, problems: ["no such folder"] })).state, "no_workspace");
});

test("New Task refuses a profile that cannot run and never substitutes another", () => {
  const ctx = newTaskContext(inspection());
  const r = newTaskRequest(ctx, { objective: "do", root: "pi-bad", acceptance: "human" });
  assert.equal(r.ok, false);
  assert.match(r.reason, /pi is not installed/);
  assert.equal(newTaskRequest(ctx, { objective: "do", root: "", acceptance: "human" }).ok, false, "no implementer profile, no task: the root profile is not defaulted");
  assert.equal(newTaskRequest(ctx, { objective: "do", root: "ghost" }).ok, false);
});

test("New Task binds exactly the chosen profiles for each role", () => {
  const ctx = newTaskContext(inspection());
  const r = newTaskRequest(ctx, { objective: "do", root: "pi-local", reviewer: "claude-code-local", acceptance: "human", requireReview: true });
  assert.deepEqual(r.request.bindings, { root: "pi-local", reviewer: "claude-code-local" });
  assert.equal(r.request.workspace, "C:/proj");
  assert.equal(r.request.requireReview, true);
  assert.equal(r.request.acceptance, "human_required");
});

test("a required stage with no profile is warned about before sending", () => {
  assert.equal(newTaskWarnings({ requireReview: true }).length, 1);
  assert.equal(newTaskWarnings({ requireReview: true, reviewer: "x", requireProve: true }).length, 1);
  assert.equal(newTaskWarnings({}).length, 0);
});

// ---- Initialize -------------------------------------------------------------------------------------------------------------------

test("init draft offers detected values as suggestions and chooses nothing", () => {
  const d = initDraft(inspection({ configState: "missing", project: null }));
  assert.equal(d.toolchain, "dotnet");
  assert.equal(d.checks[0].executable, "dotnet");
  assert.equal(d.root, "");
  assert.equal(d.acceptance, "");
  assert.deepEqual(d.writable, []);
  assert.deepEqual(d.profiles, []);
});

test("init refuses until the person has decided everything that matters", () => {
  const r = initChoices(inspection(), { toolchain: "", checks: [], writable: [], acceptance: "", profiles: [], root: "" });
  assert.equal(r.ok, false);
  const text = r.problems.join("\n");
  assert.match(text, /toolchain/);
  assert.match(text, /verification check/);
  assert.match(text, /writable/);
  assert.match(text, /ACCEPT/);
  assert.match(text, /implementer runs under/);
});

test("init builds typed choices from the confirmed form and does not invent anything", () => {
  const r = initChoices(inspection(), {
    toolchain: "dotnet",
    checks: [{ id: "build", executable: "dotnet", arguments: splitArguments("build App.sln"), timeoutSeconds: 600 }],
    writable: ["src", "tests"],
    protectedPaths: ["agenticworkflow.json"],
    acceptance: "human",
    profiles: [{ id: "pi-local", harness: "pi", model: "", reasoning: "", description: "" }],
    root: "pi-local",
    reviewer: "claude-code-local",
    prover: "",
  });
  assert.equal(r.ok, true, r.problems?.join());
  assert.deepEqual(r.choices.profiles, [{ id: "pi-local", harness: "pi", model: undefined, reasoning: undefined, description: undefined }]);
  assert.equal(r.choices.profile, "pi-local");
  assert.equal(r.choices.reviewerProfile, "claude-code-local");
  assert.equal(r.choices.proverProfile, undefined);
  assert.deepEqual(r.choices.verification[0].arguments, ["build", "App.sln"]);
  const wire = JSON.parse(JSON.stringify(r.choices));
  assert.equal("model" in wire.profiles[0], false, "no model is sent unless the person chose one");
});

test("a custom toolchain is chosen like any other and brings only the checks the person typed", () => {
  const r = initChoices(inspection(), {
    toolchain: "custom",
    checks: [{ id: "tests", executable: "make", arguments: splitArguments("verify"), timeoutSeconds: 600 }],
    writable: ["Assets"],
    protectedPaths: [],
    acceptance: "human",
    profiles: [],
    root: "claude-code-local",
  });
  assert.equal(r.ok, true, r.problems?.join());
  assert.equal(r.choices.toolchain, "custom");
  assert.deepEqual(r.choices.verification.map((c) => c.id), ["tests"]);
  const unknown = initChoices(inspection(), { toolchain: "unity", checks: [{ id: "a", executable: "x" }], writable: ["src"], acceptance: "none", profiles: [], root: "claude-code-local" });
  assert.match(unknown.problems.join(), /not a supported toolchain/);
});

test("init refuses duplicate check ids, unknown profiles and unsupported harnesses", () => {
  const base = { toolchain: "dotnet", writable: ["src"], acceptance: "none", root: "claude-code-local", profiles: [] };
  const dup = initChoices(inspection(), { ...base, checks: [{ id: "a", executable: "x" }, { id: "A", executable: "y" }] });
  assert.match(dup.problems.join(), /Two checks/);
  const unknown = initChoices(inspection(), { ...base, root: "ghost", checks: [{ id: "a", executable: "x" }] });
  assert.match(unknown.problems.join(), /not defined/);
  const harness = initChoices(inspection(), { ...base, checks: [{ id: "a", executable: "x" }], profiles: [{ id: "p", harness: "codex" }] });
  assert.match(harness.problems.join(), /supported harness/);
});

test("argument splitting has no shell semantics", () => {
  assert.deepEqual(splitArguments('test "a b" $(x) ; y'), ["test", '"a', 'b"', "$(x)", ";", "y"]);
});

// ---- polling ----------------------------------------------------------------------------------------------------------------------

function poller(listSeq, taskSeq = []) {
  const changes = [];
  const calls = { list: 0, task: 0 };
  const p = new Poller({
    fetchList: async () => listSeq[Math.min(calls.list++, listSeq.length - 1)],
    fetchTask: async () => taskSeq[Math.min(calls.task++, taskSeq.length - 1)],
    onChange: (c) => changes.push(c),
  });
  return { p, changes, calls };
}

const listOk = (...rows) => ({ ok: true, value: { tasks: rows } });

test("polling re-renders only when a stream version moved", async () => {
  const { p, changes } = poller([listOk(summary({ streamVersion: 1 })), listOk(summary({ streamVersion: 1 })), listOk(summary({ streamVersion: 2 }))]);
  await p.tick();
  await p.tick();
  await p.tick();
  assert.equal(changes.length, 2);
  assert.equal(changes[1].list[0].streamVersion, 2);
});

test("the open task is re-read only when its stream version moved", async () => {
  const t1 = { ok: true, value: { task: { ...task(), streamVersion: 1 } } };
  const t2 = { ok: true, value: { task: { ...task(), streamVersion: 2 } } };
  const { p, changes, calls } = poller([listOk(summary({ streamVersion: 1 })), listOk(summary({ streamVersion: 1 })), listOk(summary({ streamVersion: 2 }))], [t1, t2]);
  p.openTask(TASK_ID);
  await p.tick();
  await p.tick();
  await p.tick();
  assert.equal(calls.task, 2);
  assert.equal(changes.at(-1).task.streamVersion, 2);
});

test("an outage is reported once, and recovery is reported when it ends", async () => {
  const down = { ok: false, error: { kind: "controller_unavailable", message: "down" } };
  const { p, changes } = poller([down, down, down, listOk(summary())]);
  for (let i = 0; i < 4; i++) await p.tick();
  assert.equal(changes.filter((c) => c.error).length, 1);
  assert.equal(changes.at(-1).recovered, true);
  assert.ok(changes.at(-1).list);
});

test("a slow poll is not stacked", async () => {
  let release;
  const gate = new Promise((r) => (release = r));
  let calls = 0;
  const p = new Poller({ fetchList: async () => (calls++, await gate, listOk()), fetchTask: async () => ({ ok: true, value: {} }), onChange: () => {} });
  const first = p.tick();
  await p.tick();
  assert.equal(calls, 1);
  release();
  await first;
});

test("invalidate makes the next poll report everything again", async () => {
  const { p, changes } = poller([listOk(summary())]);
  await p.tick();
  await p.tick();
  assert.equal(changes.length, 1);
  p.invalidate();
  await p.tick();
  assert.equal(changes.length, 2);
});

test("an unreadable task is fingerprinted as such", () => {
  assert.match(listFingerprint([{ taskId: "x", readable: false }]), /unreadable/);
});

test("a command runs one at a time and the view is re-read afterwards, success or failure", async () => {
  const pending = new PendingCommand();
  const log = [];
  const slow = pending.run("approve", async () => (log.push("work"), { ok: true }), async () => log.push("refresh"));
  assert.equal(pending.active, true);
  const second = await pending.run("approve", async () => log.push("second"), async () => log.push("refresh2"));
  assert.equal(second.ignored, true);
  await slow;
  assert.deepEqual(log, ["work", "refresh"]);
  await assert.rejects(pending.run("x", async () => { throw new Error("boom"); }, async () => log.push("refreshed after failure")));
  assert.equal(log.at(-1), "refreshed after failure");
  assert.equal(pending.active, false);
});

// ---- native session ---------------------------------------------------------------------------------------------------------------

test("native card: Claude Code working, session available to open", () => {
  const t = task();
  const card = nativeCard(t, { available: true, agentState: "working", hostSession: "s", terminal: "t" });
  assert.equal(card.harness, "Claude Code");
  assert.equal(card.status, "working");
  assert.equal(card.canOpen, true);
  assert.equal(card.needsYou, false);
});

test("native card: Pi blocked in its own session is native interaction required", () => {
  const t = task({ bindings: [{ role: "root", profile: { id: "pi-local" }, configuration: { harness: "pi", model: null, reasoning: null } }] });
  const card = nativeCard(t, { available: true, agentState: "blocked", agentMessage: "Permission needed" });
  assert.equal(card.harness, "Pi");
  assert.equal(card.status, "waiting for you");
  assert.equal(card.needsYou, true);
  assert.match(card.needsYouText, /never answers it/);
});

test("native card: the recorded native interaction marks it even before the session is described", () => {
  const t = task({ operational: { ...task().operational, nativeInteraction: { kind: "permission", description: "run a command", hostSession: "s", terminal: "t", observedAt: "x" } } });
  assert.equal(nativeCard(t, null).needsYou, true);
});

test("native card: no live agent gives a plain reason and no open action", () => {
  const t = task({ operational: { ...task().operational, liveness: "not_live" } });
  const card = nativeCard(t, { available: false, reason: "No process is working on this run" });
  assert.equal(card.canOpen, false);
  assert.equal(card.status, "not running");
  assert.match(card.unavailableReason, /No process/);
});

test("Pi startup hint appears only for a quiet Pi session, never for Claude Code", () => {
  const pi = task({ bindings: [{ role: "root", profile: { id: "p" }, configuration: { harness: "pi" } }] });
  const idle = { available: true, agentState: "idle" };
  assert.match(startupHint(pi, idle, 40), /trust this project/);
  assert.equal(startupHint(pi, idle, 5), null);
  assert.equal(startupHint(pi, { available: true, agentState: "working" }, 90), null);
  assert.equal(startupHint(task(), idle, 90), null);
});

test("attach argv validates the task id before it becomes an argument", () => {
  assert.deepEqual(attachArgv("C:/aw.exe", TASK_ID), ["C:/aw.exe", "attach", TASK_ID]);
  assert.throws(() => attachArgv("aw", "x; calc"));
  assert.throws(() => attachArgv("aw", "--help"));
});

// ---- notifications ----------------------------------------------------------------------------------------------------------------

test("each human request, block and completion is announced once", () => {
  const seen = new Map();
  const need = summary({ state: "awaiting_human", pendingRequest: "what_approval", stage: "what" });
  assert.equal(newNotifications(seen, [need]).length, 1);
  assert.equal(newNotifications(seen, [need]).length, 0, "not repeated");
  const blocked = summary({ state: "blocked", block: "runtime_failure" });
  assert.match(newNotifications(seen, [blocked])[0].title, /BLOCKED/);
  assert.match(newNotifications(seen, [summary({ state: "completed" })])[0].title, /completed/);
});

test("routine progress is silent", () => {
  const seen = new Map();
  assert.equal(newNotifications(seen, [summary({ stage: "implement" })]).length, 0);
  assert.equal(newNotifications(seen, [summary({ stage: "verify" })]).length, 0);
});

test("a native interaction request announces itself separately from a gate", () => {
  const n = newNotifications(new Map(), [summary({ state: "awaiting_human", pendingRequest: "native_agent_interaction" })]);
  assert.match(n[0].title, /needs you in its session/);
});

test("a task already completed when the cockpit opens is not announced", () => {
  assert.equal(newNotifications(new Map(), [summary({ state: "completed" })], true).length, 0);
});

test("unreadable tasks give no signal", () => {
  assert.equal(signalOf({ readable: false }), null);
});

test("controller problems are announced on start and on recovery only", () => {
  const err = { kind: "controller_unavailable", message: "could not start" };
  assert.equal(controllerNotice(null, err).level, "attention");
  assert.equal(controllerNotice("controller_unavailable", err), null);
  assert.match(controllerNotice("controller_unavailable", null).title, /reachable again/);
  assert.equal(controllerNotice(null, null), null);
});

test("a stale decision flow: the stale-model approve is refused locally, the refreshed model is approvable", async () => {
  const { approveRequest } = await import("../lib/commands.js");
  const shown = whatTask();
  const moved = whatTask({ streamVersion: 13 });
  moved.gates[0].isCurrent = false;
  assert.equal(approveRequest(moved).ok, false);
  assert.equal(approveRequest(shown).ok, true);
});
