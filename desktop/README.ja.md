# WinUI 3 ネイティブ版（開発プレビュー）

`codex/winui-rebuild` で開発する新しい .NET 10 / WinUI 3 版です。画面は XAML、実行・接続は C# で構成し、旧ブラウザー UI や Node.js bridge は起動しません。外部エージェントはユーザーが別途導入・ログインする必要があります。

## 構成

- `ChatAgentBridge.Desktop`: WinUI 3、MVVM、概要・ワークベンチ・接続設定・チャット受信。
- `ChatAgentBridge.Core`: 設定、Windows DPAPI による秘密情報保護、CLI の標準入出力・停止・同一作業フォルダーの実行排他。
- `ChatAgentBridge.Transports`: Slack Socket Mode / Mattermost WebSocket。受信と返信を担当し、エージェントを直接実行しません。
- `*.Tests`: CLI・保存・イベントフィルターの自動テスト。外部サービスへの送信や有料エージェント呼び出しは行いません。

## ビルド・起動

Windows、.NET 10 SDK、Windows App SDK 用 WinUI テンプレート、WinApp CLI、開発者モードが必要です。

```powershell
cd desktop/ChatAgentBridge.Desktop
.\BuildAndRun.ps1 -SkipRun
.\BuildAndRun.ps1 -Detach
```

ビルドスクリプトは既定で .NET CLI を使用します。Visual Studio の MSBuild を使う場合は `.NET 10` 対応版を用意し `-UseVisualStudioMSBuild` を指定してください。

```powershell
dotnet test desktop/ChatAgentBridge.Core.Tests/ChatAgentBridge.Core.Tests.csproj
dotnet test desktop/ChatAgentBridge.Transports.Tests/ChatAgentBridge.Transports.Tests.csproj
```

## 使い方

1. ワークベンチで既存の作業フォルダー、エージェント実行ファイル、引数を指定して設定を保存します。Codex の既定引数は `exec --json -` です。任意の CLI を設定する場合は標準入力からプロンプトを受け取れる形式を指定してください。プロバイダー別の対話プロトコルはまだありません。
2. 接続設定で認証情報を入力して保存します。Mattermost には対象チャンネル ID も指定します。HTTPS、またはローカルホストの HTTP が利用できます。
3. チャット受信からどちらかのプロバイダーへ接続します。設定変更後は切断・再接続してください。接続はアプリ起動のたびに明示的に開始します。
4. Mattermost では指定チャンネルに `!cab 依頼内容` を投稿します。Slack では bot をメンション、またはスレッド返信に `!cab 依頼内容` を投稿します。必要な bot 権限・イベント購読・Socket Mode の設定は別途必要です。
5. PC に表示された依頼を選択し、「内容を確認して実行」で作業フォルダーと実行内容を承認します。チャット受信では確認設定にかかわらず毎回承認が必要です。

返信は完了／失敗の状態だけを送ります。CLI 出力はローカル画面で確認します。許可フォルダーは保存した単一ワークスペースで固定し、チャットから実行ファイル・引数・フォルダーを変更できません。これは CLI 自体のファイルアクセスを OS で制限する機構ではありません。

接続エラーでは認証情報を含む可能性のあるサーバー応答を表示しません。接続先・権限・ネットワークを確認して再接続してください。アプリを閉じると受信と実行を停止します。トレイ常駐・自動再接続・自動起動はまだありません。

## データと移行

パッケージ版の設定は `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState\settings.json` に保存します。トークンは現在の Windows ユーザーの DPAPI で暗号化します。他ユーザー・他PCへのファイルコピーだけでは復号できません。コア単体の既定は `%LOCALAPPDATA%\ChatAgentBridge.WinUI` です。

パッケージ起動では呼び出し元の環境変数が引き継がれないため、テスト用保存先は `--data-dir` 起動引数で指定します。直接プロセスから呼び出すコアでは `CAB_WINUI_DATA_DIR` も利用できます。

```powershell
winapp run .\desktop\ChatAgentBridge.Desktop\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64 --detach --json -- --data-dir 'C:\TestData\ChatAgentBridge'
# 返された ProcessId を使い、同じ保存先でUI操作テストを実行:
.\desktop\SmokeTest.ps1 -AppPid 12345 -DataDirectory 'C:\TestData\ChatAgentBridge'
```

旧 TypeScript 版の設定・セッション・履歴は自動インポートしません。既存データを上書きしないよう保存先を分けています。破損設定や復号エラーでは実行・保存を停止し、ファイルを修復後に再読み込みしてください。

## MSIX

```powershell
.\desktop\Package.ps1
# 自分で用意した開発証明書で署名する場合:
.\desktop\Package.ps1 -Certificate 'C:\certificates\development.pfx'
```

証明書の生成・信頼ストアへの登録はこのスクリプトでは行いません。未署名パッケージは Store 提出準備や検査向けで、通常のローカルインストールには署名と信頼設定が必要です。

manifest の identity / publisher は開発用です。Store 提出時には Partner Center の値、正式なアイコン、説明、プライバシーポリシー、依存関係と対象 OS の確認が必要です。パッケージ生成と Store 審査通過は別の確認です。

未移行機能と段階別の進捗は [移行計画](../docs/winui-migration-plan.ja.md) を参照してください。
