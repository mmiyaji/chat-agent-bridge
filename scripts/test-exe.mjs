import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import net from "node:net";
import { fileURLToPath } from "node:url";
import { spawn, execFileSync } from "node:child_process";
import { setTimeout as delay } from "node:timers/promises";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const pkg = JSON.parse(await fs.readFile(path.join(root, "package.json"), "utf8"));
const parent = path.resolve(process.env.CAB_EXE_TEST_ROOT ?? os.tmpdir());
await fs.mkdir(parent, { recursive: true });
const scratch = await fs.mkdtemp(path.join(parent, "cab-exe-test-"));
const install = path.join(scratch, "standalone install");
const data = path.join(scratch, "application data");
await fs.mkdir(install);
const exe = path.join(install, "ChatAgentBridge.exe");
await fs.copyFile(path.join(process.env.CAB_RELEASE_DIR ?? path.join(root, "release"), "ChatAgentBridge.exe"), exe);
const env = Object.fromEntries(Object.entries(process.env).filter(([key]) =>
  !/^(CAB_|SLACK_|MATTERMOST_|CODEX_|CLAUDE_|GEMINI_|NODE_|PATH$)/i.test(key)));
// The child gets no Node.js, npm, agent CLI, repository, or node_modules on PATH.
env.PATH = path.join(process.env.SystemRoot ?? "C:\\Windows", "System32");
env.CAB_AUTO_CONNECT = "0";
env.CAB_ADMIN_HOST = "127.0.0.1";
const freePort = async () => {
  const server = net.createServer();
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(0, "127.0.0.1", resolve); });
  const port = server.address().port;
  await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  return port;
};
env.CAB_ADMIN_PORT = String(await freePort());
env.CAB_PORT = String(await freePort());
const base = `http://127.0.0.1:${env.CAB_ADMIN_PORT}`;
const until = async (condition, label) => {
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    if (await condition()) return;
    await delay(150);
  }
  throw new Error(`Timed out: ${label}`);
};
const audit = async () => {
  try { return (await fs.readFile(path.join(data, "audit.jsonl"), "utf8")).trim().split("\n").map(line => JSON.parse(line)); }
  catch (error) { if (error.code === "ENOENT") return []; throw error; }
};
const healthy = async () => {
  try { return (await fetch(base + "/admin/healthz", { signal: AbortSignal.timeout(1000) })).ok; }
  catch { return false; }
};
let child;
let output = "";
try {
  assert.equal(execFileSync(exe, ["--version"], { cwd: install, env, encoding: "utf8", timeout: 10_000 }).trim(), pkg.version);
  assert.match(execFileSync(exe, ["--help"], { cwd: install, env, encoding: "utf8", timeout: 10_000 }), /--data-dir/);
  child = spawn(exe, ["--data-dir", data], { cwd: install, env, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
  child.stdout.on("data", chunk => { output += chunk; });
  child.stderr.on("data", chunk => { output += chunk; });
  child.on("error", error => { output += error.message; });
  await until(healthy, "initial startup");
  await until(() => output.includes(`Control center: ${base}/admin`), "visible control center URL");
  assert.equal((await audit()).some(record => record.type === "admin.browser.launch-requested"), false);
  assert.match(await (await fetch(base + "/admin")).text(), /コントロールセンター/);
  const connections = await (await fetch(base + "/admin/api/connections")).json();
  assert.equal(connections.agent.state, "disconnected");
  assert.equal(connections.chat.state, "disconnected");
  const settings = (await (await fetch(base + "/admin/api/settings")).json()).settings;
  assert.equal(path.resolve(settings.settingsFile), path.join(data, "admin-settings.json"));
  const workspace = path.join(data, "workspace");
  assert.equal(path.resolve(settings.defaultWorkingDirectory), workspace);
  if (process.argv.includes("--browser=chrome")) {
    const { chromium } = await import("@playwright/test");
    // Playwright uses a temporary profile, leaving the user's Chrome session alone.
    const browser = await chromium.launch({ channel: "chrome", headless: true });
    const errors = [];
    try {
      const page = await browser.newPage();
      page.on("crash", () => errors.push("Chrome page crashed"));
      page.on("pageerror", error => errors.push(error.message));
      await page.goto(base + "/admin");
      await page.getByTestId("stat-sessions").filter({ hasText: "0" }).waitFor();
      await page.getByRole("button", { name: "接続と設定", exact: true }).click();
      await page.getByTestId("approval-policy").waitFor();
      await page.getByRole("button", { name: "概要", exact: true }).click();
      // Exercise two automatic refreshes on the page served by the actual exe.
      await delay(21_000);
      assert.equal(browser.isConnected(), true);
      assert.equal(await page.getByTestId("stat-sessions").textContent(), "0");
      assert.deepEqual(errors, []);
      console.log("PASS: installed Chrome with a temporary profile rendered the exe UI and refreshed without a crash or script error.");
    } finally { await browser.close(); }
  }
  const saved = await fetch(base + "/admin/api/settings", {
    method: "POST", headers: { "content-type": "application/json", "x-cab-request": "admin" },
    body: JSON.stringify({ codexModel: "packaging-persistence-check", defaultWorkingDirectory: workspace }),
  });
  assert.equal(saved.status, 200, await saved.text());
  const restarting = await fetch(base + "/admin/api/bridge/restart", { method: "POST", headers: { "x-cab-request": "admin" } });
  assert.equal(restarting.status, 202, await restarting.text());
  await until(async () => (await audit()).some(record => record.type === "bridge.process.start" && record.pid !== child.pid), "executable restart");
  await until(() => child.exitCode !== null || child.signalCode !== null, "old process exit");
  await until(healthy, "restart health");
  const restored = (await (await fetch(base + "/admin/api/settings")).json()).settings;
  assert.equal(restored.codexModel, "packaging-persistence-check");
  assert.equal(path.resolve(restored.settingsFile), path.join(data, "admin-settings.json"));
  assert.equal(path.resolve(restored.defaultWorkingDirectory), workspace);
  assert.deepEqual(await fs.readdir(install), ["ChatAgentBridge.exe"]);
  assert.equal((await audit()).some(record => record.type === "admin.browser.launch-requested"), false);
  console.log("PASS: standalone exe, no automatic browser launch, visible URL, no Node.js on PATH, Japanese UI, settings persistence, executable restart.");
} catch (error) {
  console.error(output);
  throw error;
} finally {
  // Only terminate processes recorded by this isolated test run.
  const pids = new Set((await audit()).filter(record => record.type === "bridge.process.start").map(record => record.pid));
  if (child?.pid) pids.add(child.pid);
  for (const pid of pids) {
    try { process.kill(pid); } catch (error) { if (error.code !== "ESRCH") console.error(error.message); }
  }
  await delay(500);
  // Validate the absolute cleanup target remains inside the chosen scratch parent.
  if (path.dirname(scratch) !== parent || !path.basename(scratch).startsWith("cab-exe-test-")) throw new Error("Unsafe scratch directory");
  await fs.rm(scratch, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 });
}
