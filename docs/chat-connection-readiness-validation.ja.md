# チャット接続準備状態の検証

2026-11-06 に、接続設定が足りない状態で Chat の接続ボタンが押せてしまう問題を修正した。保存済み設定から各サービスの準備状態を計算し、設定が揃ったサービスだけ接続可能にする。未設定時は理由と Chat settings への導線を表示する。実際の接続状態を準備条件に含めない。

## 検証

- `dotnet test desktop/ChatAgentBridge.Core.Tests/ChatAgentBridge.Core.Tests.csproj -c Release --no-restore`: 1,613 件成功。
- `dotnet test desktop/ChatAgentBridge.Transports.Tests/ChatAgentBridge.Transports.Tests.csproj -c Release --no-restore`: 296 件成功。
- `python desktop/CheckLocalization.py` と `python desktop/CheckSettingsExpander.py`: 成功。
- Release の自己完結ポータブル版を `.artifacts/chat-readiness-build` に発行: 成功。
- 隔離したポータブル版を CUA で操作し、設定を分離したテストプロファイル内だけに保存した。実サービスへの接続や投稿はしていない。

## UI で確認したケース

- 設定なし: Slack / Mattermost / Discord の接続ボタンがすべて無効、Reconnect も無効。設定を促す案内、常時使える Open chat settings ボタン、空受信箱の説明、0 件表示を確認。
- Mattermost の必須設定だけを用意: Mattermost のみ有効。他サービスは無効。設定案内が消え、設定リンクは残る。
- Slack の必須設定を追加: Slack と Mattermost が有効、Discord は無効。
- Discord は bot token と許可ユーザーだけでは無効。DM を許可した後に有効になることを確認。サーバー ID の経路は実行していない。
- 英語への切り替えと再起動後に保存済みのテスト設定を復元。英語ライト／ダークと、960 px 幅のスナップ表示を確認。3 つの接続ボタン・設定リンク・Reconnect が上部に収まり、切れないことを確認。
- 設定 CTA から Chat settings へ移動できることを確認。
- サイドバーの切断状態ラベルが標準幅で省略される既存表示を見つけ、短い「Chat」/「チャット」表記へ変更。フル名はアクセシブル名とツールチップに維持される。変更後に再ビルドし、標準幅の英語画面で省略されないことを確認。

画面証跡は `.artifacts/chat-readiness/proof/` に保存した: `01-chat-no-config-en-dark.png`、`02-chat-compact-en-dark.png`、`03-chat-all-ready-en-dark.png`。

## 範囲と制限

- UI 確認は設定なし、Mattermost/Slack/Discord の設定遷移、設定 CTA、空受信箱、英語のライト／ダーク、スナップ幅に限定した手動 CUA 操作。最後に Chat・History・Agent settings・Chat settings を混ぜて31回ナビゲーション／フィルターを開閉した。クラッシュは見られなかった。自動ランダム monkey ではなく、限定的な探索操作。
- 接続・再接続ボタンを押しておらず、外部ネットワーク・実トークン・実サービスの送信を試していない。
- 手動実行画面はユーザーの実ワークスペースを指していたため、実行せず、無効なワークスペースの操作テストも行っていない。
- アプリの内部コンソール／ログは確認していない。
- UI 検証は未設定・完全設定の一部状態を確認した。日本語表示の最終修正後画面や、狭幅の日本語表示は未確認。
