// Polling the authoritative read model. Pure and clock-free: the caller supplies time and the fetchers.
//
// The cockpit never keeps its own copy of workflow state. It asks `aw ctl` for the list (and, for the open task, the task) on an interval, and re-renders only when a task's
// `streamVersion` (monotonic) moved or something it shows changed. Commands do not update state optimistically; after a command the view is simply re-read.

/** A cheap fingerprint of a list: ids with their stream versions. Equal fingerprints mean nothing a row shows has changed on the controller. */
export function listFingerprint(tasks) {
  return (tasks ?? []).map((t) => `${t.taskId}:${t.readable ? (t.streamVersion ?? "?") : "unreadable"}`).join("|");
}

export class Poller {
  /**
   * @param {object} o
   * @param {()=>Promise<{ok:boolean, value?:any, error?:any}>} o.fetchList
   * @param {(taskId:string)=>Promise<{ok:boolean, value?:any, error?:any}>} o.fetchTask
   * @param {(change:object)=>void} o.onChange  called with {list?, task?, error?, recovered?}; only when something changed
   */
  constructor({ fetchList, fetchTask, onChange }) {
    this.fetchList = fetchList;
    this.fetchTask = fetchTask;
    this.onChange = onChange;
    this.listPrint = null;
    this.openTaskId = null;
    this.taskVersion = null;
    this.failing = false;
    this.inFlight = false;
  }

  openTask(taskId) {
    this.openTaskId = taskId;
    this.taskVersion = null;
  }

  closeTask() {
    this.openTaskId = null;
    this.taskVersion = null;
  }

  /** Drop what is remembered so the next tick reports everything again (after a command, or when the person asks to refresh). */
  invalidate() {
    this.listPrint = null;
    this.taskVersion = null;
  }

  /** One poll. A tick that arrives while the previous one is still running is skipped, so a slow `aw` never stacks calls. */
  async tick() {
    if (this.inFlight) return;
    this.inFlight = true;
    try {
      const list = await this.fetchList();
      if (!list.ok) {
        this.#fail(list.error);
        return;
      }
      const recovered = this.failing;
      this.failing = false;
      const tasks = list.value?.tasks ?? [];
      const print = listFingerprint(tasks);
      const change = {};
      if (print !== this.listPrint || recovered) {
        this.listPrint = print;
        change.list = tasks;
      }
      if (recovered) change.recovered = true;

      if (this.openTaskId) {
        const row = tasks.find((t) => t.taskId === this.openTaskId);
        const moved = !row || !row.readable || row.streamVersion == null || row.streamVersion !== this.taskVersion;
        if (moved) {
          const read = await this.fetchTask(this.openTaskId);
          if (read.ok) {
            const model = read.value?.task ?? read.value;
            this.taskVersion = model?.streamVersion ?? null;
            change.task = model;
          } else {
            change.error = read.error;
          }
        }
      }
      if (Object.keys(change).length) this.onChange(change);
    } finally {
      this.inFlight = false;
    }
  }

  #fail(error) {
    // Report an outage once, not on every tick; a recovery is reported when it ends.
    if (!this.failing) {
      this.failing = true;
      this.onChange({ error });
    }
  }
}

/** The current-or-failed state of a command the person issued: shown while it runs, replaced by the authoritative state afterwards. */
export class PendingCommand {
  constructor() {
    this.running = null;
  }

  get active() {
    return this.running !== null;
  }

  /** Runs one command at a time (a second press while one is running is ignored), then calls `after` whatever the outcome so the view is re-read. */
  async run(label, work, after) {
    if (this.running) return { ok: false, ignored: true };
    this.running = label;
    try {
      return await work();
    } finally {
      this.running = null;
      await after?.();
    }
  }
}
