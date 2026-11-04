# 手動実行の作業先・CLI連携の確認

2026-11-04。未公開ブランチ `fix/manual-codex-trust-guidance` の確認記録。

## 作業先と信頼範囲

- CABのインストール・展開先ではなく、設定した作業フォルダーをCLIのカレントディレクトリに使う。
- 手動実行では、作業フォルダー自身または親フォルダーの明示的な信頼登録を要求する。未登録・許可範囲外・フォルダー不存在は実行前に警告し、実行ボタンを無効にする。
- 親フォルダーを信頼すると下位フォルダーも対象。信頼範囲外へのリンクは既存の検証で拒否する。
- この追加ガードは手動実行用。CLIチェックや既存チャットの互換用の暗黙フォールバックは変更していない。
- 登録画面のキャンセルでは実行を許可しない。登録・保存後に警告を解除する。
- Git管理は不要。CABの信頼登録と、CLIの操作権限・サンドボックスは別に扱う。

## CLIごとの確認

| CLI | 非対話起動・対応 | 実サービスの結果 |
|---|---|---|
| Codex | CABの信頼確認後、`exec` / `exec resume` に `--skip-git-repo-check` を追加。サンドボックス・承認引数を維持 | Git管理外で応答成功。実画面の手動承認後の実行も成功 |
| Claude Code | 既存の `--print --output-format stream-json --verbose` と標準入力を維持 | 同じGit管理外の作業先で応答成功 |
| GitHub Copilot | 標準入力による非対話起動。全パス・全ツール許可は追加しない | 応答成功 |
| Antigravity | 標準入力による非対話起動。設定した権限モードを維持 | 応答成功 |
| Kiro | 既存のV2非対話モードを維持。`--trust-all-tools` は追加しない | 応答成功 |
| Gemini | 信頼確認後、子プロセスにだけ `GEMINI_CLI_TRUST_WORKSPACE=true` を設定。ユーザーの信頼ファイルは変更しない | このPCでCLI未検出。オフライン起動契約テストで確認 |
| Qwen Code | 既存の標準入力・構造化出力を維持。結果イベント内の既知の認証エラーを固定文言に分類 | APIキーの401。フォルダーエラーではない。修正後の案内表示を再確認 |
| Kimi Code CLI | 既存のACP起動・承認設定を維持。ACP応答内の既知のアカウントエラーを固定文言に分類 | 初期化・会話作成・モード指定は成功、依頼時に契約権限403。修正後の案内表示を再確認 |

Qwen・Kimiの認証情報・契約は変更していない。CLIの全権限付与、フックの信頼回避、グローバル設定の書き換えは追加していない。

## 検証

- 関連Coreテスト213件成功。CLI起動契約・明示的信頼・許可外の拒否・Codex継続起動・権限引数維持・認証エラーからの秘密情報非露出を含む。
- 追加でCoreTests / AgentLiveProgressTests 27件成功。
- 日英リソース整合性・SettingsExpander検査成功。
- 隔離したポータブル版の日本語・ダーク画面で、未登録の警告と無効化、キャンセル、登録・保存、実行承認、Codexの結果表示をComputer Useで確認。
- 英語・ライト・狭幅の画面操作は今回未実施。Geminiの実サービス、Qwen・Kimiの成功応答は未確認。
- 一時成果物は `.artifacts/manual-workspace-demo`、実CLIの確認用ハーネスは `.artifacts/cli-workspace-live`。接続トークンはコピーしていない。

## 調査に使った一次情報

- Codex: インストール済みCLIの `exec --help` / `exec resume --help`。両方にGit確認省略オプションがあることを確認。
- [Claude Code CLI reference](https://code.claude.com/docs/en/cli-reference)
- [Gemini trusted folders](https://geminicli.com/docs/cli/trusted-folders/)
- [Copilot programmatic reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-programmatic-reference)
- [Antigravity headless mode](https://antigravity.google/docs/cli/headless/)
- [Kiro CLI commands](https://kiro.dev/docs/reference/cli-commands/)
- [Qwen headless mode](https://qwenlm.github.io/qwen-code-docs/en/users/features/headless/)
- [Kimi command reference](https://moonshotai.github.io/kimi-cli/en/reference/kimi-command.html)

最新版の文書にあるオプションを一律追加せず、インストール済みCLIのヘルプと既存の起動方式も照合した。
