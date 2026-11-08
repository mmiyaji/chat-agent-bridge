# README screenshots

Captured on Windows on 2026-11-08 from the current native desktop development
build. The images show General, Agent settings, shared Chat settings, and the
History and usage Activity tab.

- `*-ja.png`: Japanese, dark appearance.
- `*-en.png`: English, light appearance.
- Each image is a 1426 × 973 native window capture. UI inspection overlays were
  excluded; no application content was altered.
- The app used separate demo data directories, not the user's normal profile.
- Chat was disconnected, tokens were absent, and no AI requests were made.
- History screenshots show 70 generated aggregate runs across 28 active days and
  52 detailed execution records still within the default 30-day history period.
  Older detail records have expired while the longer-lived statistics remain.
  Counts, tokens and model names are sample data, not performance results.
- Agent screenshots show Codex selected with its CLI unverified and a generic
  sample workspace path. Chat settings show no credentials and the app remains
  disconnected.

When refreshing, use an isolated profile with no credentials, disable update and
price-table network checks, capture the same four pages in both languages, and
review every image for clipping, error banners and sensitive information before
replacing the files. Update the screenshot date in both root READMEs and note if
the Partner Center draft still contains an earlier image set.

The Store screenshot assets are in `../store-screenshots/{ja,en}/`. Each
language has four unannotated native app captures and one chat-flow composite.
The Store listing is paused; these files have not been uploaded to Partner
Center. Storeframe checked the eight native captures against the cached MSIX
desktop target, with no machine-check issues. Both composites were separately
checked for PNG format, 2560 × 1440 dimensions, and file size under 50 MB.
Microsoft lists a minimum of 1366 × 768 pixels and a 50 MB maximum per
screenshot. See the current [Microsoft screenshot
requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images).

## Chat client demos

`mattermost-demo-running-en.png` and `mattermost-demo-completed-en.png` are the
English versions; the un-suffixed files are identical copies for existing links.
They show the current CAB app connected to a dedicated channel on the local
Mattermost test Docker server. The UI is English, progress reporting is set to
Standard, and the request only waits before replying; it does not read, create,
or modify files. The channel and accounts are generic test identities. No real
workspace, service, or personal account data is shown.

`slack-demo-completed-ja.png` and `slack-demo-running-ja.png` show the Japanese
Slack thread. `slack-demo-completed-en.png` shows the English Slack interface
and request/reply; CAB's status labels in that earlier capture are Japanese.
`slack-demo-running-en.png` shows both the English Slack interface and CAB's
English in-progress status. These captures use the existing `#cab` channel.
Human account names and avatars are redacted; screenshots are cropped to the
thread, excluding workspace navigation and account context. The English
in-progress request only waits before replying and does not access or modify
files.

## Store chat-flow composites

`../store-screenshots/ja/chat-service-flow.png` and
`../store-screenshots/en/chat-service-flow.png` combine real Mattermost and Slack
thread captures into a two-column, running-to-completed flow. Both are PNGs at
2560 × 1440 (under 50 MB); the source mapping and SHA-256 values are recorded in
`../store-screenshots/chat-flow-manifest.json`. The Japanese image labels that
the Mattermost UI capture is English. The English image notes that the Slack
completion capture has Japanese bot status text. Copies for the root READMEs
are `chat-service-flow-ja.png` and `chat-service-flow-en.png` in this directory.
The composites are also included in the website gallery with a Japanese/English
language switch.

These are annotated composites, made because the two-service state transition
needs to be presented in one image. Microsoft Store's technical screenshot
minimums are met, but its content guidance recommends unaltered app screenshots
without marketing overlays. Treat these files as requiring manual content
review before any future submission. The Store listing remains paused and the
images have not been uploaded to Partner Center.
