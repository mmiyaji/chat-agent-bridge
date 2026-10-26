# Inbox reconnect probe

Run the published `ChatAgentBridge.Desktop.dll` against a new, isolated data directory:

```powershell
dotnet run --project desktop/test-support/InboxReconnectProbe/InboxReconnectProbe.csproj -- <published-app-directory> <new-test-data-directory>
```

The probe loads the published ViewModel directly, injects an in-memory recording transport, and writes `results.json` into the isolated data directory. Its agent executable points to a nonexistent file inside that directory. It does not open a WinUI window, invoke a CLI, or make a network connection. Use a new data directory for each run.
