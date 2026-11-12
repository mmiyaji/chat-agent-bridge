---
name: remote-ui-smoke
description: Run the WinUI desktop smoke test on the LAN test PC (SSH alias stampede) without showing windows on this PC, and read the results. Use for remote UI smoke/E2E runs, "STAMPEDEでテスト", "画面を出さずにUIテスト", or inspecting the live app UI on the test PC.
---

The canonical instructions are shared with Codex. Read and follow `.agents/skills/remote-ui-smoke/SKILL.md`
in the repository root.

Claude Code specifics:
- Run the launcher with the Bash/PowerShell tool and `run_in_background: true` (it takes 2–4 minutes),
  then read `desktop/artifacts/remote-ui/` when notified.
- Per this repo's AGENTS.md, delegating a fixed-scenario smoke run to a cheaper model (subagent) is fine;
  judging failures from `failure.png` and `smoke.log` stays with the main model.
