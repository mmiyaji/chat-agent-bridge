---
name: remote-ui-smoke
description: Run this repository's WinUI smoke test (desktop/SmokeTest.ps1) on the LAN test PC without showing windows on the dev PC, or inspect the live app UI there. Use for remote UI smoke runs in chat-agent-bridge, "STAMPEDEでテスト", "画面を出さずにUIテスト".
---

# Remote UI smoke test (chat-agent-bridge)

Uses the user-scope `remote-win-ui-test` skill for transport and results; read its SKILL.md for the
mechanics, result files and test PC setup. If that skill is not installed, say so and stop.

## This repository

- Entry script: `desktop/remote-ui/Run-UiSmoke.ps1` (build portable app → launch with isolated data →
  defer first-run wizard → `desktop/SmokeTest.ps1`).
- Run: `pwsh -NoProfile -File desktop/remote-ui/Invoke-RemoteUiSmoke.ps1` (2–4 minutes; run in the background).
  Results: `desktop/artifacts/remote-ui/` (`entry.log`, `smoke-results.json`, `failure.png`).
- Inspect the live UI: write a probe script with `param([int]$AppPid, [string]$DataDirectory, [string]$OutDirectory)`
  and run `Invoke-RemoteUiSmoke.ps1 -ProbeScript <file>`; it starts after the first-run wizard.

## App behaviors the smoke test depends on

- The one-time `ChatConnectTip` teaching tip is pre-marked seen via `tips.json`; its OK button is also
  `CloseButton` and would steal dialog commands.
- Security-relevant setting edits show a "確認して保存" dialog (`PrimaryButton`) when leaving the scope or
  checking the CLI. Each changed field may need its own confirmation, and a confirmation cancels the
  navigation/check that triggered it, so repeat it.
- The custom CLI check runs `<exe> --version`, so the fixture uses `pwsh`, not `powershell.exe`.
- Never run paid models in the smoke test; it uses only the local PowerShell fixture.
