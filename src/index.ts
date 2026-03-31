import { createAppConfig } from "./config/env.js";
import { SlackAdapter } from "./adapters/slack/slack-adapter.js";
import { MattermostAdapter } from "./adapters/mattermost/mattermost-adapter.js";
import { AgentRegistry } from "./agents/base.js";
import { CodexAgent } from "./agents/codex/codex-agent.js";
import { ClaudeCodeAgent } from "./agents/claude/claude-agent.js";
import { BridgeService } from "./core/bridge-service.js";
import { SessionManager } from "./core/session-manager.js";
import { InMemoryJobQueue } from "./core/job-queue.js";
import { FileStateStore } from "./state/file-state-store.js";
import { FileAuditLogger } from "./core/audit.js";
import { registerProcessAuditHooks, writeProcessAudit } from "./core/process-audit.js";
import { AdminServer } from "./admin/server.js";

async function main(): Promise<void> {
  const config = createAppConfig(process.env);
  registerProcessAuditHooks(config);
  const store = new FileStateStore(config.stateFile);
  const auditLogger = new FileAuditLogger(config.auditLogFile);
  const sessionManager = new SessionManager(store);
  const jobQueue = new InMemoryJobQueue();
  const chatAdapter =
    config.chatProvider === "mattermost"
      ? new MattermostAdapter(config, auditLogger)
      : new SlackAdapter(config, auditLogger);
  const agents: AgentRegistry = {
    codex: new CodexAgent(config),
    "claude-code": new ClaudeCodeAgent(config),
  };

  const bridge = new BridgeService({
    config,
    stateStore: store,
    sessionManager,
    jobQueue,
    chatAdapter,
    agents,
    auditLogger,
  });

  const adminServer = new AdminServer({
    config,
    stateStore: store,
    auditLogger,
    bridge,
  });

  await bridge.start();
  await adminServer.start();
}

main().catch((error: unknown) => {
  console.error("Failed to start Chat Agent Bridge.", error);
  const config = createAppConfig(process.env);
  writeProcessAudit(config.auditLogFile, "bridge.start.failed", {
    pid: process.pid,
    message: error instanceof Error ? error.message : String(error),
    stack: error instanceof Error ? error.stack : undefined,
  });
  process.exitCode = 1;
});
