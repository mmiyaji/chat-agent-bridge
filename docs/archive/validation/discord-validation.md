# Discord validation

> 過去の検証・実装記録です。記載は当時の状態を示します。現行の手順は[ドキュメント索引](../../README.md)を参照してください。

Validated on Windows on 2026-10-24. All automated runs use isolated data directories and synthetic identities. They do not reuse the user's Slack/Mattermost settings or invoke a paid AI service.

## Automated checks

| Area | Result |
| --- | --- |
| Core tests | 1,060 passed |
| Transport tests | 251 passed, including 22 Discord cases |
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

The user installed the bot and entered its token directly in an isolated app profile. Live checks used the real Discord Gateway/REST service and the dedicated `#cab-discord-test` channel in `ruhenheim`. Agent execution used the repository's offline TestAgent; these checks did not call a paid AI service.

| Live workflow | Result |
| --- | --- |
| Connection and `!cab help` | Connected; bot created a thread and returned the command list |
| Initial approval | Requester clicked Discord Approve; execution and automatic result reply completed |
| Same-owner continuation | Plain replies resumed the same CLI session, including after app restart |
| Incoming files | Synthetic text log and PNG downloaded; prompts contained local temporary-file references |
| Generated-file upload | No automatic upload under the default consent policy; PC confirmation showed one unchecked 32-byte `first.txt`, and only the selected file was sent |
| Rejection | Discord Reject prevented CLI execution |
| Stop button | Requester cancelled an approval-pending request; progress changed to stopped and controls were removed |
| Restart safety | Unapproved request was restored as interrupted; fixture call count stayed unchanged; clicking its old approval returned a private unavailable notice |
| Names | History resolved server, channel and user names; a second restart restored those names in Inbox from the identity cache |
| Terminal controls | Final build removed Stop from completed/stopped progress posts |

The first live CLI execution failed because an artifact-only launcher set the fixture transcript path incorrectly. The launcher was corrected and subsequent executions passed. Live testing also found that terminal message edits could preserve a stale Stop button; commit `856c0eb` fixes the payload and adds a regression test. Existing posts from the earlier build can still contain old controls; the restarted transport refuses them.

Live evidence is under `desktop/artifacts/01d74cb9-d747-4df3-9376-c181e1052198/`, including `discord-live-file-roundtrip.png` and `discord-live-stop-final.png`. The final ordinary portable publish is `desktop/artifacts/discord-live-final`.

After the fixture phase, the isolated app was left open and disconnected. The user's normal chat profile was not changed.

Wrong-owner, wrong-server and replay attempts were exercised with synthetic identities in the isolated tests, not a second real Discord account. Actual cancellation of a running paid CLI was not repeated against Discord; the live Stop check cancelled pending work. Server-specific permission overrides, DM operation and rate-limit behavior were not exhaustively exercised live. Existing Inbox rows use cached names on restoration; refreshing History does not immediately relabel already loaded Inbox rows.

## Real AI continuation check

On 2026-10-24 the same isolated profile was switched through the app's agent settings to the installed, authenticated Claude Code CLI 2.1.308, using `exec` and the economical `haiku` model alias. The app was relaunched with all inherited `CAB_*` fixture variables removed and its CLI check passed. The dedicated workspace and Discord access restrictions were preserved.

In a new real Discord thread, the requester approved the initial request using the Discord button. Claude replied `準備できました。` and Bridge automatically posted the result. A plain reply in that thread asked for the previously supplied synthetic passphrase without repeating it; Claude correctly returned `瑠璃の橋-4729`. This second request ran without another initial-approval prompt and its result was also automatically posted. Neither request asked for tools, file access or external searches.

Both execution records completed successfully and contain the same CLI session ID. The provider reported `claude-haiku-5-5`; the continuation recorded 27,883 cached-input tokens, confirming cache usage was captured with the real response.

Unlike the earlier fixture runs, these two requests used the actual AI service. Evidence: `desktop/artifacts/01d74cb9-d747-4df3-9376-c181e1052198/discord-real-ai-continuation.png`. The app was left connected with the real CLI after this check; the separate normal user profile remains unchanged. Nothing was published or pushed.
