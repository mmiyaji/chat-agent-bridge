# Testing

## Core validation

```powershell
npm run check
npm test
```

`npm test` runs the fast local suite only. It does not require Docker and does not execute the live Mattermost E2E harness.

## Browser admin E2E

Install Playwright browsers once:

```powershell
npx playwright install
```

Run the browser suite:

```powershell
npm run test:e2e
```

## Mattermost Docker E2E

These scenarios are opt-in and run only when you call the dedicated Mattermost E2E command.

Start Mattermost:

```powershell
npm run mattermost:up
```

Bootstrap the local test workspace:

```powershell
npm run mattermost:e2e:bootstrap
```

Run the Mattermost suite:

```powershell
npm run test:e2e:mattermost
```

Capture a README-ready Mattermost demo:

```powershell
npm run mattermost:demo:capture
```

This writes:

- `docs/assets/mattermost-demo-2026-03-31.gif`
- `docs/assets/mattermost-demo-2026-03-31-palette.png`
- `docs/assets/mattermost-demo.webm`
- `docs/assets/mattermost-demo.gif`
- `docs/assets/mattermost-demo-final.png`

Bootstrap writes `data/runtime/mattermost-e2e.json`.

Current live scenarios:

- websocket ingress from a public channel mention to the bridge user
- websocket ingress from a self-authored self-DM mention
- approval button flow
- cancel button flow

The current bootstrap creates:

- server URL: `http://127.0.0.1:8065`
- bridge user: `cab-bridge`
- member user: `cab-member`

## Useful overrides

- `MATTERMOST_E2E_SERVER_URL`
- `CAB_MATTERMOST_E2E_STATE_FILE`
- `MATTERMOST_E2E_BRIDGE_USERNAME`
- `MATTERMOST_E2E_MEMBER_USERNAME`

## Test strategy

The repository uses a layered approach:

- unit and integration tests for adapters and bridge orchestration
- Playwright E2E for the local admin UI
- Docker-backed E2E for Mattermost live integration paths

This keeps the fast tests local while still exercising the live chat stack where behavior differs from mocks.
