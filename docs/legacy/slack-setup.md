# Slack Setup

## Supported modes

The Slack adapter supports:

- HTTP mode via `POST /slack/events`
- Socket Mode via an outbound WebSocket connection to Slack

Use Socket Mode when the bridge runs on a local workstation and you do not want to expose a public webhook URL.

## Required app capabilities

Recommended bot events:

- `app_mention`
- `message.channels`
- `message.groups` if you use private channels
- `message.im` if you use direct messages
- `message.mpim` if you use group direct messages

Required bot scopes:

- `app_mentions:read`
- `chat:write`

For names in the WinUI desktop app's history and lookup controls:

- `users:read`: usernames and display names.
- `channels:read`: public channel names.
- `groups:read`: names of private channels the app has joined.
- `im:read` / `mpim:read`: direct-message / group direct-message metadata, when used.

Choose the read scopes for the conversation types you use. A `C`-prefixed channel ID can also identify a private channel; use the scope requested by Slack's `missing_scope` response instead of inferring privacy from the ID prefix. After changing bot scopes in **OAuth & Permissions**, reinstall the app to the intended workspace. A message-history scope alone does not grant access to channel metadata. See [Slack's Conversations API permissions](https://docs.slack.dev/apis/web-api/using-the-conversations-api/).

Workspace names come from `auth.test`, which requires no additional scope. Name lookups use metadata APIs and do not require the app to read message text. The desktop app caches successful names for 24 hours; a failed lookup may remain cached for 5 minutes after a permission change. Existing IDs and allowlists continue to control execution; display names do not grant access.

For Socket Mode:

- app-level token with `connections:write`

## Environment variables

### HTTP mode

```powershell
$env:CAB_CHAT_PROVIDER="slack"
$env:CAB_SLACK_MODE="http"
$env:SLACK_BOT_TOKEN="xoxb-..."
$env:SLACK_SIGNING_SECRET="..."
```

### Socket Mode

```powershell
$env:CAB_CHAT_PROVIDER="slack"
$env:CAB_SLACK_MODE="socket"
$env:SLACK_BOT_TOKEN="xoxb-..."
$env:SLACK_APP_TOKEN="xapp-..."
```

## HTTP mode setup

The bridge exposes:

- `POST /slack/events`
- `GET /healthz`

If the bridge runs locally, place a tunnel or reverse proxy in front of it and configure Slack to call:

- Event Subscriptions: `https://<public-host>/slack/events`
- Interactivity: `https://<public-host>/slack/events`

## Socket Mode setup

1. Enable Socket Mode in the Slack app.
2. Create an app-level token with `connections:write`.
3. Set `CAB_SLACK_MODE=socket`.
4. Set `SLACK_APP_TOKEN` and `SLACK_BOT_TOKEN`.
5. Start the bridge.

Socket Mode does not require a public HTTP endpoint for Slack ingress.

## Expected behavior

- A new bridge interaction starts from `@Chat Agent Bridge ...`
- Once a thread is bound, plain thread replies are accepted
- Approval prompts can be resolved by:
  - replying `approve` or `reject`
  - clicking the Slack action button

## Bridge commands commonly used from Slack

- `@Chat Agent Bridge help`
- `@Chat Agent Bridge status`
- `@Chat Agent Bridge history [count]`
- `@Chat Agent Bridge cancel`
- `@Chat Agent Bridge sessions`
- `@Chat Agent Bridge approvals`
- `@Chat Agent Bridge failures [count]`
- `@Chat Agent Bridge repos`
- `@Chat Agent Bridge repo <alias>`
- `@Chat Agent Bridge cwd <path>`
- `@Chat Agent Bridge agent <codex|claude|cursor|gemini>`
- `@Chat Agent Bridge attach <session-id>`
- `@Chat Agent Bridge attach-last`
- `@Chat Agent Bridge detach`
- `@Chat Agent Bridge new <codex|claude|cursor|gemini> <allow|manual>`
- `@Chat Agent Bridge restart`
- `@Chat Agent Bridge restart rebuild`
- `@Chat Agent Bridge reject`

`cwd` accepts either an absolute path or a path relative to the current working directory, but the destination must remain under `CAB_ALLOWED_DIRS`.

## Notes for Codex hooks

The bridge launches Codex with:

- `CHAT_AGENT_BRIDGE=1`
- `CHAT_AGENT_BRIDGE_SESSION_ID`
- `CHAT_AGENT_BRIDGE_AGENT`
- `CHAT_AGENT_BRIDGE_CHAT_PROVIDER`
- `CHAT_AGENT_BRIDGE_CHAT_CHANNEL_ID`
- `CHAT_AGENT_BRIDGE_CHAT_THREAD_ID`
- `CHAT_AGENT_BRIDGE_WORKDIR`

If you already have a Codex hook that posts its own Slack notifications, use `CHAT_AGENT_BRIDGE=1` to skip duplicate notifications for bridge-driven executions.
