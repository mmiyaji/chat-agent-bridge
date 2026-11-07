# 実Mattermost接続の検証（2026-11-07）

対象は `dec6744` のWinUI x64ポータブル版。Lunaがテストを実行し、親エージェントが判定と証跡を確認する。通常のプレビューとは別プロセス・専用データを使用する。CLIは `ChatAgentBridge.TestAgent` に固定し、実モデルの推論や外部コマンドは実行しない。

## 接続先と範囲

専用Dockerコンテナー `cab-winui-e2e` の実Mattermostサーバー（localhost:8070）に接続する。APIを代替する模擬サーバーではなく、本物の認証・REST・WebSocket・投稿スレッドを経由する。外部の本番Mattermost環境や公開ネットワーク越しの接続を検証するものではない。

既存の専用テスト用team／channelとbridge／memberユーザーを使用する。認証情報はignored fixtureからプロセス内で読み、アプリ用にはWindows DPAPIで保護する。トークン・パスワードを検証記録へ転記しない。

初回のみ承認（first）、結果自動共有（auto）に固定し、初回依頼の受信・承認・ダミー実行・スレッド返信、同スレッドでの継続、切断／再接続後の実行と二重実行の有無を確認する。

## 結果

4件すべて成功。実投稿の受信、アプリの実行履歴、Mattermost側の返信を照合した。REST認証のみ、またはUIの共有済み表示のみでは成功と判定しない。

- 初回依頼はWebSocket経由で受信箱に到着。ローカル承認後、post IDに対応する履歴1件が `completed` となり、bridgeユーザーが元スレッドへ `claude-e2e:` の実行結果を自動返信した。進捗通知とは別の投稿として確認した。
- 同じスレッドの続きは新たなローカル承認操作なしで完了。同一SessionIdの履歴と `claude-e2e: resumed;` の返信を確認した。
- 別の新規スレッドでは、承認前の該当履歴が0件のままであることを確認。明示的に承認すると、新しいSessionIdで完了し `claude-e2e: new;` を自動返信した。
- 手動切断／再接続後、元スレッドの追加依頼は承認操作なしで同じSessionIdを継続し、`claude-e2e: resumed;` を自動返信した。
- 元スレッドの完了履歴は再接続前2件、新しい依頼を含む再接続後3件。待機後の追加増加は0件で、観測した範囲に二重実行はなかった。同一WebSocketイベントの強制再送を注入した重複排除テストではない。
## 判定と制限

前段の通常系では実Mattermost上の4依頼（初回・同スレッド継続・別スレッド初回・再接続後継続）で、受信からダミーCLI実行、結果の自動返信までを確認した。この範囲で安定性に関わる製品不具合は検出していない。

検証したのはローカルの実MattermostとダミーCLI。実CLI／AI推論、外部ホスト・TLS・プロキシ越しの接続、サーバー再起動など通信側の強制障害、長時間負荷、WebSocketイベントの強制重複配信は未実施。長文出力後の保存障害と診断保持は、後述の追加試験で実Mattermost経由でも検証した。[既存の模擬チャット経由の検証](execution-error-handling-validation.ja.md)とは別の証跡に残す。

製品コードの変更はないため、ビルド・ユニットテスト・ローカライズ検査は再実行していない。旧 `MattermostUiE2E.ps1` は旧設定UIへの依存があるため実行せず、現行UIと専用設定データで対象経路を操作した。
## 証跡と終了確認

機密情報を除いた結果: `.artifacts/mattermost-live-luna/results.json`。各依頼の履歴状態・終了コード、ID／SessionIdのSHA-256、返信のスレッド／投稿者／fixture接頭辞／テストマーカー一致、承認操作数、再接続前後の件数を保存した。4件とも `completed`、終了コード0、対応する結果返信は各1件。

親エージェントでもJSONを再検査し、4件の結果返信・継続セッション一致・別スレッドのセッション相違・承認2回・待機後の履歴増加0を確認。履歴全体は再接続前3件、後4件（元スレッドだけでは2→3件）だった。

試験用アプリとTestAgentのみを終了し、専用設定内のトークンを消去した。既存の通常プレビューPID50504とMattermostコンテナーは維持した。製品ソースの編集・コミットは行っていない。
## 追加の失敗系検証

同じ専用実Mattermostサーバーへ接続し、LunaでダミーCLI失敗系を追加検証する。通常系4依頼の結果と、今回の失敗系結果は別の証跡に残す。

最初のケースでは `muse-fixture.exe` に `__CAB_MUSE_CONFIRMED_FAIL__` を渡した。これはセッション開始確認と失敗terminalを出し、OSプロセスも終了コード1となる既存TestAgentのモード。実投稿→受信箱→ローカル承認を経て、履歴の `failed`／exitCode 1、元スレッドへのbridge失敗通知を確認した。成功fixtureの返信、依頼マーカーやローカル診断のechoはなかった。

不正JSON (`__CAB_MUSE_CONFIRMED_INVALID_JSON__`) とterminal欠落 (`__CAB_MUSE_MISSING_TERMINAL__`) も、各1回の実投稿・受信・承認を経て履歴は `failed`／exitCode -1となった。fixtureのOS終了コードは0だが、アプリは応答の異常を失敗として扱った。各スレッドには失敗通知が1件届き、成功出力、依頼マーカー、Exception／HRESULTの転送はなかった。

3つの失敗後、同一アプリへ通常依頼を送ると `completed`／exitCode 0となり、実Mattermostへ `muse-fixture:` の結果が1件自動返信された。履歴完了と返信送信は非同期のため、RESTの返信確認は25秒上限のpollingで行った。

### 長文出力後の履歴保存障害

別の専用データでClaude形式の既存live-output fixtureを使い、実Mattermost投稿を受信・承認して実行。CLIが実行中であることとstderrマーカーを確認後、履歴ファイルを `FileShare.Read` で保持して書き込みを阻害し、ゲートを開いて65,500文字の結果を出力させた。

- UIは `Execution or history error` へ遷移し、保存失敗の状態表示を確認。
- 表示出力は63,997文字。例外型とHRESULTの診断が長文より前にあり、`LIVE_FIXTURE_FINAL` で始まるCLI出力も後方に残った。
- `execution_exception` 監査1件が保存され、実行IDが該当履歴と一致。診断は `Cause=0`（AccessDenied）、`ExceptionType=UnauthorizedAccessException`、`HResult=80070005` の3項目のみ。親エージェントでも実ファイル内の診断を独立確認した。
- 実Mattermost上の進捗投稿は最終的にFailed／❌へ更新され、Runningのまま残っていなかった。Exception／HRESULT／長文CLI結果はチャットへ送られなかった。
- 書き込み不能の履歴ファイルは `running` のまま残った。これは保存障害のためfailed状態を永続化できないという既知の制約であり、UI終端状態・監査記録と区別する。

ロックを解除しゲートを消去した後、同一アプリへ新しい実Mattermost依頼を送信。CLI開始とstderr進捗を確認してからゲートを開くと、履歴は `completed`／exitCode 0、画面は固定の最終結果となり、元スレッドへ結果が1件自動返信された。履歴全体は2件で、障害対象の古いレコードは復旧後もディスク上 `running` のまま、新しい回復依頼は `completed` だった。

親エージェントでも監査ファイル全体に実bridgeトークンと `LIVE_RUN_ERROR_LONG`／`LIVE_RUN_RECOVERY` の依頼マーカーが含まれないこと、診断の項目がallowlistの3項目だけであることを再検査した。
### 追加試験の証跡・判定

統合結果は `.artifacts/mattermost-live-failures-luna/results.json`、CLI失敗4依頼の詳細は同ディレクトリの `muse/results.json`。3種類の失敗とその後の回復、長文保存障害とその後の回復の計6依頼を実Mattermost経由で検証し、想定した失敗検出・通知・診断保持・回復を確認した。親エージェントもJSONの状態・通知・返信件数・診断と機密除外条件を再検査した。

TestAgentプロセスのOS終了値を別の監視器で直接採取してはいない。fixtureソースで定義された終了値と、アプリrunnerが履歴に記録した終了値を証跡で分けた。不正JSON／terminal欠落ではfixtureソース上のOS終了値0に対し、runnerの失敗結果は-1だった。

確認コードの途中で、正常回復の合成依頼名に含まれたFAILUREという語を失敗通知と誤検出した。実Mattermost投稿を再照合し、明示的な❌ Failed通知を判定する条件へ修正した。最終結果では回復依頼の失敗通知はfalseで、成功結果返信1件を確認している。これは検証側の判定修正で、製品不具合ではない。

試験アプリ・TestAgent・履歴ロックhelperを停止し、専用設定のトークンを消去した。通常プレビューPID50504と既存Mattermostコンテナーは維持。製品コードは変更していない。今回検出した保存障害時の履歴running残存以外に、この範囲で新しい製品不具合は確定していない。
### running残存と終了・再起動時の扱いの補足

残存する `running` は `history.json` の生データ上の値であり、実行中判定が永続する意味ではない。実行のfinallyで `EndTracking` と `IsExecuting=false` が実行される。終了確認の集計は履歴ファイルではなく受信箱の現在状態を使い、`実行／記録エラー` は実行中件数に含めない。

`ExecutionHistoryStore.ListAsync` はlive追跡されていないrunningを戻り値で `interrupted` に変換する。再起動後はlive追跡がないため中断扱いになる。受信箱の復元もpending／runningをinterruptedへ変換し、実行を自動再開しない。これは生のhistory.jsonを書き換える処理ではないため、元のfailed状態が復元されるわけではなく、原因の照合にはexecution_exception監査を使う。

ソース確認に加え、既存の `RunningExecution_RemainsRunningWhileLiveAndStaleRecordDisplaysInterrupted` と `EndTracking_DisplaysPersistedRunningRecordAsInterruptedWithoutWritingFile` をRelease／no-buildで再実行し2／2成功。終了確認・再起動の実UI操作はこの追加確認では実施していない。