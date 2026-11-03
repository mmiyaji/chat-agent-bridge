# TODO

直近の作業メモです。大きめの作業や長期の要望は GitHub Issues を使います。完了したら削除するか、`[x]` に変えてください。

## 直近

- [x] 承認済みのCLI／チャットサービスのアウトラインアイコン11種を設定ナビゲーションへ反映。Gemini CLIに移行推奨バッジと対象範囲の説明を追加。日・ダーク／英・ライトの実画面、設定切り替え、関連18テスト、ビルド・ローカライズ検査を確認

- [x] Qwen・Kimiの設定見出しと選択肢にPreview表示を追加（実サービス検証保留中）

- [x] Qwen Code・Kimi Code CLI の設定、CLI確認、モデル指定、明示的フルアクセス実行ガードを実装。Qwenのnpm版・公式スタンドアロン版と、新Kimi `kimi-code` 2.4.1の `--prompt` / stream-json・CLI発行セッションIDに対応（旧 `kimi-cli` は非対応）。Core全1450件・修正後の模擬プロセス（会話継続・保存済みモデル変更・ユーザー分離）とWinUIビルドを確認
- [ ] 実サービス検証は利用者の希望で保留。Kimiは契約の利用権不足でHTTP 403、QwenはOpenAI認証・`qwen3.6-plus` でAPIキー制限によるHTTP 403。認証済み応答の成功、チャット経由の会話継続・モデル切り替え・返信を検証する。モデル一覧取得・実行中の直接追記は未対応（モデルは直接指定、追加指示は順番待ち）

- [x] Kiro CLI の設定・CLI確認・モデル取得・会話IDによる分離と継続・チャットからの切り替えを実装。模擬CLIで実プロセスと日英UI・再起動後の復元を検証
- [ ] Kiro CLI本体を導入し、KIRO_API_KEYをPC側に設定した環境で実サービス検証（認証、実際のstream-json互換性、Slack/Mattermost/Discord経由の会話継続と返信）。現時点では実サービス未検証。推論設定・操作権限はCLI側の設定を使用

## 保留

- [x] `claude/startup-stall-timeout` の移行タイムアウト修正を main に取り込み、ブランチと worktree を削除する。`cab-chat-reactions` の未コミット変更（main より古い版）は破棄した
- [x] `worktree-agent-a3b85f5f220f27144`（`d8b881c`、NDJSON 行の一度だけパース）は main に内容が入っていたため、取り込みは不要と判断した。Core テスト 1366 件成功。ブランチと worktree は残している
- [x] main の未pushコミットを origin へpushする
- [ ] 削除した `worktree-agent-*`（`a39cc156579a1c9a3`、`a51c28e6a4b859eb7`、`a8fbd9ede9f9f6d31`、`ac36c612001513f42`）は内容が main に入っている。必要なら commit ハッシュ（`9fbad64`、`a9bb292`、`dd2bd90`、`c15b3e8`）からブランチを再作成できる
