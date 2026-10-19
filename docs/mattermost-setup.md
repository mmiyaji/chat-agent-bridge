# Mattermost Setup

## Native Windows app: bot DM and request format

For a bot such as `cc-agent-bot`, register that bot's access token, set the **channel ID of the DM with the bot**, and look up/add the **personal account that will send requests** to allowed users. The allowlist stores internal user IDs; typing a username directly into the ID list does not resolve it. Save and reconnect after changing these settings.

Start or continue a request with either `@cc-agent-bot your request` (mention at the beginning) or `!cab your request`. The username is read from the authenticated token's account, so other bot names or partial matches do not trigger execution. A root post starts a new conversation; an explicit request in that post's thread continues it for the same sender. An ordinary unmentioned message is ignored, including in DMs. Bot/self/webhook messages and unauthorized users/channels/conversations remain excluded.

日本語UIでは、Botのトークンと **そのBotとのDMのチャンネルID** を設定し、「ユーザー名から許可するユーザーを検索」から **依頼を送る個人アカウント** を検索・追加します。Bot名を許可ユーザーとして追加する必要はありません。保存・再接続後、`@cc-agent-bot 依頼内容` または `!cab 依頼内容` を送ってください。続ける場合も同じ本人が元のスレッド内でメンションまたは `!cab` を付けます。

Leading bot mentions are supported from portable `v0.1.0-preview.4`. In `preview.3` and earlier, use the `!cab` prefix.

## Native Windows app: direct connection

In **Connection settings → Mattermost**, enable **Connect without a proxy**, save, and disconnect/reconnect Mattermost. The first-run wizard offers the same setting below the server URL. Its default is off, preserving the system proxy setting for existing configurations.

This applies to Mattermost REST authentication, user lookup, post link checks, replies, connection notices, and WebSocket events. Slack and agent CLI proxy settings are independent. Changing the mode during a user or post lookup cancels that lookup; retry it with the new mode.

日本語UIでは **接続設定 → Mattermost → プロキシを使わず直接接続** をオンにし、**設定を保存 → 切断 → Mattermostに接続** の順に操作します。初回ウィザードにも同じ項目があります。

If direct mode still fails, check name resolution, routing/VPN access, server availability, and TLS certificate trust. Direct mode disables the application's explicit/system proxy use; it cannot change a transparent network gateway or an endpoint agent that intercepts connections. HTTPS certificate validation remains enabled. Error messages and screenshots shared for diagnosis should omit tokens and internal URLs.

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
