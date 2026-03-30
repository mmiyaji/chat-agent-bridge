import fs from "node:fs";
import path from "node:path";
import { spawn } from "node:child_process";

const repoRoot = process.cwd();
const testDir = path.join(repoRoot, "test");
const testFiles = fs
  .readdirSync(testDir, { withFileTypes: true })
  .filter((entry) => entry.isFile() && entry.name.endsWith(".test.ts"))
  .map((entry) => path.posix.join("test", entry.name))
  .sort();

if (testFiles.length === 0) {
  console.error("No test files were found.");
  process.exit(1);
}

const child = spawn(
  process.execPath,
  ["./node_modules/tsx/dist/cli.mjs", "--test", ...testFiles],
  {
    cwd: repoRoot,
    stdio: "inherit",
  },
);

child.on("exit", (code, signal) => {
  if (signal) {
    process.kill(process.pid, signal);
    return;
  }

  process.exit(code ?? 1);
});

