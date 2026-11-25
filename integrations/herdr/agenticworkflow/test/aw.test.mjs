import test from "node:test";
import assert from "node:assert/strict";
import { AwClient, EXIT, KIND, classifyError, explain, interpret } from "../lib/aw.js";

const reply = (body, code = 0) => ({ code, stdout: JSON.stringify(body) + "\n", stderr: "" });

function client(execImpl) {
  const calls = [];
  const files = new Map();
  let n = 0;
  const c = new AwClient({
    exec: async (program, args, options) => {
      calls.push({ program, args, options });
      return execImpl(program, args, options, files);
    },
    writeFile: async (path, text) => {
      if (files.has(path)) throw new Error("exists");
      files.set(path, text);
    },
    discardFile: async (path) => files.delete(path),
    awPath: "C:/aw/aw.exe",
    requestDir: "C:/state/requests",
    newId: () => `id${++n}`,
  });
  return { c, calls, files };
}

test("read commands are argv lists with no shell string", async () => {
  const { c, calls } = client(() => reply({ kind: "task_list", tasks: [] }));
  await c.listTasks(20);
  await c.readTask("6f1c2b3a-1111-4222-8333-444455556666");
  await c.health();
  assert.deepEqual(calls.map((x) => x.args), [["ctl", "tasks", "--limit", "20"], ["ctl", "task", "6f1c2b3a-1111-4222-8333-444455556666"], ["ctl", "health"]]);
  assert.ok(calls.every((x) => x.program === "C:/aw/aw.exe" && Array.isArray(x.args)));
});

test("user text never appears on the command line: send uses a request file", async () => {
  const objective = "\"; calc.exe & echo pwned";
  const { c, calls, files } = client(() => reply({ kind: "accepted", operation: "start_task", workScheduled: true, task: {} }));
  const res = await c.send({ operation: "start_task", objective });
  assert.equal(res.ok, true);
  assert.equal(calls.length, 1);
  assert.deepEqual(calls[0].args.slice(0, 3), ["ctl", "send", "--request-file"]);
  assert.equal(calls[0].args.length, 4);
  assert.ok(!calls[0].args.join(" ").includes("pwned"));
  assert.match(calls[0].args[3], /^C:\/state\/requests\/req-id1\.json$/);
  assert.ok(files.get(calls[0].args[3]).includes("pwned"));
});

test("each request file name is unique", async () => {
  const { c, calls } = client(() => reply({ kind: "accepted" }));
  await c.send({ operation: "abort" });
  await c.send({ operation: "abort" });
  assert.notEqual(calls[0].args[3], calls[1].args[3]);
});

test("a missing aw is reported as exe_not_found with how to fix it", async () => {
  const { c } = client(() => {
    throw new Error("herdr.exec`: no such file");
  });
  const r = await c.health();
  assert.equal(r.error.kind, KIND.exeNotFound);
  assert.match(explain(r.error), /install\.mjs/);
});

test("a timeout is not mistaken for a missing exe", async () => {
  const { c } = client(() => {
    throw new Error("command took longer than 30s");
  });
  assert.equal((await c.health()).error.kind, KIND.timeout);
});

test("controller errors map to understandable kinds", () => {
  assert.equal(classifyError("controller_not_durable", "x").kind, KIND.controllerNotDurable);
  assert.equal(classifyError("incompatible_protocol", "x").kind, KIND.incompatible);
  assert.equal(classifyError("controller_unavailable", "x").kind, KIND.controllerUnavailable);
  for (const code of ["stale_gate", "stale_binding", "already_decided", "no_such_gate", "state_moved", "illegal_state"]) assert.equal(classifyError(code, "x").kind, KIND.stale, code);
  assert.equal(classifyError("run_owned", "x").kind, KIND.refused);
  assert.equal(classifyError("something_new", "x").kind, KIND.internal);
});

test("exit codes with an empty body still classify controller start and protocol failures", () => {
  assert.equal(interpret({ code: EXIT.noController, stdout: "", stderr: "could not start" }).error.kind, KIND.controllerUnavailable);
  assert.equal(interpret({ code: EXIT.incompatible, stdout: "", stderr: "protocol 2 vs 1" }).error.kind, KIND.incompatible);
});

test("unexpected output fails closed", () => {
  assert.equal(interpret({ code: 0, stdout: "garbage", stderr: "" }).error.kind, KIND.badReply);
  assert.equal(interpret({ code: 1, stdout: "", stderr: "boom" }).ok, false);
});

test("an error body wins over a zero exit code", () => {
  const r = interpret(reply({ error: { code: "stale_gate", message: "moved" } }, 18));
  assert.equal(r.ok, false);
  assert.equal(r.error.kind, KIND.stale);
});

test("a request file that could not be handed over is discarded", async () => {
  const { c, files } = client(() => {
    throw new Error("herdr.exec`: not found");
  });
  await c.send({ operation: "abort" });
  assert.equal(files.size, 0);
});

test("project init refusals surface as problems to fix, not as a crash", async () => {
  const { c, calls } = client(() => ({ code: 2, stdout: JSON.stringify({ ok: false, configPath: null, problems: ["Choose the toolchain."] }), stderr: "" }));
  const r = await c.initProject("C:/p", { profile: "x" });
  assert.equal(r.ok, false);
  assert.match(r.error.message, /toolchain/);
  assert.deepEqual(calls[0].args.slice(0, 4), ["project", "init", "--workspace", "C:/p"]);
});

test("describing a native session passes the task id as one argument", async () => {
  const { c, calls } = client(() => reply({ available: false, reason: "none" }));
  await c.describeNativeSession("6f1c2b3a-1111-4222-8333-444455556666");
  assert.deepEqual(calls[0].args, ["attach", "6f1c2b3a-1111-4222-8333-444455556666", "--describe"]);
});

test("each explain() text is non-empty and never says 'undefined'", () => {
  for (const kind of Object.values(KIND)) {
    const text = explain({ kind, message: "detail" });
    assert.ok(text.length > 0 && !text.includes("undefined"), kind);
  }
});
