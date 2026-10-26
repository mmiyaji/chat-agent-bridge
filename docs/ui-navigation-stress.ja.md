# 画面切り替えの耐久テスト

`desktop/NavigationStressUiSmoke.ps1` は、新規の隔離設定で画面切り替えを繰り返します。
実際のチャットには接続せず、CLIやAIも実行しません。実行履歴と会話一覧には、それぞれ500件の架空データを用意します。

PowerShell 7で、`desktop/artifacts` 内にあるポータブル版のビルドを指定します。

```powershell
pwsh -NoProfile -File desktop/NavigationStressUiSmoke.ps1 -AppDirectory desktop/artifacts/demo-app -Actions 600 -Seed 20261026 -Language ja -Theme dark
```

同じシードでは同じ操作列を生成します。画面・設定タブ・履歴内タブ・折りたたみの操作と、幅500～1440のサイズ変更を混ぜます。現在表示されていない操作対象はスキップします。

`-IncludeEdits` を付けると、隔離設定内の作業フォルダー変更とテーマ変更も混ぜます。信頼範囲や実行するCLIは変更しません。

結果は `desktop/artifacts/navigation-stress-<GUID>/` に保存します。

- `actions.json`：操作列、経過時間、プライベートメモリ量
- `results.json`：シード、完了件数、失敗内容、対象実行ファイルとSHA-256
- `hang.dmp`：異常時にテストプロセスが残っており、`dotnet-dump` が利用できる場合の診断ダンプ

UI Automationの呼び出しにタイムアウトを設け、ウィンドウの応答も確認します。終了時に止めるのは、この試験が起動したプロセスだけです。

## 2026-10-26の確認

起動中の `demo-app` と同じビルドで、次の試験を完了しました。

| 条件 | 操作数 | 結果 |
| --- | ---: | --- |
| 日本語・ダーク、シード20261026 | 600 | 応答停止・クラッシュなし、設定変更なし |
| 英語・ライト、シード20261027 | 1,400 | 応答停止・クラッシュなし、設定変更なし |
| 日本語・テーマ／作業フォルダー変更あり、シード20261028 | 350 | 応答停止・クラッシュなし、無関係な実行設定を保持 |

加えて、既存の `SettingsScopesUiSmoke.ps1` でループバックのテスト用Mattermostへ接続し、サイズ変更を含む120回の設定タブ切り替え後も接続と設定を保持できることを確認しました。検索経由の設定移動も日本語・ライト／ダーク・幅500で成功しました。

今回報告されたクラッシュは再現できておらず、原因は未特定です。この結果は、描画ドライバーの問題、別PC、別の保存データ、実際のCLI実行中の問題まで否定するものではありません。

## 内部タブに絞った追加試験

エージェント設定・チャット設定の内部タブで発生したという補足を受け、ループバック接続中に追加で990回切り替えました。

- 日本語・ダーク：770回。各タブの選択直後に幅を変更。
- 英語・ライト：220回。内部タブ選択とサイズ変更の間の待機時間をなくして実行。

全エージェント、共通設定、Mattermost・Slack・Discordを対象とし、幅500／800／1000／1440で内部ナビゲーションの縦・上部タブ切り替えも重ねました。いずれもプロセスが終了せず、接続と保存済み設定を保持しました。クラッシュは引き続き未再現です。

```powershell
pwsh -NoProfile -File desktop/SettingsScopesUiSmoke.ps1 -AppDirectory desktop/artifacts/demo-app -TestAgentPath desktop/artifacts/cli-verification-test-agent/ChatAgentBridge.TestAgent.exe -Language en -Theme light -StressCycles 20 -StressSelectionDelayMs 0 -ResizeDuringSelection
```

この試験のCLIはオフラインのテスト用実行ファイルです。実際のCLIやチャットの認証情報を設定する必要はありません。
