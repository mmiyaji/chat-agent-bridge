import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawn } from "node:child_process";
import { setTimeout as delay } from "node:timers/promises";

/** Executable installations keep data outside the install folder and working directory. */
export function prepareExecutableEnvironment(env: NodeJS.ProcessEnv, dataDir?: string): string {
  const root = path.resolve(dataDir ?? env.CAB_DATA_DIR ?? path.join(env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local"), "ChatAgentBridge"));
  const workspace = path.join(root, "workspace");
  fs.mkdirSync(workspace, { recursive: true });
  env.CAB_DATA_DIR = root;
  env.CAB_SETTINGS_FILE ??= path.join(root, "admin-settings.json");
  env.CAB_STATE_FILE ??= path.join(root, "state.json");
  env.CAB_AUDIT_LOG_FILE ??= path.join(root, "audit.jsonl");
  env.CAB_RESTART_COMPLETION_FILE ??= path.join(root, "restart-completion.jsonl");
  // Opening the system browser may affect an existing browser session. Opt in.
  env.CAB_OPEN_ADMIN ??= "0";
  // The cwd is only the initial workspace fallback. Saved roots keep precedence.
  process.chdir(workspace);
  return root;
}

export async function waitForPreviousProcess(pid: number, timeoutMs = 30_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try { process.kill(pid, 0); }
    catch (error) { if ((error as NodeJS.ErrnoException).code === "ESRCH") return; throw error; }
    await delay(200);
  }
  throw new Error("Previous bridge process did not stop within 30 seconds.");
}

export function restartExecutable(stop: () => Promise<void>, input: { rebuild?: boolean; channelId?: string; threadId?: string }): void {
  if (input.rebuild) throw new Error("実行ファイル版は再ビルドできません。新しい.exeに置き換えてください。");
  const child = spawn(process.execPath, ["--wait-for-pid", String(process.pid), "--no-browser"], {
    cwd: process.cwd(), env: process.env, detached: true, stdio: "ignore", windowsHide: true,
  });
  child.once("error", (error) => { console.error("Restart failed:", error.message); });
  child.once("spawn", () => {
    child.unref();
    if (input.channelId && input.threadId && process.env.CAB_RESTART_COMPLETION_FILE) {
      fs.appendFileSync(process.env.CAB_RESTART_COMPLETION_FILE, JSON.stringify({ channelId: input.channelId, threadId: input.threadId, startedAt: new Date().toISOString() }) + "\n");
    }
    // Allow the HTTP/chat acknowledgement to finish before closing the transport.
    setTimeout(() => { void stop().then(() => process.exit(0)).catch((error) => { console.error(error); process.exit(1); }); }, 500);
  });
}
