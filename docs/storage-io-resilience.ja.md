# 保存先が不安定な場合の設定処理

`codex/ui-navigation-crash-audit` の `0f46de4` から `codex/storage-io-resilience` を作成して修正しました。ZENMUの実環境を変更するものではありません。

## 対応内容

- 設定読込・保存、作業フォルダーの存在確認、信頼フォルダーの検査、CLIの信頼回復、初期設定・手動実行の作業フォルダー検証を画面処理から分離します。
- 非同期の検査中に作業フォルダーや信頼設定が変わった場合は、古い検査結果で設定を変更しません。権限を拡張する場合の同意は維持します。
- 設定は `File.Exists` の結果だけで初回起動と判断せず、ファイルを直接開きます。未作成のDataフォルダーは、利用可能な親ディレクトリを確認して初回起動として扱います。読み込み失敗・破損・利用不能なドライブは初期値に置き換えません。
- 同じ `SettingsStore` で一度読み込み・保存に成功した設定が消えた場合も、初回起動として扱いません。この記憶はインスタンスの存続中だけ有効です。
- 同じストアの処理を直列化します。共有・ロック違反（Windowsエラー32／33）の追加再試行は、初回を含めて最大3回、待機は40／80msです。既存の原子的なファイル置換・暗号化は維持します。ファイル置換側の既存の再試行も別途適用されます。
- 時間切れとして表示した後も裏で保存を続ける方式にはしません。OS側のI/Oが戻らないケースを強制的に打ち切ることまでは保証しません。

## 診断情報

既存の情報ページのサポート情報エクスポートに次を追加します。

- `StorageOperations`：設定I/Oの時刻、種類、所要時間、再試行回数、成否、数値のHRESULT。最大64件。
- `SettingsNavigation`：エージェント設定／チャット設定の内部タブ切り替えの時刻、所要時間、成否。最大32件。

診断はメモリ内のみで、アプリ終了時に消えます。障害中のDataへの追加ログ書き込みは行いません。パス、設定値、トークン、会話本文、生の例外メッセージは含めず、エクスポート時にも許可された項目だけに制限します。突然終了した場合の解析には別途クラッシュダンプが必要です。

## 検証

Coreのテストでは、実際のWindowsファイルロックからの復旧、ロック継続時の保存済みファイル保持、利用不能なドライブ、初回起動、破損ファイル、順序制御、キャンセル、診断の件数制限・機密情報除外を確認します。

画面の試験には、隔離したData、ループバックのMattermost、オフラインのテスト用CLIを使用します。普段使っているアプリや実チャットの設定は変更しません。

ZENMUのドライブ自体の停止や実行ファイル／DLLへのアクセス不能は、この試験では再現していません。保護対象の配置変更、暗号化の無効化、公開は行いません。

### 実行結果

- Windows上の関連単体テスト：104件成功、スキップなし。設定、ロック、診断の機密情報除外、信頼ポリシー、初期設定、バックアップを対象。
- 日本語・ダーク：保存失敗と復旧、未保存編集の保持／破棄、再起動、手動実行の拒否と復旧、55回の高速切り替えに成功。`desktop/artifacts/941b21e7-d4d5-416c-84a4-e6addf1fe687/`。
- 英語・ライト：同じ設定・障害復旧・手動実行の試験と110回の切り替えに成功。`desktop/artifacts/2bb40b93-f19e-4084-9a80-06821431282e/`。その後、事前警告を維持する非同期検証のまとめ処理を追加し、日本語側でも再検証。
- 信頼回復のウィザード：日英でキャンセル、同意中のリンク先差し替えの拒否、明示的な同意による復旧に成功。`desktop/artifacts/cli-diagnostics-bc62d31f8a0a47c0a635e49904a384e8/`。
- サポート情報：日英・ライト／ダークの4条件、幅500で診断表示・安全なJSONプレビュー・キャンセルの8項目が成功。`desktop/artifacts/diagnostics-ui-8f12887ebd8c438581bbec42a356246f/`。

再実行例（ポータブル検証ビルドを作成後）：

```powershell
pwsh -NoProfile -File desktop/SettingsScopesUiSmoke.ps1 -AppDirectory desktop/artifacts/storage-io-resilience-reviewed -TestAgentPath desktop/artifacts/cli-verification-test-agent/ChatAgentBridge.TestAgent.exe -Language ja -Theme dark -DataAccessFaults -StressCycles 5 -StressSelectionDelayMs 0 -ResizeDuringSelection
pwsh -NoProfile -File desktop/CliDiagnosticsUiE2E.ps1 -AppDirectory desktop/artifacts/storage-io-resilience-reviewed -WizardTrustOnly
pwsh -NoProfile -File desktop/DiagnosticsUiE2E.ps1 -ArtifactRoot desktop/artifacts/storage-io-resilience-reviewed -SupportOnly
```
