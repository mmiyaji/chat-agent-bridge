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

この時点では今回報告されたクラッシュは再現できていませんでした。この結果は、描画ドライバーの問題、別PC、別の保存データ、実際のCLI実行中の問題まで否定するものではありません。後続の障害注入試験については下記を参照してください。

## 内部タブに絞った追加試験

エージェント設定・チャット設定の内部タブで発生したという補足を受け、ループバック接続中に追加で990回切り替えました。

- 日本語・ダーク：770回。各タブの選択直後に幅を変更。
- 英語・ライト：220回。内部タブ選択とサイズ変更の間の待機時間をなくして実行。

全エージェント、共通設定、Mattermost・Slack・Discordを対象とし、幅500／800／1000／1440で内部ナビゲーションの縦・上部タブ切り替えも重ねました。いずれもプロセスが終了せず、接続と保存済み設定を保持しました。この通常試験ではクラッシュは未再現でした。

```powershell
pwsh -NoProfile -File desktop/SettingsScopesUiSmoke.ps1 -AppDirectory desktop/artifacts/demo-app -TestAgentPath desktop/artifacts/cli-verification-test-agent/ChatAgentBridge.TestAgent.exe -Language en -Theme light -StressCycles 20 -StressSelectionDelayMs 0 -ResizeDuringSelection
```

この試験のCLIはオフラインのテスト用実行ファイルです。実際のCLIやチャットの認証情報を設定する必要はありません。

## Dataへのアクセス失敗を含む試験

利用者から、別PCではアプリの展開先とDataがともにZENMUのVirtual Driveにあるとの補足がありました。実際のZENMUはこの検証PCにはないため、隔離した `agent-settings-drafts.json` を排他的に開いて読み込み失敗を注入しました。実行ファイルがあるドライブ全体の切断や、ZENMU固有の動作を再現する試験ではありません。

この試験では次の2点を確認しました。

- 保存先にアクセスできないとタブが元に戻るものの、理由が見えない不具合。ページ全体のInfoBarにエラーを出し、アクセス復旧後に再選択できるよう修正。初回の共通設定からの移動も対象。
- 障害復旧後の高速なタブ選択・サイズ変更中に、WinUIの `NavigationViewItemAutomationPeer` → `NavigationView::RaiseItemInvokedForNavigationViewItem` → `IItemsRepeater::ItemsSourceView` でアクセス違反（0xc0000005）を再現。ダンプと一致するMicrosoftの公開ソースでは、親ItemsRepeaterがnullの場合のガードがありません。

内部設定タブのアクセシビリティ選択は、ネイティブの同期Invokeを呼ばず、通常の設定切り替えと同じ待機処理を使う方式に変更しました。選択状態・名前・選択コンテナーはUI Automationに公開します。設定の保存・権限確認・有効なエージェントの切り替え条件は維持します。また、設定読み書き・CLI検証キャッシュ読み込みの初期ファイルアクセスをUIスレッドの外へ移しました。

参考：[MicrosoftのNavigationView実装](https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/NavigationView/NavigationView.cpp)、[標準AutomationPeerの実装](https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/NavigationView/NavigationViewItemAutomationPeer.cpp)。類似するUIA関連の報告 [#11028](https://github.com/microsoft/microsoft-ui-xaml/issues/11028) はありますが、同一の不具合と断定していません。

```powershell
pwsh -NoProfile -File desktop/SettingsScopesUiSmoke.ps1 -AppDirectory desktop/artifacts/zenmu-nav-resilience -TestAgentPath desktop/artifacts/cli-verification-test-agent/ChatAgentBridge.TestAgent.exe -Language ja -Theme dark -DataAccessFaults -StressCycles 20 -StressSelectionDelayMs 0 -ResizeDuringSelection
```

この再現はUI Automationによる選択を含みます。別PCでの停止原因が同じか、ZENMUの動作が直接の引き金だったかは未確認です。実行ファイル自体が読めなくなる事象まで、この修正で防げるとは限りません。保護対象のDataや作業フォルダーを移動したり、ディスク暗号化を無効化したりする変更は行っていません。

修正後の日本語・ダークでは、2種類の読み込み失敗からの復旧、保存失敗のキャンセル／破棄、再起動後の設定復元、待機時間なしの220回のタブ切り替え・サイズ変更を完了しました。クラッシュは発生せず、使用中の設定とループバックのチャット接続を保持しました。結果は `desktop/artifacts/743c148b-3c6b-4139-bcde-3a172318bcd7/results.json` に保存しています。

英語・ライトでも同じ一連の試験と220回の切り替えが成功しました。エラーInfoBarの表示も日英・両テーマで確認済みです。英語側の結果は `desktop/artifacts/185e850d-00ce-44c9-9df8-d75a008370af/results.json` に保存しています。ポータブルビルドと翻訳リソースの整合性検査も成功しました。
