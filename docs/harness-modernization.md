# Connection control center

The bridge now starts its local control center even when chat credentials or an agent login are missing. Open `http://127.0.0.1:8788/admin` after `npm start`.

1. Choose Codex, Claude Code, Cursor, or Gemini and a working directory in **接続設定**. Save the settings, then connect the agent.
2. Choose Slack (Socket Mode) or Mattermost (WebSocket), enter credentials and allowed user IDs, save, then connect the chat.
3. Mention the bot in a chat thread. Continue the conversation in that thread. Use `status` or `cancel` to inspect or stop work.

The control center separates saved configuration from live connections. Reconnect the affected connection after changing credentials, provider, or transport mode. Active work blocks configuration changes and connection switching. Existing sessions retain their chosen agent; changing the default agent affects new sessions. Connect the agent associated with an existing session before continuing it.

## Codex harness

Codex uses the official [App Server protocol](https://learn.chatgpt.com/docs/app-server), with JSON RPC over local stdio. The bridge starts or resumes a Codex thread, starts turns, forwards progress, handles native approvals and questions, and interrupts turns on cancellation. It reuses the locally installed Codex CLI login; no API key is required when Codex is already signed in through ChatGPT.

Connection checks read account/model metadata without sending an inference prompt. `CAB_CODEX_MODEL=auto` uses the harness default; explicitly configured model names remain respected. The UI supports overriding the model. Existing `codex exec` code remains available for compatibility tests, but the application entry point uses App Server.

Approval and question replies are scoped to the originating chat thread and user. Pending native requests live only for the running process; cancellation, timeout, or shutdown closes them. Restarted executions are marked interrupted rather than silently replayed. Claude/Cursor/Gemini retain their CLI adapters; their connection check detects the CLI, while authentication is verified on execution.

Codex manual mode starts read-only and forwards escalation requests to chat. Permission grants are limited to a turn and constrained by the configured filesystem roots and network policy. The legacy `on-failure` choice maps to the current harness's `on-request` policy; `never` runs within the configured workspace sandbox without prompting.

## Startup and operations

- `npm start`: build and open the control center (Windows).
- `npm run dev`: run from source.
- `CAB_AUTO_CONNECT=0`: disable automatic connection checks and chat connection on startup.
- Saved chat credentials enable automatic reconnection on startup; failure is displayed in the control center.
- `/admin/legacy`, `/admin/settings`, and `/admin/onboarding` retain the earlier diagnostic/configuration screens.
- Admin binds to loopback by default. Explicit existing host settings are preserved; only expose the admin service through a trusted access boundary.
- Credentials remain in the ignored local settings file; API responses expose only presence flags.

## Verification

`npm run check`, `npm test`, and `npm run test:e2e` cover types, bridge behavior, native harness protocol, and browser UI. For local Mattermost, start the existing Docker container, run `npm run mattermost:e2e:bootstrap`, then `npm run test:e2e:mattermost`. The Mattermost integration suite uses real REST/WebSocket transport and deterministic agents to avoid inference costs.

On Windows, `PLAYWRIGHT_CHANNEL=msedge` uses an installed Edge instead of downloading Chromium. Native requests are answered with `approve <request-id>`, `reject <request-id>`, or `answer <request-id> <answer>`; for multiple questions use `answer <request-id> <question-id> <answer>` for each question.

Validated locally on 2026-09-29: five browser tests, ten Mattermost integration tests (including native approval and two questions in one execution), and a real Mattermost → Codex `gpt-6-luna` → Mattermost reply. The verification container is `cab-mattermost-harness-test` on loopback port 8065, using the locally available `mattermost-preview:9.5.11` image. This image is a local test fixture, not a production deployment recommendation. Existing Mattermost content was kept separate.

The running verification control center is on port 43888 with separate ignored settings/state/audit files under `data/runtime/harness-*`. Its chat credentials are test credentials. Normal startup continues to use the original settings file unless `CAB_SETTINGS_FILE` is specified.
