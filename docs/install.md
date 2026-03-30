# Installation

## Prerequisites

- Windows with PowerShell
- Node.js 20 or later
- npm
- Local agent CLI
  - Codex CLI for `CAB_AGENT=codex`
  - Claude Code CLI for `CAB_AGENT=claude-code`

## Install dependencies

```powershell
npm install
npm run check
```

## Minimum configuration

At a minimum, set the allowed repositories and choose a default working directory.

```powershell
$env:CAB_ALLOWED_DIRS="C:\Users\mail\Documents\git"
$env:CAB_DEFAULT_WORKDIR="C:\Users\mail\Documents\git\chat-agent-bridge"
```

Optional but commonly useful:

```powershell
$env:CAB_AGENT="codex"
$env:CAB_CHAT_PROVIDER="slack"
$env:CAB_DEFAULT_APPROVAL_POLICY="manual"
$env:CAB_NETWORK_ACCESS="enabled"
```

## Development mode

Use this while iterating locally in the current shell.

```powershell
npm run dev
```

## Managed service mode

Use this when the bridge should keep running independently from the current shell.

```powershell
npm run service:start
npm run service:status
```

Service mode stores runtime files under `data/runtime/`.

- PID: `data/runtime/bridge.pid`
- stdout: `data/runtime/bridge.stdout.log`
- stderr: `data/runtime/bridge.stderr.log`

## Core environment variables

| Name | Required | Description |
| --- | --- | --- |
| `CAB_ALLOWED_DIRS` | yes | Semicolon-separated list of allowed repository roots |
| `CAB_DEFAULT_WORKDIR` | no | Default working directory. Defaults to the first allowed directory |
| `CAB_CHAT_PROVIDER` | no | `slack` or `mattermost`. Defaults to `slack` |
| `CAB_AGENT` | no | `codex` or `claude-code`. Defaults to `codex` |
| `CAB_DEFAULT_APPROVAL_POLICY` | no | `manual`, `on-failure`, or `never` |
| `CAB_NETWORK_ACCESS` | no | `enabled` or `disabled` |
| `CAB_ADMIN_HOST` | no | Admin server bind host. Defaults to `CAB_HOST` |
| `CAB_ADMIN_PORT` | no | Admin server port. Defaults to `8788` |
| `CAB_AUDIT_LOG_FILE` | no | Audit JSONL path. Defaults to `./data/audit.jsonl` |
| `CAB_REPO_ALIASES` | no | Optional `alias=path;alias2=path2` mapping |

## Agent-specific environment variables

### Codex

| Name | Required | Description |
| --- | --- | --- |
| `CAB_CODEX_MODEL` | no | Model passed to `codex exec --model`. Defaults to `gpt-5.4` |
| `CAB_CODEX_COMMAND_TIMEOUT_MS` | no | Idle timeout for a single `codex exec` invocation |
| `CAB_CODEX_MAX_RUNTIME_MS` | no | Hard maximum runtime for a single `codex exec` invocation |
| `CAB_CODEX_SESSION_INDEX_FILE` | no | Local Codex session index path |

The bridge uses the locally installed Codex CLI and does not require `OPENAI_API_KEY` when the local CLI is already logged in.

### Claude Code

| Name | Required | Description |
| --- | --- | --- |
| `CAB_CLAUDE_MODEL` | no | Model passed to `claude --model`. Defaults to `sonnet` |
| `CAB_CLAUDE_COMMAND_TIMEOUT_MS` | no | Idle timeout for a single Claude Code invocation |
| `CAB_CLAUDE_MAX_RUNTIME_MS` | no | Hard maximum runtime for a single Claude Code invocation |
| `CAB_CLAUDE_SESSION_INDEX_FILE` | no | Local Claude Code session index path |

## Next steps

- Slack setup: [docs/slack-setup.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/slack-setup.md)
- Mattermost setup: [docs/mattermost-setup.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/mattermost-setup.md)
- Operations: [docs/operations.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/operations.md)
- Testing: [docs/testing.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/testing.md)
