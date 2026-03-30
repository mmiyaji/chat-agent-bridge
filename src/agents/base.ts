import {
  AgentCapabilities,
  AgentExecutionRequest,
  AgentExecutionResult,
  AgentKind,
} from "../core/types.js";

export class AgentExecutionCancelledError extends Error {
  constructor(message = "Execution cancelled.") {
    super(message);
    this.name = "AgentExecutionCancelledError";
  }
}

export interface Agent {
  readonly kind: AgentKind;
  readonly capabilities: AgentCapabilities;
  execute(request: AgentExecutionRequest): Promise<AgentExecutionResult>;
  cancelExecution(sessionId: string): Promise<boolean>;
}
