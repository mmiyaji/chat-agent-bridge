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
- この検証はハンドラーへの直接注入であり、画面描画、承認からCLI実行・finallyによる解放までの経路全体、実サービスへの投稿、認証済みCLIの推論は未実施。エンドツーエンドの保証ではない。

## 再現コマンド

```powershell
dotnet test desktop/ChatAgentBridge.Core.Tests/ChatAgentBridge.Core.Tests.csproj -c Release --filter FullyQualifiedName~ExecutionExceptionDiagnosticTests
dotnet publish desktop/ChatAgentBridge.Desktop/ChatAgentBridge.Desktop.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:CABPortable=true -o .artifacts/execution-diagnostics/fixed-app
dotnet run --project desktop/test-support/ExecutionExceptionProbe/ExecutionExceptionProbe.csproj -c Release -- .artifacts/execution-diagnostics/fixed-app .artifacts/execution-diagnostics/probe-new
```

プローブの保存先は新規ディレクトリが必須。結果は `.artifacts/execution-diagnostics/probe-new/results.json` に保存される。今回の最終証跡は `.artifacts/execution-diagnostics/probe-fixed-verified/results.json`、Core全件のTRXは `.artifacts/execution-diagnostics/full-core/` に保存した。Windowsでは保存先までのパスにジャンクションなどの再解析ポイントが含まれると、アプリのストレージ安全確認により書き込みが拒否される。今回のリポジトリは `C:\Users\mail\Documents\git\chat-agent-bridge` が `D:\git\chat-agent-bridge` へのジャンクションのため、検証は実パス `D:\git\chat-agent-bridge` から実行した。

## 検証範囲

実ViewModelの例外処理と監査ログ保存を検証した。UI画面描画、承認からCLI実行・finallyによる解放までの経路全体、実サービスへの投稿、認証済みCLIの推論は未実施。今回は例外ハンドラーへの直接注入による検証であり、エンドツーエンドの保証ではない。

## コミット前レビュー

2026-11-07、診断の表示順・文字数制限、保存項目の検証、保存失敗時の終了処理、終端履歴の保持、関連文書を確認。コミットを妨げる問題は見つからなかった。関連Coreテスト71件、日英プローブ14件、ローカライズ検査を再実行して成功。証跡は `.artifacts/execution-diagnostics/precommit-tests/` と `.artifacts/execution-diagnostics/probe-precommit/results.json`。製品コードの追加変更はなく、Core全件・Releaseビルドは直前の成功結果を確認し、再実行していない。
