// The one way the cockpit reaches AgenticWorkflow: short-lived `aw` commands with an argument list (never a shell string), whose answers are one JSON document.
//
// Everything Herdr-specific (`exec`, writing a file) is injected, so this module is pure and testable. Free text the person typed (an objective, an answer, feedback) is never
// placed on a command line: a typed request is written to a private file and handed over with --request-file, which `aw` reads once and deletes.

export const EXIT = { usage: 2, notFound: 3, unreadable: 4, refused: 18, noController: 31, incompatible: 32 };

/** Error kinds the UI distinguishes. `stale` ones mean the person decided on something that has moved: the view must refresh. */
export const KIND = {
  exeNotFound: "exe_not_found",
  timeout: "timeout",
  controllerUnavailable: "controller_unavailable",
  controllerNotDurable: "controller_not_durable",
  incompatible: "incompatible_protocol",
  stale: "stale",
  refused: "refused",
  notFound: "not_found",
  unreadable: "state_unreadable",
  usage: "usage",
  badReply: "bad_reply",
  internal: "internal",
};

const STALE_CODES = new Set(["stale_gate", "stale_binding", "already_decided", "no_such_gate", "state_moved", "wrong_gate_kind", "illegal_state", "workspace_changed"]);
const REFUSED_CODES = new Set(["run_owned", "precondition_failed", "already_bound"]);
const USAGE_CODES = new Set(["malformed_request", "unsupported_operation", "invalid_argument", "invalid_task_id", "invalid_gate_id", "missing_binding", "unknown_profile", "unsupported_harness", "unsupported_configuration"]);

const tryParse = (text) => {
  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
};

/** Maps a controller error (code + message) to a kind the UI understands. */
export function classifyError(code, message) {
  let kind = KIND.internal;
  if (code === "controller_not_durable") kind = KIND.controllerNotDurable;
  else if (code === "controller_unavailable" || code === "controller_unresponsive" || code === "shutting_down") kind = KIND.controllerUnavailable;
  else if (code === "incompatible_protocol") kind = KIND.incompatible;
  else if (STALE_CODES.has(code)) kind = KIND.stale;
  else if (REFUSED_CODES.has(code)) kind = KIND.refused;
  else if (code === "not_found") kind = KIND.notFound;
  else if (code === "state_unreadable") kind = KIND.unreadable;
  else if (USAGE_CODES.has(code)) kind = KIND.usage;
  return { kind, code: code ?? "unknown", message: message ?? "" };
}

/** A failed `exec` (the program could not be started or took too long) as an error. */
export function classifyExecFailure(error, awPath) {
  const text = String(error?.message ?? error ?? "");
  if (/took longer/i.test(text)) return { kind: KIND.timeout, code: "timeout", message: "aw did not answer in time." };
  return { kind: KIND.exeNotFound, code: "exe_not_found", message: `The aw program could not be run (${awPath}): ${text.replace(/^.*?herdr\.exec`:\s*/i, "")}` };
}

/** What an `aw` command returned, as `{ok:true, value}` or `{ok:false, error}`. Anything unexpected fails closed. */
export function interpret(result) {
  const body = tryParse(String(result?.stdout ?? "").trim());
  const code = result?.code;
  if (body && typeof body === "object" && body.error && typeof body.error === "object") {
    const classified = classifyError(body.error.code, body.error.message);
    if (code === EXIT.noController && classified.kind === KIND.internal) return { ok: false, error: { ...classified, kind: KIND.controllerUnavailable } };
    if (code === EXIT.incompatible && classified.kind === KIND.internal) return { ok: false, error: { ...classified, kind: KIND.incompatible } };
    return { ok: false, error: classified };
  }
  if (code === 0 && body !== undefined) return { ok: true, value: body };
  if (code === 0) return { ok: false, error: { kind: KIND.badReply, code: "bad_reply", message: "aw answered with something that is not a JSON document." } };
  const stderr = String(result?.stderr ?? "").trim();
  if (code === EXIT.noController) return { ok: false, error: { kind: KIND.controllerUnavailable, code: "controller_unavailable", message: stderr } };
  if (code === EXIT.incompatible) return { ok: false, error: { kind: KIND.incompatible, code: "incompatible_protocol", message: stderr } };
  if (code === EXIT.usage) return { ok: false, error: { kind: KIND.usage, code: "usage", message: stderr || "aw refused the request." } };
  return { ok: false, error: { kind: KIND.internal, code: `exit_${code}`, message: stderr || `aw exited with code ${code}.` } };
}

/** Plain-language text for an error, with what the person can do about it. */
export function explain(error) {
  switch (error?.kind) {
    case KIND.exeNotFound:
      return `${error.message}\nInstall AgenticWorkflow (run install.ps1 from the release package), or run it again if it was moved or removed. From a source checkout: node integrations/herdr/agenticworkflow/install.mjs --aw <path to aw>`;
    case KIND.timeout:
      return "aw did not answer in time. Nothing was lost; try again.";
    case KIND.controllerUnavailable:
      return `The AgenticWorkflow controller could not be started or reached.${error.message ? `\n${error.message}` : ""}\nThe cockpit never falls back to running work itself. See <state root>/controller/controller.log.`;
    case KIND.controllerNotDurable:
      return "The controller was started in a way that would end it together with its parent, so it refuses to own agent work. Stop it (aw ctl stop --force) and let the cockpit start a fresh one.";
    case KIND.incompatible:
      return `The installed aw and the running controller speak different protocol versions.\n${error.message}\nStop the controller (aw ctl stop) and try again.`;
    case KIND.stale:
      return `That decision no longer applies: the task moved on. ${error.message}\nThe view was refreshed; look at it again before deciding.`;
    case KIND.refused:
      return `Refused: ${error.message}`;
    case KIND.notFound:
      return "That task no longer exists.";
    case KIND.unreadable:
      return `The task's durable state cannot be read. Nothing was repaired or guessed. ${error.message}`;
    case KIND.usage:
      return error.message || "aw refused the request as malformed.";
    case KIND.badReply:
      return error.message;
    default:
      return error?.message || "Something unexpected went wrong.";
  }
}

export class AwClient {
  /**
   * @param {object} options
   * @param {(program:string, args:string[], options?:object)=>Promise<{code:number,stdout:string,stderr:string}>} options.exec  herdr's exec
   * @param {(path:string, text:string)=>Promise<unknown>} options.writeFile  writes a new private file (creating its folder)
   * @param {(path:string)=>Promise<unknown>} [options.discardFile]  empties a request file that was never handed over
   * @param {string} options.awPath  the aw program ("aw" resolves on PATH)
   * @param {string} options.requestDir  an absolute folder for request files
   * @param {()=>string} options.newId  a unique token for a request file
   */
  constructor({ exec, writeFile, discardFile, awPath, requestDir, newId }) {
    this.exec = exec;
    this.writeFile = writeFile;
    this.discardFile = discardFile ?? (async () => {});
    this.awPath = awPath;
    this.requestDir = requestDir;
    this.newId = newId;
  }

  async #run(args, timeout) {
    let result;
    try {
      result = await this.exec(this.awPath, args, { timeout });
    } catch (error) {
      return { ok: false, error: classifyExecFailure(error, this.awPath) };
    }
    return interpret(result);
  }

  health() {
    return this.#run(["ctl", "health"], 30000);
  }

  listTasks(limit = 50) {
    return this.#run(["ctl", "tasks", "--limit", String(limit)], 30000);
  }

  readTask(taskId) {
    return this.#run(["ctl", "task", taskId], 30000);
  }

  /** A workflow request, handed over through a private file. The reply's `task` is the model as it stands once the decision is recorded. */
  async send(request) {
    const path = `${this.requestDir}/req-${this.newId()}.json`;
    try {
      await this.writeFile(path, JSON.stringify(request));
    } catch (error) {
      return { ok: false, error: { kind: KIND.internal, code: "request_file", message: `The request could not be prepared: ${String(error?.message ?? error)}` } };
    }
    let result;
    try {
      result = await this.exec(this.awPath, ["ctl", "send", "--request-file", path], { timeout: 120000 });
    } catch (error) {
      await this.discardFile(path).catch(() => {});
      return { ok: false, error: classifyExecFailure(error, this.awPath) };
    }
    return interpret(result);
  }

  inspectProject(workspace) {
    return this.#run(["project", "inspect", "--workspace", workspace], 120000);
  }

  /** Creates the project's configuration from the person's confirmed choices. */
  async initProject(workspace, choices) {
    const path = `${this.requestDir}/init-${this.newId()}.json`;
    try {
      await this.writeFile(path, JSON.stringify(choices));
    } catch (error) {
      return { ok: false, error: { kind: KIND.internal, code: "request_file", message: `The request could not be prepared: ${String(error?.message ?? error)}` } };
    }
    let result;
    try {
      result = await this.exec(this.awPath, ["project", "init", "--workspace", workspace, "--request-file", path], { timeout: 60000 });
    } catch (error) {
      await this.discardFile(path).catch(() => {});
      return { ok: false, error: classifyExecFailure(error, this.awPath) };
    }
    // `project init` answers {ok:false, problems:[...]} on refusal (exit 2): those are the person's to fix, not a failure of the program.
    const body = tryParse(String(result?.stdout ?? "").trim());
    if (body && typeof body === "object" && "ok" in body) return body.ok ? { ok: true, value: body } : { ok: false, error: { kind: KIND.usage, code: "init_refused", message: (body.problems ?? []).join("\n") } };
    return interpret(result);
  }

  describeNativeSession(taskId) {
    return this.#run(["attach", taskId, "--describe"], 30000);
  }
}
