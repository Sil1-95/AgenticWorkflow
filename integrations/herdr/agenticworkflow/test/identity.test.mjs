import test from "node:test";
import assert from "node:assert/strict";
import { identityNotice, controllerVersionOf } from "../lib/identity.js";

test("matching versions say nothing", () => {
  assert.equal(identityNotice("0.1.0", "0.1.0"), null);
});

test("a source checkout (no stamp) is never reported stale", () => {
  assert.equal(identityNotice(null, "0.1.0"), null);
  assert.equal(identityNotice(undefined, "0.1.0"), null);
});

test("an unknown controller version says nothing", () => {
  assert.equal(identityNotice("0.1.0", null), null);
});

test("different versions are explained with both numbers and the way out", () => {
  const notice = identityNotice("0.0.9", "0.1.0");
  assert.match(notice.text, /0\.0\.9/);
  assert.match(notice.text, /0\.1\.0/);
  assert.match(notice.text, /install\.ps1/);
});

test("the controller version is read from a health result", () => {
  assert.equal(controllerVersionOf({ kind: "health", controller: { version: "0.1.0" } }), "0.1.0");
  assert.equal(controllerVersionOf({ kind: "health" }), null);
  assert.equal(controllerVersionOf(null), null);
  assert.equal(controllerVersionOf({ controller: { version: 3 } }), null);
});
