# TODO

直近の作業メモです。大きめの作業や長期の要望は GitHub Issues を使います。完了したら削除するか、`[x]` に変えてください。

## 直近

- [x] 未コミットだった docs 整理（`docs/legacy/` への移動、README 更新、`LICENSE` 追加）をコミットする
- [x] ルートの別プロジェクト（ghiaccio-vault）のメモを削除する
- [x] `.artifacts/` を `.gitignore` に追加する
- [ ] `codex/inbox-gallery-ui` の作業を区切りまで進め、main への取り込みを判断する（main より 120 コミット遅れている）
- [ ] Kiro CLI に対応する（WinUI 版でエージェント CLI として選べるようにする）

## 保留

- [ ] `worktree-agent-*`（5件、未マージ、各 2〜3 コミット）の中身を確認し、残すか削除するか決める
- [ ] main にマージ済みで作業ツリーに変更がない worktree を削除する（`D:/git/cab-gallery`、`cab-resizable`、`cab-single-instance`、`.claude/worktrees/agent-acdfab…`、ブランチは `codex/gallery-design`、`codex/resizable-panes`、`codex/about-expander`、`codex/antigravity-cli`）
- [ ] 変更が残っている worktree（`D:/git/cab-chat-controls`、`cab-chat-reactions`）の変更を確認し、コミットか破棄かを決める
