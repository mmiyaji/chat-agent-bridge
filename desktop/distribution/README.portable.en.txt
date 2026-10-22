Chat Agent Bridge Portable

This is an unpackaged preview for Windows x64 or ARM64. Windows 11 is recommended; the minimum Windows 10 version is 1809 (build 17763). The app includes .NET and Windows App SDK, so separate runtime installation is unnecessary. It needs no installer, MSIX registration, certificate installation, or Windows Developer Mode.

Run the app
1. Extract the complete ChatAgentBridge.Portable folder from the ZIP to a writable location.
2. Start ChatAgentBridge.Desktop.exe from that folder.
3. On first launch, the app creates a Data folder beside the executable for its data. The distribution ZIP contains no user data or settings.

Credentials and data
Install the CLI you plan to use, such as Codex, Claude Code, Gemini CLI, or GitHub Copilot CLI, and sign in through that CLI. Slack / Mattermost connection tokens saved by the app are protected with Windows DPAPI and work with the same Windows user on the same PC. Before moving to another PC, clear the connection tokens and save on the original PC, then re-enter them on the destination. Copying encrypted settings directly prevents them from loading safely; close the app and move Data/settings.json aside to start setup again. Also check the CLI login and working folder on the destination. Keep the app folder, including its Data folder, secure because it contains user data.

Startup and removal
Before moving the app folder, turn off Windows sign-in startup in the app's General settings. Turn it back on after moving if you want it. To remove the app, first turn off startup, close the app, and then delete its folder. A per-user Windows notification registration may remain. The app does not install software machine-wide.

Optional notification identity
Standard notifications use the existing registration without extra setup. Builds that include a signed Identity folder offer a per-user package registration under General > Windows notifications on Windows 10 version 2004 or later. Windows must trust the package signer; certificate trust is never changed automatically. If you enable this option, remove its registration from the same screen before moving or deleting the app folder. Settings and history are retained. Updates must also include a compatible Identity folder; an update that would remove it is rejected safely.

Updating
By default, the app checks for portable updates at startup and periodically while it is running, including when it is in the system tray. You can turn automatic checks off or include preview releases under About > Portable updates. These checks only look for a release; they never download or apply one automatically. You can also choose Check for updates in About. Review the offered version, download it, then choose Apply in the confirmation. If you choose Later, the verified download stays ready when you close and reopen the app, and the existing update screen offers it again. Applying an update requires your confirmation and restarts the app. The updater replaces only files listed in portable-files.json; your Data folder and other unlisted files are kept. Keep a separate backup of Data before updating. The updater runs from a self-contained Windows executable and does not require PowerShell or administrator access. Older portable releases do not include the updater, so download and extract a current ZIP once before using in-app updates.

Files
- About > Third-party licenses shows the included licenses and copyright notices offline. THIRD-PARTY-NOTICES.txt and Assets/Licenses contain the same notices in the distribution folder; retain them when copying the app.
- SHA256SUMS.txt contains SHA-256 hashes for the payload files inside the ZIP.
- portable-release.json identifies the release version and x64 or ARM64 package architecture.
- portable-files.json lists the files managed by the updater.
- Use the accompanying .sha256 file from the distribution source to verify the downloaded ZIP.

This is a preview release. Keep a separate backup of important data.
