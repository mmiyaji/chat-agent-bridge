import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { findInstalledCodexExecutable } from "../src/agents/codex/codex-installation.js";

test("finds the most recently installed Codex desktop executable", (t) => {
  const localAppData = fs.mkdtempSync(path.join(os.tmpdir(), "cab-codex-install-"));
  t.after(() => fs.rmSync(localAppData, { recursive: true, force: true }));
  const binDirectory = path.join(localAppData, "OpenAI", "Codex", "bin");
  const oldExecutable = path.join(binDirectory, "old", "codex.exe");
  const newExecutable = path.join(binDirectory, "new", "codex.exe");
  fs.mkdirSync(path.dirname(oldExecutable), { recursive: true });
  fs.mkdirSync(path.dirname(newExecutable), { recursive: true });
  fs.writeFileSync(oldExecutable, "old");
  fs.writeFileSync(newExecutable, "new");
  const oldDate = new Date("2025-01-01T00:00:00Z");
  const newDate = new Date("2026-01-01T00:00:00Z");
  fs.utimesSync(oldExecutable, oldDate, oldDate);
  fs.utimesSync(newExecutable, newDate, newDate);

  assert.equal(findInstalledCodexExecutable(localAppData), newExecutable);
});

test("ignores missing roots, incomplete versions, and directories named codex.exe", (t) => {
  assert.equal(findInstalledCodexExecutable(undefined), undefined);
  assert.equal(findInstalledCodexExecutable(path.join(os.tmpdir(), "cab-codex-no-such-root")), undefined);

  const localAppData = fs.mkdtempSync(path.join(os.tmpdir(), "cab-codex-incomplete-"));
  t.after(() => fs.rmSync(localAppData, { recursive: true, force: true }));
  const versionsDirectory = path.join(localAppData, "OpenAI", "Codex", "bin");
  fs.mkdirSync(path.join(versionsDirectory, "incomplete"), { recursive: true });
  fs.mkdirSync(path.join(versionsDirectory, "not-a-file", "codex.exe"), { recursive: true });

  assert.equal(findInstalledCodexExecutable(localAppData), undefined);
});
