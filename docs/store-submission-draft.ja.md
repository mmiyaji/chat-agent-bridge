# Microsoft Store初回申請の下書き

2026-10-28にPartner Centerで登録しました。審査への提出・一般公開は実施していません。

- 製品名：Chat Agent Bridge
- Store ID：`9N1LZK25FSGR`
- 申請：Submission 1
- パッケージ：`mmiyaji.ChatAgentBridge` / `1.0.0.0` / x64・ARM64
- [Partner Centerの製品概要](https://partner.microsoft.com/ja-jp/dashboard/products/9N1LZK25FSGR/overview)

## 保存・確認できた項目

- 価格：基本価格0（無料）で下書き保存。市場は既定の全市場。
- プロパティ：「開発者ツール／ユーティリティ」、個人情報の使用あり、生成AI機能あり。
- プライバシーポリシー：GitHubの現行英語文書。サポートはGitHub Issues。
- Windowsユーザー単位で暗号化された設定を含むため、OneDriveへの自動バックアップの宣言はオフ。
- Store掲載情報：日本語・英語。各言語の説明、短い説明、主要機能4件、実機スクリーンショット4枚、開発元・権利表記を保存。「完了」を確認。
- 画像は `docs/assets/screenshots` の実機キャプチャを使用。履歴・統計には説明用サンプルデータの注記を付与。
- パッケージ：x64・ARM64をアップロードし、両方の `Validated` とパッケージ項目の「完了」を確認。
- 対象デバイスはWindowsデスクトップのみ。将来のデバイスファミリーへの自動提供はオフ。
- 公開オプション：「[今すぐ公開]を選択するまで、この提出物を公開しない」を選択・保存。

## 未完了の項目

- 年齢区分は回答を下書き保存。ユーザー投稿のヌード・露骨な暴力の扱いについて、投稿内容を検出・モデレートする機能がない点を踏まえた最終確認が必要。
- 追加のテスト情報の登録。ページを開いた時点で再サインインが必要になったため未保存。
- Store経由のインストール・更新・通知ボタン・CLI実行の実機検証、認定・審査への提出。

## 審査担当者向けの説明案（未登録）

Chat Agent Bridge is a native Windows desktop utility that connects a user's installed AI agent CLI to Slack, Mattermost or Discord. It requires runFullTrust to launch the CLI selected by the user and to read and write within user-selected working folders. It runs as the current Windows user and does not request administrator elevation.

No account or CLI is required to open the app. On first launch, review the introduction and policies, then choose to configure the app later. General, agent settings, chat settings, history, About and the diagnostics pages can be inspected without chat credentials. To test CLI diagnostics, select an installed supported CLI and an existing trusted working folder, then run the CLI check. Live AI requests require that CLI's own authentication and service entitlement. Chat connectivity requires a bot configured in a supported chat service. No credentials are embedded in the package.

Requests are restricted by allowed-user and conversation settings. The default request mode requires approval when starting a conversation. Sharing result text and uploading generated files require confirmation by default. The Store build uses Store updates rather than replacing installed files through the portable ZIP updater. Optional portable notification-package setup is hidden in the Store build. The app does not install agent CLIs or silently elevate their permissions.

The product is an unofficial alternative for environments where the AI provider's official remote-control feature cannot be used. AI subscriptions and service charges are separate. Before final submission, confirm whether dedicated review credentials or further testing instructions are needed; do not provide a normal user's bot token or AI login.
