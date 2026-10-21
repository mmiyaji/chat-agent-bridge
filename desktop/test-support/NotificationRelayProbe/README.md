# Portable notification owner routing

This console probe exercises the production pipe transport and process-local decision registry in separate processes. It does not prove Windows displayed a toast or delivered a real button click. No CLI, chat connection, certificate, or package registration is used.

```powershell
dotnet run --project desktop/test-support/NotificationRelayProbe/NotificationRelayProbe.csproj -c Release
```

The scenarios cover approve, decline, body-open, duplicate single use, expiry, another owner, and an exited owner. Activation capabilities travel through redirected process streams and are not printed by the scenario runner.

## Check the portable build on the affected PC

1. Close all older Chat Agent Bridge processes on that PC. Back up its existing `Data` folder. Extract the complete portable ZIP and replace the application files while keeping that PC's `Data` folder. Do not copy credentials from another PC.
2. Start `ChatAgentBridge.Desktop.exe` normally, without elevation. Confirm the test version in About. No certificate, package ID, administrator privileges, or Developer Mode is required by this build.
3. Connect the existing chat configuration. Start a **new** thread that requires its first approval, then click **Approve and run** in the new Windows notification. The exact request in the original app should leave the pending state and run once. Do not reuse notifications created by an older executable or before restarting.
4. Start another fresh thread and choose **Deny**. It should be rejected without running the CLI. Clicking the notification body should only open the request.
5. Repeated clicks must not execute twice. A notification whose original request has expired, disconnected, or lost its owning process must not approve work. Busy requests must not execute automatically when the current run finishes.

When Windows starts a second process for a notification, that process forwards only to the live owner identified by the notification. It exits before creating a window or connecting chats when forwarding succeeds. A failed/stale activation opens the app for attention and suppresses startup chat auto-connect in that fallback process. It never recreates approval callbacks from disk.

## Diagnostics

Logs are under the active data directory's `diagnostics/notification-events-<pid>.jsonl` (normally `Data/diagnostics`). Each record contains a UTC timestamp, process ID, executable version/path hash, fixed event/action/outcome, a one-way request-token hash, and exception type/HRESULT when needed. Request text, replies, credentials, raw activation capabilities, owner-pipe identifiers, and command lines are excluded. Files rotate at 128 KiB and retain at most eight process logs with one backup each.

Compare the same `tokenHash` between the producer's `notification.shown` and the receiver's `activation.received`, `activation.forwarded`, and owner's `activation.result`. `completed` indicates callback completion; `busy`, `expired`, `missing`, and `unavailable` explain why execution did not begin. A forwarding `accepted` means the owner dispatcher queued the action, not that an AI run has completed. `decision.*` events further identify connection/selection/busy rejection paths.
