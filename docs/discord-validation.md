# Discord validation

Validated on Windows on 2026-10-24. All automated runs use isolated data directories and synthetic identities. They do not reuse the user's Slack/Mattermost settings or invoke a paid AI service.

## Automated checks

| Area | Result |
| --- | --- |
| Core tests | 1,060 passed |
| Transport tests | 250 passed, including 21 Discord cases |
| Native Discord workflow | 8 checks passed using loopback REST/Gateway and the local TestAgent |
| Settings UI | English/Japanese × light/dark passed; wide and 500-DIP layouts inspected |
| Static validation | PowerShell/Node syntax, resource XML and `git diff --check` passed |
| Production build | Normal portable publish succeeds; loopback factory and fixture URL hooks are absent |

The native workflow checks denied users and servers before thread creation, wrong-owner/guild/replayed approval actions, rejection, incoming attachment download only after request approval, generated-file upload only after separate PC consent, same-owner continuation, and restart safety. Restart restores conversation metadata; it does not resume pending work or authorize continuation from display metadata. A different allowed user cannot take over an existing thread, including when its original owner is revoked.

Transport tests additionally cover REST message metadata without `guild_id`, attachment source binding, CDN host/redirect/authentication restrictions, suppressed mentions in multipart uploads, Gateway heartbeat/resume behavior during slow message handling, progress updates and Stop after reconnect, invalid session close codes, and system-proxy selection.

Native evidence is stored locally at `desktop/artifacts/d807b9be-cdcc-483a-bfc2-f5eb3ba82dcd/results.json`. Four settings runs are under `desktop/artifacts/5c95f296-9d0e-4635-bb13-5e4fdbb5a0e7`, `f0039d7c-16d0-4e86-a2b8-77d2183aa2f3`, `7ef280ad-2404-4ac8-92f4-0fbfcd68793c`, and `e6703861-3d1f-49a9-853f-8748011bcdc6`. Artifacts are local and excluded from Git.

## Reproduction

- Run `dotnet test desktop/ChatAgentBridge.Core.Tests` and `dotnet test desktop/ChatAgentBridge.Transports.Tests`.
- Publish the Desktop project with `CABPortable=true` for settings checks. Run `desktop/DiscordSettingsUiSmoke.ps1` against that publish for each language/theme combination.
- For offline protocol/UI checks only, publish with both `CABPortable=true` and `CABDiscordFixture=true`, then run `desktop/DiscordBridgeUiSmoke.ps1` with that app directory and a self-contained `ChatAgentBridge.TestAgent` executable. The script creates its own loopback server and marked, isolated profile.
- Never distribute the fixture build. The ordinary portable build must not contain `CreateLoopbackFixture`, `FixtureCdnHandler`, or `CAB_DISCORD_FIXTURE_URL`.

## Live service check

Developer Portal setup and bot installation are separate from the automated results above. Live checks require the user to enter a bot token directly in the app. Real account permissions, server overrides and Discord rate limits are not simulated by the offline fixture. Record live results separately after connecting; do not describe the fixture as live-service verification.
