Chat Agent Bridge WinUI Preview
==============================

This ZIP installs a self-contained MSIX app for Windows. It is a preview release, not a Microsoft Store release. The package supports x64 and ARM64; Windows 11 is recommended. The manifest minimum is Windows 10 version 1809 (build 17763). ARM64 support is packaged when that architecture is selected, but availability does not mean every ARM64 device has been tested.

No separate .NET runtime or Windows App SDK installation is required. The ZIP contains the MSIX and installer, not a standalone EXE: install it, then launch Chat Agent Bridge from the Start menu. The package contains no personal settings. Install and sign in to your preferred agent CLI separately, then configure its executable, model, and working folder in the app. Configure Slack or Mattermost separately if you want chat connections.

Verify the release files before installation:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -ValidateOnly

The script checks the package hash, signature signer thumbprint, publisher certificate, and preview package identity. Compare the displayed certificate thumbprint with the value published on the [official GitHub releases page](https://github.com/mmiyaji/chat-agent-bridge/releases) before trusting it. The preview uses a self-signed certificate, so Windows will not trust it by default.

If the verified publisher is not trusted, import the included public CER into Local Computer > Trusted People. Open an elevated PowerShell in the extracted ZIP directory and run:

    Import-Certificate -FilePath .\ChatAgentBridge.WinUI.Preview.cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'

Import only the public CER whose thumbprint matches the official release. Never import or distribute a PFX; it contains the private signing key. Close the elevated window, then install as your normal Windows user:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1

The installer does not elevate or change certificate trust. Launch the app from the Start menu. To install a later download, verify it and run its installer again; the matching package identity updates the existing preview installation. To remove the app, open Windows Settings > Apps > Installed apps, find Chat Agent Bridge (WinUI Preview), and uninstall it. When you stop using this preview certificate, remove it from Local Computer > Trusted People.
