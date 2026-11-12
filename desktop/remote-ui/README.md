# Remote UI smoke tests (LAN test PC)

Runs `desktop/SmokeTest.ps1` on another always-on Windows PC in the LAN so nothing appears on the dev PC.

Transport, scheduling and result collection come from a user-scope agent skill, `remote-win-ui-test`
(`~/.agents/skills/remote-win-ui-test`, linked into `~/.claude/skills` and `~/.codex/skills`). It is not part of
this repository; its `SKILL.md` describes the test PC setup. This folder holds only the repository side:

| File | Runs on | Role |
|---|---|---|
| `Run-UiSmoke.ps1` | test PC | entry script: build the portable app, launch it with isolated data, defer the first-run wizard, run `SmokeTest.ps1` (or a probe) |
| `Invoke-RemoteUiSmoke.ps1` | dev PC | wrapper that calls the skill with this entry; results go to `desktop/artifacts/remote-ui` |

```powershell
pwsh -NoProfile -File desktop\remote-ui\Invoke-RemoteUiSmoke.ps1                 # smoke test
pwsh -NoProfile -File desktop\remote-ui\Invoke-RemoteUiSmoke.ps1 -ProbeScript p.ps1  # inspect live UI
```

The host defaults to `$env:REMOTE_WIN_TEST_HOST`, then the SSH alias `stampede`.
The test PC also needs the .NET 10 SDK and the `winapp` CLI on PATH.
Only committed files and uncommitted changes to tracked files are sent; `git add` new files first.
