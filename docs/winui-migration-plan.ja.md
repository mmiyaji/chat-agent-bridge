# WinUI への移行計画

## 目的と現在地

本書は、既存の Node.js/TypeScript 版 Chat Agent Bridge を Windows 向け WinUI 3 アプリへ段階的に移行するための実装計画である。目標は、チャット上のスレッドをローカル agent session に結び付ける既存の運用を維持しながら、設定・接続・状態確認をネイティブ UI で行える Store 対応アプリにすること。

## 進捗（2026-10-16）

ブランチ: `codex/winui-rebuild`。旧版の参照ブランチは `codex/reference-operations-ux`。

| 段階 | 状態 | 実装・確認 |
|---|---|---|
| 機能棚卸し・構成決定 | 完了 | 本文の機能対応表。WinUI / Core / Transports の3層に分離 |
| ネイティブUI・コア | 実装・Debugビルド確認済み | XAML/MVVM、設定保存・再読み込み、DPAPI、CLI実行・停止・同一フォルダー排他 |
| チャット受信 | Mattermost実通信確認済み | Slack Socket Mode、Mattermost WebSocket、フィルター・重複排除、PC上での承認・拒否・実行、状態返信。専用Docker環境で受信・返信・再接続を検証 |
| Codexセッション継続 | 実装・プロセス検証済み | 標準exec・App Serverでスレッド別に保存・再開。設定不一致と再開ID不一致を拒否。解除は実行と排他 |
| 履歴・監査 | 実装・UI確認済み | メタデータのみ各500件、破損検出、中断表示。セッション解除・本文共有の操作を追加 |
| 自動テスト | 成功 | Core 54件、Transports 15件。App Serverはオフライン実プロセス、MattermostはDocker実通信。有料モデル実行なし |
| 起動・UI・MSIX | 確認済み | Debug/Releaseビルド、既存UI操作7項目とMattermost通し確認9項目成功。Release MSIX約104MiB、開発レイアウト混入検査を追加 |
| 既存機能の完全移行 | 未完了 | 追加入力のチャット転送、複数root、全チャットコマンド、他agent継続、旧設定インポート、自動接続回復 |
| Store提出 | 未実施 | 正式identity、ブランド素材、ポリシー・プライバシー、実サービスE2E、クリーン環境検証が必要 |

実装作業は主に軽量モデル `gpt-6-luna` に範囲を分けて委任し、リードが統合とレビューを担当した。価格差や節約率は計測していない。

受信依頼ごとに保存済み設定を固定し、PC上で確認してから実行する。チャットから実行ファイル・作業フォルダーを変更しない。Codexの標準 `exec --json -` 設定ではチャットスレッドごとにセッションIDを永続化し、次の依頼を明示IDで再開する。任意CLI・カスタム引数・ワークベンチは単発実行。自動返信は終了状態のみとし、結果本文は送信先・本文の確認後に手動共有できる。切断・終了で未実行依頼は無効化する。

パッケージ版の設定は `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState\settings.json`（schema 1）に分離した。コア単体では `%LOCALAPPDATA%\ChatAgentBridge.WinUI` を使用する。旧版のファイルは読み書きしない。明示的なインポートと永続的なセッション移行は後続段階で実装する。Windows以外ではトークンの平文保存へフォールバックしない。

## 移行方針

- **移行中の正本を明示する。** 各リリースでユーザーが使う実装を指定し、未移行機能がある間は既存 Node 版を継続利用できるようにする。WinUI の空画面を完成品として案内しない。
- **ドメインと外部接続を分ける。** .NET 側では、設定・接続状態・セッション・承認・ジョブを UI から独立したサービスとして設計する。XAML の code-behind に実行ロジックを置かない。
- **互換性を先に固定する。** 設定 JSON、状態ファイル、監査ログ、チャットコマンド、thread/session binding の意味を棚卸しし、移行期間の読み書き互換性と更新手順を定義する。既存データを自動削除・上書きしない。
- **プロバイダーごとに縦に移行する。** UI だけを先に作り込まず、接続からメッセージ受信、agent 実行、返信、再接続までを小さく動作させてから対象を増やす。
- **Store 制約を初期設計に含める。** MSIX の実行モデル、フル信頼 capability、外部 CLI の起動、ユーザーデータ保存、署名とパッケージ提出を早期に検証する。Store 審査の通過は manifest の設定だけでは保証できない。

## 現行機能と移行先の対応表

「現状」は README、運用・アーキテクチャ文書、`src/` の実装から確認した範囲を示す。「WinUI 側」は移行計画上の目標であり、現在の実装済み機能を意味しない。

| 領域 | TypeScript 版の現状 | WinUI 移行先 | 優先度 / 完了条件 |
|---|---|---|---|
| 接続管理 | Admin UI から chat / agent を設定・接続・切断。対応 chat は Slack と Mattermost。 | ネイティブ接続設定、接続状態、接続/切断、接続テスト、エラー表示。 | P0: Slack + Codex の一連の実通信を確認。後続で Mattermost と他 agent。 |
| chat ingress / replies | Slack HTTP events / Socket Mode、Mattermost HTTP outgoing webhook / WebSocket。スレッドへ返信する。Mattermost は bot/self mode がある。 | 各プロバイダーの .NET adapter。再接続、重複イベント、タイムアウトを扱う。 | P1: プロバイダー別の受信・返信・再接続確認。トークンをログへ出さない。 |
| agent 実行 | Codex、Claude Code、Cursor、Gemini CLI の adapter。ローカル CLI と既存のログイン状態を利用。 | CLI 検出・起動・標準入出力ストリーム・キャンセル・終了理由を扱う .NET adapter。 | P0: Codex CLI の開始/継続/失敗を UI とチャットの両方で確認。P2: 残り3 agent。 |
| Codex App Server | 承認・追加入力を同じ chat thread へ返す対応がある。 | プロトコルクライアントと承認 UI/通知を移植。 | P1: 承認待ち、応答、拒否、再開を E2E で確認。 |
| thread/session | provider + channelId + threadId を基点に session を束縛。Claude/Codex の既存 session 再開候補、attach/detach、repo/cwd 変更を扱う。 | 同じ識別子と状態遷移を持つ .NET ドメインサービス。再開候補を安全に列挙する。 | P0: 新規スレッド実行と再起動後の束縛復旧。P2: 再開・attach・切替。 |
| 作業ディレクトリ | alias/path/default/picker で初回に決定。許可 root 配下に制限。チャンネル既定値と実行時の allow-dir 変更がある。 | 設定 UI と chat command を移植。パス正規化と許可 root 判定を一か所に実装。 | P0: root 外パスを確実に拒否し、初回選択を thread に固定。 |
| 実行キュー | 同一 session の同時実行を抑止し、job queue で処理。 | UI スレッドから独立したキューと排他制御。キャンセルと終了処理を明示。 | P0: 同一 session の逐次性、別 session の並行性を確認。 |
| 承認・安全性 | 承認ポリシー、許可 root、agent ごとの実行能力、監査を組み合わせる。 | 安全ポリシーを UI と独立した境界として実装。危険操作の承認状態と理由を表示。 | P0: root 制限とポリシーを移植。P1: 書き込み承認の往復を確認。 |
| 設定と秘密情報 | admin-settings JSON / 環境変数。Slack/Mattermost token、agent command/model、allowed roots 等を扱う。 | 設定移行、入力マスキング、Windows の保護された資格情報保存を設計。 | P0: 秘密値を画面・監査・例外に露出させず、再起動後の接続確認。 |
| 状態・監査 | JSON state、JSONL audit、セッション履歴/承認/失敗の読み取り API。 | バージョン付き永続化と診断画面。移行失敗・破損時に回復可能なエラーを出す。 | P0: 既存設定を保持。P1: セッション/承認/失敗の表示と監査追跡。 |
| 管理画面 | Dashboard、設定、onboarding、environment、metrics、session detail 等のブラウザー UI/API。 | WinUI のナビゲーション、初回設定、接続、状態、承認、失敗、環境診断へ再構成。 | P0: 初回設定と接続状態。P1: dashboard とセッション詳細。P2: 詳細診断/metrics。 |
| 運用・更新 | PowerShell による start/stop/restart/status、Task Scheduler 自動起動、ログ。 | MSIX 起動・終了、ログ位置、アップデート、必要なら起動時実行の適合方式を設計。 | P1: クリーンインストール、更新、アンインストール、再起動を確認。Store の自動起動制約を調査。 |
| チャットコマンド | status/history/sessions/approvals/failures/repos/allow-dir/repo/cwd/channel-default/attach/detach/restart 等。 | 意味と応答を保持し、UI 操作と状態を一貫させる。 | P1: 既存コマンド一覧ごとの互換性確認。未対応コマンドは明確に拒否する。 |

## 段階とチェックリスト

### 0. 移行前の棚卸しと互換性契約

- [ ] `src/core`, `src/adapters`, `src/agents`, `src/state`, `src/admin` の責務と公開境界を一覧化する。
- [ ] 現行設定・state・audit の実例、ファイル位置、スキーマ、秘密情報を含むかを確認する。
- [ ] 既存の unit / integration / E2E を機能表へ紐付け、移行後に維持する振る舞いを定義する。
- [ ] Windows App SDK の配布方式と Store 要件を調べる。特に `runFullTrust`、外部 CLI 起動、プロセス管理、ユーザーフォルダーアクセスを検証項目にする。
- [ ] データ移行方式、ロールバック、TypeScript 版との同時利用可否、ログの取り扱いを決める。

**Exit:** 既存機能の基準とデータ互換性の判断がレビュー可能である。

### 1. WinUI アプリ基盤

- [x] サンプル greeting/counter を実アプリのシェルへ置き換え、ナビゲーションと MVVM の構成を作る。
- [ ] DI、構成読み込み、ロギング、アプリ終了時の graceful shutdown を実装する。
- [ ] 起動時に bridge engine の状態を表示し、未設定・起動中・接続中・異常を区別する。
- [x] 設定/秘密情報の保存先と MSIX package identity 下のデータ配置を確定する。
- [ ] 現行設定の読み込み、バックアップ、バージョン付き変換を実装する。

**Exit:** 起動、設定読み込み、エラー表示、終了が安定し、設定を失わずに再起動できる。

### 2. 最小の実用経路（Slack + Codex）

- [ ] Slack 接続と状態確認を実装する。
- [ ] Codex CLI の検出、起動、標準入出力、タイムアウト、キャンセル、終了コード処理を実装する。
- [ ] thread/session binding、working directory 選択、allowed roots、session ごとの job queue を移植する。
- [ ] 監査記録と失敗表示を用意する。
- [ ] 実チャットからメッセージを受信し、Codex が指定 repository で実行され、返信が thread に戻ることを確認する。

**Exit:** 日常的な最小経路が Node 版に頼らず完結し、root 外実行と同一 session の並行実行を拒否できる。

### 3. 管理 UI と安全機能

- [ ] onboarding、chat/agent 設定、接続テスト、接続/切断を移植する。
- [ ] セッション一覧/詳細、承認待ち、失敗一覧、稼働状態、環境診断を移植する。
- [ ] 承認/追加入力を同じ thread へ返す Codex App Server の流れを移植する。
- [ ] agent 実行ポリシー、許可 root の追加/削除、repo/cwd/channel default の操作を移植する。
- [ ] audit と UI の操作履歴を照合し、拒否・失敗・再試行の理由を示す。

**Exit:** 管理者が設定・承認・診断をブラウザーなしで行え、ポリシー境界をテストで確認できる。

### 4. Provider と既存機能の拡張

- [ ] Mattermost の bot/self、ingress mode、WebSocket 再接続を移植する。
- [ ] Claude Code、Cursor、Gemini の adapter と model/command 設定を移植する。
- [ ] Codex/Claude session index と resume 候補、attach/detach、session picker を移植する。
- [ ] 既存 chat commands 全体を機能表と照合し、対応・非対応を記録する。
- [ ] provider/agent 組合せごとに接続切断、認証失敗、時間切れ、再起動の確認を行う。

**Exit:** 公開する機能範囲が parity matrix に一致し、未対応項目がユーザーへ明確に示される。

### 5. MSIX / Store 提出準備

- [ ] Package identity、Publisher、表示名、バージョン、アイコン、タイル、説明、ローカライズを本番値にする。
- [x] 未使用の `systemAIModels` capability を削除し、`runFullTrust` を保持する。
- [ ] `runFullTrust` の用途説明と Store 審査適合性を確認する。
- [ ] Store の対象アーキテクチャ、最小 OS、Windows App SDK 依存、インストーラー/フレームワーク依存を確定する。
- [ ] 外部 agent CLI が未導入・未認証の場合の案内と、Store の配布/利用条件との整合を確認する。
- [ ] クリーン環境でインストール、初回起動、設定保存、更新、修復、アンインストール後のデータ扱いを確認する。
- [ ] パッケージ検証、署名、提出メタデータ、プライバシー/サポート情報、スクリーンショットを準備する。
- [ ] Store の審査結果と実機確認後に公開判断する。

**Exit:** パッケージ検証に合格し、Store ポリシーと実行モデルの適合性が確認され、サポート可能な配布手順がある。審査前に「Store ready」と断定しない。

## Store 対応で特に確認する点

`Package.appxmanifest` は開発用 identity `ChatAgentBridge.WinUI.Development`、Publisher `CN=ChatAgentBridgeDevelopment` を使う。不要な `systemAIModels` capability は削除し、デスクトップ実行用の `runFullTrust` を保持した。プロジェクトは .NET 10 / Windows App SDK 2.5.1、Windows Desktop を対象とする。最小OS 17763 は依存ライブラリーと実機での動作確認が残っており、サポート保証を意味しない。

最大の設計リスクは、アプリがユーザー導入済み CLI を起動し、ローカル repository を操作することと、MSIX / Store の trust・capability・プロセス起動・ファイルアクセス要件との適合である。まず小さな packaged spike で Codex CLI の起動、標準入出力、許可フォルダーへのアクセス、ログ保存を確認する。成立しない場合は、Store を前提に機能を削るのではなく、配布チャネルと製品境界を再設計する。

秘密情報には Windows の資格情報保護機構を使い、平文設定やログへの token 出力を避ける。利用する外部 CLI のライセンス、配布物に同梱する場合の再配布条件、プライバシー開示、クラッシュ/ログに含まれる repository 情報も提出前に確認する。

## 移行完了の判定

「完了」はコードが存在することではなく、公開対象の各行で完了条件が満たされ、対応する自動テストまたは再現可能な手動確認が記録されている状態とする。少なくとも、インストール済み環境での接続から返信まで、再起動後の状態復元、許可 root の境界、承認フロー、設定/秘密情報の保護、Store パッケージ検証を確認する。残る差分は parity matrix に明示し、Store 公開の機能範囲と一致させる。


## コミットと検証記録

- `5700a6b feat: add native bridge core and chat transports`: Core / Transports と21件の自動テスト。
- `35a4f90 feat: add native WinUI preview and MSIX packaging`: 初期WinUI・MSIXと移行計画。
- `600e31a feat: persist native Codex sessions and execution metadata`: セッション保存・再開・排他、履歴・監査、コアテスト36件。
- UIスモーク: 設定読込・画面切替、隔離設定保存、実行確認のキャンセル、承認後の標準入力実行、接続画面・承認ボタンの初期状態の5項目が成功。
- 最初のUI検証で保存先の隔離と状態表示のセレクターに問題を検出。AUMID起動時のコマンドライン引数を処理し、修正後の5項目を再実行して成功。
- 外部サービスの実トークンを利用した接続・返信は未検証。CLI実行のUI確認には PowerShell の標準入力エコーを使用し、AIモデルは呼び出していない。
- Light / High Contrast / スクリーンリーダー、ARM64、クリーン環境の署名インストール・更新、Windows App Certification Kit は未検証。

- 接続情報未設定時のMattermost接続失敗と、接続ボタンが再び有効になることをUIから確認。

### 続行時の検証

- ローカルCLIヘルプと [OpenAI公式の非対話モード仕様](https://learn.chatgpt.com/docs/non-interactive-mode) を確認し、JSONLのsession IDと最終メッセージを解析した。新規・再開・失敗・設定不一致・解除競合はfake runnerで検証し、実モデルは呼び出していない。
- 起動時に `Icon="History"` がXAML解析例外になる不具合を実起動で検出し、SDKで利用できる `Clock` に修正した。修正後に隔離データディレクトリでUI操作7項目をすべて再検証した。
- 新しいUI検証では履歴と監査の保存、本文が記録に混入しないこと、セッション解除のキャンセルと確定、解除後の履歴保持を追加確認した。
- App Serverのプロトコル承認、他agentの会話継続、実サービスの認証・受信・返信、署名済みMSIXの新規インストール／更新は引き続き未検証。

- `ce479ec fix: stop native agent immediately on cancellation`: ウィンドウ終了時のasync処理待ちに依存せず、キャンセル要求で子プロセスを即時停止。変更後のCoreテスト36件が成功。

### Docker Mattermost / App Serverの追加実装

- `9a87769`: 履歴画面、セッション切り離し、確認後の結果本文共有。
- `bf4c5c8`: App ServerのJSONL通信、新規・再開、コマンド・ファイル承認、追加入力、execと共通の作業フォルダー排他。Core 47件成功。実プロセスのオフラインfixtureで承認・入力・完了・再開後の拒否を確認。
- `b1a0cb9`: 同じ依頼者・provider・channel・threadに限定した一回限りの承認コード、実接続状態通知、専用Docker Mattermost起動手順。実通信を有効化したTransportsテスト15件成功。
- `9a05c50`: 再利用したpublishフォルダーの `AppX` がMSIXへ重複混入する問題を修正。新しいレイアウトで毎回生成し、nested AppX・テストプロセス・認証fixtureの混入を拒否。再生成したMSIXは約104MiB。
- Dockerは `cab-winui-e2e` / `127.0.0.1:8070` を利用。既存の8065–8069番の環境を停止・初期化していない。
- App ServerはローカルCodexの生成schemaと公式仕様を参照した。実Codexモデルの呼び出し、Slackの実接続、Store提出とクリーン環境検証は未実施。
- 最新Releaseのpackaged起動で `MattermostUiE2E.ps1` の9項目が成功。DPAPI保存、実接続、受信のPC承認、チャットの一回限りコードでの操作許可（PCの許可ボタンを押さずダイアログが閉じること）、追加入力、同じthreadへの確認後の本文共有、session保存、本文・トークンを含まない履歴を検証。
- 同じReleaseで別の空データフォルダーを使い、既存 `SmokeTest.ps1 -TestSessionDetach` も7項目すべて成功。
- UIテスト専用プロセスはself-containedで配布アプリと別に生成する。アプリ起動時と開発シェルで見える.NETランタイムの差により、framework-dependent fixtureが起動できない問題を通し確認で発見して修正した。


### UI再検証と起動・モデル設定（2026-10-16）

1. 現行の手動実行・履歴・チャット画面を撮影して確認。実行操作が設定の下へ埋もれる、履歴のパスが省略される、小さいウィンドウでチャット結果欄が縮む、内部用語がそのまま表示される問題を確認した。
2. エージェント設定を独立させ、手動実行の依頼・ボタン・結果を集約。設定保存ボタンをスクロール領域の外へ配置した。履歴は縦に並べてパスを折り返し、操作イベントを日本語へ変換した。チャットは画面全体をスクロールでき、結果欄の最小高さを確保した。
3. Codex／Claude Code／その他のCLI、既定モデル、作業フォルダーを設定可能にした。履歴から設定へ移動でき、保存済みCodexの会話はモデルを個別に指定できる。空欄なら既定値へ戻る。CLI設定ファイル自体は変更しない。
4. Windowsログイン時の起動と、アプリ起動後のMattermost／Slack接続を別設定にした。Windows自動起動は初期無効。今回の検証で有効化していない。チャット自動接続は専用の隔離設定で保存・再起動・実接続まで確認し、依頼を自動実行しないことも確認した。

Core 54件とオフライン実プロセス検証が成功。UIスモーク8項目（Claudeモデル保存・会話モデル保存を含む）、Docker Mattermost E2E9項目が成功。通常サイズと720×600のウィンドウで画面を確認した。スクロールで結果と設定へ到達できることを確認している。OS全体の拡大率変更、高コントラスト、スクリーンリーダーの実機確認は未実施で、アクセシビリティの完全適合を意味しない。

`a70d93f` はCLI・モデル設定、会話モデルの保存と反映。UI・起動設定は同日の後続コミットで実装。Claudeは標準入力の単発実行で、会話継続と途中の追加入力は未対応。実Codex／Claudeの有料モデルは呼び出していない。

仕様の参照元: [Codex CLI](https://learn.chatgpt.com/docs/developer-commands?surface=cli)、[Claude Code CLI](https://code.claude.com/docs/en/cli-reference)。モデルの有効性と利用可否は導入したCLIと契約に依存する。

追加確認: 導入済みClaude Code CLIの `--help` で `--print`・`--model`・`--output-format` を確認した。Windowsログインそのものを伴う自動起動試験は行っておらず、manifest登録とOS状態の取得・表示を確認した。


## 性能・トークン・キャッシュのレビュー（2026-10-16）

対象は現在のWinUI/C#版を中心に、旧TypeScript版との差分も参照した。コードレビューとオフライン検証であり、実モデルのトークン数・キャッシュ命中率・料金・応答時間は計測していない。以下は料金削減率やキャッシュ命中の保証ではない。

| 優先度 | 確認事項 | 判定と対応 |
|---|---|---|
| 修正 | 長いCodex出力の末尾を失う | AgentRunnerは先頭100万文字で表示用ログを打ち切り、後続の最終回答・turn.failedを解析できなかった。出力上限とは独立してJSONLを逐次解析し、完了情報を保持する。行長にも上限を設け、メモリー使用量を無制限にしない |
| 修正 | 共有用結果を最大100件、全文保持 | 送信は先頭4,000文字なのに各依頼が全文を保持していた。4,001文字までに限定し、省略表示の判定を保つ。最新のローカル結果表示とは分離する。文字部分の上限は約1億文字から約40万文字へ減るが、全体の実メモリー消費を測った値ではない |
| P1・次の計測項目 | トークン・キャッシュ利用量が見えない | Codex execのusage、App Serverのthread/tokenUsage/updatedを永続化していない。Claudeのtext出力も利用量を取得できない。実行ID、実モデル、入力・出力・cached input、観測範囲、起動/応答時間の記録が先。未取得値を0と扱わない。thread累計・last request・1実行の差分を混同しない |
| P2 | Claude Codeの会話継続が未対応 | nativeはprint/textの単発実行。旧版には明示session IDによるresumeがある。関連した連続依頼では文脈の再説明や再調査が増える可能性がある。ただし、新規sessionだから必ずcache missとは断定できず、無関係な依頼を長い会話へ結合するのも逆効果。導入する場合は会話ごとの明示IDで管理し、曖昧なcontinueにしない |
| P2・要計測 | App Serverを実行ごとに起動・終了 | initialize、thread/resume、CLI/MCP等の起動処理を繰り返す。プロセス常駐は起動遅延を減らせる可能性があるが、サーバー側cache hitの保証にはならない。実計測前にプールを増設しない。導入時は設定変更・キャンセル・終了時の所有権と隔離を維持する |
| P2・長期運用 | threadごとのロックが残り続ける | SessionStore.SessionGatesとAgentWorkspaceGateの辞書は項目を解放しない。大量の異なる会話/フォルダーを処理すると増え続ける。参照カウント付きlease等で解放する改善候補。安易なRemoveは待機者との競合で排他を壊すため今回変更しない |
| 現状許容 | 保存ファイルの読込・全体書換え | 履歴・監査は各500件で上限がある。接続時の読込には破損検出の役割もあるため単純削除しない。高頻度の実負荷で遅延が出た場合にbatch化・DB化を検討する。本文やtokenをこれらへ保存していない |

### 踏んでいなかったパターン

- チャット履歴をbridge側で丸ごとプロンプトへ付け直していない。Codexは保存したthread IDで再開し、新しい依頼文を一度だけ渡す。プロンプト先頭へ日時やランダム値を毎回付け加える処理もない。
- nativeにはモデルへの自動リトライ、並列の同一依頼実行、キャッシュ維持だけを目的にした定期モデル呼出しがない。App Serverの操作承認は実行中の要求への応答で、同じ依頼を新たなturnとして投げ直さない。
- transportの重複排除は直近1万ID、受信依頼は100件、WebSocket入力と表示用CLI出力にも上限がある。重複排除は永続ではないが、nativeは受信だけで自動実行しない。
- ローカルに保存する実行履歴の500件上限は、Codex自身の会話コンテキストの上限ではない。長い会話のトークンを減らすにはCLI側の圧縮・会話分離を別途扱う必要がある。

### キャッシュについての運用判断

キャッシュは安定した共通プレフィックスの再利用であり、CLIの会話履歴そのものを消す仕組みではない。Claude Codeはキャッシュを自動管理する。resume時にも会話は再送され、内容と有効期間が合う部分だけが再利用される。モデルの切替、system promptやtool定義の変更等は再利用に影響し得る。頻繁なモデル切替・毎turnの強制要約・毎回の会話初期化を節約策として一律に採用しない。キャッシュ維持のためだけに無意味な依頼を送り続けない。

共通の入力でもモデル・CLI・認証方式・プランで挙動が異なるため、APIの単価やTTLをそのままChatGPT/Claudeの購読枠へ当てはめない。まず実行単位の観測を追加し、同モデル・同じプロジェクト・同程度の依頼で、新規/再開、短い/長い会話を比較する。独立した仕事は新しい会話、同じ仕事の続きは明示sessionで継続する方針が妥当。

公式参照: [OpenAI Prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching)、[Codex App Server](https://learn.chatgpt.com/docs/app-server)、[Claude Code prompt caching](https://code.claude.com/docs/en/prompt-caching)。

### 旧TypeScript版で別途見つかった点

`src/core/bridge-service.ts` の自動retryは、同じsessionのqueue処理内でhandleMessageをawaitし、同じqueueへ再投入している。retryを有効にした場合に待ち合わせが循環する可能性があり、旧版専用の再現テストと修正が必要。nativeへは移植しない。今回のWinUI修正に旧版の実行制御変更は含めていない。


今回の修正検証: Coreは55/55成功。200万文字を超える1行を出すWindowsのオフラインプロセスから、表示上限後に届くthread ID・最終回答・turn.failedを回収できることを確認した。DesktopのDebugビルドは警告・エラー0。初回のCore全件実行では既存保存テストにatomic moveのアクセスエラーが発生したが、単独の全件再実行では再現しなかった。原因は未特定で、保存処理自体の変更はしていない。

変更後の隔離データでUIスモーク8項目も成功。共有用プレビューは4001文字に抑え、4000文字超の既存の省略案内を維持している。キャッシュ命中率や課金額を実測したものではない。

### 性能レビューの残項目の実装完了（2026-10-16）

上記の未対応3項目を実装した。Coreのチェックポイントは `a50e328`。

- Codex exec・App Server・Claude Codeの報告利用量と実行時間を履歴へ保存し、手動実行・チャット受信・履歴に表示。未取得値を0にせず、ターン／直近APIリクエスト／会話累計を区別する。Claudeの再開後resultは会話累計であり、履歴間の単純合算はしない。
- Claude Codeはstream-json出力から明示セッションIDを取得し、同じチャットスレッドを `--resume` で再開する。完了イベントなしの終了は成功と扱わず、設定の不一致や別IDへのすり替わりも拒否する。
- 実Codexのinitializeのみを3回計測。プロセス起動＋初期化は407.0／89.2／107.7ms。モデル推論は0回。App Serverは設定ごとに最大2プロセスを再利用し、アイドル2分・失敗・キャンセル・設定変更・画面終了時に破棄する。再利用は起動処理を省くためで、サーバー側キャッシュ命中の保証ではない。
- 保存時のWindows共有違反を再現し、atomic renameに限定した短時間リトライを追加。恒久的なアクセス拒否はエラーとして返す。

最終検証: Core 71/71成功（明示的に有効化したオフライン実プロセステストを含む）。Release MSIX生成成功。Release版で既存UIスモーク8/8、利用量のUI・履歴検証5/5、Docker Mattermostの承認・追加入力・共有・永続化9/9成功。実モデルへの有料リクエストは発行していない。実運用でのトークン削減率・料金削減率は未計測。大量会話でのロック辞書の解放と旧TypeScript版のretry問題は、上記の別途改善候補として残す。

### CLIのモデル候補選択（2026-10-16）

設定画面と会話別のモデル変更ダイアログに「CLIモデル候補を取得」と候補選択を追加した。Codexは専用App Serverでinitializeとmodel/listのみ、Claude Codeはstream-jsonのinitializeのみを使い、依頼文・thread/turn・推論要求は送信しない。取得後、キャンセル時、画面終了時には子プロセスを終了する。CLIのモデルIDと表示名を分け、取得だけで入力欄や設定を変更しない。候補の手動更新、20秒の期限、ページ数・応答サイズの上限、設定変更時の候補破棄、手入力へのフォールバックを備える。

会話別の候補は、その会話に保存されたCLIと作業フォルダーで取得する。モデル候補の一覧は利用権限や料金を保証するものではない。モデル名の固定リストをアプリ側で管理せず、CLIの報告値を使う。

Core 81/81成功（オフラインCLIの過大応答・応答ID不一致の検証を有効化）。実CLIの画面操作ではCodex 5件、Claude Code 6件を推論なしで取得。Release版のモデル候補UIテスト6/6、既存UIスモーク8/8成功。選択・手入力・設定保存・会話別の選択・CLI取得失敗からの復帰を検証した。Release MSIXの生成と、テスト用CLIがパッケージに混入していないことも確認。Cドライブ容量不足のため、今回のパッケージとパッケージ作成用の一時フォルダーはDドライブに出力した。

### 推論の強さの候補選択（2026-10-16）

CodexのsupportedReasoningEffortsとClaude CodeのsupportedEffortLevelsを読み、設定画面と会話別ダイアログでモデルごとの候補を選べるようにした。未報告の候補は補わない。モデル変更時は以前の推論指定を解除し、候補再取得だけでは保存値を変更しない。保存済み指定が現在の候補にない場合は明示的な選び直しを求める。

設定・会話・実行履歴にnullableの推論指定を追加し、旧保存ファイルとの互換性を維持。モデルと推論の会話別更新は同一のatomic書込みで保存する。会話別に別モデルを指定した場合は、既定モデル用の推論指定を流用しない。未指定はCLIに任せる（CLI自身の保存済み会話設定が適用される場合がある）。CLIの設定ファイルを直接変更しない。

Core 87/87成功。異なる会話モデルへの不適切な継承がないことも追加検証した。Releaseの候補・推論UIテスト11/11、既存UIスモーク8/8成功。保存・解除・手入力変更時のリセット・未編集設定の保持・実行履歴まで確認。実Claude CLIのモデル依存候補と、候補未報告モデルで既定値だけになる表示を推論なしで確認した。Debugビルドは警告・エラー0、Release MSIX生成成功。
