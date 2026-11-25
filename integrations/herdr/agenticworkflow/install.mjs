#!/usr/bin/env node
// Installs the AgenticWorkflow cockpit into Herdr's mods folder.
//
//   node install.mjs                  copy the cockpit into <herdr home>/mods/agenticworkflow (re-run to update after a `git pull`)
//   node install.mjs --link           DEVELOPMENT: link this folder instead, so edits apply on save. Herdr does not list a linked mod's command-palette entry; it still opens
//   node install.mjs --aw <path>      the aw program the cockpit runs (default: found on PATH, else this repo's Debug/Release build)
//   node install.mjs --home <dir>     the Herdr app home (default: $HERDR_APP_HOME, else ~/.config/herdr-app)
//   node install.mjs --driver        DEV/ACCEPTANCE ONLY: also enable driver.js, which lets a test press the cockpit's own buttons (see that file); off by default
//   node install.mjs --uninstall      remove the mod and its cockpit settings (never touches AgenticWorkflow's own state)
//
// Herdr discovers a mod's command-palette entry only in a real folder under mods/, so the default install copies the few files the mod needs. The install writes one small settings file, <herdr home>/agenticworkflow/config.json,
// holding the path of `aw`. Nothing else is stored, and no secret ever is.

import { cpSync, existsSync, lstatSync, mkdirSync, readlinkSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";

const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const flag = (name) => args.includes(name);
const value = (name) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};

const home = resolve(value("--home") ?? process.env.HERDR_APP_HOME ?? join(homedir(), ".config", "herdr-app"));
const modsDir = join(home, "mods");
const target = join(modsDir, "agenticworkflow");
const settingsDir = join(home, "agenticworkflow");

const MOD_FILES = ["main.js", "ui.js", "driver.js", "mod.json", "lib"];

function exists(path) {
  try {
    lstatSync(path);
    return true;
  } catch {
    return false;
  }
}

function remove(path) {
  if (!exists(path)) return;
  const stat = lstatSync(path);
  // A junction or symlink is removed without touching what it points at.
  if (stat.isSymbolicLink()) rmSync(path, { force: true, recursive: false });
  else rmSync(path, { recursive: true, force: true });
}

function findAw() {
  const explicit = value("--aw");
  if (explicit) return resolve(explicit);
  const lookup = spawnSync(process.platform === "win32" ? "where" : "which", ["aw"], { encoding: "utf8" });
  if (lookup.status === 0) {
    const first = lookup.stdout.split(/\r?\n/).find(Boolean);
    if (first) return first.trim();
  }
  const exe = process.platform === "win32" ? "aw.exe" : "aw";
  for (const config of ["Debug", "Release"]) {
    const candidate = resolve(here, "..", "..", "..", "src", "AgenticWorkflow.Cli", "bin", config, "net10.0", exe);
    if (existsSync(candidate)) return candidate;
  }
  return null;
}

if (flag("--uninstall")) {
  remove(target);
  remove(settingsDir);
  console.log(`Removed ${target} and ${settingsDir}.`);
  process.exit(0);
}

mkdirSync(modsDir, { recursive: true });
remove(target);

if (!flag("--link")) {
  mkdirSync(target, { recursive: true });
  for (const name of MOD_FILES) cpSync(join(here, name), join(target, name), { recursive: true });
  console.log(`Copied the cockpit to ${target}. Re-run this command after updating the repository.`);
} else {
  // A junction needs no elevated rights on Windows; on other systems it is an ordinary symlink.
  symlinkSync(here, target, "junction");
  console.log(`Linked ${target} -> ${here}. Edits apply as soon as they are saved. (Herdr does not list a linked mod in the command palette; use a copy for that.)`);
}

const aw = findAw();
mkdirSync(settingsDir, { recursive: true });
const settings = aw ? { aw: aw.replace(/\\/g, "/") } : {};

if (flag("--driver"))
  settings.driver = true;

writeFileSync(join(settingsDir, "config.json"), JSON.stringify(settings, null, 2) + "\n");

if (aw) {
  console.log(`The cockpit will run: ${aw}`);
  if (/\s/.test(aw)) console.log("Note: that path contains spaces. Opening an agent's session passes it as a command, so prefer a path without spaces (--aw <path>).");
} else {
  console.log("aw was not found on PATH or in this repository's build output. Build it (dotnet build) or pass --aw <path>; until then the cockpit will say it cannot find aw.");
}

console.log("");
console.log("Open the cockpit in Herdr:");
console.log("  - command palette (Ctrl+Shift+P): Open AgenticWorkflow, or");
console.log('  - herdr-app do tab.new --name "AgenticWorkflow" --mod agenticworkflow');
console.log("  - to bind a key, add to keys.json:  \"ctrl-alt-a\": \"mod.open:agenticworkflow\"");
if (!existsSync(join(here, "main.js"))) process.exit(1);
