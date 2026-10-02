import { spawn } from "node:child_process";
import { createAppConfig } from "./config/env.js";
import { logger } from "./core/logger.js";
import { SlackAdapter } from "./adapters/slack/slack-adapter.js";
import { MattermostAdapter } from "./adapters/mattermost/mattermost-adapter.js";
import { AgentRegistry } from "./agents/base.js";
import { CodexAppServerAgent } from "./agents/codex/app-server-agent.js";
import { ConnectionManager } from "./core/connection-manager.js";
import { resolveCommand } from "./core/command-resolution.js";
import { ClaudeCodeAgent } from "./agents/claude/claude-agent.js";
import { CursorAgent } from "./agents/cursor/cursor-agent.js";
import { GeminiAgent } from "./agents/gemini/gemini-agent.js";
import { BridgeService } from "./core/bridge-service.js";
import { SessionManager } from "./core/session-manager.js";
import { InMemoryJobQueue } from "./core/job-queue.js";
import { FileStateStore } from "./state/file-state-store.js";
import { FileAuditLogger } from "./core/audit.js";
import { registerProcessAuditHooks, writeProcessAudit } from "./core/process-audit.js";
import { AdminServer } from "./admin/server.js";

export async function main(options: {
  launchDetachedRestart?: NonNullable<ConstructorParameters<typeof BridgeService>[0]["launchDetachedRestart"]>;
} = {}): Promise<{ stop: () => Promise<void> }> {
  const config = createAppConfig(process.env);
  registerProcessAuditHooks(config);
  const store = new FileStateStore(config.stateFile);
  const auditLogger = new FileAuditLogger(config.auditLogFile);
  const sessionManager = new SessionManager(store);
  const jobQueue = new InMemoryJobQueue();
  const codex = new CodexAppServerAgent(config);
  const agents: AgentRegistry = {
    codex,
    "claude-code": new ClaudeCodeAgent(config),
    cursor: new CursorAgent(config),
    gemini: new GeminiAgent(config),
  };
  const connections = new ConnectionManager(config, agents, (settings) =>
    settings.chatProvider === "mattermost"
      ? new MattermostAdapter(settings, auditLogger)
      : new SlackAdapter(settings, auditLogger), async (kind) => {
    if (kind === "codex") return await codex.probe();
    const command = kind === "claude-code" ? "claude" : kind === "cursor" ? (config.cursorCommand ?? "cursor-agent") : (config.geminiCommand ?? "gemini");
    if (resolveCommand(command, config.defaultWorkingDirectory).status !== "resolved") throw new Error(`${command} が見つかりません。CLIをインストールしてください。`);
    return { statusText: "CLIを検出しました。認証は初回実行時に確認します。" };
  });

  const bridge = new BridgeService({
    config,
    stateStore: store,
    sessionManager,
    jobQueue,
    chatAdapter: connections,
    agents: connections.agents,
    auditLogger,
    ...options,
  });

  const adminServer = new AdminServer({
    config,
    stateStore: store,
    auditLogger,
    bridge,
    connections,
  });

  await bridge.start();
  await adminServer.start();

  let stopping = false;
  // The control center remains available even when credentials or services fail.
  if (process.env.CAB_AUTO_CONNECT !== "0") {
    void (async () => {
      try { await connections.connectAgent(); }
      catch (error) { logger.warn("Agent connection needs attention.", { error: error instanceof Error ? error.message : String(error) }); }
      if (stopping) return;
      if (config.chatProvider === "slack" ? config.slackBotToken : config.mattermostBotToken) {
        try { await connections.connectChat(); }
        catch (error) { logger.warn("Chat connection needs attention.", { error: error instanceof Error ? error.message : String(error) }); }
      }
    })();
  }

  const stop = async () => {
    if (stopping) return;
    stopping = true;
    await bridge.stop();
    await adminServer.stop();
  };
  const shutdown = () => {
    void stop().catch((error) => {
      logger.error("Shutdown failed.", { error: String(error) }); process.exitCode = 1;
    });
  };
  process.once("SIGINT", shutdown);
  process.once("SIGTERM", shutdown);

  const adminHost = config.adminHost === "0.0.0.0" || config.adminHost === "::"
    ? "127.0.0.1" : (config.adminHost ?? "127.0.0.1");
  const url = `http://${adminHost.includes(":") ? `[${adminHost}]` : adminHost}:${adminServer.getListeningPort()}/admin`;
  console.log(`\n管理画面 / Control center: ${url}\nこのURLをお使いのブラウザーで開いてください。\n`);
  if (process.env.CAB_OPEN_ADMIN === "1") {
    const failed = (error: unknown) => {
      logger.warn("Browser launch failed. Open the control center URL manually.", { url, error: String(error) });
      writeProcessAudit(config.auditLogFile, "admin.browser.launch-failed", { url, error: String(error) });
    };
    try {
      const browser = spawn("cmd", ["/d", "/c", "start", "", url], { stdio: "ignore", detached: true, windowsHide: true });
      browser.once("error", failed);
      browser.once("exit", (code) => { if (code !== null && code !== 0) failed(`Launcher exited with code ${code}`); });
      browser.unref();
      writeProcessAudit(config.auditLogFile, "admin.browser.launch-requested", { url });
    } catch (error) { failed(error); }
  }
  return { stop };
}

export function reportStartupFailure(error: unknown): void {
  logger.error("Failed to start Chat Agent Bridge.", { error: error instanceof Error ? error.message : String(error) });
  writeProcessAudit(process.env.CAB_AUDIT_LOG_FILE ?? "./data/audit.jsonl", "bridge.start.failed", {
    pid: process.pid,
    message: error instanceof Error ? error.message : String(error),
    stack: error instanceof Error ? error.stack : undefined,
  });
  process.exitCode = 1;
}
