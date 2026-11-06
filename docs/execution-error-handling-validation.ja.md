# エージェント実行エラー処理の検証

対象: 2026-11-07 JST。
診断表示の切り捨てを防ぎ、安全な診断情報を監査ログへ記録する修正を検証した。

## 結果

- 前回: `ExecutionExceptionDiagnosticTests` 21件成功。14種類の例外分類、内部例外の型・HRESULT、メッセージ・パス・トークン・スタック非露出、終端状態の保持を確認。
- 前回: 関連Coreテスト73件、x64ポータブルRelease publish、日英ローカライズ検査成功。実ViewModelプローブは日英各5件、計10件成功。
- 前回に確認した問題: 診断を既存結果の後ろへ追加すると上限で切り捨てられる。診断詳細が結果欄にしかなく永続ログから原因を調べられない。
- Core全テスト1,644件成功（Release、no-build）。
- x64ポータブルRelease publish成功。`CheckLocalization.py` 成功。
- 公開ビルドの `ChatInboxViewModel.HandleExecutionExceptionAsync` を隔離した検証専用データ領域で直接呼び出すプローブが日英計14件成功。通常・長文出力の診断可視性とCLI出力の保持、未作成・実行中・終端履歴、監査イベントの種別・実行ID・原因分類・例外型・HRESULT、メッセージ・パス・依頼文の非保存を確認。監査保存を故意に失敗させた日英2件では診断表示と実行終了・警告を確認。
- 通常の監査保存シナリオでは各言語6件の診断イベントを記録。監査保存を失敗させた2件は記録されないことを確認。
- 上記の14件はハンドラーへの直接注入プローブであり、単独では画面描画・実行経路全体を検証していない。後述のE2Eでローカル模擬Mattermost経由の画面・承認・実行・監査・回復を追加確認した。実サービスへの投稿と認証済みCLIの推論は未実施。

## 実行経路E2E（2026-11-07）

- 最終成功実行は日英それぞれ1回。ポータブル公開ビルド `fixed-app` と隔離した自己完結型 TestAgent を使用。ローカル模擬Mattermostから依頼を受信し、アプリ上で承認して、65,500文字以上の決定的なCLI結果を生成した。
- 結果受信後の履歴確定時に、検証用データ内の `history.json` を `FileShare.Read` で保持し、Windowsの置換を失敗させた。実行経路で `UnauthorizedAccessException` / HRESULT `80070005` が発生し、英語・日本語の画面で終端状態、履歴・監査保存の警告、原因・例外型・HRESULTを確認した。
- 表示出力は診断を先頭に保持し、CLI結果の先頭も続けて表示。TextBoxのUI Automation値は改行正規化で63,997文字だったが、64,000文字上限内で診断が見えることを確認した。保存監査イベントは `execution_exception`、対象実行ID、cause `0`、例外型、HRESULTを含み、依頼文・トークン・例外メッセージ・パス・スタックを含まない。チャット返信にも診断・CLI結果は含まれなかった。
- ファイル共有を解除した後の別依頼は実行・履歴保存とも成功。意図的に履歴保存を壊した依頼は、画面で終端状態を示し監査イベントも保存した一方、故障中の履歴JSONには `running` が残る。failed状態の履歴永続化まで検証できるシナリオではない。
- 成功結果: 英語4確認、[英語画面](../.artifacts/execution-error-e2e/run-en-c8762199ab9e40aa8a2ea8e05db2ee3f/execution-error-en-long-output.png)、[英語回復後](../.artifacts/execution-error-e2e/run-en-c8762199ab9e40aa8a2ea8e05db2ee3f/execution-error-en-recovery.png)。日本語4確認、[日本語画面](../.artifacts/execution-error-e2e/run-ja-29843b85782345f2be18eca5a2727681/execution-error-ja-long-output.png)、[日本語回復後](../.artifacts/execution-error-e2e/run-ja-29843b85782345f2be18eca5a2727681/execution-error-ja-recovery.png)。JSON結果は各実行フォルダーの `results.json`。
- 実サービスへの通信・投稿と認証済みCLI／AI推論は未実施。例外ハンドラーを直接呼ぶ検証とは異なり、受信・承認・CLIプロセス・UI表示・監査保存・後続依頼までを確認した。

初期の試行は検索入力・依頼選択の自動操作、およびUI Automationの改行正規化を考慮しない文字数アサーションで停止した。ハーネスを修正した後、日英で例外から回復まで成功した。製品コードの追加修正は行っていない。

## 再現コマンド

```powershell
dotnet test desktop/ChatAgentBridge.Core.Tests/ChatAgentBridge.Core.Tests.csproj -c Release --filter FullyQualifiedName~ExecutionExceptionDiagnosticTests
dotnet publish desktop/ChatAgentBridge.Desktop/ChatAgentBridge.Desktop.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:CABPortable=true -o .artifacts/execution-diagnostics/fixed-app
dotnet run --project desktop/test-support/ExecutionExceptionProbe/ExecutionExceptionProbe.csproj -c Release -- .artifacts/execution-diagnostics/fixed-app .artifacts/execution-diagnostics/probe-new
dotnet publish desktop/ChatAgentBridge.TestAgent/ChatAgentBridge.TestAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .artifacts/execution-error-e2e/test-agent
& desktop/test-support/ExecutionExceptionUiE2E.ps1 -AppDirectory .artifacts/execution-diagnostics/fixed-app -TestAgentPath .artifacts/execution-error-e2e/test-agent/ChatAgentBridge.TestAgent.exe -Language both
```

プローブの保存先は新規ディレクトリが必須。結果は `.artifacts/execution-diagnostics/probe-new/results.json` に保存される。今回の最終証跡は `.artifacts/execution-diagnostics/probe-fixed-verified/results.json`、Core全件のTRXは `.artifacts/execution-diagnostics/full-core/` に保存した。Windowsでは保存先までのパスにジャンクションなどの再解析ポイントが含まれると、アプリのストレージ安全確認により書き込みが拒否される。今回のリポジトリは `C:\Users\mail\Documents\git\chat-agent-bridge` が `D:\git\chat-agent-bridge` へのジャンクションのため、検証は実パス `D:\git\chat-agent-bridge` から実行した。

## 直接プローブの検証範囲

実ViewModelの例外処理と監査ログ保存をハンドラーへの直接注入で検証した。実行経路の確認は上記「実行経路E2E」を参照。実サービスへの投稿と認証済みCLIの推論は未実施。

## コミット前レビュー

2026-11-07、診断の表示順・文字数制限、保存項目の検証、保存失敗時の終了処理、終端履歴の保持、関連文書を確認。コミットを妨げる問題は見つからなかった。関連Coreテスト71件、日英プローブ14件、ローカライズ検査を再実行して成功。証跡は `.artifacts/execution-diagnostics/precommit-tests/` と `.artifacts/execution-diagnostics/probe-precommit/results.json`。製品コードの追加変更はなく、Core全件・Releaseビルドは直前の成功結果を確認し、再実行していない。
