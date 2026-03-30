# Chat Agent Bridge

Chat Agent Bridge is a local bridge service that lets users continue operating a development agent from chat threads while preserving the local machine context.

## What it does

- Binds a chat thread to a local agent session
- Keeps local repository context, uncommitted work, and tool state on the same machine
- Supports Slack and Mattermost chat adapters
- Supports Codex and Claude Code agent adapters
- Adds approvals, audit logging, repo targeting, and an admin surface
- Mattermost supports both `bot` mode and `self` mode

## Quick start

1. Install dependencies:

```powershell
npm install
npm run check
```

2. Set the minimum environment:

```powershell
$env:CAB_ALLOWED_DIRS="C:\Users\mail\Documents\git"
$env:CAB_DEFAULT_WORKDIR="C:\Users\mail\Documents\git\chat-agent-bridge"
```

3. Start the bridge:

```powershell
npm run dev
```

Or run it as a detached service:

```powershell
npm run service:start
npm run service:status
```

## Common service commands

```powershell
npm run service:start
npm run service:status
npm run service:restart
npm run service:restart:build
npm run service:stop
```

## Documentation

- Install and base configuration: [docs/install.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/install.md)
- Slack setup: [docs/slack-setup.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/slack-setup.md)
- Mattermost setup: [docs/mattermost-setup.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/mattermost-setup.md)
- Operations and service management: [docs/operations.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/operations.md)
- Testing and E2E flows: [docs/testing.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/testing.md)
- Architecture: [docs/architecture.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/architecture.md)

## Notes

- The bridge starts an admin server by default on `http://<bridge-host>:8788/admin`
- Initial setup can be guided from `http://<bridge-host>:8788/admin/onboarding`
- Codex sessions use the locally installed `codex` CLI and can reuse an existing local login
- The bridge injects `CHAT_AGENT_BRIDGE=1` into Codex child processes so external hooks can suppress duplicate notifications
- In Mattermost, `MATTERMOST_ACTOR_MODE=self` is intended for user-token operation, accepts only self-DMs, and does not require mentions in that self-DM
