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

`events-PID.jsonl` records scenario, service status, callbacks, completion and attention with timestamps/counters. `native-events-PID.jsonl` independently records the raw OS `NotificationInvoked` event's action and whether a request key was present; that observer is installed before production initialization. Neither file records the opaque request token or any real user data. Toast visibility and activation require an interactive Windows desktop with notifications already enabled. Do not change user settings to make the test pass.
