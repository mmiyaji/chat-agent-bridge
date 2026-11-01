# TODO

直近の作業メモです。大きめの作業や長期の要望は GitHub Issues を使います。完了したら削除するか、`[x]` に変えてください。

## 直近

- [x] 未コミットだった docs 整理（`docs/legacy/` への移動、README 更新、`LICENSE` 追加）をコミットする
- [x] ルートの別プロジェクト（ghiaccio-vault）のメモを削除する
- [x] `.artifacts/` を `.gitignore` に追加する
- [x] `codex/inbox-gallery-ui` を main に取り込み、ブランチを削除する（docs は main の構成を優先）
- [ ] Kiro CLI に対応する（WinUI 版でエージェント CLI として選べるようにする）

## 保留

- [x] main にマージ済みで変更のない worktree とそのブランチを削除する（`cab-gallery`、`cab-resizable`、`cab-single-instance`、`agent-acdfab…`）
- [x] `claude/startup-stall-timeout` の移行タイムアウト修正を main に取り込み、ブランチと worktree を削除する。`cab-chat-reactions` の未コミット変更（main より古い版）は破棄した
- [x] `worktree-agent-a3b85f5f220f27144`（`d8b881c`、NDJSON 行の一度だけパース）は main に内容が入っていたため、取り込みは不要と判断した。Core テスト 1366 件成功。ブランチと worktree は残している
- [ ] main（origin/main より未 push のコミットあり）を push する（利用者の許可が必要）
- [ ] 削除した `worktree-agent-*`（`a39cc156579a1c9a3`、`a51c28e6a4b859eb7`、`a8fbd9ede9f9f6d31`、`ac36c612001513f42`）は内容が main に入っている。必要なら commit ハッシュ（`9fbad64`、`a9bb292`、`dd2bd90`、`c15b3e8`）からブランチを再作成できる
