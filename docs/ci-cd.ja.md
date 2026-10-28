# GitHub Actions による検証と配信

## 何が自動化されるか

| きっかけ | 処理 | 公開先 |
| --- | --- | --- |
| PR、main / codex/* への push | TypeScript、ローカライズ、配信JSON、.NETテスト、WinUI x64 ビルド | テスト結果のみ（Actions artifact、7日） |
| CI の手動実行、またはリリースタグの検証 | 発行済みポータブルアプリの UI スモークテスト | UI 証跡 artifact（7日） |
| Portable release の手動実行 | 上記検証と x64 / ARM64 ポータブルZIPの作成 | Actions artifact のみ（7日）。ReleaseやPagesは変更しない |
| Store package (build only) の手動実行 | x64 / ARM64 のStore用MSIX生成・内容検査 | `.msixupload` のActions artifactのみ（7日）。Storeへの提出・公開はしない |
| main 上のコミットへ `vX.Y.Z` / `vX.Y.Z-preview.N` タグをpush | 検証 → 両CPU向けZIP → 下書きRelease → アセット照合 → Release公開 → 配信JSON生成 | GitHub Releases、GitHub Pages |
| main の料金表・配信生成スクリプト変更 | 公開済みRelease一覧と最新料金表からJSONを再生成 | GitHub Pages |
| GitHub上でReleaseを手動公開・編集・非公開化・削除 | 公開対象を再取得してJSONを再生成 | GitHub Pages |
| Distribution data の手動実行（main） | 配信データを再生成・再配置 | GitHub Pages |

タグは自動では作成しません。ブランチをpushしただけではアプリを公開しません。タグ対象がmainの履歴に含まれない場合や、ビルド中にリモートのタグが別コミットへ移された場合は公開を拒否します。手動ビルドは機能ブランチでも試せます。

## 初回の準備

1. ワークフローをmainへ取り込みます。
2. Repository Settings → Pages → Build and deployment → Source を **GitHub Actions** にします。
3. Actions がリポジトリで有効になっていることを確認します。公開に必要な権限は各ジョブの `permissions` で限定しています。専用PATやSlack/Mattermostトークンは不要です。
4. `github-pages` environment の配信許可がmainと `v*` タグに対応していることを確認します。組織側で承認を必須にしている場合はその承認が必要です。
5. 最初は **Portable release → Run workflow** で任意のバージョンを指定し、配布せずZIPを検証できます。

Pagesの初回配信はmainへの取り込み時、以降は料金表変更時とRelease公開後に実行します。Releaseを `GITHUB_TOKEN` で作成しても別ワークフローのイベントが発火しないため、公開ジョブから配信ワークフローを直接呼び出しています。

## 公開する場合

レビュー・mainへの取り込み後、未使用のバージョンを選んで実行します。

```powershell
git switch main
git pull --ff-only
git tag v0.1.0-preview.6
git push origin v0.1.0-preview.6
```

安定版は `v0.1.0` のようにpreview部分を省きます。数字の先頭ゼロ、rc/beta等の形式は受け付けません。Windowsファイルバージョンの各要素は0～65534です。上のバージョンは例なので、既存タグと重複しないことを確認してください。

タグを作る前に、CI を `workflow_dispatch` で実行し、UI スモークテスト成功と UI 証跡 artifact を確認します。タグ検証にも同じ UI スモークテストが含まれますが、Windows hosted runner の対話型デスクトップ可用性はこの環境で事前確認できていません。失敗した場合は記録された証跡を確認し、対話型 Windows 環境でスクリプトを実行してからリリースを続けてください。`Portable release` の全ジョブ成功も確認します。ZIPには.NET / Windows App SDK / 更新ヘルパーを含め、アプリ設定・認証情報・テスト用実行ファイルは含めません。ZIP内のマニフェストとSHA-256、CPU、自己完結ランタイムを検証し、アップロード後のGitHubアセットのサイズ・SHA-256も照合します。MSIXやMicrosoft Store公開・コード署名はこのワークフローの対象外です。

公開前には実ZIPとモックGitHub CLIで、正常公開、ハッシュ不一致による停止、公開済みアセットの上書き防止、API失敗時の停止を検証します。この検証ジョブには公開権限や実チャットの認証情報を渡しません。

## 配信URLと更新動作

- 安定版: https://mmiyaji.github.io/chat-agent-bridge/updates/stable.json
- プレビューを含む版: https://mmiyaji.github.io/chat-agent-bridge/updates/preview.json
- モデル料金表: https://mmiyaji.github.io/chat-agent-bridge/pricing/models.json

更新JSONは `schemaVersion: 1`、`channel`、`releases` を持ちます。ZIP本体はGitHub Releasesから取得します。URLは固定のリポジトリに限定し、サイズとSHA-256をアプリ側でも検証します。アプリの定期確認の有効/無効と、ダウンロード・適用時の利用者確認は維持します。

Pagesの展開前はHTTP 404の場合だけ従来のGitHub Releases API / raw料金表へフォールバックします。不正JSONやサーバーエラーを別経路で隠しません。旧アプリは従来APIで引き続き確認できます。

## 料金表だけを更新する場合

`desktop/ChatAgentBridge.Core/Pricing/model-prices.json` の単価と `checkedOn` を更新し、PRで検証後mainへ取り込みます。Releaseタグもアプリの再ビルドも不要です。配信はアプリに組み込んだスキーマと互換のデータのみ受け付けます。

料金の優先順位は **手動指定 > 自動取得 > アプリ同梱** です。利用者の `pricing-manual.json` を配信処理が書き換えることはありません。ETagキャッシュと、不正・古い料金表を受け入れない処理を維持しています。料金を外部サイトから自動収集する処理は含みません。

## 失敗した場合

- 検証・パッケージ作成失敗: Releaseは作りません。修正後に新しいタグでやり直します。
- アセット送信・照合失敗: Releaseは下書きのまま残します。同じワークフローを再実行すると下書きのアセットを置換して再検証できます。
- 公開済みRelease: アセットは上書きしません。同一内容の再実行のみ照合して進めます。内容を修正する場合は新しいバージョンを使います。
- Release公開後のPages配信失敗: ZIPは公開済みです。原因修正後 `Distribution data` をmainから手動実行できます。旧JSONは成功した配信まで維持されます。
- 不具合版を配信から外す場合: 対象Releaseを下書きに戻します。手動変更のReleaseイベントでも配信データを再生成します。反映されない場合は `Distribution data` をmainから再実行できます。すでに利用者が適用した版は自動でダウングレードされません。

CIは実チャットの認証情報を使いません。Slack/Mattermostの実環境・Windows通知・画面の視認性の確認は、公開前の手動検証として別途行います。ARM64はクロスビルドと配布形式の検証で、ARM64実機での起動試験を代替しません。
