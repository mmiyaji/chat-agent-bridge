# Inbox confirmation cancellation probe

Runs the published desktop ViewModel through reflection without opening WinUI or starting an agent or chat connection. A recording `IChatTransport` proxy completes replies in memory and rejects attempts to start a transport. All settings, sessions, and synthetic registered profiles live below a new test directory selected through `CAB_WINUI_DATA_DIR`; an existing directory is rejected.

After publishing the desktop application, run from the repository root:

```powershell
dotnet run --project desktop/test-support/InboxCancellationProbe -- "D:\path\to\published-app" "D:\path\to\new-inbox-probe-data"
```

The project has no references to application projects, so compiling it does not rebuild or modify the published application. Use a publication containing the preview rollback fixes.

The probe covers workspace and agent confirmation rejection, missing callbacks, dialog cancellation exceptions, independent later workspace/session display updates, and accepted persistence in English and Japanese. Rejected cases require byte-identical persisted files and no transport calls. Accepted cases require saved changes and exactly one in-memory reply. Results are printed and saved as `results.json` in the test directory; failures return a nonzero exit code. No real credentials or connections are used.
