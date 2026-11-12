# Remote UI smoke tests (LAN test PC)

Runs the WinUI smoke test on another always-on PC in the LAN so nothing appears on the dev PC.
The dev PC talks to it only over SSH; GitHub is not involved.

```
dev PC: Invoke-RemoteUiSmoke.ps1
  ├─ git push  → stampede:C:/cab-ci/repo.git (branch ui-test)
  ├─ ssh       → write request.txt, schtasks /run CabUiSmoke
  │                └─ runs Run-UiSmoke.ps1 in the logged-on desktop (build → launch → SmokeTest.ps1)
  ├─ ssh       → poll out\status.txt
  └─ scp       → copy out\* to desktop\artifacts\remote-ui
```

UI tests need an interactive desktop. A process started directly from SSH has none, so the test itself is
started through a scheduled task that runs only while the user is logged on.

## Test PC setup (once, as administrator where noted)

1. Choose the user that runs the tests (a dedicated local user, or your own account if the PC is a dedicated test box) and set it to sign in automatically
   (Sysinternals Autologon is the simplest). Disable sleep and screen lock for that user.
2. Install: Git, PowerShell 7, .NET 10 SDK, `winapp` CLI.
3. Enable OpenSSH Server (admin PowerShell):
   ```powershell
   Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
   Set-Service sshd -StartupType Automatic; Start-Service sshd
   New-ItemProperty HKLM:\SOFTWARE\OpenSSH -Name DefaultShell -PropertyType String -Force `
     -Value 'C:\Program Files\PowerShell\7\pwsh.exe'
   ```
   PowerShell as the default shell is required: git's quoted paths do not work under cmd.exe.
   Restrict the firewall rule `OpenSSH-Server-In-TCP` to the LAN profile / subnet.
4. Add the dev PC's public key to `C:\Users\cabci\.ssh\authorized_keys`
   (or `C:\ProgramData\ssh\administrators_authorized_keys` if `cabci` is an administrator),
   and set `PasswordAuthentication no` in `C:\ProgramData\ssh\sshd_config`.
5. As `cabci`:
   ```powershell
   New-Item -ItemType Directory C:\cab-ci | Out-Null
   git init --bare C:\cab-ci\repo.git
   Copy-Item <this folder>\Run-UiSmoke.ps1 C:\cab-ci\
   schtasks /create /tn CabUiSmoke /sc once /st 00:00 /it /rl limited /f `
     /tr "pwsh -NoProfile -ExecutionPolicy Bypass -File C:\cab-ci\Run-UiSmoke.ps1"
   ```
   `/it` makes the task run only in the logged-on session, which gives it the desktop.

## Dev PC

Add to `~/.ssh/config`:
```
Host stampede
  HostName STAMPEDE
  User cabci
```
Then run from the repository:
```powershell
pwsh desktop\remote-ui\Invoke-RemoteUiSmoke.ps1
```
Results: `desktop\artifacts\remote-ui\` (`status.txt`, `run.log`, `smoke-results.json`, `failure.png` on failure).
The exit code is 0 only when every check passed.
