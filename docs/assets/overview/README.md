# システム構成図

初回起動時のウィザードと全般画面、日英READMEで使用する図です。依頼者がスマートフォンやブラウザーから専用Botへメンションし、ローカルPC上のBridgeからCLIへ依頼を渡して、結果を同じスレッドへ返す流れを示します。Botには専用アカウントの利用を推奨します。Codex CLIとClaude Code CLIは例示で、アプリには同梱されず別途インストールが必要です。

- 日本語プレビュー: [OverviewArchitecture.ja.png](OverviewArchitecture.ja.png)
- English preview: [OverviewArchitecture.en.png](OverviewArchitecture.en.png)
- 編集元SVG: [日本語](OverviewArchitecture.svg) · [English](OverviewArchitecture.en.svg)
- アプリ用アウトライン済みSVGは `desktop/ChatAgentBridge.Desktop/Assets/` にあります。編集元を更新した場合は `pwsh -NoProfile -File desktop/Convert-ArchitectureSvg.ps1` で再生成してください。
