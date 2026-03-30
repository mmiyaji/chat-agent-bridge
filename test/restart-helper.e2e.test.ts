import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawn } from "node:child_process";

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

test("bridge-restart-detached helper runs stop/start and posts completion", async () => {
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

  await runPowershellScript(
    copiedHelperScript,
    ["-ChannelId", "C123", "-ThreadId", "T123"],
    env,
  );

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
