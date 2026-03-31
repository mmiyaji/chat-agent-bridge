import { ChatThreadRef, IncomingChatMessage } from "../core/types.js";

export type ChatMessageHandler = (message: IncomingChatMessage) => Promise<void>;

export interface ChatAction {
  id: string;
  label: string;
  value?: string;
  style?: "default" | "primary" | "danger";
}

export interface ChatConnectionStatus {
  provider: ChatThreadRef["provider"];
  mode: string;
  state: "connected" | "connecting" | "disconnected" | "listening";
  connected: boolean;
  statusText: string;
  lastChangedAt?: string;
  lastConnectedAt?: string;
  lastDisconnectedAt?: string;
  lastRecoveredAt?: string;
  lastOutageDurationMs?: number;
  reconnectCount?: number;
  disconnectCount?: number;
  lastError?: string;
  lastErrorAt?: string;
}

export interface ChatMessageOptions {
  blocks?: unknown[];
  actions?: ChatAction[];
}

export interface ChatAdapter {
  start(handler: ChatMessageHandler): Promise<void>;
  stop?(): Promise<void>;
  getConnectionStatus?(): ChatConnectionStatus | undefined;
  startNewThread(
    channelId: string,
    text: string,
    options?: ChatMessageOptions,
  ): Promise<ChatThreadRef | undefined>;
  postThreadMessage(
    thread: ChatThreadRef,
    text: string,
    options?: ChatMessageOptions,
  ): Promise<string | undefined>;
  updateThreadMessage(
    thread: ChatThreadRef,
    messageId: string,
    text: string,
    options?: ChatMessageOptions,
  ): Promise<void>;
}
