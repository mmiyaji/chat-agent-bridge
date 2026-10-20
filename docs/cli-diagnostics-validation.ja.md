# CLI確認の診断改善（2026-10-21）

作業フォルダーの未入力・相対パス・無効なパス・存在しないフォルダー・信頼範囲外を個別に案内します。パスが指定されている場合は対象も表示し、信頼範囲外では「エージェント設定」の「信頼するフォルダー」を案内します。信頼条件を緩めたり、保存済みの作業先を自動変更したりはしません。

設定ファイルがない場合に限り、存在するユーザーの「ドキュメント」を初期表示します。Windowsの既知のフォルダーを使うため、リダイレクトされたドキュメントにも対応します。取得できない場合は空欄とし、選択を案内します。初期表示だけでは設定ファイルを作成せず、全般のセットアップ状態も未設定のままです。

設定画面と初期設定ガイドでは、CLI確認ボタンの直下に標準InfoBarでエラーを表示します。バージョン確認までの失敗と、その後のモデル一覧取得の失敗を分けます。モデル一覧が空の場合も、手入力だけでチェックを完了できるような案内はしません。

バージョン確認の異常終了では終了コードを案内します。.NETホストの既知の実行環境不足メッセージは専用の案内に置き換えます。任意の標準エラーや内部例外をそのまま表示しません。

## 検証

```powershell
dotnet test desktop/ChatAgentBridge.Core.Tests/ChatAgentBridge.Core.Tests.csproj -c Release --filter "FullyQualifiedName~CliProbeServiceTests|FullyQualifiedName~CoreTests|FullyQualifiedName~ModelCatalogServiceTests" --verbosity quiet
pwsh -NoProfile -File desktop/CliDiagnosticsUiE2E.ps1 -AppDirectory desktop/artifacts/cli-diagnostics-publish
python desktop/CheckLocalization.py
```

関連Coreテスト47件、Releaseのポータブル構成ビルド、日英リソースの整合性検査はすべて成功しました。Coreテストには既存の空欄・無効な保存済み作業先の保持、日英の診断、実プロセスによる実行環境不足・終了コードの確認、標準エラーを表示しない確認を含みます。

UI試験は独立したデータフォルダーとテスト用CLIを使います。日本語・ダークと英語・ライトで、ドキュメントの初期表示、ウィザードのエラー表示、作業先の未入力・相対パス・存在しないフォルダー、信頼範囲、実行ファイルの未検出、設定修正後のモデル取得、モデル取得だけが失敗する場合を確認し、両条件の全項目が成功しました。エラー表示の折り返しと見切れもスクリーンショットで確認しています。結果とスクリーンショットは `desktop/artifacts/cli-diagnostics-359315d355334b36b1b15e17e9cf6346/` に保存しました。試験用プロセスの.NET参照先だけをビルドに使った環境にそろえ、利用者の環境変数は変更しません。

公開やGitHubへのpushは行っていません。
