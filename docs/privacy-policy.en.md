Chat Agent Bridge Privacy Policy (Development Preview)
Document version: 2026-10-20.2
Provider: mmiyaji
Contact: mail@ruhenheim.org

1. Information handled and purposes
The application connects chat services configured by you with a local CLI to run requests, continue conversations, control access, and show status. It handles request text, execution results, account, channel and thread IDs, settings such as working folders and models, session identifiers, execution status, and usage information.

2. Storage on this PC
Connection and execution settings, session associations, execution history and audit information, and the version and date of documents you accepted are stored in this Windows user's application data folder. Connection tokens are encrypted with Windows DPAPI. Bridge history and audit files ordinarily do not store request or result text, but can include working paths and identifiers. After you register a CLI connection, excerpts of up to three recent successful requests and results are saved in a separate file encrypted with Windows DPAPI. At most 200 conversation notes are retained, each up to 8,000 characters. A note is attached to the next request to the target CLI only when you select summary transfer. Text is also handled in memory for display and processing, and may also be stored separately by a CLI or external service. Encryption does not guarantee protection from a PC administrator or a compromised PC.

For the optional "first" file-approval mode, the application stores a bounded consent record in its data folder. The record contains conversation and native-session identifiers, a fingerprint of the conversation and attachment settings, and the approval time; it does not contain file names, file contents, or per-file hashes. Discovered file candidates (including names, workspace paths, sizes, timestamps, and hashes) are held in memory. The bridge does not save those candidates or create a separate on-disk staging copy of file contents. After approval, the bytes selected for transfer are held in memory while they are uploaded.

3. External communications and sharing
The application exchanges requests, responses, and connection status with Slack, Discord, or the Mattermost server you configure. During execution, it passes the request and information needed for the task to the selected CLI. Depending on its configuration and provider policies, the CLI may send data to AI services. Running a CLI on a local PC does not mean that all AI processing or data storage happens on that PC. Review the privacy policies and agreements of each external service.

Results shared to chat and details of operation approvals are visible to people who can view that channel or thread. The authorized-user setting does not limit who can view posts on the chat service. Mattermost connection and disconnection notices that do not include message text may also be sent when that option is enabled.

When chat result sharing is enabled, newly created workspace files can be sent under a separate file-attachment approval setting. In the default mode, selected file names, contents, and sizes are sent to the Slack, Mattermost, or Discord service configured for the conversation only after you select and approve each transfer; text execution or result-sharing approval does not approve files. The optional "first" mode requires an initial approval and can then automatically send later eligible files from the same native session within the configured limits. The optional "auto" mode sends eligible files without a per-transfer prompt after you explicitly enable and save that setting. Limits are configurable from 1–100 MiB per file, 1–10 files, and 1–100 MiB total; the defaults are 10 MiB, 3 files, and 20 MiB total. The application excludes certain file names and types, but does not inspect file contents to detect secrets. Review files before approving them. Sent files are visible to people who can view the recipient channel or thread and are subject to that chat provider's handling and retention policies.

4. Information sent to the provider
This development preview does not include automatic sending of usage information or conversation text to a bridge-operated analytics or advertising service. Information handling by the OS, distribution store, CLI, chat, and AI services is governed by each provider's policies. If you send information to the provider, for example in a support request, limit it to what is needed and remove tokens and secrets.

5. Retention, deletion, and stopping use
Stored data may remain on the PC until you delete it. You can delete bridge-stored information by closing the application and deleting the data in the settings storage location shown on the General screen. Uninstallation behavior depends on the Windows distribution method. Deleting bridge data does not delete conversation history held by chat or AI services or the CLI; manage that history in each service or CLI.

6. Contact
For questions about this application's handling of data, contact provider mmiyaji at mail@ruhenheim.org. Do not post tokens or confidential request or result information in public chats or public support fields.
