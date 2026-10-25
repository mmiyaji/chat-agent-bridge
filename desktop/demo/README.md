# デモ動画の撮影手順

Mattermost（左）と CAB（右）を並べ、使い方が伝わる動画を自動で撮影するためのスクリプトと手順です。
スクリプトはこのフォルダー（`desktop/demo/`）にあります。生成物（CAB のデータ、ブラウザープロファイル、
Mattermost の資格情報、録画、スクリーンショット、ログ）は Git 管理外の `desktop/artifacts/demo/` に出力します。

## 前提

- Windows 11、PowerShell 7、Google Chrome、ffmpeg（`ddagrab` 対応のビルド）
- ローカルの Mattermost（Docker）: `desktop/docker-compose.mattermost-e2e.yml`（`http://127.0.0.1:8070`）
  - E2E セットアップが作る管理用の状態ファイル `desktop/artifacts/mattermost-e2e.json` を使います
- CAB のデモ用ビルド（ポータブル版）を `desktop/artifacts/demo-app` に出力しておきます
  ```
  dotnet publish desktop\ChatAgentBridge.Desktop -c Release -p:Platform=x64 -r win-x64 --self-contained -p:CABPortable=true -o desktop\artifacts\demo-app
  ```
- AI CLI: Antigravity CLI（agy）。エージェント切り替えデモでは Claude Code CLI も使います（ログイン済みであること）
- 撮影はメインモニターの作業領域（1920×1032 を想定）で行います。Windows 通知はメインモニターにしか表示されないためです

ツールの場所は自動で探します。見つからない場合は環境変数で指定します:
`CAB_DEMO_APP`、`CAB_DEMO_AGY`、`CAB_DEMO_CLAUDE`、`CAB_DEMO_FFMPEG`、`CAB_DEMO_CHROME`（`demo-env.ps1`）。

## 初回の準備

```
pwsh -NoProfile -File desktop\demo\setup-mattermost-demo.ps1
```

チーム `demo`、チャンネル `#ai-requests`、ユーザー `alice`（依頼者）と `cab-bot`（CAB 用）を作ります。
パスワードとトークンは生成して `desktop/artifacts/demo/mattermost-demo.json` にだけ保存し、画面には表示しません。

## 撮影

撮影前に、デスクトップやAIを操作せずシナリオの承認・応答待ち処理を検証できます。
リポジトリのルートから実行してください。

```powershell
pwsh -NoProfile -File desktop\demo\test-demo-scenario.ps1
```

このテストは自動承認・手動承認・タイムアウト時の処理をモックで確認します。
CLIのログイン状態や、録画が最後まで進むことを検証するものではありません。

撮影中は PC に触れないでください（カーソル移動とクリックを自動で行います）。
CAB を MSIX 版と同じユーザー環境で動かすため、スクリプトは通常のターミナルから実行します。

```
pwsh -NoProfile -File desktop\demo\take.ps1 -Lang ja -Record
pwsh -NoProfile -File desktop\demo\take.ps1 -Lang en -Record
pwsh -NoProfile -File desktop\demo\take-feature.ps1 -Scenario project -Record
pwsh -NoProfile -File desktop\demo\take-feature.ps1 -Scenario agent -Record
```

- `-Record` を外すとリハーサル（録画なし）です。最後に画面を `final-*.png` に保存します。
- `take-feature.ps1 -AutoSwitch` は承認モード「自動承認」と「切り替えも自動承認」をオンにします。プロジェクト切り替えデモは承認操作なしで進みます。エージェント切り替えデモでは切り替え後の最初の依頼に承認が必要です。リセットを省略する `-NoReset` と併用する場合は、既存の CAB 設定も自動承認にしてください。
- 録画は `desktop/artifacts/demo/demo-<lang|scenario>-raw.mkv` に出力されます。
- 機能デモ（`take-feature.ps1`）は CAB をダークテーマで起動します。

### 1本の流れ

1. `reset-demo.ps1` — デモ用 CAB を終了し、`#ai-requests` の投稿を削除し、CAB のデータとサンプルプロジェクトを作り直して起動
   - `setup-cab-demo.ps1` のオプション: `-Lang ja|en`、`-Theme light|dark`、`-AutoSwitch`
   - サンプルプロジェクト: `weather-cli`（言語別）と `todo-cli`（日本語）。どちらも信頼済みフォルダー
   - 切り替え先の CLI として agy と Claude Code（見つかった場合）を登録
   - 同意画面を出さないため、同意済みのポリシー版を書き込みます。アプリの同意ポリシーを更新したら `setup-cab-demo.ps1` の版も更新してください
2. `open-browser.ps1` — 専用プロファイルの Chrome をアプリモードで起動（DevTools ポート 9333）し、ページ内で alice としてログイン
3. `prep-mattermost-ui.ps1` — alice の表示言語を設定し、スレッドの返信をチャンネル内に表示し、通知バナーを閉じる
4. `run-demo.ps1` / `run-scenario.ps1` — シナリオを再生（冒頭で `stage.ps1` を実行）
5. 後処理（下記）

### 画面の配置とマスク

`stage.ps1` がメインモニターの作業領域に無地の背景ウィンドウ（`backdrop.ps1`）を置き、
その上に Mattermost（左、幅 880）と CAB（右）を最前面で固定します。
背景は作業領域のサイズにします。画面全体を覆うと Windows が全画面アプリと判断し、通知を止めるためです。
`stage.ps1 -Off` で固定を解除し、背景を閉じます。

## シナリオ

### 基本デモ（`run-demo.ps1 -Lang ja|en`）

1. CAB のサイドバーで「チャット」を開き、「Mattermost に接続」
2. alice が `#ai-requests` に投稿: 「@cab-bot README.md を読んで、このプロジェクトの概要を3行で教えて」
3. Windows 通知が表示される → CAB で会話を開いて「承認して実行」→ 確認ダイアログで「実行」
4. 実行中の出力が流れ、Mattermost のスレッドに回答が届く
5. alice がスレッドで返信: 「ありがとう！ test_weather.py に大阪のテストも追加して（テストの実行は不要です）」
   → 承認モード「最初だけ確認」なので再承認なしで実行され、テスト追加の回答が届く

### プロジェクト切り替え（`run-scenario.ps1 -Scenario project`）

1. 接続 → 「@cab-bot README.md を読んで、このプロジェクトの概要を2行で教えて」→ 承認 → weather-cli についての回答
2. スレッドで `folder <todo-cli のパス>` を送信 → 通知 → CAB で承認待ちの依頼を選んで「承認して実行」→ 確認ダイアログで「変更」
3. スレッドで「このプロジェクトの README.md を読んで、使い方を2行で教えて」
   → フォルダーが変わったので「最初だけ確認」でも再承認 → todo-cli についての回答

`-AutoSwitch` 付きでは自動承認が有効になり、初回依頼とフォルダー切り替えの承認操作を省略します。

### エージェント切り替え（`run-scenario.ps1 -Scenario agent`）

1. 接続 → 「@cab-bot weather.py を読んで、対応している都市を教えて」→ 承認 → agy の回答
2. スレッドで `!cab agent claude summary` → 承認 → 確認ダイアログで「変更」（会話の要約を引き継いで Claude Code に切り替え）
3. スレッドで「続けて、weather.py に福岡の予報を追加して（テストの実行は不要です）」→ 承認 → Claude Code の回答

`-AutoSwitch` 付きでは初回依頼とエージェント切り替えは自動承認されますが、切り替え後に新しいエージェントで行う依頼は承認が必要です。

### 利用実績の詳細（未作成）

複数のエージェント・プロジェクトで数回実行して実績を貯めたうえで、
「履歴と利用状況」ページを順に見せる予定です（会話一覧 → 実行記録 → 利用量 → 監査ログ、ダークテーマ）。

## 後処理

```
ffprobe -v error -select_streams v -show_entries packet=pts_time -of csv=p=0 <raw.mkv>
ffmpeg -y -ss 1 -to <終了秒> -i <raw.mkv> -c:v libx264 -preset slow -crf 24 -pix_fmt yuv420p -movflags +faststart <出力.mp4>
```

- 1行目はフレーム落ちの確認です（30fps なので間隔は約 33ms。67ms は 1 フレーム落ち）。
- 終了秒は `run-demo.log` / `run-scenario.log` の `start` から `end` までの時間で決めます（録画は `start` の約 2 秒前に始まります）。
- 確認用のコマ一覧: `ffmpeg -ss 18 -i <raw.mkv> -vf "fps=1/3,scale=960:-1,tile=3x3" -frames:v 1 sheet.png`

## ハマりどころ（対策済み）

| 症状 | 原因 | 対策 |
|---|---|---|
| 約1秒ごとにカクつく | gdigrab が 30fps に追いつかずフレームが落ちる | Desktop Duplication の `ddagrab` で録画 |
| 録画が止まらない・ファイルが壊れる | ffmpeg の stderr パイプが詰まって停止する | 進捗出力を止め、MKV に出力し、終了を 120 秒待つ |
| Windows 通知が出ない | サブモニターで撮影している／背景が全画面扱い | メインモニターで撮影し、背景は作業領域のサイズにする |
| 承認ボタンが見つからない | 開いている会話の詳細が前の依頼のまま | 依頼一覧（`ChatRequests`）から承認待ちを選ぶ |
| `folder` のパスが崩れる | Mattermost の Markdown が `\.` をエスケープとして扱う | パスはスラッシュ区切りで送る |
| agy の返信に「RunCommand を拒否」 | ヘッドレスの agy は「編集を自動許可」でコマンドを実行しない | 依頼文で「テストの実行は不要」と書く |
| Chrome の通知許可の吹き出し | サイトの通知許可要求 | `--deny-permission-prompts` |
| キー入力が別のアプリに入る | 前面ウィンドウの取り違え | Mattermost は CDP で入力し、CAB は UI Automation とカーソル下の確認付きクリックで操作 |

## スクリプト一覧

| スクリプト | 役割 |
|---|---|
| `take.ps1` / `take-feature.ps1` | 1本分（リセット → ブラウザー → シナリオ → 最終画面）をまとめて実行 |
| `run-demo.ps1` / `run-scenario.ps1` | シナリオの再生と録画 |
| `reset-demo.ps1`, `setup-cab-demo.ps1`, `launch-cab.ps1` | CAB のデータとサンプルの作り直し、起動 |
| `setup-mattermost-demo.ps1`, `open-browser.ps1`, `prep-mattermost-ui.ps1` | Mattermost の準備とログイン |
| `stage.ps1`, `backdrop.ps1` | 画面の配置とマスク |
| `demo-env.ps1` | 出力先とツールの場所 |
| `demo-lib.ps1`, `cdp.ps1` | 録画、カーソル移動、CAB の UI Automation、Mattermost の CDP 操作 |
| `shot.ps1` | メインモニター作業領域のスクリーンショット |
| `check-switch-setting.ps1`, `verify-switch-setting.ps1` | 「切り替えも自動承認」設定の実環境確認 |
| `watch-windows.ps1` | 新しく開いたウィンドウの記録（コンソールが一瞬表示されるかの調査用） |
| `test-demo-scenario.ps1` | アプリ・ブラウザー・AIを起動しない、承認と応答待ち処理のテスト |
