# Operations

## Service commands

```powershell
npm run service:start
npm run service:status
npm run service:restart
npm run service:restart:build
npm run service:stop
```

Behavior:

- `service:start` starts the bridge as a detached Node process
- `service:restart` performs a fast restart without rebuilding
- `service:restart:build` rebuilds before restarting

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

By default the admin server follows `CAB_HOST`. Set `CAB_ADMIN_HOST=127.0.0.1` if the bridge is LAN-reachable but the admin screen should stay local-only.

## Audit and state

Default files:

- state: `./data/slack-run-state.json`
- audit log: `./data/audit.jsonl`
- admin settings: `./data/admin-settings.json`

The audit log records accepted chat events, bridge commands, execution start/completion, approvals, and failures.

## Restart behavior

Slack and admin-triggered restart flows use an external helper so the bridge can request its own restart without killing the request handler mid-response.

Expected Slack flow:

1. `Bridge restart requested...`
2. bridge restarts
3. `Bridge restart completed. The bridge is back online.`

## Operational commands from chat

These are the most useful during day-to-day operation:

- `status`
- `history [count]`
- `sessions`
- `approvals`
- `failures [count]`
- `repos`
- `repo <alias>`
- `cwd <path>`
- `attach <session-id>`
- `attach-last`
- `detach`
- `restart`
- `restart rebuild`

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
