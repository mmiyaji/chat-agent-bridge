# Slack setup / Slackの設定

Chat Agent BridgeのWindowsアプリをSlackへ接続する手順です。

## Slackアプリを準備する

BridgeはSocket Modeで、PCからSlackへ接続します。公開URLやポート開放は不要です。

1. [Slack API](https://api.slack.com/apps)で **Create New App → From scratch** を選び、利用するワークスペースにアプリを作成します。
2. **Socket Mode** を有効にし、`connections:write` を持つアプリレベルトークン（`xapp-` で始まる値）を作成します。
3. **Event Subscriptions** を有効にし、**Subscribe to bot events** に次のイベントを追加します。

   | イベント | 用途 |
   | --- | --- |
   | `app_mention` | メンションで依頼を開始する（必須） |
   | `message.channels` | 公開チャンネルのスレッドで会話を続ける |
   | `message.groups` | 非公開チャンネルで使う場合 |
   | `message.im` / `message.mpim` | DM・グループDMのスレッドで返信を受け取る場合（任意） |

4. **OAuth & Permissions** の **Bot Token Scopes** に、必要な権限を追加します。

   | 権限 | 用途 | 必要な場合 |
   | --- | --- | --- |
   | `app_mentions:read` | メンションの受信 | 必須 |
   | `chat:write` | 進捗・結果の投稿と更新 | 必須 |
   | `channels:history` / `groups:history` / `im:history` / `mpim:history` | スレッドでの返信の受信、投稿リンクからの会話の確認 | 使う会話の種類に応じて |
   | `users:read` | 許可ユーザーの検索、履歴でのユーザー名表示 | 推奨 |
   | `channels:read` / `groups:read` / `im:read` / `mpim:read` | 履歴でのチャンネル名表示、投稿リンクからの会話の確認 | 推奨（使う会話の種類に応じて） |
   | `files:read` | 依頼に添付したファイルの受信 | ファイル受信を使う場合 |
   | `files:write` | 生成ファイルのスレッドへの送信 | ファイル送信を使う場合 |

5. **Install to Workspace** でアプリをインストールし、Botトークン（`xoxb-` で始まる値）を控えます。
6. 依頼に使うチャンネルで `/invite @アプリ名` を実行し、Botを招待します。

権限を後から追加した場合は、**アプリをワークスペースへ再インストール**してから、Bridgeで接続し直してください。トークンはパスワードに相当するため、チャットやスクリーンショットで共有しないでください。

## Bridgeで設定する

1. **チャット設定 → Slack** に、BotトークンとアプリレベルトークンをBridgeへ入力します。
2. **Slackの許可ユーザーを検索** に依頼を送る本人のユーザー名（例: `@alice`）かメンバーIDを入力し、結果を確認して「このユーザーを追加」を選びます。許可リストにはメンバーIDが保存されます。
3. 必要に応じて **Slackの投稿リンクから会話を指定** で、依頼を受け付けるチャンネル・スレッドを限定します。メッセージのリンクを貼り付けると、ワークスペース・チャンネル・スレッドを確認してから追加できます。
4. 保存して **チャット** からSlackに接続します。

## 依頼と操作

- **開始**：Botを招待したチャンネルで `@アプリ名 依頼内容` と投稿します。
- **続き**：同じユーザーが同じスレッドへ返信すると、メンションなしで会話を続けます。他のユーザーの返信は、あなたの会話として扱いません。
- **状態確認・中断**：同じスレッドで `!cab status` / `!cab stop` を投稿します。
- **承認**：依頼の開始はPCで承認します。Codex App Serverでチャットへの承認共有を有効にすると、CLIの操作を `!cab approve <コード>` / `!cab reject <コード>` で承認できます。

履歴に表示するワークスペース名は `auth.test` から取得し、追加の権限は不要です。ユーザー名・チャンネル名の取得はメタデータAPIだけを使い、投稿本文は読みません。取得に成功した名前は24時間、失敗は権限変更後も最大5分間キャッシュされます。表示名は実行の許可に影響しません。許可はユーザーIDで判定します。

## トラブルシューティング

| 症状 | 確認すること |
| --- | --- |
| メンションしても反応しない | Socket Modeが有効か。`app_mention` イベントと `app_mentions:read` があるか。Botをチャンネルに招待したか。投稿者が許可ユーザーか |
| スレッドの返信で続かない | 使う会話の種類の `message.*` イベントと `*:history` 権限があるか。最初の依頼と同じユーザーが返信しているか |
| 名前が表示されない | `users:read` と、会話の種類に応じた `*:read` 権限があるか。権限追加後に再インストールしたか |
| 添付を受け取れない・送れない | 受信は `files:read`、送信は `files:write` が必要。追加後に再インストールしたか |
| `missing_scope` エラー | エラーに示された権限を追加して再インストール。チャンネルIDの先頭（`C` など）から公開・非公開を推測せず、Slackが要求する権限に従ってください |

---

## English

This guide connects the Chat Agent Bridge Windows app to Slack.

### Prepare the Slack app

Bridge connects out to Slack with Socket Mode. No public URL or open port is needed.

1. In [Slack API](https://api.slack.com/apps), choose **Create New App → From scratch** and create the app in your workspace.
2. Enable **Socket Mode** and create an app-level token (starting with `xapp-`) with `connections:write`.
3. Enable **Event Subscriptions** and add these under **Subscribe to bot events**:

   | Event | Purpose |
   | --- | --- |
   | `app_mention` | Start requests with a mention (required) |
   | `message.channels` | Continue conversations in public-channel threads |
   | `message.groups` | Use private channels |
   | `message.im` / `message.mpim` | Receive thread replies in direct messages or group direct messages (optional) |

4. Add these under **OAuth & Permissions → Bot Token Scopes**:

   | Scope | Purpose | When needed |
   | --- | --- | --- |
   | `app_mentions:read` | Receive mentions | Required |
   | `chat:write` | Post and update progress and results | Required |
   | `channels:history` / `groups:history` / `im:history` / `mpim:history` | Receive thread replies and check conversations from message links | For the conversation types you use |
   | `users:read` | Look up allowed users and show user names in history | Recommended |
   | `channels:read` / `groups:read` / `im:read` / `mpim:read` | Show channel names in history and check conversations from message links | Recommended (for the conversation types you use) |
   | `files:read` | Receive files attached to requests | When receiving files |
   | `files:write` | Send generated files to the thread | When sending files |

5. Choose **Install to Workspace** and copy the bot token (starting with `xoxb-`).
6. Run `/invite @your-app` in each channel you use for requests.

After adding scopes later, **reinstall the app to the workspace**, then reconnect in Bridge. Tokens are equivalent to passwords; do not share them in chat or screenshots.

### Configure Bridge

1. In **Chat settings → Slack**, enter the bot token and the app-level token.
2. Under the Slack allowed-user lookup, enter the requester's user name (for example `@alice`) or member ID, check the result and choose to add the user. The allowlist stores member IDs.
3. Optionally, restrict requests to specific channels or threads by pasting a Slack message link into the conversation lookup. Bridge shows the workspace, channel and thread before you add it.
4. Save, then connect to Slack from **Chat**.

### Requests and operations

- **Start**: post `@your-app your request` in a channel the bot has joined.
- **Continue**: replies from the same user in the same thread continue the conversation without a mention. Replies from other users are not treated as part of your conversation.
- **Status and stop**: post `!cab status` / `!cab stop` in the same thread.
- **Approvals**: approve the request to start on the PC. With Codex App Server and sharing approvals in chat enabled, approve CLI operations with `!cab approve <code>` / `!cab reject <code>`.

Workspace names in history come from `auth.test`, which needs no extra scope. User and channel names use metadata APIs only and never read message text. Successful names are cached for 24 hours; a failed lookup may stay cached for up to 5 minutes after a permission change. Display names never grant access; access is decided by user ID.

### Troubleshooting

| Symptom | What to check |
| --- | --- |
| No response to a mention | Socket Mode is enabled, the `app_mention` event and `app_mentions:read` are configured, the bot was invited to the channel and the poster is an allowed user |
| Thread replies do not continue | The `message.*` events and `*:history` scopes for that conversation type are configured, and the reply comes from the original requester |
| Names are not shown | `users:read` and the `*:read` scopes for the conversation type are configured, and the app was reinstalled afterwards |
| Files cannot be received or sent | Receiving needs `files:read` and sending needs `files:write`; reinstall after adding them |
| `missing_scope` errors | Add the scope named in the error and reinstall. Do not infer public or private channels from the ID prefix (such as `C`); follow the scope Slack requests |

### References

- [Socket Mode](https://api.slack.com/apis/socket-mode)
- [Using the Conversations API](https://docs.slack.dev/apis/web-api/using-the-conversations-api/)
