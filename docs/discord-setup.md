# Discord setup / Discordの設定

Chat Agent BridgeのWindowsアプリをDiscordへ接続する手順です。

## Botを準備する

BridgeはPCからDiscordのGatewayへ接続します。公開のHTTPエンドポイントやポート開放は不要です。

1. [Discord Developer Portal](https://discord.com/developers/applications)でアプリを作成し、**Bot** ページを開きます。
2. **Message Content Intent** を有効にします。投稿本文と添付ファイルの受信に使います。Server Members IntentとPresence Intentは不要です。
3. Botを利用するサーバーへ招待します。必要な権限は次のとおりです。Administrator権限は不要です。チャンネル固有の権限設定で打ち消されていないかも確認してください。

   | 権限 | 用途 |
   | --- | --- |
   | View Channel / Read Message History | 依頼と返信の受信 |
   | Send Messages / Send Messages in Threads | 進捗・結果の投稿 |
   | Create Public Threads | 依頼ごとのスレッド作成 |
   | Attach Files | 生成ファイルの送信 |

4. Botトークンを控えます。トークンはパスワードに相当するため、チャットやスクリーンショットで共有しないでください。
5. Discordの **ユーザー設定 → 詳細設定** で開発者モードを有効にし、ユーザーやサーバーを右クリックしてIDをコピーします。

## Bridgeで設定する

**チャット設定 → Discord** で次を設定し、保存して **チャット** から接続します。

| 項目 | 内容 |
| --- | --- |
| Botトークン | 手順4のトークン |
| 許可するユーザーID | 必須。依頼を送る本人のユーザーIDを1行に1つ。表示名やユーザー名ではありません |
| 許可するサーバーID | DMを無効にする場合は必須。サーバーIDを1行に1つ |
| ダイレクトメッセージを許可 | 既定はオフ。オンにすると、許可ユーザーはサーバーIDなしでDMから依頼できます。許可ユーザー以外はDMでも実行できません |
| 許可するチャンネルID / スレッドID | 任意。指定すると、その範囲に限定します |

サーバーでの実行には、ユーザーとサーバーの両方が許可されている必要があります。

## 依頼と操作

- **開始**：許可されたチャンネルで `@Bot 依頼内容` または `!cab 依頼内容` を投稿します。Bridgeが投稿から公開スレッドを作成します。
- **続き**：同じユーザーがそのスレッドへ返信すると、会話を続けます。
- **状態確認・中断**：スレッドで `!cab status` / `!cab stop` を投稿するか、進捗投稿の中断ボタンを押します。
- **承認**：既定では依頼の開始をPCで承認します。Codex App Serverでチャットへの承認共有を有効にすると、依頼の開始とCLIの操作に承認・拒否ボタンが表示されます。
- **ボタンの操作**：承認・拒否・中断のボタンは、依頼した本人だけが操作できます。承認の投稿や結果は、そのスレッドを閲覧できるメンバーにも見えます。
- **ファイル**：画像やログは依頼に添付します。受信する形式・サイズ・件数はアプリ共通の制限に従い、依頼の承認後に作業フォルダーへ一時保存します。生成ファイルの送信も、共通の添付承認を通ります。AIの操作の承認と、生成ファイルを送るための承認は別の設定です。

利用中はPCを起動し、接続を維持してください。Botトークンは現在のWindowsユーザー用に暗号化して保存するため、データを別PCへ移した場合は入力し直します。

## トラブルシューティング

| 症状 | 確認すること |
| --- | --- |
| 投稿に反応しない | **Message Content Intent** が有効か。投稿者のユーザーIDとサーバーIDが許可されているか。チャンネル・スレッドを限定している場合はその範囲内か |
| スレッドが作られない | Botに **Create Public Threads** と **Send Messages in Threads** の権限があるか。チャンネル固有の権限で拒否されていないか |
| DMで反応しない | **ダイレクトメッセージを許可** がオンか。送信者が許可ユーザーか |
| ファイルを送れない | Botに **Attach Files** の権限があるか。添付の承認設定とサイズ・件数の上限 |
| ボタンが効かない | 依頼した本人が押しているか。依頼がすでに完了・中断していないか |

---

## English

This guide connects the Chat Agent Bridge Windows app to Discord.

### Prepare the bot

Bridge maintains an outbound Gateway connection from the PC. No public HTTP endpoint or open port is needed.

1. Create an application in the [Discord Developer Portal](https://discord.com/developers/applications) and open the **Bot** page.
2. Enable **Message Content Intent**, which is used to receive message text and attachments. Server Members Intent and Presence Intent are unnecessary.
3. Invite the bot to your server with the permissions below. Administrator is unnecessary. Also check that channel-specific permission overrides do not remove them.

   | Permission | Purpose |
   | --- | --- |
   | View Channel / Read Message History | Receive requests and replies |
   | Send Messages / Send Messages in Threads | Post progress and results |
   | Create Public Threads | Create a thread for each request |
   | Attach Files | Send generated files |

4. Copy the bot token. Tokens are equivalent to passwords; do not share them in chat or screenshots.
5. Enable Developer Mode in Discord's **User Settings → Advanced**, then right-click users and servers to copy their IDs.

### Configure Bridge

In **Chat settings → Discord**, configure the following, save, then connect from **Chat**.

| Setting | Details |
| --- | --- |
| Bot token | The token from step 4 |
| Allowed user IDs | Required. The requester's user ID, one per line. Display names and user names are not IDs |
| Allowed server IDs | Required unless direct messages are enabled. One server ID per line |
| Allow direct messages | Off by default. When on, allowed users can send requests by DM without a server ID. Users who are not allowed still cannot run requests by DM |
| Allowed channel IDs / thread IDs | Optional. Narrow access to those channels or threads |

Requests in a server require both the user and the server to be allowed.

### Requests and operations

- **Start**: post `@Bot your request` or `!cab your request` in an allowed channel. Bridge creates a public thread from the post.
- **Continue**: replies from the same user in that thread continue the conversation.
- **Status and stop**: post `!cab status` / `!cab stop` in the thread, or press the Stop button on the progress post.
- **Approvals**: by default, approve the request to start on the PC. With Codex App Server and sharing approvals in chat enabled, Approve and Reject buttons appear for starting the request and for CLI operations.
- **Buttons**: only the original requester can use the Approve, Reject and Stop buttons. Approval posts and results are visible to members who can view the thread.
- **Files**: attach images or logs to the request. Received files follow the app's shared type, size and count limits and are saved temporarily in the working folder after the request is approved. Generated files go through the shared attachment approval. Approving agent operations and approving generated-file uploads are separate settings.

Keep the PC awake and connected while you use it. The bot token is encrypted for the current Windows user and must be re-entered after moving data to another PC.

### Troubleshooting

| Symptom | What to check |
| --- | --- |
| Posts get no response | **Message Content Intent** is enabled, the poster's user ID and the server ID are allowed, and the post is within any channel or thread restriction |
| No thread is created | The bot has **Create Public Threads** and **Send Messages in Threads**, and channel-specific permissions do not deny them |
| No response to DMs | **Allow direct messages** is on and the sender is an allowed user |
| Files cannot be sent | The bot has **Attach Files**; check the attachment approval setting and size/count limits |
| Buttons do nothing | The original requester pressed them, and the request has not already finished or stopped |

### References

- [Gateway and intents](https://docs.discord.com/developers/events/gateway)
- [Receiving and responding to interactions](https://docs.discord.com/developers/interactions/receiving-and-responding)
- [Threads](https://docs.discord.com/developers/topics/threads)
- [Messages and attachments](https://docs.discord.com/developers/resources/message)
