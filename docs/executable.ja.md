# Windows実行ファイル版

`ChatAgentBridge.exe` をダブルクリックするとサービスが起動し、コンソールに管理画面のURLを表示します。そのURLをお使いのブラウザーで開いてください。既定ではブラウザーを自動起動しません。Node.jsやnpmのインストールは不要です。Windows x64用です。

初回起動中はコンソールウィンドウを開いておいてください。`Ctrl+C` またはウィンドウを閉じると終了します。チャットや管理画面から再起動した後は、バックグラウンドで動作します。その場合はタスクマネージャーで `ChatAgentBridge.exe` を終了できます。実行ファイル版での「再ビルド」はできません。更新するときはアプリを終了し、新しいexeに置き換えてください。

## 初回設定

管理画面の「接続と設定」で、作業フォルダーと許可するディレクトリ、エージェント、Slack/Mattermostの接続情報を設定します。Codex、Claude Code、Cursor、GeminiのCLIは別途インストールし、ログインしてください。実行ファイルにはこれらのCLIや認証情報は含めません。

通常の管理画面は `http://127.0.0.1:8788/admin` です。同じポートで別のbridgeが動いている場合は、先に終了するか `CAB_ADMIN_PORT` で別ポートを指定してください。

設定・会話状態・監査ログは `%LOCALAPPDATA%\ChatAgentBridge` に保存します。exeの置き場所や起動元フォルダーを変えても設定を引き継ぎます。ソース版の `data` フォルダーとは分離しています。保存先を変更するには `--data-dir` または `CAB_DATA_DIR` を指定します。

```powershell
.\ChatAgentBridge.exe --data-dir "D:\Apps\ChatAgentBridgeData"
.\ChatAgentBridge.exe --no-browser
.\ChatAgentBridge.exe --open-browser
.\ChatAgentBridge.exe --help
```

従来の `CAB_*`、`SLACK_*`、`MATTERMOST_*` 環境変数も利用でき、保存済み設定より優先されます。起動エラーはデータフォルダーの `startup-error.log` と `audit.jsonl` を確認してください。

`--open-browser` または `CAB_OPEN_ADMIN=1` を指定すると既定のブラウザーを開きます。`--no-browser` はこれらより優先されます。ブラウザーが不調な場合は自動起動を使わず、表示されたURLを別のブラウザーで開いてください。ブラウザーの起動要求と起動処理のエラーは `audit.jsonl` に記録します（ブラウザー内部のクラッシュ検出ではありません）。

## 開発時の生成

Windows x64 / Node.js 22以降で `npm ci`、`npm run package:exe` を実行します。`release/ChatAgentBridge.exe` が生成されます。`npm run test:exe` でNode.jsをPATHから外した起動、設定保存、再起動を確認します。検証では一時フォルダーにexeをコピーするため約100 MBの空き容量が必要です。`CAB_EXE_TEST_ROOT` で一時フォルダーの親を指定できます。

Chromeがインストールされている場合、`npm run test:exe -- --browser=chrome` でexeが配信する管理画面の操作と定期更新も検証できます。普段のChromeプロファイルとは別の一時プロファイルを使います。

[Node.js Single Executable Applications](https://nodejs.org/download/release/latest-jod/docs/api/single-executable-applications.html) を使用し、ビルドに使ったNode.js本体とバンドル済みアプリを同梱します。出力に個人の設定・トークン・履歴は含みません。`SHA256SUMS.txt` にハッシュ、`build-info.json` にバージョン、`THIRD-PARTY-NOTICES.txt` に同梱ライセンスを記録します。

このローカルビルドにはコード署名を付けていません。配布時にはWindowsの保護機能による確認が表示される場合があります。
