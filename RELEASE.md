# ポータブル版のリリース手順

このリポジトリのルートから PowerShell 7（`pwsh`）で実行します。
通常は GitHub Actions で x64 / ARM64 の ZIP を作成・検証し、GitHub Release とアップデーター用の JSON を配信します。

**手順の確認やビルドだけでは公開しません。リリースタグの push は、公開が依頼されたときだけ行います。**
main へのマージ・push と、Release の公開は別の操作です。

## 1. 公開する変更を確定する

- 対象の変更をレビューし、テストを通してコミットします。
- 公開対象を main に取り込み、origin/main へ反映します。
- README、既知の制限、サードパーティーのライセンスを確認します。
- 日英・ライト／ダーク・狭いウィンドウで変更箇所を確認します。
- チャット関連の変更では、接続・会話継続・承認・結果返信・許可範囲を実サービスで確認します。未検証の項目はリリースノートに残します。

作業前の確認:

```powershell
git status --short
git worktree list
git fetch origin --tags
```

未コミットの変更を混ぜないでください。main が別の worktree で開かれている場合は、そちらで main を更新します。

検証項目は [.github/workflows/ci.yml](.github/workflows/ci.yml) が基準です。
CI は TypeScript、翻訳、配信データ、Core / Transports のテスト、WinUI のビルド、ライセンス表記を検証します。
CI の手動実行とリリースタグの検証では UI スモークテストも実行します。
実チャットの認証情報は CI に渡しません。ARM64 のクロスビルドは ARM64 実機での起動確認を代替しません。

## 2. 公開せずにパッケージを確認する

GitHub の Actions → **Portable release** → **Run workflow** で対象ブランチとバージョンを指定します。
手動実行はビルド・検証と artifact 保存だけを行い、Release / Pages を公開しません。
`portable-x64` / `portable-ARM64` の artifact を取得して確認します。保持期間は7日です。

ローカルで作成する場合は Windows、.NET SDK 10、Windows SDK、PowerShell 7 を用意し、バージョンを明示します。
スクリプトの既定バージョンをそのまま使わないでください。

```powershell
$releaseVersion = Read-Host 'バージョン（X.Y.Z または X.Y.Z-preview.N、v は不要）'
$release = ./desktop/Resolve-ReleaseBuildVersion.ps1 -Version $releaseVersion
$packageDirectory = Join-Path (Get-Location).Path "desktop/artifacts/portable-releases/$($release.ReleaseVersion)"

foreach ($architecture in @('x64', 'ARM64')) {
    ./desktop/Create-PortableArchive.ps1 -Architecture $architecture -ReleaseVersion $release.ReleaseVersion -ApplicationVersion $release.ApplicationVersion -OutputDirectory $packageDirectory
}

./desktop/Publish-PortableRelease.ps1 -Tag $release.Tag -PackageDirectory $packageDirectory -ValidateOnly
./desktop/Test-PortableReleasePublishing.ps1 -Tag $release.Tag -PackageDirectory $packageDirectory
```

最後の2行も公開しません。前者は両 ZIP を検証し、後者はモックの GitHub CLI で公開処理の正常系・失敗系を検証します。
`Publish-PortableRelease.ps1` を **`-ValidateOnly` なしで実行すると実際に公開する**ため、通常のローカル確認では外さないでください。

バージョンは `Resolve-ReleaseBuildVersion.ps1` で一元変換します。
たとえば `1.2.3-preview.4` はタグ `v1.2.3-preview.4`、Windows ファイルバージョン `1.2.3.4` になります。
`beta` / `rc`、数字の先頭ゼロは受け付けません。各数値は 0〜65534 です。

ZIP 内のルートは `ChatAgentBridge.Portable/` です。通常の作成スクリプトは以下も同梱・検証します。

- アプリ、.NET / Windows App SDK、`ChatAgentBridge.Updater.exe`
- `portable-release.json`、`portable-files.json`、`SHA256SUMS.txt`
- 日英の案内とライセンス表記。ZIP 自体の `.sha256` も別途作成します。

個人の設定・トークン・履歴・テスト用 CLI は含めません。
通常配布は署名用証明書やパッケージ ID を必須としないポータブル版です。証明書の信頼登録は、このリリース手順では行いません。
実機確認に使ったフォルダーを丸ごと ZIP にせず、通常の作成スクリプトを使います。
`CABPortableUpdateTest=true` の試験用ビルドは配布しません。

## 3. 既存版からの更新を試す

公開前は利用中の配置と設定を使わず、隔離した環境で試します。

```powershell
# 次の2つには、手元に用意した同じ CPU 向け ZIP の絶対パスを入力します。
$previousZip = Read-Host '以前の公開版 ZIP の絶対パス'
$nextZip = Join-Path $packageDirectory "ChatAgentBridge.Portable-$($release.ReleaseVersion)-win-x64.zip"
./desktop/PortableUpdateSmokeTest.ps1 -OldArchivePath $previousZip -ArchivePath $nextZip
```

`PortableUpdateSmokeTest.ps1` は ZIP が `desktop/artifacts` または指定した `-WorkingDirectory` 以下にあることを要求します。
以前の ZIP もその試験領域へ置いてください。以前の版には更新メタデータが必要です。

画面からダウンロード・再起動まで試す方法は、[アップデータのローカル試験](docs/portable-update-tryout.ja.md)を参照してください。
確認する項目:

- 更新候補・リリースノート・バージョンが正しい。
- 再起動確認をキャンセルしても、起動中のアプリを置き換えない。
- 適用後に新版が起動し、設定・履歴・言語・テーマが残る。
- ハッシュ不一致を拒否し、適用失敗時にはエラーを表示して旧版へ復元できる。

## 4. 公開する

**ここからは公開操作です。** main に取り込み済みの対象コミットで実行します。
既存タグ・既存 Release と重複しないバージョンを選んでください。

```powershell
git switch main
git pull --ff-only

$releaseVersion = Read-Host '公開する未使用のバージョン（v は不要）'
$release = ./desktop/Resolve-ReleaseBuildVersion.ps1 -Version $releaseVersion
git status --short
git rev-parse HEAD
git tag --list $release.Tag

# 上の差分が空で、対象コミットと未使用タグであることを確認してから実行します。
git tag $release.Tag
git push origin $release.Tag
```

`Portable release` が次の順に処理します。

1. タグの形式と main の履歴に含まれるコミットかを確認。
2. CI、x64 / ARM64 のパッケージ作成、公開処理の隔離テスト。
3. 下書き Release を作成し、両 CPU の ZIP と `.sha256`（計4アセット）をアップロード。
4. リモートのタグ、アセットのサイズ・SHA-256 を照合して Release を公開。
5. `Distribution data` を呼び出し、公開済み Release から更新 JSON と料金表を Pages に配信。

手順2でローカル作成した ZIP を自動アップロードするのではなく、タグのコードから Actions が再ビルドします。
MSIX / Microsoft Store への公開はこのワークフローに含みません。
Store版の識別子・MSIXビルドは [Microsoft Store公開の準備](docs/store-preparation.ja.md) を参照してください。

## 5. 公開結果を確認する

- Actions の **Portable release** が `distribution` まで成功している。
- [GitHub Releases](https://github.com/mmiyaji/chat-agent-bridge/releases) でタグ、説明、プレビュー指定、4アセットを確認する。
- 配信 JSON に対象バージョン・両 CPU の ZIP・サイズ・SHA-256 が反映されている。
- 以前のポータブル版の「情報」から実際に更新を確認し、ダウンロード・再起動・データ引き継ぎを確認する。プレビュー版はプレビューを含む設定で確認する。
- 確認した旧版→新版、CPU、結果、未検証事項をリリースノートなどに記録する。

配信先:

| 用途 | URL |
| --- | --- |
| 安定版 | https://mmiyaji.github.io/chat-agent-bridge/updates/stable.json |
| プレビューを含む版 | https://mmiyaji.github.io/chat-agent-bridge/updates/preview.json |
| モデル料金表 | https://mmiyaji.github.io/chat-agent-bridge/pricing/models.json |

更新 JSON が候補を案内し、ZIP 本体は GitHub Releases から取得します。Release ページの HTML スクレイピングはしません。
旧アプリ用の GitHub Releases API 経由の確認も考慮し、公開後は実際の旧版で確認します。

## 失敗時の対処

| 状況 | 対処 |
| --- | --- |
| ビルド・テスト失敗 | 証跡を確認して修正し、新しいバージョンでやり直す。公開済みタグを付け替えない。 |
| アップロード・照合失敗で下書きが残る | 原因を解消して同じ Actions run を再実行する。下書きに限りアセットを置換して再検証する。 |
| Release 公開済み・Pages 配信失敗 | `Distribution data` を main から手動実行する。Release を重複作成しない。 |
| 公開済みパッケージの内容を修正したい | 新しいバージョンを発行する。公開済み ZIP・ハッシュを上書きしない。 |
| 不具合版の配信を止めたい | 対象 Release を下書きに戻し、配信 JSON の再生成を確認する。反映しない場合は `Distribution data` を main から実行する。適用済みアプリは自動でダウングレードされない。 |

Pages の初回設定、権限、料金表だけの更新は [CI/CD の運用手順](docs/ci-cd.ja.md)を参照してください。
動作の根拠は [release.yml](.github/workflows/release.yml)、[distribution.yml](.github/workflows/distribution.yml)、[Publish-PortableRelease.ps1](desktop/Publish-PortableRelease.ps1) です。
