# 手動実行の作業先・CLI連携の確認

2026-11-04。未公開ブランチ `fix/manual-codex-trust-guidance` の確認記録。

## 作業先と信頼範囲

- CABのインストール・展開先ではなく、設定した作業フォルダーをCLIのカレントディレクトリに使う。
- PCで選択・保存した作業フォルダーを信頼一覧へ自動追加する。許可範囲が広がる場合は、保存前の確認に追加先も示す。信頼一覧が空の既存設定では、既定作業フォルダーの信頼を維持する。許可範囲外・フォルダー不存在は実行前に警告し、実行ボタンを無効にする。
- 親フォルダーを信頼すると下位フォルダーも対象。信頼範囲外へのリンクは既存の検証で拒否する。
- 自動追加はPC側の設定保存時にのみ行う。チャットからの切り替え処理では既存の信頼範囲だけを使用し、範囲を広げない。
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

- 最終の関連Coreテスト284件成功。既定作業先の信頼登録、親の信頼と下位フォルダー、許可範囲外と紛らわしい接頭辞の拒否、保存後・再起動後の復元、他ユーザーの分離、CLI起動・認証エラーを含む。
- WinUIポータブル版publish成功。日英リソース整合性・SettingsExpander検査成功。
- 隔離アプリの日本語・ダーク画面で、PCの作業先を許可範囲外の新しい場所へ変更。信頼一覧にも追加される差分を保存前ダイアログで確認し、保存データと再起動後の表示を照合。
- DockerのMattermostに実接続し、許可ユーザーから `!cab folder` を送信。登録した親配下の child-b への変更は承認後に成功し、Mattermostへ変更完了を返信。
- 同じスレッドから範囲外の outside を指定。承認操作を行っても範囲外として拒否され、会話の作業先はchild-bを維持。outsideは信頼一覧へ追加されていない。
- アプリを正常終了・再起動・再接続し、同じスレッドからchild-aへ変更。確認画面の変更前がchild-bであること、変更後にchild-aが保存されることを確認。
- 後続の依頼・会話継続を検証用CLIで実行し、子プロセスが記録したカレントディレクトリがchild-aと一致することを確認。
- 検証用CLIは最初に.NETランタイム不足で起動に失敗したため、ランタイム同梱のオフライン版へ置き換えて再検証。実AIへの課金は発生しない。
- 英語・ライト・狭幅の画面操作は今回未実施。Geminiの実サービス、Qwen・Kimiの成功応答は未確認。
- 作業先切り替えの証跡は `.artifacts/default-workspace-fixture/validation.json`。トークンは隔離設定へ暗号化保存し、出力・コミットしていない。

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
