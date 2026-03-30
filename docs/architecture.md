# Architecture

## Core model

The bridge centers on one invariant:

`chat thread -> local agent session`

For the MVP, a thread is identified as:

- `provider`: currently `slack`
- `channelId`
- `threadId`

When a chat message arrives, the bridge:

1. normalizes the incoming thread reference
2. loads or creates a session binding
3. serializes work for that session
4. checks execution policy against the requested repository and command intent
5. forwards the request to the agent adapter
6. returns a status update or summary back to the chat adapter

## Module split

### `src/core`

- `types.ts`: shared domain types
- `session-manager.ts`: thread/session binding lifecycle
- `job-queue.ts`: one-active-job-per-session guarantee
- `bridge-service.ts`: orchestration of chat, state, policy, and agent execution

### `src/state`

- `file-state-store.ts`: JSON-backed persistence for mappings and session metadata

### `src/agents`

- `base.ts`: generic agent interface
- `codex/codex-agent.ts`: Codex CLI adapter using `codex exec` and `codex exec resume`

### `src/adapters`

- `chat.ts`: generic chat interface
- `slack/slack-adapter.ts`: Slack ingress over either local HTTP events or Socket Mode

### `src/config`

- `env.ts`: environment-driven configuration and policy defaults

## Safety model

The scaffold already enforces the first hard boundary:

- requested working directories must resolve under one of the configured allowed roots
- the Codex CLI is launched with the bridge-selected working directory
- session continuity stays pinned to the bridge-managed thread binding

The policy model is designed to be extended with:

- interactive approval before dangerous operations
- command allow/deny rules
- optional network access suppression
- audit logging

Execution safety should be split across the bridge and the agent adapter:

- the bridge owns thread/session binding, allowed directories, approvals, and audit records
- the agent adapter declares which approval and sandbox capabilities it supports
- execution-specific safety, such as sandbox mode or write reruns, should be delegated to the agent when possible

## Recommended next steps

1. Harden Slack ingress with reconnect backoff, ping telemetry, and Socket Mode coverage.
2. Add a bridge-level approval layer before invoking `codex exec` with write-capable sandbox settings.
3. Implement explicit approval handoff so blocked write actions can be resumed from chat.
4. Persist job history and execution summaries.
5. Add integration tests around thread rebinding, queue ordering, and Codex CLI resume behavior.
