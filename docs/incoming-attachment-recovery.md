# Incoming attachment recovery

Received chat attachments are staged inside a generated `.cab-incoming-<GUID>` folder in the request's workspace. Their ownership records live separately in the app data directory under `incoming-attachments`. Generated output files are not registered for this cleanup.

The app attempts recovery in the background at startup. It reads only its bounded ownership registry; it does not recursively search workspaces or delete folders just because their names look temporary. Old folders without an ownership record are left untouched.

Each incoming-file lease holds an exclusive lock. Before a CLI receives attachment paths, its process ID and start time are persisted too, including reused Codex App Server workers. Recovery skips a locked lease or a matching live worker, even if the Bridge process that started it has crashed. If worker identity cannot be recorded, the request fails before its attachment prompt is sent.

On Windows, cleanup verifies the exact generated path, regular-file status, single hardlink, volume/file identity and creation time through the file handle. Completed downloads additionally require the recorded length and SHA-256 to match. Deletion uses that verified handle. Links, replaced or modified files, unknown files inside the staging directory, and unrelated workspace outputs are preserved. Directories are removed only when empty; recursive deletion is never used.

Deletion failures keep the ownership record for a later attempt. Malformed or oversized records are skipped. Recovery is bounded to 256 records and a five-second budget checked between records, and runs off the UI thread. The existing incoming-file size/count limits still apply.

Validation includes focused ownership/lock/process/hash/link tests, runner checks that refuse attachment turns without a native worker identity, and `desktop/IncomingAttachmentRecoveryStartupSmoke.ps1`. The smoke test hard-kills a fixture owner, verifies a real WinUI startup removes a separate abandoned canary while preserving a surviving worker's input, then stops that worker and restarts the app to verify selective cleanup. No real chat account or AI service is used.

Validated on Windows on 2026-10-24: Core 1,071/1,071 tests, including 25 incoming-file tests; transport 251/251 tests; all six startup-smoke checks passed against the ordinary portable build. Local evidence is `desktop/artifacts/4d229a0b-f193-4415-9ac2-fa8203e028c8/results.json`, with `startup-one.png` and `startup-two.png` alongside it. Artifacts are excluded from Git. The verified build is under `desktop/artifacts/attachment-recovery-verified`; nothing was published.
