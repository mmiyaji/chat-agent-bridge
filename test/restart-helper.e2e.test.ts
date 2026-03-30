import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import http from "node:http";
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

async function listen(server: http.Server): Promise<number> {
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => resolve());
  });
  const address = server.address();
  if (!address || typeof address === "string") {
    throw new Error("Failed to bind test server.");
  }
  return address.port;
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
      'param([switch]$SkipBuild)',
      '$log = Join-Path (Split-Path -Parent $PSScriptRoot) "actions.log"',
      '"start skipBuild=$($SkipBuild.IsPresent)" | Add-Content -Path $log -Encoding utf8',
    ].join("\r\n"),
    "utf8",
  );

  const slackBodies: string[] = [];
  const slackServer = http.createServer(async (request, response) => {
    if (request.method !== "POST" || request.url !== "/api/chat.postMessage") {
      response.statusCode = 404;
      response.end();
      return;
    }

    const chunks: Buffer[] = [];
    for await (const chunk of request) {
      chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
    }
    slackBodies.push(Buffer.concat(chunks).toString("utf8"));
    response.setHeader("content-type", "application/json; charset=utf-8");
    response.end(JSON.stringify({ ok: true, ts: "123.456" }));
  });

  const adminServer = http.createServer((request, response) => {
    if (request.url === "/admin/healthz") {
      response.setHeader("content-type", "application/json; charset=utf-8");
      response.end(JSON.stringify({ ok: true }));
      return;
    }

    response.statusCode = 404;
    response.end();
  });

  const slackPort = await listen(slackServer);
  const adminPort = await listen(adminServer);

  const env = {
    ...process.env,
    CAB_BRIDGE_STOP_SCRIPT: stopScript,
    CAB_BRIDGE_START_SCRIPT: startScript,
    CAB_SLACK_API_BASE_URL: `http://127.0.0.1:${slackPort}`,
    CAB_ADMIN_PORT: String(adminPort),
    SLACK_BOT_TOKEN: "xoxb-test",
  };

  try {
    await runPowershellScript(
      copiedHelperScript,
      ["-ChannelId", "C123", "-ThreadId", "T123"],
      env,
    );
  } finally {
    await Promise.all([
      new Promise<void>((resolve, reject) => slackServer.close((error) => (error ? reject(error) : resolve()))),
      new Promise<void>((resolve, reject) => adminServer.close((error) => (error ? reject(error) : resolve()))),
    ]);
  }

  const actions = fs.readFileSync(actionsLog, "utf8");
  assert.match(actions, /stop/);
  assert.match(actions, /start skipBuild=True/);
  assert.equal(slackBodies.length, 1);
  const payload = JSON.parse(slackBodies[0] ?? "{}") as {
    channel?: string;
    thread_ts?: string;
    text?: string;
  };
  assert.equal(payload.channel, "C123");
  assert.equal(payload.thread_ts, "T123");
  assert.match(payload.text ?? "", /Bridge restart completed/);
});

