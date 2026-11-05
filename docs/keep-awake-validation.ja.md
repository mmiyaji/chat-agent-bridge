# スリープ抑止の検証記録

## 対象

WinUI版の「全般」に、既定オフのスリープ抑止設定を追加しました。設定は `keep-awake.json` に独立保存し、「オフ」「依頼または手動操作の実行中」「チャット受信が有効な間」から選べます。AC電源限定が初期値です。

Windowsの `PowerCreateRequest` / `PowerSetRequest` を使い、依頼・手動実行・チャット受信の意図がある間だけ `SystemRequired` 要求を保持します。画面消灯・ロック要求、電源プラン、セキュリティ設定は変更しません。Windowsの手動スリープやふたを閉じる操作は引き続き有効です。モダンスタンバイでは、バッテリー時の抑止がシステムのタイムアウトから約5分に制限される場合があります。

## 実施した確認

- `dotnet test desktop/ChatAgentBridge.Core.Tests/ChatAgentBridge.Core.Tests.csproj -c Release` — 1608件成功。設定の既定値・保存・不正JSONからの復旧、ポリシー条件、要求の重複取得防止・終了時解放・復帰後の再適用を確認。
- `python desktop/CheckLocalization.py` — 日英のリソースキーと書式引数が一致。
- `python desktop/CheckSettingsExpander.py` — 成功。
- `dotnet build desktop/ChatAgentBridge.Desktop/ChatAgentBridge.Desktop.csproj -c Release -p:Platform=x64` — 成功、警告0件。
- `dotnet publish desktop/ChatAgentBridge.Desktop/ChatAgentBridge.Desktop.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:CABPortable=true -o .artifacts/keep-awake-build` — ローカルUI確認用のビルドを作成。

## 未確認

- 実チャット受信中の電源要求は未検証です。受信意図の条件はポリシーテストとコードレビューで確認しています。
- `powercfg /requests` の実機確認は管理者権限が必要と表示されたため、昇格を行わず未実施です。OSスリープや電源プランを変える操作も実施していません。
- 実際のスリープ、復帰、AC／バッテリー切り替え試験は行っていません。ポリシーと電源要求ライフサイクルはテスト用の偽アダプターで確認しています。
- 実チャットサービスへの接続・送信は行っていません。
## 隔離した実画面・ネイティブAPIの確認

最新main `8860e63` から `feature/prevent-sleep` を作成。Lunaによる実装後、主担当がAPIの要求種別・構造体・ハンドルの解放、保存失敗時のポリシー継続、UI通知、電源切り替え後の再取得、起動時の非同期読み込みと終了時の保存待機をレビュー・補強した。

- `.artifacts/keep-awake-ui-root.txt` が示す専用Data・ワークスペースのみ使用。資格情報や実AIサービスは使用していない。
- 初回は「オフ」「AC電源接続中のみ」が初期値。UIから処理中モードへ変更し、`keep-awake.json` の保存とアプリ再起動後の復元を確認。
- 作業先内の30秒待機するPowerShellスクリプトを手動画面から実行。実行中は「Windowsの電源要求は有効です。」、正常完了（終了コード0）後は「現在はスリープを抑止していません。」へ遷移し、ネイティブAPIの取得・解除がエラーなく成功した。
- 日本語・英語 × ライト・ダークの実画面を確認。英語ダークでは約680px幅へ縮小し、カード内の設定が折り返され、選択欄・チェック・説明が見切れないことを確認。
- AC限定チェックを10回続けて操作した後、画面と保存ファイルが同じ値であることを確認。保存中は編集を一時的に無効化する。
- 選択欄に日英の読み上げ名を付与。待機中の表示を、設定オフと混同しにくい表現に変更。
- 証跡: `.artifacts/keep-awake-active.jpg`、`keep-awake-ja-light.jpg`、`keep-awake-en-light.jpg`、`keep-awake-en-dark.jpg`、`keep-awake-en-narrow.jpg`。
- 検証アプリは通常の終了ダイアログから終了。push・mainへのマージ・配信は行っていない。
