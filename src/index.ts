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

async function main(): Promise<void> {
  const config = createAppConfig(process.env);
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

  await bridge.start();
}

main().catch((error: unknown) => {
  console.error("Failed to start Chat Agent Bridge.", error);
  process.exitCode = 1;
});
