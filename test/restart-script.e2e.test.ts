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

function readActionsLog(actionsLog: string): string[] {
  return fs
    .readFileSync(actionsLog, "utf8")
    .split(/\r?\n/)
    .map((line) => line.replace(/^\uFEFF/, ""))
    .filter((line) => line.trim().length > 0);
}

function readAuditLog(auditLog: string): Array<Record<string, unknown>> {
  return fs
    .readFileSync(auditLog, "utf8")
    .split(/\r?\n/)
    .map((line) => line.replace(/^\uFEFF/, "").trim())
    .filter((line) => line.length > 0)
    .map((line) => JSON.parse(line) as Record<string, unknown>);
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

test("bridge-restart script runs stop then start without build by default", async (t) => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cab-restart-script-"));
  const scriptsDir = path.join(tempRoot, "scripts");
  fs.mkdirSync(scriptsDir, { recursive: true });

  const restartScript = path.join(process.cwd(), "scripts", "bridge-restart.ps1");
  const copiedRestartScript = path.join(scriptsDir, "bridge-restart.ps1");
  const commonScript = path.join(process.cwd(), "scripts", "bridge-common.ps1");
  const copiedCommonScript = path.join(scriptsDir, "bridge-common.ps1");
  fs.copyFileSync(restartScript, copiedRestartScript);
  fs.copyFileSync(commonScript, copiedCommonScript);

  const actionsLog = path.join(tempRoot, "actions.log");
  const auditLog = path.join(tempRoot, "audit.jsonl");
  const stopScript = path.join(scriptsDir, "fake-stop.ps1");
  const startScript = path.join(scriptsDir, "fake-start.ps1");

  fs.writeFileSync(
    stopScript,
    [
      '$log = Join-Path (Split-Path -Parent $PSScriptRoot) "actions.log"',
      '"stop" | Add-Content -Path $log -Encoding utf8',
    ].join("\r\n"),
    "utf8",
  );

  fs.writeFileSync(
    startScript,
    [
      "param([switch]$SkipBuild)",
      '$log = Join-Path (Split-Path -Parent $PSScriptRoot) "actions.log"',
      '"start skipBuild=$($SkipBuild.IsPresent)" | Add-Content -Path $log -Encoding utf8',
    ].join("\r\n"),
    "utf8",
  );

  const env = {
    ...process.env,
    CAB_BRIDGE_STOP_SCRIPT: stopScript,
    CAB_BRIDGE_START_SCRIPT: startScript,
    CAB_AUDIT_LOG_FILE: auditLog,
  };

  try {
    await runPowershellScript(copiedRestartScript, [], env);
  } catch (error) {
    if (skipIfPowerShellSpawnBlocked(t, error)) {
      return;
    }
    throw error;
  }

  const actions = readActionsLog(actionsLog);
  assert.deepEqual(actions, ["stop", "start skipBuild=True"]);
  const auditEvents = readAuditLog(auditLog).map((entry) => entry.type);
  assert.ok(auditEvents.includes("service.restart.requested"));
  assert.ok(auditEvents.includes("service.restart.completed"));
});

test("bridge-restart script runs stop then start with build when requested", async (t) => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cab-restart-script-build-"));
  const scriptsDir = path.join(tempRoot, "scripts");
  fs.mkdirSync(scriptsDir, { recursive: true });

  const restartScript = path.join(process.cwd(), "scripts", "bridge-restart.ps1");
  const copiedRestartScript = path.join(scriptsDir, "bridge-restart.ps1");
  const commonScript = path.join(process.cwd(), "scripts", "bridge-common.ps1");
  const copiedCommonScript = path.join(scriptsDir, "bridge-common.ps1");
  fs.copyFileSync(restartScript, copiedRestartScript);
  fs.copyFileSync(commonScript, copiedCommonScript);

  const actionsLog = path.join(tempRoot, "actions.log");
  const auditLog = path.join(tempRoot, "audit.jsonl");
  const stopScript = path.join(scriptsDir, "fake-stop.ps1");
  const startScript = path.join(scriptsDir, "fake-start.ps1");

  fs.writeFileSync(
    stopScript,
    [
      '$log = Join-Path (Split-Path -Parent $PSScriptRoot) "actions.log"',
      '"stop" | Add-Content -Path $log -Encoding utf8',
    ].join("\r\n"),
    "utf8",
  );

  fs.writeFileSync(
    startScript,
    [
      "param([switch]$SkipBuild)",
      '$log = Join-Path (Split-Path -Parent $PSScriptRoot) "actions.log"',
      '"start skipBuild=$($SkipBuild.IsPresent)" | Add-Content -Path $log -Encoding utf8',
    ].join("\r\n"),
    "utf8",
  );

  const env = {
    ...process.env,
    CAB_BRIDGE_STOP_SCRIPT: stopScript,
    CAB_BRIDGE_START_SCRIPT: startScript,
    CAB_AUDIT_LOG_FILE: auditLog,
  };

  try {
    await runPowershellScript(copiedRestartScript, ["-Build"], env);
  } catch (error) {
    if (skipIfPowerShellSpawnBlocked(t, error)) {
      return;
    }
    throw error;
  }

  const actions = readActionsLog(actionsLog);
  assert.deepEqual(actions, ["stop", "start skipBuild=False"]);
  const auditEntries = readAuditLog(auditLog);
  assert.equal(auditEntries.some((entry) => entry.type === "service.restart.requested"), true);
  assert.equal(auditEntries.some((entry) => entry.type === "service.restart.completed"), true);
});
