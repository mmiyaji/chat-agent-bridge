# 紹介サイトの公開手順

Chat Agent Bridge の紹介ページは、リポジトリの `site/` にある静的ファイルです。
`main` へ取り込むと、GitHub Actions が Cloudflare Pages へ公開します。

公開先: https://chat-agent-bridge.ruhenheim.org/

## 構成

| 場所 | 内容 |
| --- | --- |
| `site/index.html` | ページ本体（日本語） |
| `site/assets/` | CSS、スクリプト、画像、Microsoft Store の公式バッジ |
| `site/_headers` | セキュリティヘッダーとキャッシュ設定（Cloudflare Pages の仕様） |
| `site/_redirects` | `/download`・`/store`・`/github` などの転送 |
| `scripts/check-site.mjs` | 公開前の検証 |
| `.github/workflows/cloudflare-pages.yml` | 検証と公開のワークフロー |

日本語が本文で、`?lang=en` を付けると英語で表示します。英語の文言は `site/assets/i18n-en.js` にあり、`index.html` の `data-i18n` と同じキーで対応させます。
英語用の画像は `data-en-src`（画像）と `data-en-href`（拡大表示のリンク）で指定します。文言を足すときは、両方の言語をそろえてください。`scripts/check-site.mjs` は、英語用の画像が存在するかも確認します。

右上の言語メニューは、ボタンに今の言語を表示し、開くと各言語をその言語の表記で並べます。言語を増やすときは、`index.html` のメニュー項目（`#lang-list`）、`site/assets/site.js` の言語名、その言語の文言ファイルをそろえて追加します。

ページは外部のスクリプト・スタイル・フォントを読み込みません。
`_headers` の Content-Security-Policy で、同じサイトのファイルだけを許可しています。
インラインの `<script>` や `onclick` などは CSP で動かないため、処理は `site/assets/*.js` に書きます。

## 更新の流れ

1. `site/` 配下を編集します。画像は `site/assets/img/` に置き、`alt` を必ず付けます。
2. ローカルで検証します。

   ```bash
   node scripts/check-site.mjs
   ```

3. ローカルで表示を確認します。リポジトリのルートで次を実行し、`http://localhost:8765/` を開きます。

   ```bash
   python -m http.server 8765 --directory site
   ```

   ライト／ダークの切り替え、画像のクリック拡大、スマートフォン幅での表示を確認します。
   `_headers` と `_redirects` はローカルのサーバーでは効かないため、公開後に確認します。

4. ブランチで変更をコミットし、レビュー後に `main` へ取り込みます。
5. `main` への push で `Cloudflare Pages` ワークフローが動き、検証が通れば本番へ公開されます。
   `site/`、`scripts/check-site.mjs`、ワークフロー自体の変更だけが対象です。
6. 公開後、本番の URL で表示と転送（`/download` など）を確認します。

ワークフローは GitHub の Actions 画面から手動実行（`workflow_dispatch`）もできます。

## 必要な設定（初回のみ）

リポジトリの Actions シークレットに次の2つを登録します。値はリポジトリやドキュメントに書きません。

| 名前 | 内容 |
| --- | --- |
| `CLOUDFLARE_API_TOKEN` | Cloudflare の API トークン。権限は「Cloudflare Pages の編集」だけに絞る |
| `CLOUDFLARE_ACCOUNT_ID` | Pages プロジェクトを持つ Cloudflare アカウントの ID |

Cloudflare 側では次を用意します。

- Pages プロジェクト `chat-agent-bridge`（Git 連携なしの Direct Upload 形式）
- プロジェクトのカスタムドメインに `chat-agent-bridge.ruhenheim.org` を追加

API トークンは、ほかのリポジトリと共用せず、不要になったら Cloudflare で失効させます。
トークンを作り直したときは、GitHub のシークレットも更新します。

## 元に戻す

Cloudflare のダッシュボードで、Pages プロジェクトの「デプロイ」から以前のデプロイを選び、ロールバックします。
その後、リポジトリでも該当の変更を revert して `main` に取り込みます（次の公開で戻らないようにするため）。

## 注意

- 画面写真は、撮影用の設定とサンプルデータで撮ったものを使います。実際の会話、ユーザー名、トークン、社内のサーバー名が写っていないことを確認してから追加します。
- 外部のサービスに画像やスクリプトを置かず、`site/assets/` に同梱します。
- Microsoft Store のバッジは Microsoft が配布している公式の画像を、改変せずに使います。
