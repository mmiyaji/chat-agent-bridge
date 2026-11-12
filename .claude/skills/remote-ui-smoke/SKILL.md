---
name: remote-ui-smoke
description: Run this repository's WinUI smoke test on the LAN test PC without showing windows on this PC, or inspect the live app UI there. Use for remote UI smoke runs in chat-agent-bridge, "STAMPEDEでテスト", "画面を出さずにUIテスト".
---

Read and follow `.agents/skills/remote-ui-smoke/SKILL.md` in the repository root (shared with Codex).
It builds on the user-scope `remote-win-ui-test` skill.

Claude Code specifics: run the launcher with `run_in_background: true` and read `desktop/artifacts/remote-ui/`
when notified. A cheaper subagent may run the fixed scenario; judging failures from `failure.png` and
`entry.log` stays with the main model.
