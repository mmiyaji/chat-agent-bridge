# Settings cancellation regression probe

Run this Windows-only probe against a published desktop build:

```powershell
dotnet run --project desktop/test-support/SettingsCancellationProbe -- <absolute-publish-directory> <new-absolute-test-data-directory>
```

It loads the real shipped ViewModel and exercises cancellation through the confirmation callback. Each test uses a fresh settings store with synthetic credentials. It verifies editor rollback, unchanged saved settings, dependent CLI fields, subsequent saves, unavailable confirmations, later edits while a dialog is pending, and a delayed picker workspace plus fractional number edit during asynchronous prevalidation, in English and Japanese. The prevalidation case holds the ViewModel's captured continuation in a controlled synchronization context, so the picker edits happen after save draft capture and before confirmation without timing sleeps. It does not open a UI, start a CLI, change Windows settings, or connect to a chat service. Results remain in the supplied test directory.
