# テスト・検証ガイド

WinUI版を中心とする現行実装の確認方法をまとめます。CIで自動実行する検査、変更箇所に合わせて選ぶUI・動作検証、リリース前の確認を分けています。旧TypeScript版のテストは互換性のためCIで実行されていますが、新機能の確認先はWinUI版です。

## どの確認を選ぶか

| 変更 | 基本の確認 | 必要に応じて追加 |
| --- | --- | --- |
| Core / ViewModel / 保存・移行ロジック | `dotnet test desktop/ChatAgentBridge.Core.Tests/ChatAgentBridge.Core.Tests.csproj -c Release` | UIから利用する主要シナリオのUI E2E |
| Slack / Mattermost / Discord接続 | `dotnet test desktop/ChatAgentBridge.Transports.Tests/ChatAgentBridge.Transports.Tests.csproj -c Release` | 影響するサービスのUI E2E。実サービスを使う検証は専用環境で別途 |
| XAML・画面遷移・設定UI | 関連Coreテストと `python desktop/CheckLocalization.py`（文字列変更時） | 影響領域のUI E2E / Smoke。レイアウトやナビゲーションは幅・言語・テーマを含む適切なシナリオ |
| 設定項目の開閉・設定ページ追加 | `python desktop/CheckSettingsExpander.py` | 関連する設定画面UI E2E / Smoke |
| CLI検出・エージェント実行 | 関連Coreテスト | CLI設定・実行・再接続を扱うUI検証。認証が必要な実CLI試験は明示的に分離 |
| 配布設定・バージョン・ライセンス告知 | CIの対応する検査、または `Test-ReleaseBuildVersion.ps1` / `Test-StorePackageConfiguration.ps1` / `Test-ThirdPartyNotices.ps1 -SelfTest` | 発行物に対する検査は対象の配布形式を作成して実行 |
| 文書のみ | リンク先・コマンド・ファイル名を確認 | コマンドや手順を変更した場合は対象の軽量チェック |

小さな変更にテスト一式を毎回実行する必要はありません。影響の大きさ、失敗時の影響、実行時間、専用環境の有無で確認範囲を選び、重要な確認を省略した場合は理由を作業報告に記載します。

## CIで自動実行する検査

PRと対象ブランチへのpushでは、[`.github/workflows/ci.yml`](../.github/workflows/ci.yml) の `checks` が次を実行します。

- TypeScript型チェックと旧Node版ユニットテスト（`npm run check`、`npm test`）
- ローカライズ、設定Expander、Pythonのユニットテスト
- リリースバージョン、Storeパッケージ設定、第三者ライセンス告知の検査
- .NET Core / TransportsテストとWinUIポータブル版のRelease publish

UI Smokeは通常のPRではなく、CIの手動実行またはバージョンタグ検証で動きます。リリースタグのワークフローはCIを呼び出してからx64 / ARM64パッケージを作成・検査します。トリガーと公開条件の正本は[CI/CD運用ガイド](ci-cd.ja.md)です。

## UIスクリプトの分類

UIスクリプトは現在 `desktop/` 直下に置かれています。ファイル移動で既存手順や資料の参照先を壊さないよう、まず用途で探せるように分類します。ファイル名の `E2E` / `Smoke` は歴史的な命名を含むため、名称だけで所要時間・隔離要件・外部接続の有無を判断せず、各スクリプトのパラメーターと説明を確認してください。

| 領域 | 主なスクリプト例 |
| --- | --- |
| 起動・初期設定 | `WizardUiE2E.ps1`、`WizardLayoutUiE2E.ps1`、`StartupSplashUiSmoke.ps1`、`StartupFailureUiSmoke.ps1`、`OnboardingWorkspaceUiE2E.ps1` |
| 設定・ナビゲーション・レイアウト | `GeneralSettingsUiE2E.ps1`、`LayoutUsabilityUiE2E.ps1`、`SettingsPlacementUiE2E.ps1`、`ResponsiveNavigationUiE2E.ps1`、`SettingsSearch*`、`Navigation*Smoke*.ps1` |
| 会話・履歴・受信箱 | `ConversationUiE2E.ps1`、`HistoryBrowserUiE2E.ps1`、`HistorySplitterUiE2E.ps1`、`InboxGalleryUiSmoke.ps1`、`InboxPersistenceUiSmoke.ps1` |
| CLI・エージェント・権限 | `CliToolsUiE2E.ps1`、`CliDiagnosticsUiE2E.ps1`、`CliVerificationUiSmoke.ps1`、`ExecutionPolicyUiE2E.ps1`、`ProviderPermissionUiE2E.ps1`、`LiveOutputUiE2E.ps1` |
| チャット接続 | `Slack*UiE2E.ps1`、`Mattermost*UiE2E.ps1`、`Discord*UiSmoke.ps1`、`RemoteOperationsUiE2E.ps1` |
| 表示・利用状況・性能 | `AppearanceUiE2E.ps1`、`LanguageUiE2E.ps1`、`ActivityUiE2E.ps1`、`Usage*UiE2E.ps1`、`PerformanceUiE2E.ps1`、`MonkeyTest.ps1` |
| 更新・配布・リリース | `AutomaticUpdateUiE2E.ps1`、`PortableUpdate*`、`PortableUiE2E.ps1`、`ReleaseReadinessUiSmoke.ps1`、`Test-PortableReleasePublishing.ps1`、`Test-StorePackageConfiguration.ps1` |
| 静的・補助チェック | `CheckLocalization.py`、`CheckSettingsExpander.py`、`Test-ThirdPartyNotices.ps1`、`desktop/test-support/` 内のProbe / Fixture |

上記は探索用の代表例であり、すべてのスクリプトや個々の実行条件を列挙したものではありません。検索時は `rg --files desktop` を使い、対象スクリプトの先頭コメント・`param(...)`・安全ガードを読んでから実行してください。

## UI検証の安全な実行

- 実アプリの通常データや認証情報を使わず、スクリプトが要求する専用データディレクトリ、マーカーファイル、隔離ビルドを使います。
- `desktop/artifacts` を指定する場合は、そのテスト専用の新しいディレクトリを作り、スクリプトが要求する `.cab-*` マーカーとTestAgent / Probeを用意します。要求を満たさない状態で通常の保存先を指定しません。
- 名前にMattermostやSlackがあっても、ローカル模擬サーバーを使うものと実サービスへ接続するものがあります。READMEとスクリプトの起動条件を確認し、実サービスを使う試験はテスト用接続先・ユーザー・チャンネルで実施します。
- UIテストは対話型Windowsデスクトップを必要とする場合があります。CIログだけで画面視認性やARM64実機の動作を保証できない場合は、該当する手動確認を別途行います。

## リリース前

リリース前は、変更範囲に対応したCIがすべて成功していることを確認します。加えて、`docs/ci-cd.ja.md` の手順に従い、手動実行のUI Smokeと証跡、Portable releaseのパッケージ検査を確認します。画面やチャット連携に変更がある場合は、影響するUI E2Eまたは実サービス検証を選んで追加します。配布CPUの実機確認が必要な変更では、CIのクロスビルドだけで代替せず対象機で確認します。

リリース候補の最低限のUI SmokeはCIの `ReleaseReadinessUiSmoke.ps1` が担います。これで全画面の回帰検証が済むわけではないため、リリース差分に応じて関連スクリプトを選びます。
