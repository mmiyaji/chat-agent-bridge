# Installation

## Prerequisites

- Windows with PowerShell
- Node.js 20 or later
- npm
- Local agent CLI
  - Codex CLI for `CAB_AGENT=codex`
  - Claude Code CLI for `CAB_AGENT=claude-code`
  - Cursor CLI for `CAB_AGENT=cursor`
  - Gemini CLI for `CAB_AGENT=gemini`

The bridge assumes the selected local agent CLI is already installed and authenticated before execution. It does not guide or perform interactive login flows for you.

Before using an agent through the bridge, verify that the corresponding CLI can already run successfully in the same machine and user session in non-interactive mode.

## Install dependencies

```powershell
npm install
npm run check
```

## Minimum configuration

At a minimum, set the allowed repositories and choose a default working directory.

```powershell
$env:CAB_ALLOWED_DIRS="$env:USERPROFILE\Documents\git"
$env:CAB_DEFAULT_WORKDIR="$env:USERPROFILE\Documents\git\chat-agent-bridge"
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
| `CAB_AGENT` | no | `codex`, `claude-code`, `cursor`, or `gemini`. Defaults to `codex` |
| `CAB_DEFAULT_APPROVAL_POLICY` | no | `manual`, `on-failure`, or `never` |
| `CAB_NETWORK_ACCESS` | no | `enabled` or `disabled` |
| `CAB_ADMIN_HOST` | no | Admin server bind host. Defaults to `CAB_HOST` |
| `CAB_ADMIN_PORT` | no | Admin server port. Defaults to `8788` |
| `CAB_AUDIT_LOG_FILE` | no | Audit JSONL path. Defaults to `./data/audit.jsonl` |
| `CAB_SLACK_ALLOWED_USER_IDS` | no | Optional Slack user-ID allowlist for bot-facing operation. Accepts newline-, comma-, or semicolon-separated IDs |
| `CAB_MATTERMOST_ALLOWED_USER_IDS` | no | Optional Mattermost user-ID allowlist for `bot` mode. Accepts newline-, comma-, or semicolon-separated IDs |
| `CAB_REPO_ALIASES` | no | Optional `alias=path;alias2=path2` mapping |
| `CAB_DISCOVER_REPOS` | no | When `true`, also discover direct child Git repos under each allowed root and expose them as `repo` aliases |

`CAB_ALLOWED_DIRS` and the resulting repo aliases also drive the first-mention working-directory flow: an alias or absolute path in the first message of a thread must resolve under `CAB_ALLOWED_DIRS` to be accepted, and the numbered directory picker offered when no directory is given lists these same aliases.

## Agent-specific environment variables

### Codex

| Name | Required | Description |
| --- | --- | --- |
| `CAB_CODEX_MODEL` | no | Model passed to `codex exec --model`. Defaults to `gpt-5.4` |
| `CAB_CODEX_COMMAND_TIMEOUT_MS` | no | Idle timeout for a single `codex exec` invocation |
| `CAB_CODEX_MAX_RUNTIME_MS` | no | Hard maximum runtime for a single `codex exec` invocation |
| `CAB_CODEX_SESSION_INDEX_FILE` | no | Local Codex session index path. Defaults to a file under the current user's home directory |

The bridge uses the locally installed Codex CLI and does not require `OPENAI_API_KEY` when the local CLI is already logged in.

### Claude Code

| Name | Required | Description |
| --- | --- | --- |
| `CAB_CLAUDE_MODEL` | no | Model passed to `claude --model`. Defaults to `sonnet` |

Claude Code must already be installed and authenticated locally before the bridge can execute it.

### Cursor

| Variable | Required | Description |
| --- | --- | --- |
| `CAB_CURSOR_COMMAND` | no | Command used for Cursor execution. Defaults to `cursor-agent --trust` |
| `CAB_CURSOR_MODEL` | no | Model passed to Cursor as `--model`. Defaults to `Auto` |
| `CAB_CURSOR_COMMAND_TIMEOUT_MS` | no | Idle timeout for a single Cursor invocation |
| `CAB_CURSOR_MAX_RUNTIME_MS` | no | Hard maximum runtime for a single Cursor invocation |

Cursor runtime settings can also be adjusted later from the Admin UI. This is useful when Cursor needs a wrapper script or a machine-specific CLI command.

The default command includes `--trust` so Cursor can run in repositories that require workspace trust.
The default model is `Auto`, which is safer for plans that cannot use named models.
Cursor must already be installed and authenticated locally before the bridge can execute it.

If you need a different wrapper, you can still override `CAB_CURSOR_COMMAND` or save another value in the Admin UI.

### Gemini

| Variable | Required | Description |
| --- | --- | --- |
| `CAB_GEMINI_COMMAND` | no | Command used for Gemini execution. Defaults to `gemini --approval-mode yolo` |
| `CAB_GEMINI_MODEL` | no | Optional Gemini model name. Omit it to use the CLI default |
| `CAB_GEMINI_COMMAND_TIMEOUT_MS` | no | Idle timeout for a single Gemini invocation |
| `CAB_GEMINI_MAX_RUNTIME_MS` | no | Hard maximum runtime for a single Gemini invocation |

Gemini runtime settings can also be changed later from the Admin UI.

Gemini must already be installed and authenticated locally before the bridge can execute it.

### Claude runtime

| Variable | Required | Description |
| --- | --- | --- |
| `CAB_CLAUDE_COMMAND_TIMEOUT_MS` | no | Idle timeout for a single Claude Code invocation |
| `CAB_CLAUDE_MAX_RUNTIME_MS` | no | Hard maximum runtime for a single Claude Code invocation |
| `CAB_CLAUDE_PROJECTS_DIR` | no | Root of the real Claude Code project session store used for resume candidates. Defaults to `~/.claude/projects` |
| `CAB_CLAUDE_SESSION_INDEX_FILE` | no | Legacy Claude Code session index path, used only as a fallback when a working directory has no sessions under `CAB_CLAUDE_PROJECTS_DIR`. Defaults to a file under the current user's home directory |

## Next steps

- Slack setup: [slack-setup.md](slack-setup.md)
- Mattermost setup: [mattermost-setup.md](mattermost-setup.md)
- Operations: [operations.md](operations.md)
- Testing: [testing.md](testing.md)

When the allowlist variables are set, the bridge silently ignores messages from users outside the list and records the rejection in the audit log.
