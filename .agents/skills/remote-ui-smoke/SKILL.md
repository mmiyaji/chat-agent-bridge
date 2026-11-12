---
name: remote-ui-smoke
description: Run the WinUI desktop smoke test (desktop/SmokeTest.ps1) on the LAN test PC over SSH so no window appears on the dev PC, and collect results. Use when asked to run UI smoke/E2E tests remotely, "STAMPEDEでテスト", "画面を出さずにUIテスト", or to inspect the live UI of the app on the test PC.
---

# Remote UI smoke test

The dev PC sends the current code to a test PC (SSH alias `stampede`), which builds the portable app,
launches it with an empty data directory in its logged-on desktop and runs `desktop/SmokeTest.ps1`.
Nothing is shown on the dev PC. Setup and architecture: `desktop/remote-ui/README.md`.

## Run

From the repository root (PowerShell 7):

```powershell
pwsh -NoProfile -File desktop/remote-ui/Invoke-RemoteUiSmoke.ps1
```

- Sends HEAD plus uncommitted changes to **tracked** files (new untracked files are not sent — `git add` them first).
- Takes about 2–4 minutes (remote build ~1–2 min). Run it in the background if the tool has a short timeout.
- Exit code 0 only when every check passed. Output lists `PASS:`/`FAIL:` per check.

## Read results

Files land in `desktop/artifacts/remote-ui/`:

| File | Use |
|---|---|
| `status.txt` | `<runId> passed|failed` |
| `smoke.log` | winapp output per step; look for `[ERROR]` / `No element found` |
| `smoke-results.json` | per-check pass/fail |
| `run.log` | git checkout + build transcript (build errors appear here) |
| `failure.png` | full-screen capture of the test PC when the run failed |

Read `failure.png` first when a check fails: a dialog or popup left open is the most common cause.

## Inspect the live UI (probe mode)

To discover AutomationIds or reproduce a failure, write a local script with
`param([int]$AppPid, [string]$DataDirectory, [string]$OutDirectory)` that calls `winapp ui ... -a $AppPid`
and saves anything useful into `$OutDirectory`, then:

```powershell
pwsh -NoProfile -File desktop/remote-ui/Invoke-RemoteUiSmoke.ps1 -ProbeScript <path-to-probe.ps1>
```

The app is already past the first-run wizard when the probe starts. Useful commands inside a probe:
`winapp ui inspect -a $AppPid -i --depth 14 > (Join-Path $OutDirectory 'tree.txt')`, and a screen capture via
`System.Drawing.Graphics.CopyFromScreen` (see `Run-UiSmoke.ps1`). Text in collected files may be cp932-garbled;
AutomationIds stay readable.

## Known app behaviors the smoke test handles

- Fresh data directory opens the first-run wizard → `Run-UiSmoke.ps1` invokes `OnboardingDefer`.
- The one-time `ChatConnectTip` teaching tip is pre-marked seen via `tips.json`; its OK button is also
  `CloseButton` and would steal dialog commands.
- Security-relevant setting edits show a "確認して保存" dialog (`PrimaryButton`) when leaving the scope or
  checking the CLI; each changed field may need its own confirmation, and a confirmation cancels the
  navigation/check that triggered it (repeat it).
- Custom CLI check runs `<exe> --version`, so the fixture uses `pwsh`, not `powershell.exe`.

## Constraints

- If `remote-ui/Run-UiSmoke.ps1` changes, copy it to the test PC: `scp desktop/remote-ui/Run-UiSmoke.ps1 stampede:C:/cab-ci/Run-UiSmoke.ps1`.
- The test PC must be logged on at its console (auto-logon). If SSH works but the run never starts or
  `failure.png` is black, the console session is locked/disconnected — ask the user.
- Never run paid models in the smoke test; it uses only the local PowerShell fixture.
