import fs from "node:fs";
import path from "node:path";
import { spawn } from "node:child_process";

const repoRoot = process.cwd();
const outDir = path.join(repoRoot, ".test-dist");
const compiledTestDir = path.join(outDir, "test");

await fs.promises.rm(outDir, { recursive: true, force: true });

const compileExitCode = await new Promise((resolve, reject) => {
  const child = spawn(
    process.execPath,
    [
      "--preserve-symlinks",
      "--preserve-symlinks-main",
      "./node_modules/typescript/bin/tsc",
      "-p",
      "tsconfig.test.json",
    ],
    {
      cwd: repoRoot,
      stdio: "inherit",
    },
  );

  child.on("error", reject);
  child.on("exit", (code, signal) => {
    if (signal) {
      process.kill(process.pid, signal);
      return;
    }

    resolve(code ?? 1);
  });
});

if (compileExitCode !== 0) {
  process.exit(Number(compileExitCode));
}

const testFiles = fs
  .readdirSync(compiledTestDir, { withFileTypes: true })
  .filter((entry) => entry.isFile() && entry.name.endsWith(".test.js"))
  .map((entry) => path.join(compiledTestDir, entry.name))
  .sort();

if (testFiles.length === 0) {
  console.error("No compiled test files were found.");
  process.exit(1);
}

let hasFailures = false;

const adminServerHarnessPath = path.join(repoRoot, "scripts", "run-admin-server-tests.mjs");
const adminServerExitCode = await new Promise((resolve, reject) => {
  const child = spawn(process.execPath, ["--test", adminServerHarnessPath], {
    cwd: repoRoot,
    stdio: "inherit",
  });

  child.on("error", reject);
  child.on("exit", (code, signal) => {
    if (signal) {
      process.kill(process.pid, signal);
      return;
    }

    resolve(code ?? 1);
  });
});

if (adminServerExitCode !== 0) {
  process.exit(Number(adminServerExitCode));
}

for (const file of testFiles) {
  if (path.basename(file) === "admin-server.test.js") {
    continue;
  }
  const testExitCode = await new Promise((resolve, reject) => {
    const child = spawn(process.execPath, ["--test", file], {
      cwd: repoRoot,
      stdio: "inherit",
    });

    child.on("error", reject);
    child.on("exit", (code, signal) => {
      if (signal) {
        process.kill(process.pid, signal);
        return;
      }

      resolve(code ?? 1);
    });
  });

  if (testExitCode !== 0) {
    hasFailures = true;
    break;
  }
}

if (hasFailures) {
  process.exit(1);
}

await fs.promises.rm(outDir, { recursive: true, force: true });
