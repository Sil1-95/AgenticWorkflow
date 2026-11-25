// Read-model fixtures shaped like the controller's `task` result (camelCase, snake_case enums). Only what the cockpit reads is filled in.

export const TASK_ID = "6f1c2b3a-1111-4222-8333-444455556666";
export const SEM = "aaaaaaaa-0000-4000-8000-000000000001";
export const IMPL = "bbbbbbbb-0000-4000-8000-000000000002";
export const RUN = "cccccccc-0000-4000-8000-000000000003";
export const GATE = "dddddddd-0000-4000-8000-000000000004";

export const binding = (role, profile, harness, model = null, reasoning = null) => ({
  role,
  profile: { id: profile, description: null, capabilities: [] },
  boundAtSemanticOrdinal: 1,
  configuration: { harness, model, reasoning },
});

export function task(overrides = {}) {
  return {
    schemaVersion: 1,
    taskId: TASK_ID,
    intent: "Add a retry to the uploader",
    createdAt: "2026-01-01T10:00:00Z",
    lastEventAt: "2026-01-01T10:05:00Z",
    streamVersion: 12,
    state: "running",
    stateReason: "working",
    completionBoundary: "accepted",
    stage: "implement",
    stageReason: "initial_implementation",
    requirements: {
      acceptance: "human_required",
      predatesAcceptanceRequirement: false,
      what: { origin: "human", satisfied: true },
      how: { origin: "none", satisfied: false },
      review: { origin: "none", satisfied: false },
      prove: { origin: "none", satisfied: false },
    },
    semanticRevision: { id: SEM, ordinal: 1, kind: "initial", explanation: "", createdAt: "2026-01-01T10:00:00Z", isCurrent: true },
    semanticRevisions: [],
    implementation: { currency: "none", latest: null, hasProvisionalWork: false, revisions: [] },
    verification: { currency: "none", outcome: null, currentRunId: null, runs: [] },
    gates: [],
    pendingRequest: null,
    reclassifications: [],
    review: { status: "none", isDue: false, isSatisfied: false, reviewerProfile: null, requests: [], rounds: [], currentReviewId: null },
    proof: { status: "none", isDue: false, isContractMissing: false, isSatisfied: false, proverProfile: null, contracts: [], rounds: [], currentProofId: null },
    completion: { accept: "not_ready", acceptGateId: null, isEligible: false, blockingReasons: [] },
    block: null,
    recovery: null,
    bindings: [binding("root", "claude-code-local", "claude-code")],
    missingBindings: [],
    operational: { project: { workspace: "C:/Projects/uploader", configPath: "C:/Projects/uploader/agenticworkflow.json", configSha256: "x", runCreatedAt: "2026-01-01T10:00:00Z" }, liveness: "live", nativeInteraction: null, nativeConversation: null, lastProcess: null, workingTree: "unchanged", continuationBaselineRecorded: null, unavailable: [] },
    ...overrides,
  };
}

const gateBase = (type, extra) => ({
  id: GATE,
  type,
  status: "open",
  openedAt: "2026-01-01T10:01:00Z",
  binding: { semanticRevisionId: SEM, semanticOrdinal: 1, implementationRevisionId: null, verificationRunId: null },
  isCurrent: true,
  staleReasons: [],
  invalidation: null,
  clarification: null,
  what: null,
  how: null,
  decision: null,
  ...extra,
});

export const clarifyTask = (o = {}) =>
  task({
    state: "awaiting_human",
    stage: "clarify",
    stageReason: "awaiting_human_answer",
    gates: [gateBase("clarify", { clarification: { question: "Which uploader?", reason: "There are two.", turn: 1 } })],
    pendingRequest: { kind: "clarify_answer", gateId: GATE, gateType: "clarify", isCurrent: true, allowedActions: ["answer", "abort"] },
    ...o,
  });

export const WHAT = { objective: "Retry failed uploads", requiredBehavior: "Retry 3 times", scope: "uploader only", constraints: null, nonGoals: null, acceptanceConditions: "tests pass", rationale: "flaky network", contentHash: "hash-what-1", turn: 1 };
export const HOW = { strategy: "Wrap the call in a retry loop", components: "Uploader", dataAndControlFlow: null, compatibility: null, risksAndTradeoffs: "latency", verificationApproach: null, rationale: "simple", contentHash: "hash-how-1", turn: 1 };

export const whatTask = (o = {}) =>
  task({
    state: "awaiting_human",
    stage: "what",
    stageReason: "awaiting_human_approval",
    gates: [gateBase("what", { what: WHAT })],
    pendingRequest: { kind: "what_approval", gateId: GATE, gateType: "what", isCurrent: true, allowedActions: ["approve", "request_changes", "abort"] },
    ...o,
  });

export const howTask = (o = {}) =>
  task({
    state: "awaiting_human",
    stage: "how",
    stageReason: "awaiting_human_approval",
    gates: [gateBase("how", { how: HOW })],
    pendingRequest: { kind: "how_approval", gateId: GATE, gateType: "how", isCurrent: true, allowedActions: ["approve", "request_changes", "abort"] },
    ...o,
  });

export const acceptTask = (o = {}) =>
  task({
    state: "awaiting_human",
    stage: "accept",
    stageReason: "awaiting_human_acceptance",
    implementation: { currency: "current", latest: { id: IMPL, fingerprint: "ffeeddccbbaa", semanticRevisionId: SEM, semanticOrdinal: 1, parentId: null, createdAt: "2026-01-01T10:03:00Z", isLatest: true, targetsCurrentSemantics: true }, hasProvisionalWork: false, revisions: [] },
    verification: { currency: "current", outcome: "passed", currentRunId: RUN, runs: [{ id: RUN, implementationRevisionId: IMPL, outcome: "passed", timestamp: "2026-01-01T10:04:00Z", isCurrent: true, checks: [{ checkId: "build", outcome: "passed", summary: "ok", exitCode: 0, source: "controller", evidenceReason: null, observation: null }] }] },
    gates: [gateBase("accept", { binding: { semanticRevisionId: SEM, semanticOrdinal: 1, implementationRevisionId: IMPL, verificationRunId: RUN } })],
    pendingRequest: { kind: "accept_decision", gateId: GATE, gateType: "accept", isCurrent: true, allowedActions: ["accept", "request_changes", "abort"] },
    ...o,
  });

export const blockedTask = (o = {}) =>
  task({
    state: "blocked",
    stateReason: "recoverable",
    stage: "implement",
    block: { kind: "recoverable_continuation", reason: "The agent process ended before it finished." },
    recovery: { actions: ["continue", "abort"], pendingWork: { kind: "implement", semanticOrdinal: 1, decisionGateId: null, reclassificationId: null, reviewId: null, proofId: null } },
    ...o,
  });

export const summary = (o = {}) => ({
  taskId: TASK_ID,
  readable: true,
  problem: null,
  intent: "Add a retry to the uploader",
  createdAt: "2026-01-01T10:00:00Z",
  lastEventAt: "2026-01-01T10:05:00Z",
  state: "running",
  stage: "implement",
  stageReason: "initial_implementation",
  pendingRequest: null,
  workspace: "C:/Projects/uploader",
  streamVersion: 12,
  block: null,
  bindings: [{ role: "root", profile: "claude-code-local", harness: "claude-code", model: null, reasoning: null }],
  ...o,
});
