# WinUI への移行計画

## 目的と現在地

本書は、既存の Node.js/TypeScript 版 Chat Agent Bridge を Windows 向け WinUI 3 アプリへ段階的に移行するための実装計画である。目標は、チャット上のスレッドをローカル agent session に結び付ける既存の運用を維持しながら、設定・接続・状態確認をネイティブ UI で行える Store 対応アプリにすること。

## 進捗（2026-10-15）

ブランチ: `codex/winui-rebuild`。旧版の参照ブランチは `codex/reference-operations-ux`。

| 段階 | 状態 | 実装・確認 |
|---|---|---|
| 機能棚卸し・構成決定 | 完了 | 本文の機能対応表。WinUI / Core / Transports の3層に分離 |
| ネイティブUI・コア | 実装・Debugビルド確認済み | XAML/MVVM、設定保存・再読み込み、DPAPI、CLI実行・停止・同一フォルダー排他 |
| チャット受信 | 実装・単体テスト済み | Slack Socket Mode、Mattermost WebSocket、フィルター・重複排除、PC上での承認・拒否・実行、状態返信 |
| Codexセッション継続 | 実装・単体検証済み | 標準exec設定でスレッド別に保存・再開。設定不一致と再開ID不一致を拒否。解除は実行と排他 |
| 履歴・監査 | 実装・UI確認済み | メタデータのみ各500件、破損検出、中断表示。セッション解除・本文共有の操作を追加 |
| 自動テスト | 成功 | Core 36件、Transports 7件。ネットワークなし、有料モデル実行なし |
| 起動・UI・MSIX | 確認済み | Debug警告0、UI操作7項目成功。今回の変更を含むRelease MSIXを生成 |
| 既存機能の完全移行 | 未完了 | App Server承認、複数root、全チャットコマンド、他agent継続、旧設定インポート、接続回復 |
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
