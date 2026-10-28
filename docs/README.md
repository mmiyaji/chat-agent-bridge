# ドキュメント / Documentation

利用の概要は [日本語README](../README.ja.md) / [English README](../README.md) を参照してください。ここでは、現行のガイドと過去の検証記録を分けて案内します。各資料の言語はリンク先に従います。

## 利用・設定

- 接続の準備：[Slack](slack-setup.md) / [Mattermost](mattermost-setup.md) / [Discord](discord-setup.md)
- [実行モードと承認](execution-modes.ja.md)
- [エージェントの切り替え](conversation-agent-switching.ja.md) / [モデルの切り替え](conversation-model-switching.ja.md)
- [データのバックアップ・復旧](release-data-recovery.ja.md)
- [ログと履歴の保持設定](log-retention.ja.md) — 保持期間・容量の設定はこの資料を参照

## 開発・検証

- [WinUI版の技術資料・開発手順](../desktop/README.ja.md)
- [CI/CDの運用](ci-cd.ja.md)
- [更新時のデータ移行](data-migrations.ja.md)
- [添付の一時ファイルと起動時の回収](incoming-attachment-recovery.md)
- [利用統計の集計設計](release-activity-statistics.md)
- [ポータブル更新のローカル試験](portable-update-tryout.ja.md)
- [画面切り替えの耐久テスト](ui-navigation-stress.ja.md)

## 配布

- [ポータブル版のリリース手順](../RELEASE.md) — 公開手順の正本
- [Microsoft Store公開の準備・パッケージ名の方針](store-preparation.ja.md)
- [公開済みバージョンと変更履歴](https://github.com/mmiyaji/chat-agent-bridge/releases)
- [preview.8の補足記録](releases/0.1.0-preview.8.md) — 当該版の記録であり、最新の変更履歴ではありません

## 規約・プライバシー・ライセンス

- 利用規約：[日本語](terms.ja.md) / [English](terms.en.md)
- プライバシーポリシー：[日本語](privacy-policy.ja.md) / [English](privacy-policy.en.md)
- [第三者ライセンス](../desktop/ThirdPartyLicenses.md)

## 過去の記録

- [検証・実装記録](archive/validation/README.md)
- [旧Web版・Node.js版の資料](archive/legacy/README.md)

過去の記録には、その時点の画面名・テスト件数・未公開表記・既知の制限が残っています。現在の仕様や公開状況を保証する資料としては使用しないでください。
