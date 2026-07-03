# Architecture

## Core model

The bridge centers on one invariant:

`chat thread -> local agent session`

For the MVP, a thread is identified as:

- `provider`: `slack` or `mattermost`
- `channelId`
- `threadId`

When a chat message arrives, the bridge:

1. normalizes the incoming thread reference
2. loads or creates a session binding
3. on the first instruction of a new thread, resolves a working directory (inline alias/path in the message, a per-channel default, or a numbered picker) before anything else runs
4. for Claude Code, offers to resume a session already known for that working directory before running a fresh one
5. serializes work for that session
6. checks execution policy against the requested repository and command intent
7. forwards the request to the agent adapter
8. returns a status update or summary back to the chat adapter

## Working directory selection

A thread's working directory is decided once, on its first instruction, and then stays pinned to the session:

- if the message starts with a known repo alias or an absolute path under an allowed root, the bridge binds to it immediately and treats the remainder of the message as the instruction
- otherwise, if the channel has a default working directory (`channel-default`), the bridge binds to it silently and continues
- otherwise, the bridge posts a numbered picker over the configured repo aliases and holds the instruction until the user replies with a number or `keep`
- changing directory later with `cwd`/`repo` detaches any stored Claude Code session binding and re-offers the resume picker for the new folder

## Agent session reuse

Codex and Claude Code sessions can be resumed instead of started fresh:

- Codex candidates come from the local Codex session index (`CAB_CODEX_SESSION_INDEX_FILE`)
- Claude Code candidates come from the real Claude Code project store under `CAB_CLAUDE_PROJECTS_DIR` (default `~/.claude/projects`), filtered to `<encoded-cwd>/*.jsonl` where the encoding replaces every non-alphanumeric character in the absolute working directory with `-`. `CAB_CLAUDE_SESSION_INDEX_FILE` remains only as a legacy fallback when no project sessions are found
- Cursor and Gemini always start a fresh bridge-owned session

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
- `mattermost/mattermost-adapter.ts`: Mattermost ingress over local HTTP outgoing webhooks plus REST posting

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
2. Expand Mattermost support beyond outgoing webhooks, especially private-channel/DM ingress and approval interactions.
3. Add a bridge-level approval layer before invoking `codex exec` with write-capable sandbox settings.
4. Implement explicit approval handoff so blocked write actions can be resumed from chat.
5. Persist job history and execution summaries.
6. Add integration tests around thread rebinding, queue ordering, and Codex CLI resume behavior.
