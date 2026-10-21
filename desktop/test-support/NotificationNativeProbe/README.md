# Native Windows notification probe

An isolated, portable WinUI harness linking the production `PendingRequestNotificationService`, registry, notification enums, and desktop strings. It only registers its own notification executable and runs synthetic in-memory callbacks. It does not connect to CLI/chat, load production settings, change notification permission, or publish a release.

From the repository root, build the self-contained local test distribution:

```powershell
dotnet publish desktop/test-support/NotificationNativeProbe/NotificationNativeProbe.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o .artifacts/notification-native-probe/publish
```

Run the portable executable with an isolated, absolute `--data-dir` argument. Example executable: `.artifacts/notification-native-probe/publish/NotificationNativeProbe.exe`; data directory: `D:\git\chat-agent-bridge\.artifacts\notification-native-probe\run1`. Without the argument it uses a fresh temporary directory. Registration occurs before the main window and `ProcessStartupActivation`, matching production ordering. Close the window to clear this process's pending toasts and unregister activation.

Use `--language ja` or `--language en` to select the notification/service language before registration. The default follows Windows user language, matching production.

For a diagnostic run without computer-use tools, pass `--auto-scenario approve` (or `reject`, `busy`, `expired`). A UI dispatcher timer creates that synthetic request after three seconds, without simulating any notification button click. `--simple-toast` sends a fixed, minimal two-line notification at the same delay, with no buttons, tag, group or explicit expiration. It can run alone to compare against production payload delivery. Each creation records notification history after 500 ms via `GetAllAsync`: count, numeric IDs, tag lengths and groups, never raw tokens or payloads. Startup diagnostic runs also record the read-only `SHQueryUserNotificationState` HRESULT and numeric state. These checks do not alter Windows notification settings.

Use the actual Windows toast buttons, with the probe open:

1. **Create approve scenario**, then click the toast's Approve button. Expect Approve 1, Deny 0, Completed 1.
2. **Create reject scenario**, then click Deny. Expect Approve 0, Deny 1, Completed 1.
3. **Create busy scenario**, then click Approve. Expect Approve 1, Completed 0, Attention 1 and the production busy warning. The service re-shows the toast. **Release busy state** must not execute anything; click Approve on the re-shown toast. Expect Approve 2 and Completed 1.
4. **Create expired scenario**, wait for **expired: ready**, then click either button. Expect both callback counts and Completed to remain zero, Attention 1 and the production expired warning.

Each scenario clears the preceding synthetic entry and resets counters. The expired scenario deliberately uses reflection to reach the service's registry and private production toast builder: the callback lifetime is two seconds, while the OS toast remains visible for five minutes. This test-only difference permits a real OS activation of an expired token; normal production uses the same five-minute lifetime for both. The service's invocation handling is unchanged.

The clearly labelled **Direct approve (bypasses Windows)** and **Direct deny (bypasses Windows)** controls invoke the current token through the production service's private handler on the UI thread, using harness-only reflection. These emit `direct_activation` records and can verify service behavior and warning UI independently. They never prove native toast delivery, Windows button interaction, or OS activation. Only an independent `native_notification_invoked` record is evidence of the raw OS callback.

`Test-NotificationShortcut.ps1` is a separate 64-bit PowerShell diagnostic for a single current-user Start Menu shortcut. Its required `-Mode Create|Verify|Remove`, absolute `-PublishedExe`, absolute `-ShortcutPath`, and optional `-TargetArguments` identify the exact artifact. Only `NotificationNativeProbe.exe` is accepted, and the `.lnk` must be directly within `[Environment]::GetFolderPath('Programs')`. Create refuses existing files. Create/Verify read the SDK's existing per-path NotificationGUID, CustomActivator and exact LocalServer32 executable command; they never write registry or set process identity. It sets the existing AUMID as VT_LPWSTR and activator as VT_CLSID, saves, then reloads target/arguments/working directory/properties from disk. Remove explicitly reloads and verifies the supplied exact target/arguments/working directory before deleting only that `.lnk`. Use the same arguments for verification/removal as creation. `ProbeShortcutInterop.cs` supplies the COM interfaces and PROPVARIANT cleanup.

Shortcut property order and VT_CLSID follow the [Microsoft DesktopToasts sample](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/DesktopToasts/CPP/DesktopToastsSample.cpp). Generated identity lookup follows [Windows App SDK notification utility](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/AppNotifications/AppNotificationUtility.cpp). This diagnostic does not establish that a shortcut fixes OS delivery until an independent native test succeeds.

For the explicit-identity comparison, pass `--app-id ChatAgentBridge.Diagnostics.NotificationProbe.<nonce>` to the harness. Only this diagnostic namespace with ASCII letters/digits/dots/hyphens and at most 128 characters is accepted. It sets the process AUMID before the first notification-manager access, logs setter/getter HRESULT and exact identity readback in `identity-events-PID.jsonl`, and lets the SDK perform its normal registration. After that first run, the shortcut script's matching `-ExplicitAppId` reads that existing identity's registry entry directly rather than the path-mapped GUID; use identical `--app-id` arguments in the shortcut. It never writes registry values itself. Use a fresh nonce per comparison to keep test identities separate.

The shortcut diagnostic's optional `-IconPath` accepts only an absolute existing `.ico` file and sets it at icon index zero. Readback includes `IconPath`/`IconIndex`; Create, Verify and Remove additionally check them when the argument is supplied. This permits a comparison with a standalone icon rather than an executable icon resource.

`--legacy-toast` requires `--app-id` and independently sends a fixed, button-free `ToastGeneric` through `Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(explicitID)` after the startup timer. It logs notifier Setting, Failed HRESULT/message, Dismissed reason, Activated marker, and explicit-ID history count after 500 ms. Event callbacks marshal to the UI dispatcher before logging. Use this flag without `--simple-toast`/`--auto-scenario` for a legacy-only comparison; no decision callback can execute from this diagnostic toast.

`--explicit-assets --simple-toast` selects a standalone unpackaged registration comparison: `Register("Chat Agent Bridge Notification Test", iconUri)` uses the bundled `Assets/AppIcon.ico` absolute filesystem path rather than automatic shell asset discovery. Only the minimal toast is permitted; scenario/legacy flags are rejected and decision controls disabled. Registration evidence logs the icon path, original URI string and manager Setting; closing unregisters the standalone manager. The normal production-service initialization path remains unchanged. Publish this diagnostic to a separate folder to preserve any running packaged control.

`Get-ProbeProcessPackage.ps1 -ProcessId <id>` is a read-only identity check using a limited-information process handle and `GetPackageFullName`. It reports executable, package-presence boolean and package full name; it neither registers identity nor changes the process.

## Measured findings, 2026-10-22 (Asia/Tokyo)

These results describe this machine and interactive user session with Windows App SDK 2.5.1. A successful `Show` and API history entry did not establish visible delivery. Native activation evidence below came from actual Windows notification interaction; no direct activation controls were used for those rows.

| Diagnostic | Observed result |
| --- | --- |
| Original unpackaged notification registration | API accepted/stored notification; user confirmed no banner or Notification Center item. |
| Unpackaged explicit diagnostic AUMID, matching verified shortcut and `.ico` | User confirmed invisible after executable and shortcut launch. |
| Legacy Windows notification API comparison | User confirmed invisible. |
| Explicit-assets registration from a new executable path | Registration succeeded with supplied name/icon; user confirmed invisible. |
| Packaged approval, run10 | Actual native `approve`; approval callback 1, rejection 0, completion 1. |
| Packaged rejection, run12 | Actual native `decline`; rejection callback 1, approval 0, completion 1. |
| Packaged busy, run13 | Actual approval callback returned Busy, warning/attention displayed; releasing busy did not execute automatically. Physical approval retry was **not verified**: the later native action was a body `open`. |
| Packaged expired token, run16 | Actual native approval after test expiry; both decision callbacks and completion remained 0, expired warning/attention appeared. |
| Same AppX executable path after diagnostic package removal, run17 | `GetPackageFullName` confirmed no identity; API accepted/stored notification; user confirmed invisible. |

For the packaged-positive and same-path unregistered comparison, executable SHA256 was identical: `51890D2CE26FF74119C7E2DA385C1FE138676CABB2CC477DD52BD011A72C79A5`. This isolates package registration/identity as the observed difference in this control, without identifying the precise Windows Shell failure. It does **not** prove that MSIX is universally required for local notifications. Microsoft [documents automatic COM registration for unpackaged applications](https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet); [Windows App SDK issue 6821](https://github.com/microsoft/WindowsAppSDK/issues/6821) reports a similar accepted-but-invisible unpackaged symptom. The explicit-assets same-Case7-path run was not independently confirmed and is not counted as a result.

All synthetic probe processes, the diagnostic debug package, and its test shortcut were removed after testing. The existing product process was left running. Artifact directories were preserved locally; no event logs or request tokens belong in source control.

## Reproduce the packaged positive control

This is a reversible, current-user **development package registration**, not a portable deployment fix. It uses the same published harness files, an isolated copy, and the existing local development prerequisites. Do not change Developer Mode, certificate trust or security settings as part of this diagnostic. Stop if registration reports a missing prerequisite.

From the repository root, publish as above, then create a fresh separate destination (the example must not already contain an unrelated test):

```powershell
Copy-Item .artifacts/notification-native-probe/publish .artifacts/notification-packaged-control -Recurse
Push-Location .artifacts/notification-packaged-control
winapp manifest generate . --package-name ChatAgentBridge.NotificationPackagedProbe --publisher-name CN=ChatAgentBridgeDiagnostics --entrypoint NotificationNativeProbe.exe --template packaged --description 'Isolated notification packaged identity diagnostic'
Pop-Location
Copy-Item desktop/test-support/NotificationNativeProbe/PackagedControl.manifest.xml .artifacts/notification-packaged-control/Package.appxmanifest
winapp run .artifacts/notification-packaged-control --manifest .artifacts/notification-packaged-control/Package.appxmanifest --exe NotificationNativeProbe.exe --detach --json -- --language ja --data-dir D:\git\chat-agent-bridge\.artifacts\notification-native-probe\run-positive --auto-scenario approve
```

Use an absolute isolated data directory appropriate to your checkout. The generated assets remain local; only the small manifest template is checked in. The template declares matching `desktop:ToastNotificationActivation` and `com:ExeServer` CLSIDs, exact executable name, and `----AppNotificationActivated:` COM arguments, as required by the Microsoft setup documentation. Do not pass `--app-id` for this packaged control. `winapp run` returns its AUMID and PID; verify process path before stopping it. For cleanup, verify `Get-AppxPackage -Name ChatAgentBridge.NotificationPackagedProbe` has the expected full name, isolated `AppX` install location and development-mode flag, then remove only that exact full package name. Preserve files when testing the same-path negative control; confirm identity absence using `Get-ProbeProcessPackage.ps1` before interpreting visibility.

`events-PID.jsonl` records scenario, service status, callbacks, completion and attention with timestamps/counters. `native-events-PID.jsonl` independently records the raw OS `NotificationInvoked` event's action and whether a request key was present; that observer is installed before production initialization. Neither file records the opaque request token or any real user data. Toast visibility and activation require an interactive Windows desktop with notifications already enabled. Do not change user settings to make the test pass.
