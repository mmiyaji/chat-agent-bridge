# Mattermost setup / Mattermostの設定

Chat Agent BridgeのWindowsアプリをMattermostへ接続する手順です。

## Botを準備する

BridgeはPCからMattermostのREST APIとWebSocketへ接続します。公開URLやポート開放は不要です。

1. システム管理者が **System Console → Integrations → Bot Accounts** で、Botアカウントの作成を有効にします。
2. **Integrations → Bot Accounts → Add Bot Account** でBot（例: `cc-agent-bot`）を作成し、アクセストークンを控えます。
3. Botをチームに追加します。依頼に使う会話を決めます。
   - **BotとのDM**：自分とBotだけの会話で使う場合（推奨）。
   - **チャンネル**：Botを追加したチャンネルで使う場合。
4. その会話のチャンネルIDを控えます。チャンネル名のメニューの **View Info** などで確認できます。

Bridgeは、ここで設定した1つの会話だけを監視します。トークンはパスワードに相当するため、チャットやスクリーンショットで共有しないでください。

## Bridgeで設定する

1. **チャット設定 → Mattermost** に、サーバーURL・Botのトークン・会話のチャンネルIDを入力します。サーバーURLは `https://` で始まる必要があります（`http://` はこのPC上のサーバーに限ります）。
2. **ユーザー名から許可するユーザーを検索** に、依頼を送る本人のユーザー名（例: `@alice`）を入力し、結果を確認して追加します。表示名やBot名ではありません。許可リストにはMattermost内部のユーザーIDが保存され、ID欄にユーザー名を直接入力しても解決されません。
3. 必要に応じて **投稿リンクから会話を指定** で、依頼を受け付けるスレッドを限定します。投稿のメニューから「リンクをコピー」を選んで貼り付けます。
4. 保存して **チャット** からMattermostに接続します。設定を変えた場合は、切断してから接続し直します。

### プロキシを使わず直接接続する

社内ネットワークなどでシステムのプロキシ経由では接続できない場合は、**プロキシを使わず直接接続** をオンにし、**設定を保存 → 切断 → Mattermostに接続** の順に操作します。初期設定ガイドのサーバーURLの下にも同じ項目があります。既定はオフで、システムのプロキシ設定を使います。

この設定は、MattermostのREST認証・ユーザー検索・投稿リンクの確認・返信・接続通知・WebSocketに適用されます。SlackやCLIのプロキシ設定には影響しません。それでも接続できない場合は、名前解決・経路（VPN）・サーバーの稼働状況・TLS証明書の信頼を確認してください。透過型のゲートウェイや接続を検査する端末エージェントは、この設定では回避できません。HTTPSの証明書検証は常に有効です。

## 依頼と操作

- **開始**：設定した会話で `@cc-agent-bot 依頼内容`（先頭にBotへのメンション）または `!cab 依頼内容` と投稿します。
- **続き**：同じ本人が元のスレッドへ普通に返信すると、メンションやコマンドなしで会話を続けます。
- **無視される投稿**：開始していないスレッド、他のユーザーからの返信、会話の先頭に置いたコマンドなしの投稿は無視します。
- **状態確認・中断**：同じスレッドで `!cab status` / `!cab stop` を投稿するか、Botの進捗投稿にある🛑リアクションを依頼した本人が押します。
- **承認**：依頼の開始はPCで承認します。Codex App Serverでチャットへの承認共有を有効にすると、CLIの操作を `!cab approve <コード>` / `!cab reject <コード>` で承認できます。

最初の依頼を受け付けた後は、承認待ちの間でもその接続中は返信で続けられます。AIのセッションか承認済みの作業フォルダーが保存された後は、再接続・再起動後も同じ本人が続けられます。何も保存される前に切断した場合は、もう一度メンションか `!cab` で開始してください。

## トラブルシューティング

| 症状 | 確認すること |
| --- | --- |
| 接続できない | サーバーURLが `https://` か。トークンが有効か。プロキシ環境なら直接接続の設定。エラーには接続先・権限・ネットワークの確認を促す内容だけを表示します |
| 投稿に反応しない | 設定したチャンネルIDの会話に投稿しているか。メンションか `!cab` で始めたか。投稿者が許可ユーザーか |
| 許可ユーザーを追加できない | ユーザー名（`@alice`）で検索しているか。表示名やBot名では見つかりません |
| 返信で続かない | 最初の依頼と同じ本人か。何も保存される前に切断していないか |
| 🛑で中断できない | 依頼した本人が押しているか。Botに投稿へリアクションを付ける権限があるか |

診断のためにエラーや画面を共有する場合は、トークンを含めないでください。

---

## English

This guide connects the Chat Agent Bridge Windows app to Mattermost.

### Prepare the bot

Bridge connects out from the PC to the Mattermost REST API and WebSocket. No public URL or open port is needed.

1. A system administrator enables bot account creation in **System Console → Integrations → Bot Accounts**.
2. Create a bot (for example `cc-agent-bot`) under **Integrations → Bot Accounts → Add Bot Account** and copy its access token.
3. Add the bot to the team and choose the conversation for requests:
   - **A DM with the bot**: for a conversation between you and the bot only (recommended).
   - **A channel**: a channel the bot has joined.
4. Copy that conversation's channel ID, for example from **View Info** in the channel menu.

Bridge monitors only the one conversation you configure. Tokens are equivalent to passwords; do not share them in chat or screenshots.

### Configure Bridge

1. In **Chat settings → Mattermost**, enter the server URL, the bot token and the conversation's channel ID. The server URL must start with `https://` (`http://` is allowed only for a server on this PC).
2. Use the user-name lookup to search for the requester's user name (for example `@alice`), check the result and add it. Do not use a display name or the bot's name. The allowlist stores Mattermost internal user IDs; typing a user name into the ID list does not resolve it.
3. Optionally, restrict requests to specific threads by pasting a post link (from the post menu, **Copy Link**) into the conversation lookup.
4. Save, then connect to Mattermost from **Chat**. After changing settings, disconnect and connect again.

#### Connect without a proxy

If the system proxy prevents connecting (for example on a corporate network), turn on **Connect without a proxy**, then **save settings → disconnect → connect to Mattermost**. The first-run wizard offers the same option below the server URL. It is off by default, using the system proxy settings.

This applies to Mattermost REST authentication, user lookup, post-link checks, replies, connection notices and WebSocket events. Slack and agent CLI proxy settings are unaffected. If direct mode still fails, check name resolution, routing or VPN access, server availability and TLS certificate trust. Direct mode cannot bypass a transparent gateway or an endpoint agent that intercepts connections. HTTPS certificate validation always stays enabled.

### Requests and operations

- **Start**: in the configured conversation, post `@cc-agent-bot your request` (a leading mention of the bot) or `!cab your request`.
- **Continue**: the same sender replies normally in the original thread, without a mention or command.
- **Ignored posts**: threads that were never started, replies from other users and plain top-level posts without a command are ignored.
- **Status and stop**: post `!cab status` / `!cab stop` in the same thread, or have the original requester click the 🛑 reaction on the bot's progress post.
- **Approvals**: approve the request to start on the PC. With Codex App Server and sharing approvals in chat enabled, approve CLI operations with `!cab approve <code>` / `!cab reject <code>`.

After the first request is received, replies continue the conversation for the current connection, even while approval is pending. Once an AI session or an approved working folder is saved, the same sender can also continue after reconnecting or restarting. If the connection ends before anything is saved, start again with a mention or `!cab`.

### Troubleshooting

| Symptom | What to check |
| --- | --- |
| Cannot connect | The server URL starts with `https://`, the token is valid, and direct connection is configured on proxied networks. Errors only ask you to check the destination, permissions and network |
| Posts get no response | You posted in the configured channel ID's conversation, started with a mention or `!cab`, and the poster is an allowed user |
| Cannot add an allowed user | Search by user name (`@alice`); display names and the bot's name are not found |
| Replies do not continue | The reply comes from the original requester, and the connection did not end before anything was saved |
| 🛑 does not stop the request | The original requester clicked it, and the bot can add reactions to posts |

When sharing errors or screenshots for diagnosis, leave out tokens.
