# Operations

## Service commands

```powershell
npm run service:start
npm run service:status
npm run service:restart
npm run service:restart:build
npm run service:stop
npm run service:install-autostart
npm run service:uninstall-autostart
```

Behavior:

- `service:start` starts the bridge as a detached Node process
- `service:restart` performs a fast restart without rebuilding
- `service:restart:build` rebuilds before restarting
- `service:install-autostart` registers a Windows Task Scheduler task named `ChatAgentBridge` that starts the bridge at logon for the current user
- `service:uninstall-autostart` removes that scheduled task

### Windows logon autostart

`npm run service:install-autostart` runs `scripts/bridge-install-autostart.ps1`, which registers a Task Scheduler task (`ChatAgentBridge`) that:

- triggers at logon of the current user
- runs `scripts/bridge-start.ps1 -SkipBuild`, so it starts the already-built `dist/` output rather than rebuilding on every logon
- restarts up to 3 times with a 1-minute interval if the process exits unexpectedly
- has no execution time limit, since the bridge is expected to run indefinitely
- is allowed to run and continue on battery power

Because it starts with `-SkipBuild`, after pulling new code you still need to rebuild and restart once, for example with `npm run service:restart:build`. The autostart task will then keep starting that build at every subsequent logon.

Re-running `npm run service:install-autostart` replaces the existing task (it is idempotent). To test the task without logging off, run `Start-ScheduledTask -TaskName ChatAgentBridge`. To remove it, run `npm run service:uninstall-autostart`.

`service:stop` (and the restart scripts) only ever stop process IDs that were independently validated as genuine bridge processes (`node.exe` running `dist/index.js`), whether that PID came from `bridge.pid` or from process discovery. A stale `bridge.pid` pointing at a dead or reused PID is removed but never used to kill an unrelated process.

## Runtime files

Stored under `data/runtime/`:

- `bridge.pid`
- `bridge.stdout.log`
- `bridge.stderr.log`
- `bridge.orchestrator.log`
- `restart-completion.jsonl`

## Admin surface

The admin server exposes:

- `GET /admin`
- `GET /admin/settings`
- `GET /admin/onboarding`
- `GET /admin/healthz`
- `GET /admin/api/sessions`
- `GET /admin/api/sessions/:sessionId`
- `GET /admin/api/approvals`
- `GET /admin/api/failures?limit=20`
- `GET /admin/api/settings`
- `POST /admin/api/settings`
- `POST /admin/api/bridge/restart`
- `GET /admin/api/environment`

By default the admin server follows `CAB_HOST`. Set `CAB_ADMIN_HOST=127.0.0.1` if the bridge is LAN-reachable but the admin screen should stay local-only.

The Admin UI's Environment panel is read-only and shows the default working directory, allowed roots, repo aliases, channel defaults, `claudeProjectsDir`, the agent/chat provider defaults, and the names (not values) of the `CHAT_AGENT_BRIDGE_*` variables passed into agent child processes.

## Audit and state

Default files:

- state: `./data/slack-run-state.json`
- audit log: `./data/audit.jsonl`
- admin settings: `./data/admin-settings.json`

The audit log records accepted chat events, bridge commands, execution start/completion, approvals, and failures.

Claude Code resume candidates are read from `CAB_CLAUDE_PROJECTS_DIR` (default `~/.claude/projects`), not from a bridge-owned index file. `CAB_CLAUDE_SESSION_INDEX_FILE` is kept only as a fallback for folders with no entries under `CAB_CLAUDE_PROJECTS_DIR`. See [install.md](install.md) for the full environment variable reference.

## Restart behavior

Slack and admin-triggered restart flows use an external helper so the bridge can request its own restart without killing the request handler mid-response.

Expected Slack flow:

1. `Bridge restart requested...`
2. bridge restarts
3. `Bridge restart completed. The bridge is back online.`

If the restart-completion message cannot be posted, the bridge retries with backoff, but it gives up instead of retrying forever: a permanent chat error (for example `channel_not_found`, or another sign the target channel/thread no longer exists) drops the pending notification immediately, and any other, transient failure is dropped after 30 attempts.

## Operational commands from chat

These are the most useful during day-to-day operation:

- `status`
- `history [count]`
- `sessions`
- `approvals`
- `failures [count]`
- `repos`
- `allow-dirs`
- `allow-dir <absolute-path>`
- `allow-dir remove <absolute-path>`
- `repo <alias>`
- `cwd <path>`
- `channel-default <alias|path>` / `channel-default clear` / `channel-default`
- `attach <session-id>`
- `attach-last`
- `detach`
- `restart`
- `restart rebuild`

`status` includes a `Channel default cwd:` line showing the current channel's default working directory, if one is set.

## Troubleshooting

### The bridge accepts restart but does not come back

Check:

- `data/runtime/bridge.orchestrator.log`
- `data/runtime/bridge.stderr.log`
- `npm run service:status`

If Slack runs in Socket Mode, `Bridge health: unavailable` is expected. Use `Admin health` and the runtime logs instead.

### A thread is bound to the wrong agent session

Use:

- `detach`
- `attach <session-id>`
- `attach-last`
- `new <agent> <mode>`

Note:

- `attach` and `attach-last` are currently intended for reusable Codex and Claude Code sessions
- Cursor and Gemini use fresh bridge-owned sessions only

### A Codex or Claude session behaves differently than expected

If the thread is attached to an existing local session, that agent session may carry its own sandbox or execution history. A bridge-owned fresh session is usually more predictable.

### Mattermost WebSocket mode drops or seems to hang

The Mattermost adapter re-authenticates on every reconnect, so a dropped socket does not leave the bridge silently unauthenticated. A 30-second ping heartbeat detects a silently dead connection and forces a reconnect, and REST calls to the Mattermost API time out after 15 seconds rather than hanging indefinitely. A socket error no longer crashes the bridge process; it is logged and followed by the normal reconnect-with-backoff flow. See [mattermost-setup.md](mattermost-setup.md) for connection-mode details.
