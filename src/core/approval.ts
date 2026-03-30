import { JsonValue } from "./types.js";

export const APPROVAL_KEYWORDS = new Set(["approve", "approved", "承認"]);
export const REJECTION_KEYWORDS = new Set(["reject", "rejected", "deny", "cancel", "却下"]);

export function isApprovalMessage(text: string): boolean {
  const normalized = text.trim().toLowerCase();
  return APPROVAL_KEYWORDS.has(normalized);
}

export function isRejectionMessage(text: string): boolean {
  const normalized = text.trim().toLowerCase();
  return REJECTION_KEYWORDS.has(normalized);
}

export function shouldRequestApproval(summary: string): boolean {
  const patterns = [
    /read-only/i,
    /approval/i,
    /permission denied/i,
    /cannot write/i,
    /can't write/i,
    /blocked/i,
    /sandbox/i,
    /write access/i,
  ];

  return patterns.some((pattern) => pattern.test(summary));
}

export interface PendingApproval {
  prompt: string;
  requestedAt: string;
  reason: string;
  executionId?: string;
}

export function readPendingApproval(
  bridgeState: Record<string, JsonValue> | undefined,
): PendingApproval | undefined {
  const pending = bridgeState?.pendingApproval;
  if (!pending || typeof pending !== "object" || Array.isArray(pending)) {
    return undefined;
  }

  const prompt = pending.prompt;
  const requestedAt = pending.requestedAt;
  const reason = pending.reason;
  const executionId = pending.executionId;

  if (
    typeof prompt !== "string" ||
    typeof requestedAt !== "string" ||
    typeof reason !== "string" ||
    (executionId !== undefined && typeof executionId !== "string")
  ) {
    return undefined;
  }

  return {
    prompt,
    requestedAt,
    reason,
    ...(executionId
      ? {
          executionId,
        }
      : {}),
  };
}
