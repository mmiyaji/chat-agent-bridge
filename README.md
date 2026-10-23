# Chat Agent Bridge

**English** · [日本語](README.ja.md) · [Download](https://github.com/mmiyaji/chat-agent-bridge/releases)

**Use Slack or Mattermost as the interface to AI agents on your own PC.**

Send a request from your phone or another computer and let an AI agent work in a project on your Windows PC. Keep working through familiar chat threads when you cannot use the Codex or Claude Code desktop app on the device at hand.

Chat Agent Bridge handles the requests, approvals and replies between chat and your local CLI. Your working folder and CLI stay on the Windows PC, which must be running and online.

![General: connection status and startup preferences](docs/assets/screenshots/general-en.png)

> Screenshots show the development build running on a Windows PC on October 24, 2026, using dedicated demo settings and sample usage records. They contain no real conversations or credentials. This README shows light mode; the Japanese README shows dark mode. Released versions may differ.

## What you can do

| Goal | Features |
| --- | --- |
| Work from chat | Send requests through Slack or Mattermost and continue with replies from the same user in the same thread |
| Choose your agent | Switch agents and models, choosing whether to pass along conversation context |
| Follow the work | Chat progress updates, live output on the PC, and request cancellation |
| Send another instruction | Queue it, or add it to a running request on supported agents |
| Control access | Allowed users, channels, threads and trusted working folders |
| Control sharing | Conversation approvals, manual or automatic result sharing, and attachment approval, size and count limits |
| Review your activity | Conversations, execution and operation history, an activity calendar, and token, cache and estimated-cost charts |
| Keep it convenient | Setup wizard, startup and auto-connect preferences, system tray mode and portable updates |

## Get started

1. Download the Windows **x64 / ARM64** portable ZIP for your PC from [GitHub Releases](https://github.com/mmiyaji/chat-agent-bridge/releases).
2. Extract the entire folder to a writable location and run `ChatAgentBridge.Desktop.exe`. No administrator rights or separate .NET runtime installation is required.
3. Install your chosen agent CLI separately and sign in through that CLI. Select the CLI and working folder in the setup wizard, then check that the app recognizes it.
4. In **Chat settings**, configure your Slack or Mattermost bot and allowed users. Connect from **Chat**. You will need a Slack app with the required permissions, or a Mattermost bot token.
5. Mention the bot to send a request. In Mattermost, you can also start with `!cab <your request>`. Approve the first request on the PC, then reply in the same thread to continue.

Defaults require approval for the first request in a conversation, manual result sharing, and approval for every attachment. A CLI check verifies recognition; it does not guarantee a valid login or available quota.

## Choose agents and models

Manage shared working-folder and trust settings separately from each agent's CLI and model configuration. You can browse for a CLI executable and change a conversation's agent or model from the PC or chat.

| Agent | Conversation continuation | Model settings |
| --- | --- | --- |
| Codex / Claude Code | Supported | Select CLI-provided model and supported reasoning-effort candidates |
| Gemini CLI / GitHub Copilot CLI | Supported | Enter a model name; reasoning settings remain CLI-managed |
| Other CLI | Independent execution for each request | Configure the CLI or its launch arguments |

Codex App Server and Claude Code support additions to running requests. Other connections queue them. Availability depends on the CLI version, authentication and account. Gemini live-service validation remains limited by the authentication environment. See the [per-CLI support guide (Japanese)](desktop/README.ja.md#利用できるcli).

<details>
<summary>Agent settings screenshot</summary>

![Agent settings: shared and per-CLI configuration](docs/assets/screenshots/agents-en.png)

</details>

## Decide what runs and what gets shared

**Chat settings** separates approval and sharing rules common to both services from their connection details and allowed users.

- Require approval for every request, only the first request, or allow requests automatically. This is separate from the CLI's permission to perform individual operations.
- Share results manually, automatically, or not at all. Sent content is visible to everyone who can access the destination chat or thread.
- File attachments require approval each time by default. You can instead require approval only for the first attachment in a thread, allow automatic attachments, or prohibit attachments. Set file size and count limits too.
- Changing the working folder from chat still requires a trusted location and approval on the PC.

![Chat settings: approvals, sharing, additional instructions and attachments](docs/assets/screenshots/chat-settings-en.png)

Common actions are also available in chat.

| Command | Purpose |
| --- | --- |
| `!cab command` | List available commands |
| `!cab status` / `!cab stop` | Check or stop your requests |
| `!cab model` | View the current model and how to change it |
| `!cab agent` | List registered agents and switching options |
| `!cab folder "C:\Projects\MyApp"` | Request a working-folder change |

In Mattermost, the requester can also click the 🛑 reaction on the bot's progress post to cancel that request. Stopping does not undo file changes already made.

## Review history and usage

Find saved conversations by name or working folder and change their model or agent. Review execution and operation history alongside daily activity and usage by model.

![History and usage: activity calendar with sample records](docs/assets/screenshots/history-en.png)

Token and cache figures cover what the CLI reports; missing values are not guessed. Estimated costs are reference values, not your bill or subscription quota. Automatic price-table updates and higher-priority manual price overrides are available.

## At home on Windows

The app supports English and Japanese, selecting Japanese for a Japanese Windows language preference and falling back to English otherwise. Choose light, dark or system appearance.

**General** groups startup, auto-connect and system tray preferences. The app can keep running in the tray or exit completely. It cannot operate while the PC is asleep or signed out.

Portable builds can check for and apply updates from **About**, retaining the adjacent `Data` folder. For manual updates, close the app and keep `Data`. Moving to another PC requires reconfiguring chat tokens and CLI authentication.

## About this project and further reading

This is an unofficial development preview for personal workflows, not an official OpenAI, Anthropic, Google, GitHub, Slack or Mattermost app. Requests and results are sent to the configured AI and chat services. Read the [Terms of Use](docs/terms.en.md) and [Privacy Policy](docs/privacy-policy.en.md) before use.

- [Windows app guide and development instructions (Japanese)](desktop/README.ja.md)
- [Portable edition guide](desktop/distribution/README.portable.en.txt)
- [Report a bug or suggest a feature](https://github.com/mmiyaji/chat-agent-bridge/issues)
- [Third-party notices](desktop/THIRD-PARTY-NOTICES.txt), also available in the app's About page

Documentation for the earlier TypeScript implementation remains in the [installation guide](docs/install.md) and [architecture guide](docs/architecture.md). Those instructions differ from the current Windows app.
