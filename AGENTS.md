# AGENTS.md

Chat Agent Bridge（Slack / Mattermost / Discord の依頼を Windows PC 上の CLI エージェントで実行するアプリ）で作業するときの最低限のルールです。

## 構成

- `desktop/ChatAgentBridge.Desktop`：WinUI 版の Windows デスクトップアプリ本体（.NET）。現行の開発・配布の中心
- `desktop/ChatAgentBridge.Core` / `Core.Tests`：共通ロジックとテスト
- `desktop/ChatAgentBridge.Transports` / `Transports.Tests`：Slack / Mattermost / Discord 接続とテスト
- `desktop/ChatAgentBridge.Updater`：更新処理
- `desktop/ChatAgentBridge.TestAgent`：テスト用のエージェント
- `desktop/*.ps1` / `desktop/*.py`：UI の E2E・スモークテスト、ローカライズ・設定の検査、リリース補助
- `src/` / `test/` / `scripts/`：旧 TypeScript / Node.js 実装。**今後はメンテナンスしない**（CI の `npm run check` / `npm test` はまだ走っている）。機能追加・修正は WinUI 版を優先し、TS 版には手を入れない
- `docs/`：検証メモ（`*-validation.ja.md`）・計画・セットアップ手順。`docs/legacy/` は旧実装の文書
- `.github/workflows/`：CI・配布・リリース

## よく使うコマンド

```
npm ci
npm run check
npm test
python desktop/CheckLocalization.py
python desktop/CheckSettingsExpander.py
dotnet test desktop/ChatAgentBridge.Core.Tests/ChatAgentBridge.Core.Tests.csproj -c Release
dotnet test desktop/ChatAgentBridge.Transports.Tests/ChatAgentBridge.Transports.Tests.csproj -c Release
```

CI の全体は `.github/workflows/ci.yml` を参照してください。
変更箇所に応じた検証の選び方とリリース前確認は [`docs/testing.ja.md`](docs/testing.ja.md) を参照してください。

## 変更時のルール

- 変更範囲に応じて、実行可能な関連チェックを選ぶ。小さな変更に重いテスト一式を常に実行する必要はない。実行しなかった重要なチェックと理由は報告する。
- UI を変えたときは、影響範囲に合う `desktop/*UiE2E.ps1` / `*UiSmoke.ps1` を選び、実行可能な環境なら確認する。UI テストは時間や環境の負荷を考慮して適宜判断し、実施できない場合は理由を報告する。リリース前には必要な UI 検証を実施する。
- UI 文字列を変えたら日英の文言を揃え、`CheckLocalization.py` を通す。
- 利用者向けの挙動を変えたら `README.md` / `README.ja.md` / `desktop/README.ja.md` を実装に合わせて更新する。
- 作業用の一時ファイルは `.artifacts/` に置く。`.env`、接続トークン、`data/` はコミットしない。
- main への直接コミットはしない。作業ブランチは変更の種類を表す一般的な prefix と短い kebab-case の説明で命名する（例：`feature/add-kiro-cli`、`fix/session-resume`、`docs/update-testing-guide`、`refactor/agent-adapters`、`test/ui-smoke-coverage`、`chore/update-dependencies`、`release/v1.2.0`）。エージェント名の prefix は使わない。
- 区切りのよいところで適宜コミットする。メッセージの prefix は add / update / fix / refactor。
- push・マージ・リリースは利用者の許可を得てから行う。「リリースしてください」の指示には push の許可を含むものとする。

## TODO の管理

- 直近の作業と保留事項は `docs/TODO.md` で管理する。
- 大きめの作業・長期の要望は GitHub Issues で管理する。
- 作業を始めるとき・終えるとき、該当する方を更新する。
