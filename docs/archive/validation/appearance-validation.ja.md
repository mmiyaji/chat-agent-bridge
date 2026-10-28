# 外観設定の実装・検証（2026-10-18）

> 過去の検証・実装記録です。記載は当時の状態を示します。現行の手順は[ドキュメント索引](../../README.md)を参照してください。

「エージェント設定 → 外観」に、ライト・ダーク・システムに合わせるの3つの選択肢を追加しました。初期値はシステムに合わせるです。選択は即時に反映・保存し、再起動後も保持します。日本語・英語に対応しています。

外観は `appearance.json` に独立して保存します。設定が存在しない・不正なJSON・null・未知の値の場合はシステムに合わせます。読み込めなかったファイルを自動で上書きせず、明示的な選択時に保存します。保存に失敗した場合は以前のテーマを維持して画面に通知します。

画面ルートの [RequestedTheme](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.frameworkelement.requestedtheme) を切り替え、標準コントロールとThemeResourceのブラシで描画します。タイトルバーには [PreferredTheme](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindowtitlebar.preferredtheme) を設定し、確認ダイアログを開く際にも選択中のテーマを適用します。システム追従はUISettingsのColorValuesChangedをUIスレッドへ転送して反映し、終了時に購読を解除します。

## 検証結果

- Coreテスト197件成功。外観設定の9件では、3種類の保存・読込、不正・null・未知の値のフォールバック、無効な値の保存拒否、接続・実行設定への非干渉を確認しました。
- Releaseビルド成功（警告0・エラー0）、self-contained publish成功。
- `CheckLocalization.py` 成功。日英724項目のキー・書式引数・参照が一致しました。
- `AppearanceUiE2E.ps1` の7項目が成功しました。現在のWindows設定との一致、両テーマへの即時切替、主要6画面と規約ダイアログの描画、両テーマでの再起動、システムへの復帰、日本語表示を確認しました。
- 英語・日本語の外観選択欄を通常サイズ・720×600で確認し、選択肢や説明の重なりはありません。
- タイトルバーの文字とキャプションボタンも配色に追従し、ライトでは黒、ダークでは白で表示しました。
- 外観を切り替える前後で、専用テストデータのsettings.jsonのSHA-256が一致しました。モデル呼び出しやチャット接続・投稿は行っていません。

テスト結果・画像はGit管理外の `desktop/artifacts/appearance-review/` に保存します。Windows全体の配色・コントラスト設定はテストで変更していません。OS設定変更へのイベント購読・反映はコードレビューで確認しています。

## 再実行

`desktop/AppearanceUiE2E.ps1 -Layout <パッケージ配置先> -DataDir <新規の専用フォルダー>` を使います。専用フォルダーは `desktop/artifacts` 配下に作り、`.cab-appearance-test` マーカーファイルを用意してください。通常利用の設定や認証情報は使わず、スクリプトが安全なテスト設定を作成します。
