// The two forms: Initialize AgenticWorkflow, and New Task. Pure: they turn what the person chose into a typed request, and refuse what is incomplete.
//
// Detected values are suggestions only. A suggestion becomes part of a request when the person has it in the form and presses the button; nothing is added behind their back,
// and nothing here invents a verification command, a profile, a harness, a model or a reasoning level.

import { startRequest } from "./commands.js";
import { describeProfile } from "./text.js";

// ---- profiles --------------------------------------------------------------------------------------------------------------------

/** One profile as the person reads it everywhere: profile id → harness → model (or native default) → reasoning (or native default). */
export function profileOption(p) {
  return {
    id: p.id,
    label: describeProfile({ profile: p.id, harness: p.harness, model: p.model, reasoning: p.reasoning }),
    harness: p.harness,
    usable: p.usable !== false,
    problems: p.problems ?? [],
    description: p.description ?? null,
  };
}

export const profileOptions = (profiles) => (profiles ?? []).map(profileOption);

// ---- New Task --------------------------------------------------------------------------------------------------------------------

/** Where the New Task form stands, from the project inspection. `ready` means the project has a valid configuration; otherwise the person is sent to Initialize. */
export function newTaskContext(inspection) {
  if (!inspection) return { ready: false, state: "unknown", message: "Choose a project folder.", profiles: [] };
  if (!inspection.workspaceExists) return { ready: false, state: "no_workspace", message: inspection.problems?.[0] ?? "That folder does not exist.", profiles: [] };
  if (inspection.configState === "missing") return { ready: false, state: "needs_init", message: "This project is not set up for AgenticWorkflow yet.", profiles: [] };
  if (inspection.configState === "invalid") return { ready: false, state: "invalid", message: "This project's agenticworkflow.json is not valid.", problems: inspection.problems ?? [], profiles: [] };
  const profiles = profileOptions(inspection.profiles);
  return {
    ready: true,
    state: "ready",
    workspace: inspection.workspace,
    acceptance: inspection.project?.acceptance ?? "human",
    profiles,
    configuredRoles: { root: inspection.project?.rootProfile ?? null, reviewer: inspection.project?.reviewerProfile ?? null, prover: inspection.project?.proverProfile ?? null },
    prerequisitesMissing: (inspection.prerequisites ?? []).filter((p) => !p.met).map((p) => `${p.name}: ${p.problem ?? "not available"}`),
  };
}

/**
 * Checks the New Task form and builds the start request. The form carries what the person selected: `root` is required; `reviewer` and `prover` are optional and only bound when chosen.
 * A profile the person selected must be one the controller can run (`usable`); anything else is refused here with the reason, so the request never relies on a substitution.
 */
export function newTaskRequest(context, form) {
  if (!context?.ready) return { ok: false, reason: context?.message ?? "The project is not ready." };
  const byId = new Map(context.profiles.map((p) => [p.id, p]));
  for (const role of ["root", "reviewer", "prover"]) {
    const id = form[role];
    if (!id) continue;
    const p = byId.get(id);
    if (!p) return { ok: false, reason: `The profile '${id}' is not one of this project's profiles.` };
    if (!p.usable) return { ok: false, reason: `The profile '${id}' cannot be run here: ${p.problems.join("; ") || "unavailable"}.` };
  }
  return startRequest({
    objective: form.objective,
    workspace: context.workspace,
    bindings: { root: form.root, reviewer: form.reviewer, prover: form.prover },
    acceptance: form.acceptance,
    requireWhat: !!form.requireWhat,
    requireHow: !!form.requireHow,
    requireReview: !!form.requireReview,
    requireProve: !!form.requireProve,
  });
}

/** A reviewer or prover requirement without a profile chosen for that role is called out before sending, since the controller would block the task. */
export function newTaskWarnings(form) {
  const warnings = [];
  if (form.requireReview && !form.reviewer) warnings.push("REVIEW is required but no reviewer profile is chosen; the task would stop until you bind one.");
  if (form.requireProve && !form.prover) warnings.push("PROVE is required but no prover profile is chosen; the task would stop until you bind one.");
  return warnings;
}

// ---- Initialize ------------------------------------------------------------------------------------------------------------------

const SAFE_ID = /^[a-z0-9][a-z0-9._-]{0,63}$/i;

/** The starting content of the Initialize form: detected values offered as suggestions the person edits and confirms. Nothing here is a default that is sent unasked. */
export function initDraft(inspection) {
  const s = inspection?.suggestions ?? {};
  return {
    toolchain: s.toolchain ?? "",
    toolchainEvidence: s.evidence ?? [],
    checks: (s.checks ?? []).map((c) => ({ id: c.id, executable: c.executable, arguments: [...(c.arguments ?? [])], timeoutSeconds: c.timeoutSeconds })),
    protectedPaths: [...(s.protected ?? [])],
    writable: [],
    acceptance: "",
    profiles: [],
    root: "",
    reviewer: "",
    prover: "",
  };
}

/** Splits one line of arguments into a list without shell semantics: whitespace-separated, no quoting or expansion. Anything needing more is edited in the file. */
export function splitArguments(text) {
  return String(text ?? "").split(/\s+/).filter(Boolean);
}

export function splitLines(text) {
  return String(text ?? "").split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
}

/**
 * Turns the confirmed Initialize form into the typed choices `aw project init` takes. Everything the person did not explicitly decide is refused as missing instead of defaulted:
 * the toolchain, at least one verification check, the writable scope, the implementer profile and the acceptance requirement.
 */
export function initChoices(inspection, form) {
  const problems = [];
  if (!form.toolchain) problems.push("Choose the toolchain.");
  else if (!(inspection?.toolchains ?? []).includes(form.toolchain)) problems.push(`'${form.toolchain}' is not a supported toolchain.`);

  const checks = (form.checks ?? []).filter((c) => c.id || c.executable);
  if (checks.length === 0) problems.push("Add at least one verification check: a command that proves the work is right. None is invented for you.");
  const seen = new Set();
  for (const c of checks) {
    if (!c.id || !SAFE_ID.test(c.id)) problems.push(`A check needs a short id (letters, digits, '.', '_', '-'); got '${c.id ?? ""}'.`);
    else if (seen.has(c.id.toLowerCase())) problems.push(`Two checks are called '${c.id}'.`);
    else seen.add(c.id.toLowerCase());
    if (!c.executable) problems.push(`Check '${c.id ?? ""}' needs an executable.`);
  }

  const writable = (form.writable ?? []).map((p) => p.trim()).filter(Boolean);
  if (writable.length === 0) problems.push("Say which folders the agent may change (writable scope).");

  if (!form.acceptance) problems.push("Choose whether you must ACCEPT the result (human) or the controller completes it once verified (none).");
  else if (!(inspection?.acceptance ?? []).includes(form.acceptance)) problems.push(`'${form.acceptance}' is not a valid acceptance choice.`);

  const known = new Set(["claude-code-local", ...(form.profiles ?? []).map((p) => p.id)]);
  const profiles = [];
  for (const p of form.profiles ?? []) {
    if (!p.id || !SAFE_ID.test(p.id)) problems.push(`A profile needs a short id; got '${p.id ?? ""}'.`);
    if (!p.harness || !(inspection?.harnesses ?? []).includes(p.harness)) problems.push(`Profile '${p.id ?? ""}' needs a supported harness (${(inspection?.harnesses ?? []).join(", ")}).`);
    profiles.push({ id: p.id, harness: p.harness, model: p.model || undefined, reasoning: p.reasoning || undefined, description: p.description || undefined });
  }
  if (!form.root) problems.push("Choose the profile the implementer runs under.");
  for (const role of ["root", "reviewer", "prover"]) if (form[role] && !known.has(form[role])) problems.push(`The ${role} profile '${form[role]}' is not defined.`);

  if (problems.length) return { ok: false, problems };
  return {
    ok: true,
    choices: {
      toolchain: form.toolchain,
      profiles: profiles.length ? profiles : undefined,
      profile: form.root,
      reviewerProfile: form.reviewer || undefined,
      proverProfile: form.prover || undefined,
      verification: checks.map((c) => ({ id: c.id, executable: c.executable, arguments: c.arguments?.length ? c.arguments : undefined, timeoutSeconds: c.timeoutSeconds || undefined })),
      writable,
      protected: (form.protectedPaths ?? []).length ? form.protectedPaths : undefined,
      acceptance: form.acceptance,
      maxTurns: form.maxTurns || undefined,
    },
  };
}

/** The structured summary of an already-configured project (the "advanced edit" path is the file itself). */
export function projectSummaryLines(inspection) {
  const p = inspection?.project;
  if (!p) return [];
  return [
    `Toolchain: ${p.toolchain}`,
    `Implementer profile: ${p.rootProfile}${p.reviewerProfile ? `; reviewer: ${p.reviewerProfile}` : ""}${p.proverProfile ? `; prover: ${p.proverProfile}` : ""}`,
    `ACCEPT: ${p.acceptance === "human" ? "you decide" : "none (completes once verified)"}; at most ${p.maxTurns} turns`,
    `Checks: ${p.checks.map((c) => `${c.id} (${[c.executable, ...c.arguments].join(" ")})`).join("; ") || "none"}`,
    `Writable: ${p.writable.join(", ") || "none"}`,
    `Protected: ${p.protected.join(", ") || "none"}`,
  ];
}
