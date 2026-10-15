# WinUI 3 ネイティブ版（開発プレビュー）

`codex/winui-rebuild` で開発する新しい .NET 10 / WinUI 3 版です。画面は XAML、実行・接続は C# で構成し、旧ブラウザー UI や Node.js bridge は起動しません。外部エージェントはユーザーが別途導入・ログインする必要があります。

## 構成

- `ChatAgentBridge.Desktop`: WinUI 3、MVVM、概要・手動実行画面・接続設定・チャット受信・履歴とセッション。
- `ChatAgentBridge.Core`: 設定、Windows DPAPI、CLI実行・停止・排他、Codexセッション継続、履歴と監査の永続化。
- `ChatAgentBridge.Transports`: Slack Socket Mode / Mattermost WebSocket。受信と返信を担当し、エージェントを直接実行しません。
- `*.Tests`: CLI・保存・イベントフィルター・承認の自動テスト。実通信テストは明示的に有効化した場合だけ実行します。
- `ChatAgentBridge.TestAgent`: モデルを呼ばずにApp Serverの承認・追加入力を再現する開発専用プロセス。配布パッケージには含めません。

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

1. 「エージェント設定」でCLI・モデル・作業フォルダーを指定して保存します。Codex CLI、Claude Code CLI、その他のCLIを選べます。モデル名が空欄ならCLIの既定値を使います。Codex の既定引数は `exec --json -` です。任意の CLI を設定する場合は標準入力からプロンプトを受け取れる形式を指定してください。Codexの対話的な承認・追加入力を使う場合は「Codex App Serverを使う」をオンにします。このモードの引数は固定です。
2. 接続設定で認証情報を入力して保存します。Mattermost には対象チャンネル ID も指定します。HTTPS、またはローカルホストの HTTP が利用できます。
3. チャット受信からどちらかのプロバイダーへ接続します。設定変更後は切断・再接続してください。接続はアプリ起動のたびに明示的に開始します。
4. Mattermost では指定チャンネルに `!cab 依頼内容` を投稿します。Slack では bot をメンション、またはスレッド返信に `!cab 依頼内容` を投稿します。必要な bot 権限・イベント購読・Socket Mode の設定は別途必要です。
5. PC に表示された依頼を選択し、「内容を確認して実行」で作業フォルダーと実行内容を承認します。チャット受信では確認設定にかかわらず毎回承認が必要です。

自動返信は完了／失敗の状態だけです。結果本文は「結果をスレッドに送信」で元の送信先と本文を確認してから共有できます。長い本文は先頭4,000文字まで送ります。Codexでは最終エージェントメッセージを抽出し、ツール出力だけのイベント列は本文として送りません。

許可フォルダーは保存した単一ワークスペースで固定し、チャットから実行ファイル・引数・フォルダーを変更できません。これは CLI 自体のファイルアクセスを OS で制限する機構ではありません。

### Codex・Claude Codeセッションの継続

実行ファイルがCodexで、引数が標準の `exec --json -` の場合、プロバイダー・チャンネル・スレッドごとにセッションIDを保存します。同じスレッドの次の依頼はそのIDで `exec resume --json <id> -` を使い、アプリ再起動後も継続できます。[公式の非対話モード仕様](https://learn.chatgpt.com/docs/non-interactive-mode)

Claude Codeの標準プロファイルも、CLIが返す明示的なセッションIDを保存し、同じチャットスレッドの次の依頼を `--resume <id>` で再開します。出力は `--output-format stream-json --verbose`、依頼文は通常の標準入力です。曖昧な直前会話の継続は使いません。実行中の追加質問には対応しません。

ワークスペースや実行ファイル・引数を変更した場合は、保存済みセッションをそのまま別設定で使いません。「履歴とセッション」で切り離してから新規依頼を実行してください。切り離しは関連付けだけを削除し、Codex自身のセッションや実行履歴は削除しません。実行中の同じスレッドの処理があれば、終了まで待って解除します。

任意のCLI・カスタム引数・手動実行画面からの実行は、現在は単発実行です。新規実行を途中でキャンセルし、CLIのセッションIDを取得できなかった場合、その新規セッションはbridgeに紐付きません。Codex自身のセッション保存はCLIの設定に従います。

### App Serverの承認と追加入力

App Serverモードでは `thread/start` / `thread/resume` / `turn/start` を使い、チャットスレッドへの関連付けを保存します。execとの切替には関連付けの解除が必要です。手動実行画面は単発実行です。[公式App Server仕様](https://learn.chatgpt.com/docs/app-server)

コマンド実行・ファイル変更の許可はPCのダイアログ、または元の依頼者による同じスレッドへの `!cab approve <コード>` / `!cab reject <コード>` で回答できます。最初の回答だけが有効で、5分経過・切断・キャンセルは拒否します。最初の依頼の実行確認は引き続きPCで必要です。追加入力はPC上の選択肢・テキスト欄で回答し、チャットには転送しません。

実行設定は承認ポリシー `on-request`、sandbox `workspaceWrite`、ネットワーク無効を要求します。追加権限の要求は付与せず、未対応の要求や別thread/turnへの要求は拒否します。外部CLI側が実際に適用する制限と対応バージョンは、実Codexでの検証が残っています。

### 履歴と監査

`history.json` に実行時刻・状態・終了コード・スレッド・ワークスペース・セッションID、`audit.json` に受信・承認・拒否・実行・共有・解除のメタデータをそれぞれ最大500件保存します。プロンプト・結果本文・トークン・例外本文はこれらのファイルへ保存しません。異常終了後に残った実行中記録は「中断」と表示します。終了状態の保存に失敗した場合も実行結果とは分けて警告します。

接続エラーでは認証情報を含む可能性のあるサーバー応答を表示しません。接続先・権限・ネットワークを確認して再接続してください。アプリを閉じると受信と実行を停止します。トレイ常駐・自動再接続・自動起動はまだありません。

## データと移行

パッケージ版の設定は `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState\settings.json` に保存します。トークンは現在の Windows ユーザーの DPAPI で暗号化します。他ユーザー・他PCへのファイルコピーだけでは復号できません。コア単体の既定は `%LOCALAPPDATA%\ChatAgentBridge.WinUI` です。

パッケージ起動では呼び出し元の環境変数が引き継がれないため、テスト用保存先は `--data-dir` 起動引数で指定します。直接プロセスから呼び出すコアでは `CAB_WINUI_DATA_DIR` も利用できます。

```powershell
winapp run .\desktop\ChatAgentBridge.Desktop\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64 --detach --json -- --data-dir 'C:\TestData\ChatAgentBridge'
# 返された ProcessId を使い、同じ保存先でUI操作テストを実行:
.\desktop\SmokeTest.ps1 -AppPid 12345 -DataDirectory 'C:\TestData\ChatAgentBridge' -TestSessionDetach
```

旧 TypeScript 版の設定・セッション・履歴は自動インポートしません。既存データを上書きしないよう保存先を分けています。破損設定や復号エラーでは実行・保存を停止し、ファイルを修復後に再読み込みしてください。

UIテストは最初に空の隔離ディレクトリを指定します。`-TestSessionDetach` はテスト用のセッション関連付けを作って解除を確認するため、新しい空の保存先で実行してください。実在するCodexセッションやモデルは呼び出しません。

### Docker Mattermostとの実通信テスト

```powershell
.\desktop\StartMattermostE2E.ps1
$env:CAB_ENABLE_NATIVE_MATTERMOST_E2E = '1'
$env:CAB_NATIVE_MATTERMOST_STATE_FILE = Join-Path $PWD 'desktop/artifacts/mattermost-e2e.json'
dotnet test desktop/ChatAgentBridge.Transports.Tests/ChatAgentBridge.Transports.Tests.csproj
```

専用の `cab-winui-e2e` を `http://127.0.0.1:8070` で起動し、固定digestの開発用Mattermostイメージを使います。既存コンテナーやデータを削除しません。認証情報はGit対象外の `desktop/artifacts/mattermost-e2e.json` に保存するため、このファイルを共有しないでください。

WinUIからの通し確認は、`desktop/artifacts` の下の新しい空フォルダーを `--data-dir` にしてアプリを起動し、次を実行します。接続設定・承認・追加入力・本文送信を操作するため、通常利用のデータフォルダーでは実行しないでください。

```powershell
.\desktop\MattermostUiE2E.ps1 -AppPid 12345 -DataDirectory "$PWD\desktop\artifacts\ui-e2e-fresh"
```

このUIテストはテスト専用のApp Serverプロセスをビルドし、Mattermostへの投稿と返信を実際に行います。AIモデルは呼び出しません。詳細結果は `desktop/artifacts/mattermost-ui-e2e-results.json` に保存します。

## MSIX

```powershell
.\desktop\Package.ps1
# 自分で用意した開発証明書で署名する場合:
.\desktop\Package.ps1 -Certificate 'C:\certificates\development.pfx'
```

証明書の生成・信頼ストアへの登録はこのスクリプトでは行いません。未署名パッケージは Store 提出準備や検査向けで、通常のローカルインストールには署名と信頼設定が必要です。

manifest の identity / publisher は開発用です。Store 提出時には Partner Center の値、正式なアイコン、説明、プライバシーポリシー、依存関係と対象 OS の確認が必要です。パッケージ生成と Store 審査通過は別の確認です。

未移行機能と段階別の進捗は [移行計画](../docs/winui-migration-plan.ja.md) を参照してください。


## 起動設定・モデル設定（2026-10-16）

「エージェント設定」のモデル名は、このアプリがCLIを起動する際に指定します。Codexは `--model` またはApp Serverのturnパラメーター、Claude Codeは `--print --output-format stream-json --verbose --model` を使用し、依頼文は標準入力で渡します。CLI自身の設定ファイルやログイン情報は変更しません。「その他のCLI」は手動の引数をそのまま使います。

「履歴と会話」から既定のエージェント設定を開けます。保存済みの会話を選び「次回の実行モデルを設定」を使うと、その会話にだけ適用するモデルを指定できます。空欄で保存すると既定モデルへ戻ります。実行中の会話では終了を待って変更し、実行中のモデルは切り替えません。

Windowsログイン時の自動起動と、アプリ起動後のチャット自動接続は別の設定です。自動起動の変更はWindowsのStartupTaskに即時反映し、OSで無効にした場合はタスクマネージャーの案内を表示します。自動接続先は設定保存後、次回起動から適用します。初期値はいずれも無効です。ウィンドウを閉じた後の常駐や、依頼の無確認実行は行いません。

接続中に既定のCLI・モデルを変更した場合は、切断して再接続してください。会話ごとのモデル上書きは次回の実行開始時に読み直します。

### CLIからモデル候補を選ぶ

「エージェント設定」の「CLIモデル候補を取得」を押すと、現在入力しているCLIと作業フォルダーで候補を取得します。候補を選ぶとモデル名の欄にIDが入り、「設定を保存」で確定します。取得だけでは入力中のモデル名や保存済み設定は変わりません。候補にないIDも手入力でき、空欄ならCLIの既定モデルを使います。

「履歴と会話」から会話ごとのモデル変更を開いた場合も取得できます。ここでは、その会話に保存されたCLI・作業フォルダーを使います。取得に失敗した場合や、その他のCLIを使う場合は手入力してください。

CodexはApp Serverの `model/list`、Claude Codeはstream-jsonの初期化応答から取得します。一覧取得では依頼文や推論要求を送らず、取得後は専用プロセスを終了します。自動取得・定期ポーリングは行いません。表示される一覧はCLIが提示した候補であり、契約上の利用可否や料金を保証するものではありません。[Codex公式仕様](https://learn.chatgpt.com/docs/app-server)、[Claude公式SDKの初期化処理](https://github.com/anthropics/claude-agent-sdk-python/blob/main/src/claude_agent_sdk/_internal/query.py)

### 推論の強さを選ぶ

CLIモデル候補を取得してモデルを選ぶと、そのモデルの「推論の強さ」を選べます。候補はCodexの `supportedReasoningEfforts` とClaude Codeの `supportedEffortLevels` から取得し、アプリ側の固定リストは使いません。CLIが候補を返さないモデルでは既定値のみを使います。強さを上げると応答時間やトークン消費が増えることがあります。

未指定の場合はCLIに設定を任せます。モデル名を変更すると以前の推論指定は解除されます。保存済みの値は候補取得前にも表示しますが、CLIが現在の候補として確認したものとは区別します。会話別のモデル変更画面でも選択でき、モデルと推論の強さをまとめて保存します。会話別の指定がない場合はエージェント設定を使いますが、会話に別モデルを指定した場合は、そのモデルのCLI設定に任せます。

Codexの通常実行では `model_reasoning_effort` 設定、App Serverでは `turn/start.effort`、Claude Codeでは `--effort` に渡します。CLI自身の設定ファイルは変更しません。[Codex App Server](https://learn.chatgpt.com/docs/app-server)、[Claude Code CLI](https://code.claude.com/docs/en/cli-reference)

## 利用量と起動効率

手動実行・チャット受信の結果と「履歴と会話」に、CLIが報告する入力・出力・キャッシュ読込／書込トークン、観測範囲、起動・初期化・待機・合計時間を表示します。未報告の値は「未取得」です。指定モデルと、利用量に実際に報告されたモデルは区別します。依頼本文と実行結果は実行履歴に保存しません。

Codex execはターン単位、App Serverは同じ会話の累計差分を確定できる場合はターン単位、それ以外は直近のAPIリクエストを表示します。Claude Codeのresult利用量は再開前を含む会話累計として扱います。累計の履歴行を足すと二重計上になります。キャッシュ再利用率は入力トークン比で、料金や購読枠の消費率ではありません。[Claude利用量の仕様](https://code.claude.com/docs/en/agent-sdk/cost-tracking)

App Serverは同じ設定の初期化済みプロセスを最大2個まで再利用し、2分間使わなければ終了します。失敗・キャンセル・設定変更時には再利用せず、ウィンドウを閉じる際にも終了します。キャッシュ維持のためのモデル呼出しは行いません。CLI自身の外部設定ファイルを編集した場合は、アプリを再起動してください。

`MeasureAppServerStartup.ps1 -Executable <codex.exeの絶対パス>` はinitializeだけを計測し、推論を開始しません。今回のローカル3回計測は起動＋初期化89～407msでした。実モデルの応答時間・料金削減率を測った値ではありません。

オフライン性能テストは、TestAgentを専用の `desktop/artifacts/performance-fixture` に発行し、`.cab-offline-performance-fixture` マーカーを置いたうえで、`CAB_ENABLE_PERFORMANCE_E2E=1` と `CAB_PERFORMANCE_FIXTURE_DIRECTORY` にその絶対パスを指定してCore.Testsを実行します。`PerformanceUiE2E.ps1 -AppPid <PID> -DataDirectory <隔離データフォルダー>` では利用量表示と履歴保存を検証します。これらは実モデルへ接続しません。
