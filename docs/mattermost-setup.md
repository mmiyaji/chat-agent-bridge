# Mattermost Setup

## Supported modes

The Mattermost adapter supports:

- HTTP mode
  - `POST /mattermost/events`
  - `POST /mattermost/commands`
  - `POST /mattermost/actions`
- WebSocket mode
  - `/api/v4/websocket`

## Environment variables

### Common

```powershell
$env:CAB_CHAT_PROVIDER="mattermost"
$env:MATTERMOST_SERVER_URL="http://mattermost.example:8065"
$env:MATTERMOST_BOT_TOKEN="<bearer-token>"
$env:MATTERMOST_ACTOR_MODE="bot"
```

Optional:

```powershell
$env:MATTERMOST_ACTOR_MODE="self"
```

Actor modes:

- `bot`
  - intended for bot-token or service-account operation
  - normal channel mentions are accepted
- `self`
  - intended for user-token operation when you cannot create a bot
  - only self-DMs are accepted
  - self-DM posts still require a mention so unrelated notes are ignored
  - direct messages with other users do not trigger the bridge

`MATTERMOST_DM_ONLY=true` is kept only as a legacy compatibility switch. New setups should use `MATTERMOST_ACTOR_MODE=self`.

### HTTP mode

```powershell
$env:MATTERMOST_MODE="http"
$env:MATTERMOST_WEBHOOK_TOKEN="<shared-webhook-token>"
$env:MATTERMOST_ACTION_BASE_URL="http://bridge-host:8787"
```

### WebSocket mode

```powershell
$env:MATTERMOST_MODE="websocket"
$env:MATTERMOST_ACTION_BASE_URL="http://bridge-host:8787"
```

## Authentication notes

The adapter expects a bearer token that can access the Mattermost REST API and WebSocket API.

Practical choices:

- personal access token
- bot token
- OAuth access token

SAML by itself is not enough for API access. In a SAML-backed deployment, the bridge still needs a bearer token issued by Mattermost.

## HTTP mode setup

Use HTTP mode when you want Mattermost to call the bridge directly over HTTP.

Recommended Mattermost configuration:

- outgoing webhook to `http://<bridge-host>:8787/mattermost/events`
- slash command to `http://<bridge-host>:8787/mattermost/commands`
- interactive actions pointing back to `http://<bridge-host>:8787/mattermost/actions`

Notes:

- outgoing webhooks are best suited to public-channel ingress
- slash commands are useful for private channels and DMs
- approval and cancel buttons require `MATTERMOST_ACTION_BASE_URL`

## WebSocket mode setup

Use WebSocket mode when the bridge should consume posts directly from Mattermost without HTTP ingress callbacks.

Behavior:

- in `bot` mode, root-level posts must mention the configured bridge username
- in `self` mode, only mentioned posts in the self-DM are accepted
- plain thread replies inside existing threads are accepted
- if `MATTERMOST_ACTOR_MODE=self`, only self-DMs are accepted

Connection reliability:

- on every reconnect, the adapter re-authenticates over the socket before it is treated as live, so a silent drop cannot leave the bridge listening on a stale, unauthenticated connection
- a 30-second ping heartbeat detects a connection that has gone silent without an explicit close, and forces a reconnect
- reconnect uses exponential backoff and does not crash the bridge process on a socket error
- REST calls made by the adapter (posting messages, resolving threads, and so on) time out after 15 seconds instead of hanging indefinitely

## Current Mattermost scope

- thread replies and new threads are posted through the Mattermost REST API
- thread identity is resolved from the incoming `post_id`
- slash commands without thread context are grouped by `channel_id + user_id`
- interactive approval and cancel actions are supported when `MATTERMOST_ACTION_BASE_URL` is configured
- self mode is designed for environments where admin rights are unavailable and the bridge must run on the user's own Mattermost account

## Docker E2E harness

There is an opt-in Docker-based Mattermost integration harness for websocket-mode tests.

See [testing.md](testing.md) for the exact commands and scenarios.
