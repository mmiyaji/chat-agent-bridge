import { ChatThreadRef, IncomingChatMessage } from "../core/types.js";

export type ChatMessageHandler = (message: IncomingChatMessage) => Promise<void>;

export interface ChatMessageOptions {
  blocks?: unknown[];
}

export interface ChatAdapter {
  start(handler: ChatMessageHandler): Promise<void>;
  stop?(): Promise<void>;
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
