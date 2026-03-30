# Chat Agent Bridge

Chat Agent Bridge is a local bridge service that lets users continue operating a development agent from chat threads while preserving the local machine context.

## MVP scope

- Chat adapter: Slack or Mattermost
- Agent adapter: Codex or Claude Code
- Session model: `channel + thread_ts -> local agent session`
- Local execution controls: working directory targeting, serial execution, policy checks
- Safety: allowed-directory restrictions, approval policy, command/network policy hooks

## Why this exists

Remote chat UIs are convenient, but they usually lose the state that makes local development productive: uncommitted changes, active branches, local tools, in-progress files, and repository-specific configuration. This project keeps the chat thread bound to a local session so "continue from the last step" becomes natural.

## Current status

This repository currently contains the initial scaffold for the bridge core:

- domain types for chat threads, sessions, jobs, and policies
- a file-backed state store for thread/session mapping
- persistent execution history per bridge session
- an in-process job serializer
- adapter interfaces for chat and agent integrations
- a bootstrap path that wires a Slack or Mattermost adapter to a local agent adapter with session resume support

## Development

```bash
npm install
npm run check
npm run dev
```

Managed service mode:

```bash
npm run service:start
npm run service:status
npm run service:restart
npm run service:stop
```

This mode runs the bridge as a detached Node process, writes a PID file under `data/runtime/bridge.pid`, and stores logs in `data/runtime/`.

## Environment variables

| Name | Required | Description |
| --- | --- | --- |
| `CAB_ALLOWED_DIRS` | yes | Semicolon-separated list of allowed repository roots |
| `CAB_CHAT_PROVIDER` | no | Chat provider: `slack` or `mattermost`. Defaults to `slack` |
| `CAB_AGENT` | no | Default agent for new bridge threads: `codex` or `claude-code`. Defaults to `codex` |
| `CAB_ADMIN_HOST` | no | Admin server bind host. Defaults to `CAB_HOST` and is LAN-reachable when the bridge host is LAN-reachable |
| `CAB_ADMIN_PORT` | no | Admin server port. Defaults to `8788` |
| `CAB_REPO_ALIASES` | no | Optional `alias=path;alias2=path2` mapping used by `repo <alias>` |
| `CAB_DEFAULT_WORKDIR` | no | Default working directory used for incoming Slack events. Defaults to the first allowed directory |
| `CAB_HOST` | no | Bind host for the local HTTP server. Defaults to `0.0.0.0` |
| `CAB_PORT` | no | Local HTTP port for the Slack-style ingress server. Defaults to `8787` |
| `CAB_STATE_FILE` | no | Path to the JSON state file. Defaults to `./data/state.json` |
| `CAB_AUDIT_LOG_FILE` | no | Path to the JSONL audit log. Defaults to `./data/audit.jsonl` |
| `CAB_CODEX_SESSION_INDEX_FILE` | no | Path to the local Codex session index. Defaults to `C:/Users/mail/.codex/session_index.jsonl` |
| `CAB_CLAUDE_SESSION_INDEX_FILE` | no | Path to the local Claude Code session index. Defaults to `C:/Users/mail/.config/claude/sessions/sessions.json` |
| `CAB_DEFAULT_APPROVAL_POLICY` | no | `manual`, `on-failure`, or `never` |
| `CAB_NETWORK_ACCESS` | no | `enabled` or `disabled` |
| `CAB_SLACK_MODE` | no | Slack ingress mode: `http` or `socket`. Defaults to `http` |
| `CAB_CODEX_MODEL` | no | Model passed to `codex exec --model`. Defaults to `gpt-5.4` |
| `CAB_CODEX_COMMAND_TIMEOUT_MS` | no | Idle timeout for each `codex exec` invocation. The timer resets whenever Codex emits output |
| `CAB_CODEX_MAX_RUNTIME_MS` | no | Hard maximum runtime for a single `codex exec` invocation |
| `CAB_CLAUDE_MODEL` | no | Model passed to `claude --model`. Defaults to `sonnet` |
| `CAB_CLAUDE_COMMAND_TIMEOUT_MS` | no | Idle timeout for each Claude Code invocation |
| `CAB_CLAUDE_MAX_RUNTIME_MS` | no | Hard maximum runtime for a single Claude Code invocation |
| `SLACK_BOT_TOKEN` | no | Bot token used for thread replies |
| `SLACK_APP_TOKEN` | no | App-level token used for Slack Socket Mode (`xapp-...`) |
| `SLACK_SIGNING_SECRET` | no | Enables Slack request signature verification when set |
| `MATTERMOST_SERVER_URL` | no | Base URL for the Mattermost server, for example `http://mattermost.lan:8065` |
| `MATTERMOST_BOT_TOKEN` | no | Mattermost personal access token or bot token used for post reads/writes |
| `MATTERMOST_WEBHOOK_TOKEN` | no | Shared token expected from the Mattermost outgoing webhook |
| `MATTERMOST_ACTION_BASE_URL` | no | Public base URL used in Mattermost interactive button callbacks, for example `http://bridge.lan:8787` |
| `MATTERMOST_MODE` | no | Mattermost ingress mode: `http` or `websocket`. Defaults to `http` |
| `MATTERMOST_DM_ONLY` | no | When `true`, the bridge accepts only direct-message (`D`) channels in Mattermost |

See [docs/architecture.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/architecture.md) for the concrete MVP structure.

The bridge also starts an admin server by default on `http://<bridge-host>:8788/admin`. By default it follows `CAB_HOST`, so if the bridge is bound to a LAN-facing address the admin screen is also reachable from the LAN. Set `CAB_ADMIN_HOST=127.0.0.1` if you want to keep it local-only.

## Codex adapter notes

The current Codex adapter invokes the locally installed `codex` CLI in non-interactive mode using `codex exec --json`. It captures the Codex thread id from the JSONL stream and stores it in bridge session state so later messages can continue with `codex exec resume`.

This path works with local ChatGPT login and does not require `OPENAI_API_KEY`.

When the bridge launches `codex`, it also injects bridge-specific environment variables into the child process:

- `CHAT_AGENT_BRIDGE=1`
- `CHAT_AGENT_BRIDGE_SESSION_ID`
- `CHAT_AGENT_BRIDGE_AGENT`
- `CHAT_AGENT_BRIDGE_CHAT_PROVIDER`
- `CHAT_AGENT_BRIDGE_CHAT_CHANNEL_ID`
- `CHAT_AGENT_BRIDGE_CHAT_THREAD_ID`
- `CHAT_AGENT_BRIDGE_WORKDIR`

If you already use Codex hooks to send Slack notifications, use `CHAT_AGENT_BRIDGE=1` as the skip condition so bridge-posted thread replies do not get duplicated by the hook.

## Claude Code adapter notes

When a session uses `claude-code`, the bridge invokes the local `claude` CLI in print mode using `-p --output-format stream-json`. Session continuity is preserved via the returned `session_id` and later resumed with `--resume`.

For approval behavior, the bridge maps:

- `manual` -> `--permission-mode plan`
- `on-failure` -> `--permission-mode acceptEdits`
- `never` -> `--dangerously-skip-permissions`

## Local ingress

The Slack adapter supports two ingress modes:

- HTTP mode via a local endpoint
- Socket Mode via an outbound WebSocket connection to Slack

HTTP mode exposes:

- `POST /slack/events`
- `GET /healthz`

Both modes acknowledge Slack immediately and process messages asynchronously.

When `CAB_CHAT_PROVIDER=mattermost`, the bridge exposes:

- `POST /mattermost/events`
- `POST /mattermost/commands`
- `POST /mattermost/actions`

In `MATTERMOST_MODE=http`, these endpoints are intended for Mattermost on the same LAN. The bridge supports outgoing webhooks for normal public-channel message ingress, and slash commands for private channels or DMs. For webhook events, the bridge reads the incoming `post_id`, resolves the root thread via the Mattermost API, and then posts replies back through the Mattermost REST API. For slash commands without an existing thread context, the bridge uses a synthetic per-channel/per-user thread key.

In `MATTERMOST_MODE=websocket`, the bridge connects to `/api/v4/websocket`, authenticates with the Mattermost bot token, and accepts:

- root-level posts that explicitly mention the bot username
- plain thread replies inside existing Mattermost threads

If `MATTERMOST_DM_ONLY=true`, both HTTP and WebSocket Mattermost ingress are filtered to direct-message channels only.

## Audit log

The bridge writes JSONL audit records by default to `./data/audit.jsonl`. This log is intended for operational debugging and captures:

- Slack events received and whether they were accepted
- Slack thread replies posted by the bridge
- bridge commands such as `status`, `repos`, and `cwd`
- Codex execution start, completion, approval requests, and failures

The bridge state file also persists structured execution history for each session. This history powers thread-local inspection via the `history` bridge command.

## Admin Surface

The local admin server exposes:

- `GET /admin`
- `GET /admin/settings`
- `GET /admin/healthz`
- `GET /admin/api/sessions`
- `GET /admin/api/sessions/:sessionId`
- `GET /admin/api/approvals`
- `GET /admin/api/failures?limit=20`
- `GET /admin/api/settings`
- `POST /admin/api/settings`
- `POST /admin/api/bridge/restart`

The HTML dashboard reads from the same JSON APIs used by admin actions. Runtime settings can be edited from the dashboard, and persisted settings are written to `CAB_SETTINGS_FILE` or `./data/admin-settings.json` by default. Secret fields stay masked; entering a new value rotates it, and the "clear stored ..." toggles remove the persisted secret on save.

For browser-based admin E2E coverage, this repository also includes Playwright fixtures under `e2e/`. After installing dependencies and Playwright browsers, run:

- `npx playwright install`
- `npm run test:e2e`

## Slack setup

To connect a real Slack app in HTTP mode, configure:

- Event Subscriptions with a public Request URL pointing to `/slack/events`
- Interactivity with the same public Request URL pointing to `/slack/events`
- Bot token with permissions to read mentions and post thread replies
- A tunnel or reverse proxy if the bridge is running on a local workstation

To connect in Socket Mode instead:

- enable Socket Mode in the Slack app
- create an App Token with `connections:write`
- set `CAB_SLACK_MODE=socket`
- set `SLACK_APP_TOKEN`
- keep `SLACK_BOT_TOKEN` for thread replies

Recommended Slack bot events for the current bridge behavior:

- `app_mention`
- `message.channels`
- `message.groups` if you use private channels
- `message.im` if you use direct messages
- `message.mpim` if you use group direct messages

Behavior:

- a new bridge interaction starts from `@Chat Agent Bridge ...`
- once a Slack thread is bound, plain thread replies are accepted without mentioning the bot
- approval requests can be accepted by replying `approve` / `reject` in the thread or by clicking the Slack button

Useful bridge commands:

- `@Chat Agent Bridge help`
- `@Chat Agent Bridge status`
- `@Chat Agent Bridge history [count]`
- `@Chat Agent Bridge cancel`
- `@Chat Agent Bridge new-thread`
- `@Chat Agent Bridge fork [codex|claude] [allow|manual]`
- `@Chat Agent Bridge sessions`
- `@Chat Agent Bridge approvals`
- `@Chat Agent Bridge failures [count]`
- `@Chat Agent Bridge repos`
- `@Chat Agent Bridge repo <alias>`
- `@Chat Agent Bridge cwd <path>`
- `@Chat Agent Bridge agent <codex|claude>`
- `@Chat Agent Bridge attach <session-id>`
- `@Chat Agent Bridge attach-last`
- `@Chat Agent Bridge detach`
- `@Chat Agent Bridge restart bridge`
- `@Chat Agent Bridge reject`

`cwd` accepts either an absolute path or a path relative to the current session working directory. The target must stay under one of `CAB_ALLOWED_DIRS`.
`repo <alias>` switches to one of the configured repo aliases. If `CAB_REPO_ALIASES` is omitted, the bridge auto-generates aliases from the basename of each allowed directory.
`agent <kind>` switches the current Slack thread to a fresh session for that agent. `new` also accepts an agent, for example `new claude manual`.
`new-thread` moves the current bridge session to a newly created Slack thread in the same channel while keeping the underlying agent context.
`fork` creates a new Slack thread with a separate bridge session, so you can run another agent or workflow in parallel without moving the current one.
`attach` binds the current Slack thread to an existing agent session id. `attach-last` reuses the most recent local session from the active agent's session index.

## Mattermost setup

For the current Mattermost MVP, configure:

- `CAB_CHAT_PROVIDER=mattermost`
- `MATTERMOST_SERVER_URL`
- `MATTERMOST_BOT_TOKEN`
- either `MATTERMOST_MODE=http` or `MATTERMOST_MODE=websocket`
- optionally, `MATTERMOST_DM_ONLY=true`

If `MATTERMOST_MODE=http`, also configure:

- `MATTERMOST_WEBHOOK_TOKEN`
- `MATTERMOST_ACTION_BASE_URL`
- an outgoing webhook in Mattermost pointing to `http://<bridge-host>:8787/mattermost/events`
- optionally, a slash command such as `/cab` pointing to `http://<bridge-host>:8787/mattermost/commands`

If `MATTERMOST_MODE=websocket`, the bridge consumes posts directly over the Mattermost WebSocket API and does not need the webhook endpoints for ingress. If you want approval and cancel buttons in Mattermost posts, set `MATTERMOST_ACTION_BASE_URL` so the buttons can call back into `/mattermost/actions`.

When `MATTERMOST_DM_ONLY=true`, Mattermost ingress is still limited to direct-message channels, but a new root post still needs an explicit mention to start a bridge session. Bridge-authored replies are tagged and ignored to prevent loops.

Current Mattermost scope:

- ingress is HTTP outgoing webhooks, slash commands, or WebSocket events
- approval and cancel buttons are available when `MATTERMOST_ACTION_BASE_URL` is configured
- thread replies and new threads are posted through the Mattermost REST API
- thread identity is resolved from the incoming `post_id` using `GET /api/v4/posts/{post_id}`
- slash commands without thread context are grouped by `channel_id + user_id`
- `MATTERMOST_DM_ONLY=true` filters ingress to channel type `D`
- Slack-only interactive buttons are not available in Mattermost yet
