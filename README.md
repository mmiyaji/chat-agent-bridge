# Chat Agent Bridge

> This branch develops the native WinUI 3 / C# application. See the [desktop guide (Japanese)](desktop/README.ja.md) and [migration plan](docs/winui-migration-plan.ja.md). The documentation below describes the original TypeScript implementation retained during migration.

Build a standalone Windows x64 executable with `npm run package:exe`. Double-click `release/ChatAgentBridge.exe`, then open the displayed control center URL. Browser launch is opt-in via `--open-browser`. Node.js/npm are not required on the target machine. Agent CLIs still require separate installation. See [executable instructions (Japanese)](docs/executable.ja.md).

The new connection control center manages Slack/Mattermost and local agent connections. Codex uses App Server with native chat approvals and input. See [connection control center](docs/harness-modernization.md).


- Languages: [English](README.md) / [Japanese](README.ja.md)

Chat Agent Bridge is a local bridge service that lets you continue operating a development agent from chat threads while preserving local machine context.

## Project status

This project is an unofficial personal tool intended for individual development workflows until official remote-control session support becomes available in upstream products.

It is designed for local, self-hosted use on a machine you control. It is not an official OpenAI, Anthropic, Slack, Mattermost, Cursor, or Google product.

The current implementation is Windows-first and is developed and validated primarily on Windows with PowerShell.

## What it does

- Binds a chat thread to a local agent session
- Keeps local repository context, uncommitted work, and tool state on the same machine
- Picks the working directory from the first message of a thread, or offers a numbered repo picker when none is given
- Offers to resume a prior Claude Code session for the bound folder, based on the real `~/.claude/projects` session store
- Remembers a default working directory per chat channel
- Lets you add or remove allowed directory roots at runtime from chat or the Admin UI, without a restart
- Supports Slack and Mattermost chat adapters
- Supports Codex, Claude Code, Cursor, and Gemini CLI agent adapters
- Adds approvals, audit logging, repo targeting, and an admin surface
- Supports Mattermost `bot` mode and `self` mode

## Quick start

The commands below assume a Windows environment with PowerShell.

This bridge assumes the local CLI for the selected agent is already installed and authenticated on the same machine before you use it from chat. The bridge does not perform interactive login flows for Codex, Claude Code, Cursor, or Gemini on your behalf.

1. Install dependencies:

```powershell
npm install
npm run check
```

2. Set the minimum environment:

```powershell
$env:CAB_ALLOWED_DIRS="$env:USERPROFILE\Documents\git"
$env:CAB_DEFAULT_WORKDIR="$env:USERPROFILE\Documents\git\chat-agent-bridge"
```

3. Start the bridge:

```powershell
npm run dev
```

Demo: send `@Chat Agent Bridge status` from a connected chat thread.

Or run it as a detached service:

```powershell
npm run service:start
npm run service:status
```

To start the bridge automatically at Windows logon:

```powershell
npm run service:install-autostart
```

## Typical first-mention flow

The first message of a new thread decides where the agent runs. A typical exchange looks like this:

```
You:    @Chat Agent Bridge haruhi fix the login screen
Bridge: This folder (`...\haruhi`) has previous Claude Code sessions. Reply with a number to resume, or `new` to start fresh. Your instruction will run after you choose.
        1. `a1b2c3d4` - fix login redirect loop (2026-07-01T09:12:00.000Z)
        2. `9f8e7d6c` - add MUI theme toggle (2026-06-28T14:03:00.000Z)
You:    1
Bridge: (resumes session `a1b2c3d4` in `...\haruhi` and runs "fix the login screen")
```

If the alias or path is omitted from the first message, the bridge asks first:

```
You:    @Chat Agent Bridge fix the login screen
Bridge: Choose a working directory for this thread (allowed roots only). Reply with a number, or `keep` to stay on `...\chat-agent-bridge`.
        1. `haruhi` -> `...\haruhi`
        2. `inori` -> `...\inori`
You:    1
Bridge: (binds the thread to `haruhi`, then offers the Claude Code resume picker if applicable, then runs "fix the login screen")
```

Set a default per channel to skip the directory picker on future threads: `@Chat Agent Bridge channel-default haruhi`.

## Demo

Slack example:

![Slack demo](docs/assets/slack-demo-2026-03-31.gif)

## Common service commands

```powershell
npm run service:start
npm run service:status
npm run service:restart
npm run service:restart:build
npm run service:stop
npm run service:install-autostart
npm run service:uninstall-autostart
```

## Architecture

```mermaid
flowchart TD
    subgraph Chat["Chat Providers"]
        Slack["Slack\n(HTTP / Socket Mode)"]
        Mattermost["Mattermost\n(HTTP / WebSocket)"]
    end

    subgraph Bridge["Chat Agent Bridge (Local)"]
        direction TB
        Adapter["Chat Adapter\nslack-adapter / mattermost-adapter"]
        BridgeSvc["Bridge Service\nrouting / approvals / sessions / job queue"]
        StateStore["File State Store\nstate.json"]
        AuditLog["Audit Logger\naudit.jsonl"]
        AdminSrv["Admin Server\n:8788/admin"]
    end

    subgraph Agents["Agent Adapters"]
        Codex["Codex CLI\ncodex-agent"]
        Claude["Claude Code CLI\nclaude-agent"]
        Cursor["Cursor CLI\ncursor-agent"]
        Gemini["Gemini CLI\ngemini"]
    end

    subgraph LocalFS["Local Machine"]
        Repos["Git Repositories\nCAB_ALLOWED_DIRS"]
        Settings["admin-settings.json"]
    end

    Browser["Browser\nAdmin UI"]
    User["User"]

    User -- "message" --> Slack
    User -- "message" --> Mattermost
    Slack -- "event" --> Adapter
    Mattermost -- "event" --> Adapter
    Adapter --> BridgeSvc
    BridgeSvc --> StateStore
    BridgeSvc --> AuditLog
    BridgeSvc --> Codex
    BridgeSvc --> Claude
    BridgeSvc --> Cursor
    BridgeSvc --> Gemini
    Codex -- "spawn" --> Repos
    Claude -- "spawn / stream-json" --> Repos
    Cursor -- "spawn" --> Repos
    Gemini -- "spawn" --> Repos
    BridgeSvc -- "reply" --> Adapter
    Adapter -- "post" --> Slack
    Adapter -- "post" --> Mattermost
    AdminSrv --> StateStore
    AdminSrv --> BridgeSvc
    Browser -- "HTTP" --> AdminSrv
    Settings --> BridgeSvc
```

## Documentation

- Install and base configuration: [docs/install.md](docs/install.md)
- Slack setup: [docs/slack-setup.md](docs/slack-setup.md)
- Mattermost setup: [docs/mattermost-setup.md](docs/mattermost-setup.md)
- Operations and service management: [docs/operations.md](docs/operations.md)
- Testing and E2E flows: [docs/testing.md](docs/testing.md)
- Architecture: [docs/architecture.md](docs/architecture.md)

## Notes

- The bridge starts an admin server by default on `http://<bridge-host>:8788/admin`
- Initial setup can be guided from `http://<bridge-host>:8788/admin/onboarding`
- Before using any agent through the bridge, verify that the corresponding local CLI can already run successfully in the same environment in non-interactive mode
- Codex sessions use the locally installed `codex` CLI and can reuse an existing local login
- Claude Code is expected to be installed and authenticated locally before bridge execution
- Cursor uses `cursor-agent --trust` by default and exposes a separate model setting in the Admin UI. The default model is `Auto`.
- Cursor is expected to be installed and authenticated locally before bridge execution
- Gemini uses `gemini --approval-mode yolo` by default and exposes separate command and model settings in the Admin UI.
- Gemini is expected to be installed and authenticated locally before bridge execution
- The bridge injects `CHAT_AGENT_BRIDGE=1` into Codex child processes so external hooks can suppress duplicate notifications
- In Mattermost, `MATTERMOST_ACTOR_MODE=self` is intended for user-token operation, accepts only self-DMs, and still requires a mention in that self-DM
- Claude Code resume candidates are read from `~/.claude/projects/<encoded-cwd>/*.jsonl` (override with `CAB_CLAUDE_PROJECTS_DIR`); only directories inside `CAB_ALLOWED_DIRS` are ever accepted as a working directory
- The Admin UI has a read-only Environment panel (`GET /admin/api/environment`) showing allowed roots, repo aliases, channel defaults, and the `CHAT_AGENT_BRIDGE_*` variable names passed to agents
- Allowed directory roots can be changed at runtime: from chat with `allow-dir <absolute-path>` / `allow-dir remove <absolute-path>` / `allow-dirs`, or from the Admin UI's "Allowed directory roots" field. Changes apply immediately and are persisted, but the `CAB_ALLOWED_DIRS` environment variable takes precedence again on the next restart if set. See [docs/install.md](docs/install.md) for the full precedence order
- `npm run service:install-autostart` registers a Windows Task Scheduler task that starts the bridge at logon; `npm run service:uninstall-autostart` removes it. See [docs/operations.md](docs/operations.md)

## Disclaimer

- This tool is provided as-is, with no warranty of any kind.
- The author does not guarantee stability, correctness, security, or fitness for any purpose.
- Use of this tool is entirely at your own risk.
- The author is not responsible for any data loss, repository damage, account issues, service disruption, security incidents, costs, or any other trouble caused by using this project.
- This bridge can trigger local agent actions against real files, repositories, and connected chat systems. Review the configuration carefully before using it.
- Restrict allowed directories, tokens, network access, and chat exposure to the minimum required for your environment.
- Do not use this project in environments where you cannot accept the operational and security risks of remote-triggered local execution.
