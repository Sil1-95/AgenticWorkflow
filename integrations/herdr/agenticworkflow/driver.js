// DEV/ACCEPTANCE ONLY. Off unless install.mjs was run with --driver (which sets "driver": true in the cockpit's settings file). Never part of a normal install.
//
// Pointer and key automation on a real display is off limits, so a live acceptance run needs another way to press the cockpit's own buttons. This is that seam, and it is
// deliberately narrow: it can only (a) invoke the handler of an element that is on screen right now, by its element id, exactly as a click would, (b) put text into an input that
// is on screen, by its label, exactly as typing would, and (c) report what the cockpit shows. It has no access the buttons do not have: a decision still goes through the typed
// request built from the read model, so a stale or illegal one is refused exactly as when clicked, and nothing here can approve anything the buttons would not offer.
//
// Commands are numbered files: <herdr home>/agenticworkflow/driver/cmd-0001.json, cmd-0002.json, ... Each is answered by out-<same number>.json.

import { registry } from "./ui.js";

const pad = (n) => String(n).padStart(4, "0");

function snapshot(view) {
  return {
    screen: view.screen,
    loaded: view.loaded,
    controllerError: view.controllerError ? { kind: view.controllerError.kind, message: view.controllerError.message } : null,
    flash: view.flash,
    rows: view.rows.map((r) => ({ id: r.id, intent: r.intent, status: r.status, stage: r.stage, pending: r.pending, tone: r.tone, agents: r.agents })),
    taskId: view.taskId,
    task: view.task ? { state: view.task.state, stage: view.task.stage, stageReason: view.task.stageReason, pending: view.task.pendingRequest?.kind ?? null, streamVersion: view.task.streamVersion, missing: (view.task.missingBindings ?? []).map((m) => m.role), block: view.task.block?.kind ?? null, recovery: view.task.recovery?.actions ?? null } : null,
    described: view.described,
    pressable: [...registry.press.keys()],
    inputs: [...registry.inputs.keys()],
    pendingCommand: view.pending.running,
  };
}

async function execute(view, cx, command) {
  switch (command.cmd) {
    case "snapshot":
      return {};
    case "press": {
      const handler = registry.press.get(command.id);
      if (!handler) return { error: `nothing on screen has the id '${command.id}'` };
      handler();
      return {};
    }
    case "type": {
      const input = registry.inputs.get(command.label);
      if (!input) return { error: `no input labelled '${command.label}' is on screen` };
      input.set_value(String(command.text ?? ""));
      return {};
    }
    case "wait":
      await new Promise((resolve) => cx.timer.after(Math.min(Number(command.ms) || 0, 60000), () => resolve()));
      return {};
    case "refresh":
      await view.refreshNow();
      return {};
    default:
      return { error: `unknown driver command '${command.cmd}'` };
  }
}

export function startDriver(view, cx, { home, readTextFile, writeTextFile }) {
  let next = 1;
  let busy = false;
  const dir = `${home}/agenticworkflow/driver`;
  cx.timer.every(700, (cx) => {
    if (busy) return;
    busy = true;
    cx.spawn(async (cx) => {
      try {
        // A command that already has its answer belongs to an earlier cockpit tab: skip it rather than answer it twice.
        while ((await readTextFile(`${dir}/out-${pad(next)}.json`)) != null) next += 1;

        const text = await readTextFile(`${dir}/cmd-${pad(next)}.json`);
        if (text == null) return;
        let result;
        try {
          result = await execute(view, cx, JSON.parse(text));
        } catch (e) {
          result = { error: String(e?.message ?? e) };
        }
        cx.notify();
        // Let the render the command caused settle before the snapshot is taken.
        await new Promise((resolve) => cx.timer.after(400, () => resolve()));
        await writeTextFile(`${dir}/out-${pad(next)}.json`, JSON.stringify({ ...result, state: snapshot(view) }), null);
        next += 1;
      } finally {
        busy = false;
      }
    });
  });
}
