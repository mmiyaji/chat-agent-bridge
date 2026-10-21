# Manual workspace regression probe

Run on Windows against a newly published desktop build:

```powershell
dotnet run --project desktop/test-support/ManualWorkspaceProbe -- <absolute-publish-directory> <new-absolute-test-root>
```

The probe loads the published `MainPageViewModel` and Core assemblies through reflection, without project references or Core.Tests dependencies. Each English/Japanese case gets a fresh settings store beneath the supplied root, selected through the process-local `CAB_WINUI_DATA_DIR`. The original environment value is restored before exit. Credentials are empty, auto-connect is disabled, and the configured agent executable is an asserted nonexistent file.

It checks persisted untrusted/missing/relative workspaces before execution confirmation, running state, history, and audit; valid root/child/empty-trust compatibility through a synthetic canceled confirmation; warning refresh after load/save; recovery path, object identity, and stale-setting rejection; and revalidation when a directory becomes an escaping junction while confirmation is pending. The junction helper uses a hidden `cmd.exe /d /c mklink /J` only inside the new test root, as the Core test helper does. No agent CLI, UI, chat connection, certificate, notification, DND, or actual user settings are used.

Only `PASS <case-name>` lines are printed. All cases, failures, localized warnings, and assembly hashes are preserved in `<new-absolute-test-root>/results.json`; failures produce exit code 1. Fixture files and the escaping junction remain for inspection. Do not recursively traverse or delete the junction target when cleaning up evidence.
