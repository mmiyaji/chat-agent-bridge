import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { extractExecutable, resolveCommand } from "../src/core/command-resolution.js";

test("extractExecutable handles quoted commands", () => {
  assert.equal(
    extractExecutable('"C:\\Program Files\\Cursor\\cursor.exe" agent --print'),
    "C:\\Program Files\\Cursor\\cursor.exe",
  );
  assert.equal(extractExecutable("cursor agent --print"), "cursor");
});

test("resolveCommand reports explicit paths", async () => {
  const tempRoot = await fs.promises.mkdtemp(path.join(os.tmpdir(), "cab-command-resolution-"));
  const scriptPath = path.join(tempRoot, "cursor.cmd");
  await fs.promises.writeFile(scriptPath, "@echo off\r\necho ok\r\n", "utf8");

  const result = resolveCommand(`"${scriptPath}" agent`, tempRoot);
  assert.equal(result.status, "resolved");
  assert.equal(result.executable, scriptPath);
  assert.equal(result.source, "explicit-path");
  assert.ok(result.resolvedPath);
});

test("resolveCommand reports not_found for missing commands", () => {
  const result = resolveCommand("definitely-missing-command-xyz", process.cwd());
  assert.equal(result.status, "not_found");
  assert.equal(result.executable, "definitely-missing-command-xyz");
});
