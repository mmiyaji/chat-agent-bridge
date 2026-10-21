# Notification approval probe

Invokes the published Inbox ViewModel's notification approval and rejection methods through reflection. It uses synthetic requests and a recording transport, and opens no WinUI window, Windows notification, CLI, or chat connection. Every fixture stores settings and session data under a new caller-selected directory via `CAB_WINUI_DATA_DIR`; existing directories are rejected. Before approving, it verifies that the real workspace command parser recognizes the exact synthetic target. Settings use the custom provider with a nonexistent absolute executable, preventing a real agent launch if a regression takes the execution branch.

After publishing the desktop application, run from the repository root:

```powershell
dotnet run --project desktop/test-support/NotificationApprovalProbe -- "D:\path\to\published-app" "D:\path\to\new-notification-probe-data"
```

The console project has no application project references and does not rebuild the published application. Use a publication containing notification action outcome and target-validation fixes.

The English and Japanese cases cover actual workspace persistence without another confirmation dialog, target selection through filters, rejection, busy and stale actions, revoked access, and a synchronous selection change during reveal. Unavailable actions require unchanged persisted files and no transport calls. A changed selection must never approve or reject a sibling request. Results are printed and saved as `results.json`; any failure returns a nonzero exit code. These checks exercise Inbox behavior; notification activation parsing and service lifecycle require separate service checks.
