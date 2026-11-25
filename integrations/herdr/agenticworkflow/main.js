// AgenticWorkflow cockpit: a Herdr app mod.
//
// This file is the only one that touches Herdr. It owns screens, inputs and timers; every decision about WHAT to show or send lives in lib/ (pure, unit-tested). The cockpit
// reaches AgenticWorkflow in exactly one way: short-lived `aw` commands (argument lists, never a shell string) that talk to the controller over its local IPC. It never reads
// SQLite or run files, never reproduces projection logic, never answers an agent's own permission prompt, and never approves anything on the person's behalf.

import { View, div } from "gpui-kit";
import { v_flex, h_flex, InputState, TextareaState } from "gpui-base";
import { exec, writeTextFile, readTextFile, appHome, run, notify, props, closeOverlay, spaces, theme, nextCommand } from "herdr";
import { randomUUID } from "crypto";

import { AwClient, explain, KIND } from "./lib/aw.js";
import { rowOf, sortRows, detailOf, pendingLabel } from "./lib/model.js";
import { decisionPanel, buildFromPanel, actionAvailability, recoveryPanel, bindingCards } from "./lib/controls.js";
import { newTaskContext, newTaskRequest, newTaskWarnings, initDraft, initChoices, projectSummaryLines, splitArguments, splitLines } from "./lib/forms.js";
import { nativeCard, startupHint, attachArgv } from "./lib/native.js";
import { Poller, PendingCommand } from "./lib/poll.js";
import { newNotifications, controllerNotice } from "./lib/notify.js";
import { stageLabel, folderName, oneLine, shortId } from "./lib/text.js";
import { button, chip, heading, para, markdown, card, field, choices, toggle, notice, toneColor, press, resetRegistry } from "./ui.js";
import { COCKPIT_VERSION } from "./lib/version.js";
import { identityNotice, controllerVersionOf } from "./lib/identity.js";

const POLL_MS = 3000;
const NATIVE_MS = 6000;

export default class Cockpit extends View {
  init(_props, cx) {
    this.cx = cx;
    this.screen = "list";
    this.rows = [];
    this.rawList = [];
    this.loaded = false;
    this.controllerError = null;
    this.controllerKind = null;
    this.taskId = null;
    this.task = null;
    this.described = null;
    this.nativeSince = 0;
    this.flash = null;
    this.seen = new Map();
    this.firstList = true;
    this.pending = new PendingCommand();
    this.newForm = null;
    this.initForm = null;
    this.inspection = null;
    this.profileCache = [];
    this.gateInputKey = null;
    this.answerInput = TextareaState.new({ placeholder: "Your answer", rows: 4 });
    this.feedbackInput = TextareaState.new({ placeholder: "What should change?", rows: 4 });
    this.awPath = "aw";
    this.client = null;
    this.identity = null;

    cx.spawn(async (cx) => {
      await this.configure();
      await this.poll(cx);
      cx.notify();
    });
    cx.timer.every(POLL_MS, (cx) => {
      cx.spawn(async (cx) => {
        await this.poll(cx);
        cx.notify();
      });
    });
    cx.spawn(async (cx) => {
      for (;;) {
        const command = await nextCommand();
        if (command === "refresh") await this.refreshNow();
        if (command === "new-task") this.openNew(cx);
        cx.notify();
      }
    });
  }

  // ---- wiring --------------------------------------------------------------------------------------------------------------------

  async configure() {
    const home = appHome();
    let awPath = "aw";
    let config = null;
    try {
      const text = await readTextFile(`${home}/agenticworkflow/config.json`);
      config = text ? JSON.parse(text) : null;
      if (config && typeof config.aw === "string" && config.aw.trim()) awPath = config.aw.trim();
    } catch {
      // No (or unreadable) install config: `aw` is looked up on PATH.
    }
    this.awPath = awPath;
    this.client = new AwClient({
      exec,
      writeFile: (path, text) => writeTextFile(path, text, null),
      awPath,
      requestDir: `${home}/agenticworkflow/requests`,
      newId: () => randomUUID(),
    });
    this.poller = new Poller({
      fetchList: () => this.client.listTasks(100),
      fetchTask: (id) => this.client.readTask(id),
      onChange: (change) => this.applyChange(change),
    });
    if (this.taskId) this.poller.openTask(this.taskId);
    // The acceptance driver is a development file that a release does not contain; it is loaded only when the install settings ask for it.
    if (config?.driver === true) {
      try {
        const { startDriver } = await import("./driver.js");
        startDriver(this, this.cx, { home, readTextFile, writeTextFile });
      } catch {
        // No driver in this copy: nothing to start.
      }
    }
    this.checkIdentity();
  }

  /** Compares the version this cockpit was installed with to the one the controller reports, once, and remembers a notice if they differ. */
  async checkIdentity() {
    if (!COCKPIT_VERSION || !this.client) return;
    const res = await this.client.health();
    this.identity = res.ok ? identityNotice(COCKPIT_VERSION, controllerVersionOf(res.value)) : null;
    this.cx.notify();
  }

  async poll(cx) {
    if (!this.poller) return;
    await this.poller.tick();
    if (this.screen === "task" && this.task && Date.now() - this.nativeSince > NATIVE_MS) await this.refreshNative();
  }

  async refreshNow() {
    if (!this.poller) return;
    this.poller.invalidate();
    await this.poller.tick();
    if (this.screen === "task") await this.refreshNative();
  }

  async refreshNative() {
    this.nativeSince = Date.now();
    if (!this.taskId) return;
    const live = this.task?.operational?.liveness === "live";
    if (!live) {
      this.described = null;
      return;
    }
    const res = await this.client.describeNativeSession(this.taskId);
    this.described = res.ok ? res.value : { available: false, reason: explain(res.error) };
  }

  applyChange(change) {
    if (change.error) {
      this.controllerError = change.error;
      this.notifyController(change.error);
    }
    if (change.recovered) {
      this.controllerError = null;
      this.notifyController(null);
    }
    if (change.list) {
      this.loaded = true;
      this.controllerError = null;
      this.rawList = change.list;
      this.rows = sortRows(change.list.filter((t) => t.readable !== undefined).map((t) => rowOf(t)));
      for (const n of newNotifications(this.seen, change.list, this.firstList)) notify({ title: n.title, body: n.body, level: n.level });
      this.firstList = false;
    }
    if (change.task) {
      this.task = change.task;
      this.syncGateInputs();
    }
  }

  notifyController(error) {
    const n = controllerNotice(this.controllerKind, error);
    if (!n) return;
    this.controllerKind = n.kind;
    notify({ title: n.title, body: n.body, level: n.level });
  }

  /** The answer/feedback boxes belong to one gate; a different gate (or none) starts them empty so text never carries over to another decision. */
  syncGateInputs() {
    const key = this.task?.pendingRequest?.gateId ?? null;
    if (key === this.gateInputKey) return;
    this.gateInputKey = key;
    this.answerInput.set_value("");
    this.feedbackInput.set_value("");
  }

  say(tone, text) {
    this.flash = { tone, text };
  }

  // ---- navigation ----------------------------------------------------------------------------------------------------------------

  openTask(cx, id) {
    this.screen = "task";
    this.taskId = id;
    this.task = null;
    this.described = null;
    this.taskOpenedAt = Date.now();
    this.flash = null;
    this.gateInputKey = null;
    this.poller?.openTask(id);
    cx.spawn(async (cx) => {
      await this.refreshNow();
      cx.notify();
    });
  }

  toList(cx) {
    this.screen = "list";
    this.taskId = null;
    this.task = null;
    this.poller?.closeTask();
    this.flash = null;
    cx.notify();
  }

  recentProjects() {
    const seen = new Set();
    const out = [];
    for (const t of this.rawList) {
      if (t.workspace && !seen.has(t.workspace)) {
        seen.add(t.workspace);
        out.push(t.workspace);
      }
    }
    return out;
  }

  // ---- commands ------------------------------------------------------------------------------------------------------------------

  /** Runs a decision built from the task as currently shown. One at a time; the view is re-read from the controller afterwards, whatever happened. */
  perform(cx, label, build) {
    if (!this.client || this.pending.active) return;
    const built = build(this.task);
    if (!built.ok) {
      this.say("attention", built.reason);
      cx.notify();
      return;
    }
    this.say("muted", `${label}…`);
    cx.notify();
    cx.spawn(async (cx) => {
      await this.pending.run(
        label,
        async () => {
          const res = await this.client.send(built.request);
          if (res.ok) this.say("good", `${label}: recorded.`);
          else this.say(res.error.kind === KIND.stale ? "attention" : "blocked", explain(res.error));
        },
        async () => {
          await this.refreshNow();
          cx.notify();
        },
      );
      cx.notify();
    });
  }

  openSession(cx) {
    try {
      const argv = attachArgv(this.awPath, this.taskId);
      const name = `agent ${shortId(this.taskId)}`;
      const existing = spaces().flatMap((s) => s.tabs).find((t) => t.named && t.name === name);
      if (existing) run("tab.select", { id: existing.id });
      else run("tab.new", { name, argv });
      this.say("muted", "The agent's session is open in its own tab. Answer anything it asks there.");
    } catch (e) {
      this.say("blocked", `The session could not be opened: ${String(e?.message ?? e)}`);
    }
    cx.notify();
  }

  // ---- New Task ------------------------------------------------------------------------------------------------------------------

  openNew(cx, workspace = "") {
    this.screen = "new";
    this.flash = null;
    this.newForm = {
      workspace: InputState.new({ placeholder: "C:/path/to/project", value: workspace }),
      objective: TextareaState.new({ placeholder: "What should be done?", rows: 5 }),
      ctx: null,
      root: null,
      reviewer: null,
      prover: null,
      acceptance: null,
      require: { what: false, how: false, review: false, prove: false },
      checking: false,
    };
    if (workspace) this.checkProject(cx);
    cx.notify();
  }

  checkProject(cx) {
    const f = this.newForm;
    const workspace = f.workspace.value().trim();
    if (!workspace) {
      this.say("attention", "Choose or type the project folder first.");
      cx.notify();
      return;
    }
    f.checking = true;
    f.ctx = null;
    cx.notify();
    cx.spawn(async (cx) => {
      const res = await this.client.inspectProject(workspace);
      f.checking = false;
      if (!res.ok) {
        this.say("blocked", explain(res.error));
      } else {
        this.inspection = res.value;
        f.ctx = newTaskContext(res.value);
        f.root = f.reviewer = f.prover = null;
        f.acceptance = f.ctx.ready ? f.ctx.acceptance : null;
      }
      cx.notify();
    });
  }

  startTask(cx) {
    const f = this.newForm;
    const form = { objective: f.objective.value(), root: f.root, reviewer: f.reviewer, prover: f.prover, acceptance: f.acceptance, requireWhat: f.require.what, requireHow: f.require.how, requireReview: f.require.review, requireProve: f.require.prove };
    const built = newTaskRequest(f.ctx, form);
    if (!built.ok) {
      this.say("attention", built.reason);
      cx.notify();
      return;
    }
    this.say("muted", "Starting the task…");
    cx.notify();
    cx.spawn(async (cx) => {
      await this.pending.run(
        "start",
        async () => {
          const res = await this.client.send(built.request);
          if (!res.ok) {
            this.say("blocked", explain(res.error));
            return;
          }
          const id = res.value?.task?.taskId;
          if (id) this.openTask(cx, id);
          else this.toList(cx);
        },
        async () => {
          await this.refreshNow();
          cx.notify();
        },
      );
      cx.notify();
    });
  }

  // ---- Initialize ----------------------------------------------------------------------------------------------------------------

  openInit(cx) {
    const insp = this.inspection;
    const draft = initDraft(insp);
    const mkCheck = (c = {}) => ({ id: InputState.new({ placeholder: "id, e.g. build", value: c.id ?? "" }), executable: InputState.new({ placeholder: "executable, e.g. dotnet", value: c.executable ?? "" }), args: InputState.new({ placeholder: "arguments, space separated", value: (c.arguments ?? []).join(" ") }), timeout: InputState.new({ placeholder: "timeout seconds", value: c.timeoutSeconds ? String(c.timeoutSeconds) : "" }) });
    this.initForm = {
      workspace: insp.workspace,
      toolchain: draft.toolchain || null,
      evidence: draft.toolchainEvidence,
      checks: draft.checks.length ? draft.checks.map(mkCheck) : [mkCheck()],
      mkCheck,
      writable: TextareaState.new({ placeholder: "one per line: src/**, a file, or ** for anywhere not protected", rows: 3, value: "" }),
      protectedPaths: TextareaState.new({ placeholder: "one path per line", rows: 2, value: draft.protectedPaths.join("\n") }),
      acceptance: null,
      profiles: [],
      root: null,
      reviewer: null,
      prover: null,
    };
    this.screen = "init";
    this.flash = null;
    cx.notify();
  }

  initProfileIds() {
    const f = this.initForm;
    return ["claude-code-local", ...f.profiles.map((p) => p.id.value().trim()).filter(Boolean)];
  }

  submitInit(cx) {
    const f = this.initForm;
    const form = {
      toolchain: f.toolchain ?? "",
      checks: f.checks.map((c) => ({ id: c.id.value().trim(), executable: c.executable.value().trim(), arguments: splitArguments(c.args.value()), timeoutSeconds: Number.parseInt(c.timeout.value(), 10) || undefined })),
      writable: splitLines(f.writable.value()),
      protectedPaths: splitLines(f.protectedPaths.value()),
      acceptance: f.acceptance ?? "",
      profiles: f.profiles.map((p) => ({ id: p.id.value().trim(), harness: p.harness, model: p.model.value().trim(), reasoning: p.reasoning.value().trim(), description: "" })),
      root: f.root ?? "",
      reviewer: f.reviewer ?? "",
      prover: f.prover ?? "",
    };
    const built = initChoices(this.inspection, form);
    if (!built.ok) {
      this.say("attention", built.problems.join("\n"));
      cx.notify();
      return;
    }
    this.say("muted", "Creating the project configuration…");
    cx.notify();
    cx.spawn(async (cx) => {
      await this.pending.run(
        "init",
        async () => {
          const res = await this.client.initProject(f.workspace, built.choices);
          if (!res.ok) {
            this.say("blocked", explain(res.error));
            return;
          }
          this.openNew(cx, f.workspace);
          this.say("good", "The project is set up. Choose the profiles for your task.");
        },
        async () => {},
      );
      cx.notify();
    });
  }

  // ---- render --------------------------------------------------------------------------------------------------------------------

  render() {
    resetRegistry();
    const t = theme();
    return v_flex()
      .id("aw-cockpit")
      .size_full()
      .bg(t.background)
      .text_color(t.foreground)
      .child(this.renderBar(t))
      .child(this.renderBody(t));
  }

  renderBar(t) {
    const cx = this.cx;
    const needs = this.rows.filter((r) => r.tone === "attention").length;
    return h_flex()
      .w_full()
      .h(44)
      .px(14)
      .gap(10)
      .items_center()
      .flex_none()
      .border_b_1()
      .border_color(t.border)
      .child(div().text_size(14).font_semibold().child("AgenticWorkflow"))
      .child(this.controllerError ? chip(this.controllerError.kind === KIND.exeNotFound ? "aw not found" : "controller problem", "blocked") : this.loaded ? chip("controller ready", "good") : chip("connecting…", "muted"))
      .when(needs > 0, (el) => el.child(chip(`${needs} need${needs === 1 ? "s" : ""} you`, "attention")))
      .child(div().flex_1())
      .child(button("bar-tasks", "Tasks", { onClick: () => this.cx && this.toList(this.cx) }))
      .child(button("bar-new", "New task", { style: "primary", onClick: () => this.cx && this.openNew(this.cx) }))
      .child(button("bar-refresh", "Refresh", { onClick: () => this.cx && this.runRefresh() }))
      .when(Boolean(props()?.overlay), (el) => el.child(button("bar-close", "Close", { onClick: () => closeOverlay(props().overlay) })));
  }

  runRefresh() {
    this.cx.spawn(async (cx) => {
      await this.refreshNow();
      cx.notify();
    });
  }

  renderBody(t) {
    const inner = this.controllerError && !this.loaded ? this.renderControllerError(t) : this.screen === "task" ? this.renderTask(t) : this.screen === "new" ? this.renderNew(t) : this.screen === "init" ? this.renderInit(t) : this.renderList(t);
    return v_flex().id("aw-body").flex_1().min_h(0).w_full().p(16).gap(10).overflow_y_scrollbar().when(Boolean(this.identity), (el) => el.child(notice(this.identity.text, "attention"))).when(Boolean(this.flash), (el) => el.child(notice(this.flash.text, this.flash.tone))).child(inner);
  }

  renderControllerError(t) {
    return card("ctl-error", {
      tone: "blocked",
      title: "AgenticWorkflow is not available",
      children: [para(explain(this.controllerError)), button("ctl-retry", "Try again", { onClick: () => this.runRefresh() })],
    });
  }

  // ---- list ----------------------------------------------------------------------------------------------------------------------

  renderList(t) {
    if (this.controllerError) return v_flex().gap(10).child(notice(explain(this.controllerError), "blocked")).child(this.renderRows(t));
    if (!this.loaded) return para("Starting the controller and reading your tasks…", { dim: true });
    if (this.rows.length === 0) {
      return card("empty", { title: "No tasks yet", children: [para("Start your first task from a project folder.", { dim: true }), button("empty-new", "New task", { style: "primary", onClick: () => this.openNew(this.cx) })] });
    }
    return this.renderRows(t);
  }

  renderRows(t) {
    return v_flex().w_full().gap(8).children(this.rows.map((r) => this.renderRow(t, r)));
  }

  renderRow(t, r) {
    const color = toneColor(t, r.tone);
    return h_flex()
      .id(`row-${r.id}`)
      .w_full()
      .gap(10)
      .p(10)
      .rounded(9)
      .border_1()
      .border_color(r.tone === "attention" ? t.blocked : t.border)
      .bg(t.surface)
      .cursor_pointer()
      .hover((s) => s.bg(t.hover))
      .on_click(press(`row-${r.id}`, () => this.openTask(this.cx, r.id)))
      .child(div().w(4).rounded(2).bg(color).flex_none())
      .child(
        v_flex()
          .flex_1()
          .min_w(0)
          .gap(3)
          .child(div().text_size(13.5).font_medium().truncate().child(r.intent))
          .child(div().text_size(11.5).text_color(t.dim).truncate().child([r.project, r.agents].filter(Boolean).join("   ")))
          .when(Boolean(r.blocked), (el) => el.child(div().text_size(11.5).text_color(t.blocked).truncate().child(r.blocked))),
      )
      .child(
        v_flex()
          .gap(4)
          .items_end()
          .flex_none()
          .child(h_flex().gap(6).when(Boolean(r.pending), (el) => el.child(chip(r.pending, "attention"))).child(chip(r.status, r.tone)).when(Boolean(r.stage), (el) => el.child(chip(r.stage, "muted"))))
          .child(div().text_size(11).text_color(t.dim).child(r.lastActivity)),
      );
  }

  // ---- task ----------------------------------------------------------------------------------------------------------------------

  renderTask(t) {
    const task = this.task;
    if (!task) return para(this.controllerError ? explain(this.controllerError) : "Reading the task…", { dim: true });
    const d = detailOf(task);
    const panel = decisionPanel(task);
    const recovery = recoveryPanel(task);
    const cards = bindingCards(task, this.inspection?.workspace === d.workspace ? this.inspection.profiles : this.profileCache);
    const needsProfiles = (task.missingBindings ?? []).length > 0;
    if (needsProfiles && d.workspace && this.inspection?.workspace !== d.workspace && !this.inspecting) this.loadProfiles(d.workspace);

    const out = v_flex().w_full().gap(10);
    out.child(
      h_flex().gap(10).items_center().child(button("task-back", "← Tasks", { onClick: () => this.toList(this.cx) })).child(chip(d.status, d.tone)).when(Boolean(d.stage), (el) => el.child(chip(d.stage, "muted"))).child(div().text_size(11.5).text_color(t.dim).child(`${d.project} · ${d.lastActivity}`)),
    );
    out.child(div().text_size(16).font_semibold().whitespace_normal().child(d.intent));
    out.child(para(d.stageReason, { dim: true }));

    for (const n of d.notices) out.child(card(`notice-${n.id}`, { tone: "attention", title: n.title, children: [para(n.text)] }));
    if (d.block && !recovery) out.child(card("block", { tone: "blocked", title: "BLOCKED", children: [para(d.block.text), para(d.block.reason, { dim: true })] }));
    if (recovery) out.child(this.renderRecovery(t, task, recovery, d));
    for (const c of cards) out.child(this.renderBindingCard(t, task, c));
    if (panel) out.child(this.renderDecision(t, task, panel));
    out.child(this.renderNative(t, task));
    for (const s of d.sections) out.child(v_flex().w_full().gap(4).child(heading(s.title)).child(markdown(`sec-${s.id}`, s.markdown)));
    if (d.unavailable.length) out.child(para(`Not available right now: ${d.unavailable.join(", ")}`, { dim: true, size: 11.5 }));
    out.child(para(`task ${shortId(d.id)} · version ${d.streamVersion}`, { dim: true, size: 11 }));
    return out;
  }

  loadProfiles(workspace) {
    this.inspecting = true;
    this.cx.spawn(async (cx) => {
      const res = await this.client.inspectProject(workspace);
      if (res.ok) {
        this.inspection = res.value;
        this.profileCache = res.value.profiles;
      }
      this.inspecting = false;
      cx.notify();
    });
  }

  renderRecovery(t, task, recovery, d) {
    const kids = [para(d.block ? d.block.text : recovery.reason), para(recovery.reason, { dim: true })];
    if (recovery.noActions) kids.push(para("There is no action to take here. The task needs inspection outside the cockpit (aw show).", { dim: true }));
    else
      kids.push(
        h_flex()
          .gap(8)
          .children(
            recovery.actions.map((a) =>
              button(`rec-${a.id}`, a.label, { style: a.style, disabled: a.disabled || this.pending.active, onClick: () => this.perform(this.cx, a.id === "continue" ? "Continue" : "Abort", a.build) }),
            ),
          ),
      );
    return card("recovery", { tone: "blocked", title: "BLOCKED — recovery", children: kids });
  }

  renderBindingCard(t, task, c) {
    const kids = [para(c.why), para("Choose the profile this role runs under. Nothing is chosen for you, and the implementer's profile is not reused.", { dim: true, size: 11.5 })];
    if (c.options.length === 0) kids.push(para("No usable profile is configured in this project for that role. Add one to agenticworkflow.json.", { dim: true }));
    for (const o of c.options) {
      kids.push(
        h_flex()
          .id(`bind-${c.role}-${o.id}`)
          .w_full()
          .gap(8)
          .px(10)
          .py(7)
          .rounded(7)
          .border_1()
          .border_color(t.input)
          .bg(t.raised)
          .items_center()
          .cursor_pointer()
          .on_click(press(`bind-${c.role}-${o.id}`, () => this.perform(this.cx, `Bind ${c.roleLabel}`, (tk) => c.build(tk, o.id))))
          .text_size(12.5)
          .child(div().flex_1().child(`${o.id} → ${o.harness} → ${o.model ?? "native default"} → ${o.reasoning ?? "native default"}`))
          .child(div().text_color(t.accent).child("Use this")),
      );
    }
    return card(`bind-${c.role}`, { tone: "attention", title: `${c.roleLabel} needs a profile`, children: kids });
  }

  renderDecision(t, task, panel) {
    const avail = actionAvailability(task, panel);
    const kids = [];
    if (panel.staleNote) kids.push(notice(panel.staleNote, "attention"));
    panel.content.forEach((c, i) => kids.push(markdown(`dec-${i}`, `**${c.heading}**\n\n${c.text}`)));
    const needsAnswer = panel.inputs.some((i) => i.id === "answer");
    const needsFeedback = panel.inputs.some((i) => i.id === "feedback");
    if (needsAnswer) kids.push(field("Your answer", this.answerInput, { multiline: true }));
    if (needsFeedback) kids.push(field("Feedback (needed to request changes)", this.feedbackInput, { multiline: true }));
    const values = () => ({ answer: this.answerInput.value(), feedback: this.feedbackInput.value() });
    kids.push(
      h_flex()
        .gap(8)
        .flex_wrap()
        .children(
          panel.actions.map((a) => {
            const probe = avail[a.id];
            return button(`act-${a.id}`, a.label, {
              style: a.style,
              disabled: !probe.enabled || this.pending.active,
              onClick: () => this.perform(this.cx, a.label, (tk) => buildFromPanel(tk, panel, a.id, values())),
            });
          }),
        ),
    );
    const why = panel.actions.map((a) => (avail[a.id].enabled ? null : `${a.label}: ${avail[a.id].reason}`)).filter(Boolean);
    for (const w of why) kids.push(para(w, { dim: true, size: 11.5 }));
    kids.push(para("This is a workflow decision, made with these buttons. Nothing typed elsewhere counts as approval.", { dim: true, size: 11 }));
    return card("decision", { tone: "attention", title: panel.title, children: kids });
  }

  renderNative(t, task) {
    const n = nativeCard(task, this.described);
    const hint = startupHint(task, this.described, (Date.now() - (this.taskOpenedAt ?? Date.now())) / 1000);
    const kids = [
      h_flex().gap(8).items_center().child(chip(n.harness, "muted")).child(chip(n.status, n.needsYou ? "attention" : n.available ? "working" : "muted")),
    ];
    if (n.needsYouText) kids.push(notice(n.needsYouText, "attention"));
    if (hint) kids.push(para(hint, { dim: true }));
    if (n.message) kids.push(para(`Agent says: ${oneLine(n.message, 200)}`, { dim: true, size: 11.5 }));
    kids.push(
      n.canOpen ? button("native-open", "Open the agent's session", { style: n.needsYou ? "primary" : "secondary", onClick: () => this.openSession(this.cx) }) : para(n.unavailableReason, { dim: true }),
    );
    kids.push(para(n.note, { dim: true, size: 11 }));
    return card("native", { tone: n.needsYou ? "attention" : undefined, title: "Agent session", children: kids });
  }

  // ---- new task ------------------------------------------------------------------------------------------------------------------

  renderNew(t) {
    const f = this.newForm;
    const out = v_flex().w_full().gap(10);
    out.child(div().text_size(16).font_semibold().child("New task"));
    out.child(field("Project folder", f.workspace));
    const recents = this.recentProjects();
    if (recents.length) out.child(h_flex().gap(6).flex_wrap().children(recents.slice(0, 6).map((p, i) => button(`recent-${i}`, folderName(p), { onClick: () => { f.workspace.set_value(p); this.checkProject(this.cx); } }))));
    out.child(button("check-project", f.checking ? "Checking…" : "Use this project", { disabled: f.checking, onClick: () => this.checkProject(this.cx) }));
    const ctx = f.ctx;
    if (!ctx) return out;
    if (!ctx.ready) return out.child(this.renderNotReady(t, ctx));

    out.child(field("What should be done?", f.objective, { multiline: true }));
    const opts = ctx.profiles.map((p) => ({ id: p.id, label: p.label, disabled: !p.usable, note: p.usable ? null : p.problems[0] }));
    const none = [{ id: "", label: "None", note: null }];
    out.child(heading("Implementer profile (required)"));
    out.child(choices("root", opts, f.root, (id) => { f.root = id; this.cx.notify(); }));
    out.child(heading("Reviewer profile (optional)"));
    out.child(choices("reviewer", [...none, ...opts], f.reviewer ?? "", (id) => { f.reviewer = id || null; this.cx.notify(); }));
    out.child(heading("Prover profile (optional)"));
    out.child(choices("prover", [...none, ...opts], f.prover ?? "", (id) => { f.prover = id || null; this.cx.notify(); }));
    out.child(heading("ACCEPT"));
    out.child(choices("acceptance", [{ id: "human", label: "I decide: the work waits for my ACCEPT" }, { id: "none", label: "No ACCEPT: it completes once the controller has verified it" }], f.acceptance, (id) => { f.acceptance = id; this.cx.notify(); }));
    out.child(heading("Require"));
    for (const [key, label] of [["what", "WHAT (approve what will be built first)"], ["how", "HOW (approve the approach first)"], ["review", "REVIEW (an independent reviewer)"], ["prove", "PROVE (an independent prover)"]]) out.child(toggle(`req-${key}`, label, f.require[key], (on) => { f.require[key] = on; this.cx.notify(); }));
    const warnings = newTaskWarnings({ requireReview: f.require.review, reviewer: f.reviewer, requireProve: f.require.prove, prover: f.prover });
    for (const w of warnings) out.child(notice(w, "attention"));
    for (const m of ctx.prerequisitesMissing) out.child(notice(m, "attention"));
    out.child(button("start-task", this.pending.active ? "Starting…" : "Start task", { style: "primary", disabled: this.pending.active, onClick: () => this.startTask(this.cx) }));
    return out;
  }

  renderNotReady(t, ctx) {
    const kids = [para(ctx.message)];
    for (const p of ctx.problems ?? []) kids.push(para(p, { dim: true, size: 11.5 }));
    if (ctx.state === "needs_init") kids.push(button("init-start", "Initialize AgenticWorkflow", { style: "primary", onClick: () => this.openInit(this.cx) }));
    return card("not-ready", { tone: ctx.state === "needs_init" ? "attention" : "blocked", title: ctx.state === "needs_init" ? "Set up this project" : "This project cannot be used yet", children: kids });
  }

  // ---- init ----------------------------------------------------------------------------------------------------------------------

  renderInit(t) {
    const f = this.initForm;
    const insp = this.inspection;
    const out = v_flex().w_full().gap(10);
    out.child(div().text_size(16).font_semibold().child("Initialize AgenticWorkflow"));
    out.child(para(`${f.workspace}`, { dim: true }));
    out.child(para("Nothing below is applied until you press Create. Detected values are suggestions you confirm or change; no command, profile, model or approval requirement is invented for you.", { dim: true }));

    out.child(heading("Toolchain"));
    if (f.evidence.length) out.child(para(`Detected from: ${f.evidence.join(", ")}`, { dim: true, size: 11.5 }));
    out.child(choices("tc", insp.toolchains.map((id) => ({ id, label: id })), f.toolchain, (id) => { f.toolchain = id; this.cx.notify(); }));
    if (f.toolchain === "custom") out.child(para("custom: no build or test command is assumed. Verification checks and allowed commands are exactly what you list.", { dim: true, size: 11.5 }));

    out.child(heading("Verification checks (commands that prove the work is right)"));
    f.checks.forEach((c, i) => {
      out.child(
        v_flex().w_full().gap(6).p(10).rounded(8).border_1().border_color(t.border)
          .child(h_flex().gap(8).child(div().flex_1().child(field("Id", c.id))).child(div().flex_1().child(field("Executable", c.executable))))
          .child(h_flex().gap(8).child(div().flex_1().child(field("Arguments", c.args))).child(div().w(140).child(field("Timeout (s)", c.timeout))))
          .child(button(`rm-check-${i}`, "Remove check", { onClick: () => { f.checks.splice(i, 1); this.cx.notify(); } })),
      );
    });
    out.child(button("add-check", "Add a check", { onClick: () => { f.checks.push(f.mkCheck()); this.cx.notify(); } }));
    out.child(para("Arguments are split on spaces, with no quoting or shell expansion. Anything more complex belongs in the file.", { dim: true, size: 11 }));

    out.child(heading("Where the agent may write"));
    out.child(field("Writable paths", f.writable, { multiline: true }));
    out.child(field("Protected paths (never changed)", f.protectedPaths, { multiline: true }));

    out.child(heading("ACCEPT"));
    out.child(choices("init-accept", [{ id: "human", label: "I decide: work waits for my ACCEPT" }, { id: "none", label: "No ACCEPT: it completes once verified" }], f.acceptance, (id) => { f.acceptance = id; this.cx.notify(); }));

    out.child(heading("Profiles"));
    out.child(para("claude-code-local is built in. Add a profile only if you want another harness, model or reasoning level; leave model and reasoning empty to use the harness's own default.", { dim: true, size: 11.5 }));
    f.profiles.forEach((p, i) => {
      out.child(
        v_flex().w_full().gap(6).p(10).rounded(8).border_1().border_color(t.border)
          .child(field("Profile id", p.id))
          .child(choices(`ph-${i}`, insp.harnesses.map((h) => ({ id: h, label: h })), p.harness, (h) => { p.harness = h; this.cx.notify(); }))
          .child(h_flex().gap(8).child(div().flex_1().child(field("Model (optional)", p.model))).child(div().flex_1().child(field("Reasoning (optional)", p.reasoning))))
          .child(button(`rm-prof-${i}`, "Remove profile", { onClick: () => { f.profiles.splice(i, 1); this.cx.notify(); } })),
      );
    });
    out.child(button("add-profile", "Add a profile", { onClick: () => { f.profiles.push({ id: InputState.new({ placeholder: "id, e.g. pi-local" }), harness: null, model: InputState.new({ placeholder: "" }), reasoning: InputState.new({ placeholder: "" }) }); this.cx.notify(); } }));

    const ids = this.initProfileIds().map((id) => ({ id, label: id }));
    out.child(heading("Implementer profile (required)"));
    out.child(choices("init-root", ids, f.root, (id) => { f.root = id; this.cx.notify(); }));
    out.child(heading("Reviewer profile (optional)"));
    out.child(choices("init-reviewer", [{ id: "", label: "None" }, ...ids], f.reviewer ?? "", (id) => { f.reviewer = id || null; this.cx.notify(); }));
    out.child(heading("Prover profile (optional)"));
    out.child(choices("init-prover", [{ id: "", label: "None" }, ...ids], f.prover ?? "", (id) => { f.prover = id || null; this.cx.notify(); }));

    out.child(h_flex().gap(8).child(button("init-create", this.pending.active ? "Creating…" : "Create agenticworkflow.json", { style: "primary", disabled: this.pending.active, onClick: () => this.submitInit(this.cx) })).child(button("init-cancel", "Cancel", { onClick: () => this.openNew(this.cx, f.workspace) })));
    return out;
  }
}
