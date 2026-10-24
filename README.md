# Chat Agent Bridge

**English** · [日本語](README.ja.md) · [Download](https://github.com/mmiyaji/chat-agent-bridge/releases)

[![Latest release](https://img.shields.io/github/v/release/mmiyaji/chat-agent-bridge?include_prereleases&label=release)](https://github.com/mmiyaji/chat-agent-bridge/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

> **Development preview.** This is an unofficial app for personal workflows, and its behavior and screens may change without notice. It is not an official OpenAI, Anthropic, Google, GitHub, Slack or Mattermost app.

Chat Agent Bridge is a Windows app that accepts requests from Slack or Mattermost and runs AI agents on your PC. You can send requests from a phone or another computer, review results and add instructions in the same thread.

It is intended for devices where you cannot use the Codex or Claude Code desktop app. Bridge starts a command-line agent (CLI) installed on your Windows PC and runs the request in the working folder you selected.

> Prefer the agent provider's official remote-control features when they are available to you. Chat Agent Bridge is developed as an alternative for environments where those official features cannot be used.

## Using it safely

A chat post causes the CLI on your PC to read and write files and run commands. Follow these guidelines:

- **Allow only yourself.** An allowed user can send requests that use your PC, your CLI sign-in and your AI service quota. Posts from users who are not allowed are ignored.
- **Limit the working folders.** Register only folders the agent may touch as trusted working folders. Avoid your home folder or an entire drive.
- **Use automatic approval and sharing with care.** Automatic approval runs requests without confirmation on the PC. Automatic sharing makes results visible to everyone who can read the thread. Defaults require approval for the first request in a conversation and manual result sharing.
- **Narrow the channels and threads.** If needed, restrict which channels and threads can send requests.
- **Do not share your connection tokens.** Tokens are encrypted per Windows user, but do not hand the `Data` folder to anyone else.

## System overview and request flow

![Requests travel from Slack or Mattermost on another device to Chat Agent Bridge and a CLI on the Windows PC; results return to chat](desktop/ChatAgentBridge.Desktop/Assets/OverviewArchitecture.en.svg)

This is the diagram shown in the app's setup guide and General page. It uses Codex and Claude Code as examples. Gemini CLI and GitHub Copilot CLI use the same arrangement.

1. **Post from another device** in Slack or Mattermost. That device does not need an agent CLI installed.
2. **Bridge on the Windows PC** receives the post and checks allowed users and conversation approval settings. Requests that need approval wait for a decision.
3. **The selected CLI** runs the request in the configured working folder. It connects to AI services according to its own settings and performs file operations or commands.
4. **Share the result in the original thread.** By default, you review it on the PC before sending it; automatic replies are also available. Replies from the same user in that thread continue the conversation (see [Chat operations](#chat-operations)).

## Requirements

| Item | Details |
| --- | --- |
| OS | Windows 10 (version 1809 or later) or Windows 11 |
| CPU | x64 / ARM64 (a separate ZIP for each) |
| Additional installs | None (no administrator rights or separate .NET runtime required) |
| CLI | Codex, Claude Code, Gemini CLI, GitHub Copilot CLI or another CLI (install and sign in yourself) |
| Chat | Slack (Socket Mode), Mattermost (bot token) |
| Network | The PC connects out to the chat service; no public URL or open port is needed |

## Main features

| Feature | Description |
| --- | --- |
| Chat requests | Slack and Mattermost requests with thread-based conversation continuation |
| Agent and model changes | Change them from the PC or chat; choose whether to pass conversation notes when switching agents |
| Progress and cancellation | Chat progress posts, live output on the PC and request cancellation |
| Additional instructions | Queue messages or add them to a running request on supported agents |
| Access restrictions | Allowed users, channels, threads and trusted working folders |
| Approval and sharing | Request approvals, result sharing, attachment approvals and upload limits |
| History and usage | Conversation management, execution and operation history, an activity calendar, and token, cache and estimated-cost charts |
| Startup and appearance | Setup wizard, startup and auto-connect, system tray mode, English/Japanese and light/dark appearance |

## Installation and setup

Portable builds are available for Windows. Install and authenticate your chosen CLI and prepare the chat bot separately.

1. Download the **x64 / ARM64** ZIP for your PC from [GitHub Releases](https://github.com/mmiyaji/chat-agent-bridge/releases).
2. Extract the entire folder to a writable location and run `ChatAgentBridge.Desktop.exe`.
3. Install the CLI and sign in through it. Select the CLI and working folder in the setup guide, then run the CLI check.
4. Prepare a bot on the chat service, then configure connection details and allowed users in **Chat settings**.
   - Slack: [Required Slack app capabilities](docs/slack-setup.md#required-app-capabilities) (Socket Mode app token, events and scopes)
   - Mattermost: [Mattermost bot and DM setup](docs/mattermost-setup.md#native-windows-app-bot-dm-and-request-format) (bot token)
5. Connect from **Chat** and post a request. How to start depends on the service (see [Chat operations](#chat-operations)).
6. Approve the first request on the PC. After execution, review and share the result. Reply in the same thread to continue.

Defaults require approval for the first request in a conversation, manual result sharing and approval for every attachment. The CLI check identifies the CLI and version. Check authentication and available quota through the CLI itself.

## Supported agents

The working folder and trusted locations are shared settings. Configure the executable and model separately for each agent. You can also select the CLI executable with a file picker.

| Agent | Conversation continuation | Model settings |
| --- | --- | --- |
| Codex / Claude Code | Supported | Select CLI-provided model and supported reasoning-effort candidates |
| Gemini CLI / GitHub Copilot CLI | Supported | Enter a model name; reasoning settings remain CLI-managed |
| Other CLI | Independent execution for each request | Configure the CLI or its launch arguments |

Change a conversation's agent or model from its details on the PC or from chat. Agent switching uses CLIs checked and registered in the app. You choose whether to pass conversation notes to the new agent.

Codex App Server and Claude Code also support adding instructions to running requests. This applies to posts from the same user in the same thread. Other connections and per-request approval settings queue messages.

See the [per-CLI support guide (Japanese)](desktop/README.ja.md#利用できるcli) for version and authentication requirements.

<details>
<summary>Agent settings screenshot</summary>

![Shared working folder and per-CLI execution settings](docs/assets/screenshots/agents-en.png)

</details>

## Approvals, access and sharing

**Chat settings** contains approval and sharing rules common to all chat services, along with each service's connection details and allowed users.

- **Request approval** can be required every time, only for the first request, or handled automatically. This approval starts the request; the CLI's permission to perform individual operations is configured separately.
- **Access restrictions** specify who and which conversations can send requests. Register folders where requests may start as trusted working folders. Changing the folder from chat requires a location within that scope and approval on the PC.
- **Result sharing** can be manual, automatic or disabled. Sent content is visible to everyone who can access the destination chat or thread.
- **Generated file attachments** require approval each time by default. You can instead require approval only for the first attachment in a thread, allow automatic attachments or prohibit attachments. Set per-file size, total size and file count limits.
- **Sharing approvals in chat** can be enabled only when using Codex App Server. When enabled, the original requester can approve or reject CLI operations (commands, file changes) by replying `!cab approve <code>` / `!cab reject <code>` in the same thread. It is off by default, and approvals happen on the PC. Approving a request to start always happens on the PC.

![Request approvals, result sharing, additional instructions and file attachment settings](docs/assets/screenshots/chat-settings-en.png)

## Chat operations

Actions are tied to the requester and the conversation (channel, thread and user). Posts from other users in the same thread are not treated as part of your conversation.

| Action | Slack | Mattermost |
| --- | --- | --- |
| Start a request | Mention the bot | Mention the bot, or `!cab <request>` |
| Continue | Reply in the same thread | Reply in the same thread |
| Check status | `!cab status` | `!cab status` |
| Stop | `!cab stop` | `!cab stop`, or the 🛑 reaction on the progress post |

- **Continuing**: after the first request, replies in the same thread become follow-up requests. No mention or `!cab` is needed.
- **After restarting the app**: requests that were awaiting approval or running are not carried over and are never resumed automatically. To continue, start a new request in the same thread (mention in Slack; mention or `!cab` in Mattermost).
- **Stopping**: only the original requester can use the reaction. Stopping does not undo file changes already made.

| Command | Purpose |
| --- | --- |
| `!cab command` | List available commands |
| `!cab status` / `!cab stop` | Check or stop your requests |
| `!cab model` | View the current model and how to change it |
| `!cab agent` | List registered agents and switching options |
| `!cab folder "C:\Projects\MyApp"` | Request a working-folder change |

`!cab status` lists running, approval-waiting and queued requests for your current thread, including agent, model and working folder. It does not expose requests from other users or threads. The reply is visible to people who can read that thread.

You can also inspect live output and stop a request on the PC.

## History and usage

Find saved conversations by name or working folder and change their model or agent. Review execution times, statuses, operation records and usage recorded by this app.

![Daily activity calendar with sample records](docs/assets/screenshots/history-en.png)

Usage is shown by day and model. Token and cache figures use CLI-reported values; missing values remain unknown. Estimated costs use public pricing and are reference values, not bills or subscription quotas. Price tables can update automatically, with manual overrides taking priority over downloaded prices.

## Stored data

Settings and history are stored in the `Data` folder next to the app.

| Stored | Not stored |
| --- | --- |
| Connection settings (tokens encrypted with DPAPI for your Windows user) | Request bodies |
| Data used to continue conversations (conversation identifiers, working folders, CLI session IDs) | Result bodies |
| Execution and operation history (times, statuses, conversation identifiers; up to 500 entries) | CLI credentials (each CLI manages its own) |
| Daily and per-model usage totals | |

Delete stored data by type from data management on the **General** page. Encrypted tokens can only be decrypted by the same Windows user, so moving to another PC requires reconfiguring chat tokens and CLI sign-in. See the [Privacy Policy](docs/privacy-policy.en.md) for details.

## Appearance, startup and updates

The app uses Japanese when the Windows language preference is Japanese and English otherwise. You can override the language in the app. Choose light, dark or system appearance.

**General** contains Windows sign-in startup, app auto-connect and system tray preferences. You can also choose whether closing the window moves it to the tray or exits the app.

![Current status, startup, auto-connect and system tray settings](docs/assets/screenshots/general-en.png)

Portable builds can check for and apply updates from **About**. The `Data` folder is retained during updates. For manual updates, close the app and replace the files while keeping `Data`.

Screenshots show a development build using dedicated demo settings and sample usage records. They contain no real conversations or credentials. Released versions may differ.

## Uninstalling

1. On the **General** page, turn off starting the app at Windows sign-in. Startup is registered in your Windows user settings, so turn it off before deleting the folder.
2. Exit the app, including from the system tray.
3. Delete the extracted folder. `Data` contains encrypted connection tokens and history; delete it too unless you need it.
4. If needed, revoke the bot tokens in Slack or Mattermost.

## Known limitations

- Windows only; macOS and Linux are not supported.
- Requests cannot be processed while the PC is asleep, signed out or the app is closed. Keep the PC running with Bridge connected to chat (it keeps working in the system tray).
- Requests interrupted by an app restart are not resumed automatically.
- Other CLIs do not continue conversations; each request runs independently.
- Some Gemini CLI live-service checks remain incomplete because of authentication-environment restrictions.
- Estimated costs are reference values and may not match actual bills.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| The bot does not respond | Whether **Chat** is connected, the poster is an allowed user, the channel or thread is within the allowed scope, and the request was started correctly ([Chat operations](#chat-operations)) |
| The CLI is not found | Run the **CLI check** in the setup guide or agent settings. Right after installing or updating a CLI, use "Reload environment variables" |
| Claude Code reports "Not logged in" | Start Claude Code in a terminal as this Windows user, complete `/login`, then post the request again |
| A request stays awaiting approval | Starting a request requires approval on the PC by default. Approve it in **Chat** on the PC or from the Windows notification |
| Results do not reach the thread | Results are shared manually by default. Review them on the PC and use "Send results to thread" |

## License

Released under the [MIT License](LICENSE). Check the terms and contracts of the chat and AI services you use separately.

## Project and documentation

- [Terms of Use](docs/terms.en.md) / [Privacy Policy](docs/privacy-policy.en.md)
- [Windows app guide and development instructions (Japanese)](desktop/README.ja.md)
- [Portable edition guide](desktop/distribution/README.portable.en.txt)
- [Report a bug or suggest a feature](https://github.com/mmiyaji/chat-agent-bridge/issues)
- [Third-party notices](desktop/THIRD-PARTY-NOTICES.txt), also available in the app's About page

Documentation for the earlier TypeScript implementation has moved to [docs/legacy](docs/legacy/). Those instructions differ from the current Windows app.
