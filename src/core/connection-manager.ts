import type { Agent, AgentRegistry } from "../agents/base.js";
import type { ChatAdapter, ChatConnectionStatus, ChatMessageHandler, ChatMessageOptions } from "../adapters/chat.js";
import type { AgentKind, AppConfig, ChatThreadRef } from "./types.js";

export interface AgentConnectionStatus {
  kind: AgentKind;
  state: "disconnected" | "connecting" | "connected" | "error";
  statusText: string;
  model?: string;
  authenticated?: boolean;
  lastError?: string;
}

/** Owns transport lifetime. A configuration save never silently replaces a live transport. */
export class ConnectionManager implements ChatAdapter {
  readonly agents: AgentRegistry = {};
  private adapter: ChatAdapter | undefined;
  private handler: ChatMessageHandler | undefined;
  private changing = false;
  private activeMessages = 0;
  private activeRuns = 0;
  private enabledAgent: AgentKind | undefined;
  private agentStatus: AgentConnectionStatus;
  private chatError: string | undefined;
  private stopped = false;

  constructor(
    private readonly config: AppConfig,
    private readonly sources: AgentRegistry,
    private readonly factory: (config: AppConfig) => ChatAdapter,
    private readonly probeAgent: (kind: AgentKind) => Promise<Partial<AgentConnectionStatus>>,
  ) {
    this.agentStatus = { kind: config.agentKind, state: "disconnected", statusText: "エージェントを接続してください。" };
    for (const [kind, agent] of Object.entries(sources)) {
      if (!agent) continue;
      this.agents[kind as AgentKind] = {
        kind: agent.kind,
        capabilities: agent.capabilities,
        execute: async (request) => {
          if (this.enabledAgent !== agent.kind) throw new Error("エージェントが未接続です。管理画面でこのエージェントを接続してください。");
          this.activeRuns++;
          try { return await agent.execute(request); }
          finally { this.activeRuns--; }
        },
        cancelExecution: (id) => agent.cancelExecution(id),
      };
    }
  }

  get busy(): boolean { return this.changing || this.activeMessages > 0 || this.activeRuns > 0; }
  snapshot() { return { chat: this.getConnectionStatus(), agent: { ...this.agentStatus }, busy: this.busy }; }
  async start(handler: ChatMessageHandler): Promise<void> { this.handler = handler; this.stopped = false; }

  private async change(action: () => Promise<void>): Promise<void> {
    if (this.stopped) throw new Error("Bridge is stopped.");
    if (this.busy) throw new Error("実行中または接続処理中です。完了後に再試行してください。");
    this.changing = true;
    try { await action(); } finally { this.changing = false; }
  }

  async connectChat(): Promise<void> {
    await this.change(async () => {
      if (!this.handler) throw new Error("Bridge is not initialized.");
      this.validateChat();
      await this.adapter?.stop?.();
      this.adapter = undefined;
      this.chatError = undefined;
      const adapter = this.factory({ ...this.config });
      this.adapter = adapter;
      try {
        await adapter.start(async (message) => {
          if (this.changing || this.stopped) return;
          this.activeMessages++;
          try { await this.handler!(message); } finally { this.activeMessages--; }
        });
        if (this.stopped) { await adapter.stop?.(); this.adapter = undefined; }
      } catch (error) {
        await adapter.stop?.();
        this.adapter = undefined;
        this.chatError = error instanceof Error ? error.message : String(error);
        throw error;
      }
    });
  }

  async disconnectChat(): Promise<void> {
    await this.change(async () => { await this.adapter?.stop?.(); this.adapter = undefined; this.chatError = undefined; });
  }

  async connectAgent(): Promise<void> {
    await this.change(async () => {
      const kind = this.config.agentKind;
      this.enabledAgent = undefined;
      await this.disposeAgents();
      this.agentStatus = { kind, state: "connecting", statusText: "接続を確認しています…" };
      try {
        if (!this.sources[kind]) throw new Error("Agent is not available.");
        const info = await this.probeAgent(kind);
        if (this.stopped) { await this.disposeAgents(); return; }
        if (info.authenticated === false) throw new Error("認証されていません。ローカルのCLIでログインして再接続してください。");
        this.enabledAgent = kind;
        this.agentStatus = { ...info, kind, state: "connected", statusText: info.statusText ?? "接続済み。チャットから実行できます。" };
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        this.agentStatus = { kind, state: "error", statusText: message, lastError: message };
        await this.disposeAgents();
        throw error;
      }
    });
  }

  async disconnectAgent(): Promise<void> {
    await this.change(async () => {
      this.enabledAgent = undefined;
      await this.disposeAgents();
      this.agentStatus = { kind: this.config.agentKind, state: "disconnected", statusText: "エージェントは切断されています。" };
    });
  }

  private async disposeAgents(): Promise<void> {
    for (const agent of Object.values(this.sources)) await (agent as Agent & { dispose?: () => void | Promise<void> })?.dispose?.();
  }

  async stop(): Promise<void> {
    this.stopped = true;
    this.enabledAgent = undefined;
    this.agentStatus = { kind: this.config.agentKind, state: "disconnected", statusText: "Bridge is stopped." };
    await this.adapter?.stop?.();
    this.adapter = undefined;
    await this.disposeAgents();
  }

  getConnectionStatus(): ChatConnectionStatus {
    return this.adapter?.getConnectionStatus?.() ?? {
      provider: this.config.chatProvider,
      mode: this.config.chatProvider === "slack" ? this.config.slackMode : this.config.mattermostMode,
      state: "disconnected", connected: false,
      statusText: this.chatError ?? "チャットを接続してください。",
      ...(this.chatError ? { lastError: this.chatError } : {}),
    };
  }

  private validateChat(): void {
    const c = this.config;
    if (c.chatProvider === "mattermost") {
      if (!c.mattermostServerUrl || !c.mattermostBotToken) throw new Error("MattermostのURLとトークンを保存してください。");
      if (!/^https?:$/.test(new URL(c.mattermostServerUrl).protocol)) throw new Error("Mattermost URL must use HTTP or HTTPS.");
    } else {
      if (!c.slackBotToken || (c.slackMode === "socket" ? !c.slackAppToken : !c.slackSigningSecret)) throw new Error("Slackの接続に必要なトークンを保存してください。");
    }
  }

  private forThread(thread?: ChatThreadRef): ChatAdapter {
    if (!this.adapter || (thread && thread.provider !== this.getConnectionStatus().provider)) throw new Error("Chat transport is disconnected or belongs to another provider.");
    return this.adapter;
  }
  startNewThread(channelId: string, text: string, options?: ChatMessageOptions) { return this.forThread().startNewThread(channelId, text, options); }
  postThreadMessage(thread: ChatThreadRef, text: string, options?: ChatMessageOptions) { return this.forThread(thread).postThreadMessage(thread, text, options); }
  updateThreadMessage(thread: ChatThreadRef, id: string, text: string, options?: ChatMessageOptions) { return this.forThread(thread).updateThreadMessage(thread, id, text, options); }
}
