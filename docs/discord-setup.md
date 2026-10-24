# Discord setup / Discordの設定

## Botを準備する

1. [Discord Developer Portal](https://discord.com/developers/applications)でアプリを作成し、Botページを開きます。
2. **Message Content Intent**を有効にします。投稿本文と添付ファイルの受信に使います。Server Members IntentとPresence Intentは不要です。
3. Botを利用するサーバーへ招待します。必要な権限は **View Channel / Send Messages / Read Message History / Create Public Threads / Send Messages in Threads / Attach Files** です。Administrator権限は不要です。チャンネル固有の権限も確認してください。
4. Botトークンをアプリの**チャット設定 → Discord**へ入力します。トークンはパスワードに相当するため、チャットやスクリーンショットで共有しないでください。
5. Discordのユーザー設定で開発者モードを有効にし、ユーザー・サーバーを右クリックしてIDをコピーします。許可ユーザーIDと許可サーバーIDを登録します。表示名やユーザー名ではありません。

サーバーでの実行にはユーザーとサーバーの両方の許可が必要です。チャンネル・スレッドの許可一覧も指定すると、その範囲に制限できます。DMは既定で無効です。DMを有効にしても許可ユーザー以外からは実行できません。

## 依頼と承認

チャット画面からDiscordへ接続し、許可されたチャンネルで `@Bot 依頼内容` または `!cab 依頼内容` を投稿します。Bridgeは投稿から公開スレッドを作成します。同じユーザーがそのスレッドへ返信すると会話を継続します。

既定では最初の依頼をPCで承認します。チャットでの承認共有を有効にするとDiscordにも承認・拒否ボタンが表示され、依頼した本人だけ操作できます。承認投稿や結果は、そのスレッドを閲覧できるメンバーにも見えます。AIの操作承認と、生成ファイルを送るための承認は別の設定です。

画像やログは依頼に添付します。受信する形式・サイズ・件数はアプリ共通の制限に従い、依頼を承認した後に作業フォルダーへ一時保存します。生成ファイルの送信も共通の添付承認を通ります。進捗投稿の中断ボタン、または `!cab stop` で自分の依頼を止められます。

## Connection and permissions

Create a bot in the Developer Portal, enable **Message Content Intent**, and invite it with **View Channel, Send Messages, Read Message History, Create Public Threads, Send Messages in Threads and Attach Files**. Administrator, Server Members Intent and Presence Intent are unnecessary.

In **Chat settings → Discord**, enter the bot token and exact allowed user/server IDs. Copy IDs using Discord's Developer Mode; display names are not IDs. Direct messages are disabled by default. Optional channel/thread lists narrow access further.

Start with a bot mention or `!cab` in an allowed channel. Bridge creates a public thread; replies from its original requester continue the conversation. Request approvals remain on the PC unless sharing approvals in chat is enabled. Approval and Stop buttons accept only the original requester. Incoming and generated files use the app's existing approval and size/count restrictions.

Bridge maintains an outbound Gateway connection. It does not need a public HTTP interaction endpoint. Keep the PC awake and connected. A bot token is encrypted for the current Windows user and must be re-entered after moving data to another PC.

## References

- [Gateway and intents](https://docs.discord.com/developers/events/gateway)
- [Receiving and responding to interactions](https://docs.discord.com/developers/interactions/receiving-and-responding)
- [Threads](https://docs.discord.com/developers/topics/threads)
- [Messages and attachments](https://docs.discord.com/developers/resources/message)
