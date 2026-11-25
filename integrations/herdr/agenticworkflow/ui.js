// Small gpui-kit element helpers shared by the screens. Presentation only: no workflow logic and no I/O.

import { div } from "gpui-kit";
import { v_flex, h_flex, Input, Textarea, TextView } from "gpui-base";
import { theme } from "herdr";

// What is on screen right now that a click or typing can reach, rebuilt on every render. Only the dev driver (driver.js, off unless install.mjs --driver) reads it: it lets a
// live acceptance run invoke exactly the handler a click would, without pointer automation on a real display.
export const registry = { press: new Map(), inputs: new Map() };

export const resetRegistry = () => {
  registry.press.clear();
  registry.inputs.clear();
};

/** A clickable element's handler, recorded under its element id, and returned so the caller still wires it. */
export const press = (id, handler) => {
  registry.press.set(id, handler);
  return handler;
};

const toneColor = (t, tone) => ({ attention: t.blocked, blocked: t.blocked, working: t.working, done: t.done, idle: t.muted, good: t.done, muted: t.dim })[tone] ?? t.muted;

export function button(id, text, { style = "secondary", disabled = false, onClick } = {}) {
  const t = theme();
  const primary = style === "primary";
  const danger = style === "danger";
  const fg = disabled ? t.dim : primary ? t.solid_foreground : danger ? t.blocked : t.foreground;
  const el = h_flex()
    .id(id)
    .h(30)
    .px(12)
    .items_center()
    .rounded(7)
    .border_1()
    .border_color(primary && !disabled ? t.solid : danger && !disabled ? t.blocked : t.input)
    .bg(primary && !disabled ? t.solid : t.raised)
    .text_color(fg)
    .text_size(12.5)
    .font_medium()
    .child(text);
  if (disabled) return el.opacity(0.55);
  press(id, () => onClick?.());
  return el.cursor_pointer().hover((s) => s.opacity(0.88)).on_click(() => onClick?.());
}

export function chip(text, tone) {
  const t = theme();
  return div().px(8).h(20).flex().items_center().rounded(10).border_1().border_color(toneColor(t, tone)).text_color(toneColor(t, tone)).text_size(11).child(text);
}

export function heading(text) {
  const t = theme();
  return div().pt(14).pb(4).text_size(11.5).font_semibold().text_color(t.dim).child(text.toUpperCase());
}

export function para(text, { dim = false, size = 12.8 } = {}) {
  const t = theme();
  return div().text_size(size).text_color(dim ? t.dim : t.foreground).whitespace_normal().child(String(text ?? ""));
}

/** Markdown the agent or the model wrote, with links inert: the cockpit never opens anything on a click inside content. */
export function markdown(id, text) {
  return TextView.markdown(id, String(text ?? "")).on_link_click(() => {}).selectable(true);
}

export function card(id, { tone, title, children = [] }) {
  const t = theme();
  return v_flex()
    .id(id)
    .w_full()
    .p(12)
    .gap(6)
    .rounded(9)
    .border_1()
    .border_color(tone ? toneColor(t, tone) : t.border)
    .bg(t.surface)
    .when(Boolean(title), (el) => el.child(div().text_size(13).font_semibold().text_color(tone ? toneColor(t, tone) : t.foreground).child(title)))
    .children(children);
}

export function field(label, input, { multiline = false, height } = {}) {
  const t = theme();
  registry.inputs.set(label, input);
  const widget = multiline ? Textarea.new(input) : Input.new(input);
  widget.px(10).rounded(8).border_1().border_color(t.input).bg(t.background).text_size(12.8).text_color(t.foreground);
  if (!multiline) widget.h(34);
  if (height) widget.h(height);
  return v_flex().w_full().gap(4).child(div().text_size(11.5).text_color(t.dim).child(label)).child(widget);
}

/** A row of mutually exclusive choices; `selected` is the id chosen, or null when the person has not chosen yet. */
export function choices(idPrefix, options, selected, onPick) {
  const t = theme();
  return v_flex()
    .w_full()
    .gap(4)
    .children(
      options.map((o) => {
        const on = o.id === selected;
        return h_flex()
          .id(`${idPrefix}-${o.id || "none"}`)
          .w_full()
          .px(10)
          .py(7)
          .gap(8)
          .items_center()
          .rounded(7)
          .border_1()
          .border_color(on ? t.accent : o.disabled ? t.border : t.input)
          .bg(on ? t.selected : t.raised)
          .text_color(o.disabled ? t.dim : t.foreground)
          .text_size(12.5)
          .when(!o.disabled, (el) => {
            press(`${idPrefix}-${o.id || "none"}`, () => onPick(o.id));
            return el.cursor_pointer().on_click(() => onPick(o.id));
          })
          .child(div().w(14).text_color(on ? t.accent : t.dim).child(on ? "●" : "○"))
          .child(div().flex_1().whitespace_normal().child(o.label))
          .when(Boolean(o.note), (el) => el.child(div().text_size(11).text_color(t.dim).child(o.note)));
      }),
    );
}

export function toggle(id, label, on, onToggle) {
  const t = theme();
  press(id, () => onToggle(!on));
  return h_flex()
    .id(id)
    .gap(8)
    .items_center()
    .cursor_pointer()
    .on_click(() => onToggle(!on))
    .text_size(12.5)
    .text_color(t.foreground)
    .child(div().w(14).text_color(on ? t.accent : t.dim).child(on ? "☑" : "☐"))
    .child(label);
}

export function notice(text, tone = "attention") {
  const t = theme();
  return h_flex().w_full().px(12).py(8).rounded(8).border_1().border_color(toneColor(t, tone)).text_color(toneColor(t, tone)).text_size(12.5).child(div().flex_1().min_w(0).whitespace_normal().child(String(text ?? "")));
}

export { toneColor };
