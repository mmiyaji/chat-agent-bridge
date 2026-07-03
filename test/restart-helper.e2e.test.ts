import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawn } from "node:child_process";
import type { TestContext } from "node:test";

function powershellPath(): string {
  return path.join(
    process.env.SystemRoot ?? "C:\\Windows",
    "System32",
    "WindowsPowerShell",
    "v1.0",
    "powershell.exe",
  );
}

async function runPowershellScript(
  scriptPath: string,
  args: string[],
  env: NodeJS.ProcessEnv,
): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const child = spawn(
      powershellPath(),
      ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, ...args],
      {
        env,
        stdio: "pipe",
      },
    );

    let stderr = "";
    child.stderr.on("data", (chunk) => {
      stderr += chunk.toString();
    });
    child.on("error", reject);
    child.on("exit", (code) => {
      if (code === 0) {
        resolve();
        return;
      }
      reject(new Error(`PowerShell exited with code ${code}. ${stderr}`));
    });
  });
}

async function runPowershellCommand(
  command: string,
): Promise<{ stdout: string; stderr: string; code: number | null }> {
  return await new Promise((resolve, reject) => {
    const child = spawn(
      powershellPath(),
      ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command],
      { stdio: "pipe" },
    );

    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => {
      stdout += chunk.toString();
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk.toString();
    });
    child.on("error", reject);
    child.on("exit", (code) => {
      resolve({ stdout, stderr, code });
    });
  });
}

function isBlockedPowerShellSpawn(error: unknown): boolean {
  return error instanceof Error && "code" in error && (error as { code?: string }).code === "EPERM";
}

function skipIfPowerShellSpawnBlocked(t: TestContext, error: unknown): boolean {
  if (isBlockedPowerShellSpawn(error)) {
    t.skip("PowerShell child process spawn is blocked in this environment.");
    return true;
  }

  return false;
}

test("bridge-restart-detached helper runs stop/start and posts completion", async (t) => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cab-restart-helper-"));
  const scriptsDir = path.join(tempRoot, "scripts");
  const runtimeDir = path.join(tempRoot, "data", "runtime");
  fs.mkdirSync(scriptsDir, { recursive: true });
  fs.mkdirSync(runtimeDir, { recursive: true });

  const helperScript = path.join(
    process.cwd(),
    "scripts",
    "bridge-restart-detached.ps1",
  );
  const copiedHelperScript = path.join(scriptsDir, "bridge-restart-detached.ps1");
  fs.copyFileSync(helperScript, copiedHelperScript);

  const actionsLog = path.join(tempRoot, "actions.log");
  const restartScript = path.join(scriptsDir, "fake-restart.ps1");
  fs.writeFileSync(
    restartScript,
    [
      'param([switch]$Build)',
      '$log = Join-Path (Split-Path -Parent $PSScriptRoot) "actions.log"',
      '"restart build=$($Build.IsPresent)" | Add-Content -Path $log -Encoding utf8',
    ].join("\r\n"),
    "utf8",
  );

  const completionFile = path.join(runtimeDir, "restart-completion.jsonl");

  const env = {
    ...process.env,
    CAB_BRIDGE_RESTART_SCRIPT: restartScript,
    CAB_RESTART_COMPLETION_FILE: completionFile,
  };

  try {
    await runPowershellScript(
      copiedHelperScript,
      ["-ChannelId", "C123", "-ThreadId", "T123"],
      env,
    );
  } catch (error) {
    if (skipIfPowerShellSpawnBlocked(t, error)) {
      return;
    }
    throw error;
  }

  const actions = fs.readFileSync(actionsLog, "utf8");
  assert.match(actions, /restart build=False/);
  const completionLines = fs
    .readFileSync(completionFile, "utf8")
    .split(/\r?\n/)
    .filter((line) => line.trim().length > 0);
  assert.equal(completionLines.length, 1);
  const payload = JSON.parse((completionLines[0] ?? "{}").replace(/^\uFEFF/, "")) as {
    channelId?: string;
    threadId?: string;
    text?: string;
  };
  assert.equal(payload.channelId, "C123");
  assert.equal(payload.threadId, "T123");
  assert.match(payload.text ?? "", /Bridge restart completed/);
});

test("Test-BridgeProcessId rejects a non-node system process (stale-pid guard)", async (t) => {
  // Hermetic guard for the stale-PID class of bug: a PID owned by something that
  // is not the bridge (here PID 4 = System) must be classified as NOT the
  // bridge, so the stop script would treat the pid file as stale and never
  // Stop-Process it. We only exercise the classifier here — we never invoke the
  // real bridge-stop.ps1, because its process discovery would find (and kill)
  // the genuine running bridge node.exe.
  const commonScript = path.join(process.cwd(), "scripts", "bridge-common.ps1");
  const commonScriptLiteral = commonScript.replace(/'/g, "''");
  const command = [
    `. '${commonScriptLiteral}'`,
    "$system = Test-BridgeProcessId -ProcessId 4",
    "$missing = Test-BridgeProcessId -ProcessId 999999",
    'Write-Output "system=$system"',
    'Write-Output "missing=$missing"',
  ].join("; ");

  let result: { stdout: string; stderr: string; code: number | null };
  try {
    result = await runPowershellCommand(command);
  } catch (error) {
    if (skipIfPowerShellSpawnBlocked(t, error)) {
      return;
    }
    throw error;
  }

  assert.equal(result.code, 0, result.stderr);
  // PID 4 (System) exists but is not node.exe -> false.
  assert.match(result.stdout, /system=False/);
  // A PID that does not exist -> false.
  assert.match(result.stdout, /missing=False/);
});
