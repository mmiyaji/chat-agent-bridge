# ポータブル版アップデータのローカル試験

公開前の更新を実画面で試すための、開発専用ビルドです。現在のコードに古いリリース番号を付け、別途用意した新しいポータブルZIPへ更新します。GitHubへの公開や、実際の旧コードへの切り戻しは不要です。

## ビルドと起動

まず、通常ビルドで更新先ZIPを作成します。

```powershell
pwsh -NoProfile -File desktop/Create-PortableArchive.ps1 -ReleaseVersion 0.1.0-preview.6 -ApplicationVersion 0.1.0.5 -OutputDirectory desktop/artifacts/update-test-packages
```

次に、旧版として起動する開発専用ビルドを作成してZIPにします。

```powershell
dotnet publish desktop/ChatAgentBridge.Desktop/ChatAgentBridge.Desktop.csproj -c Release -p:Platform=x64 -p:CABPortable=true -p:CABPortableUpdateTest=true -r win-x64 --self-contained true -p:Version=0.1.0-preview.5 -p:AssemblyVersion=0.1.0.4 -p:FileVersion=0.1.0.4 -p:DebugType=None -p:DebugSymbols=false -o desktop/artifacts/updater-test-publish
pwsh -NoProfile -File desktop/Create-PortableArchive.ps1 -ExistingLayout desktop/artifacts/updater-test-publish -ReleaseVersion 0.1.0-preview.5 -ApplicationVersion 0.1.0.4 -OutputDirectory desktop/artifacts/update-test-packages
```

専用の配置・設定・更新ファイルを作成して起動します。

```powershell
pwsh -NoProfile -File desktop/New-PortableUpdateTryout.ps1 -TestArchive desktop/artifacts/update-test-packages/ChatAgentBridge.Portable-0.1.0-preview.5-win-x64.zip -UpdateArchive desktop/artifacts/update-test-packages/ChatAgentBridge.Portable-0.1.0-preview.6-win-x64.zip
```

「情報 → 更新を確認 → 更新をダウンロード → 更新して再起動」で操作します。情報画面のアセンブリ版は `0.1.0.4` → `0.1.0.5`、更新判定のリリース番号は `preview.5` → `preview.6` に変わります。

既定の試験場所は `%LOCALAPPDATA%/ChatAgentBridge/UpdateTryouts/portable-update-tryout-<GUID>` です。通常の利用設定はコピーせず、その中の `ChatAgentBridge.Portable/Data` を明示指定して起動します。オンボーディングは「後で設定」として扱い、利用条件への同意やチャット認証は作りません。料金表の自動取得も無効にします。

更新前の試験版を閉じた場合は、その試験場所の `Start-Tryout.ps1` から再起動してください。専用ビルドのexeを直接起動するのではなく、ローカル更新ファイルの指定を渡すこのスクリプトを使います。更新後にもう一度最初から試すには、上の `New-PortableUpdateTryout.ps1` を再実行します。毎回新しいフォルダーを作り、既存の試験結果を上書きしません。

`-WorkingDirectory` で別の配置先も指定できます。実際のアップデータと同様、シンボリックリンク・ジャンクションを経由した配置は拒否します。

## 試験ビルドの範囲

`CABPortable=true` と `CABPortableUpdateTest=true` の両方を指定したビルドだけに、ローカル更新通信の実装を含めます。通常のビルドにはクラス自体が含まれず、環境変数による更新先変更もありません。通常版と試験版の中間生成物は別フォルダーに分離しています。試験用ZIPは配布しないでください。

試験版は `CAB_PORTABLE_UPDATE_TEST_DIRECTORY` にある `releases.json` と `assets/<ZIP名>` を読みます。既存のGitHub APIとリリースURLに対するGETだけをローカルのファイル読み込みに置き換え、他の通信先へのフォールバックは行いません。リリース番号比較、SHA-256検証、ZIP内のメタデータ検証、アップデータによる置き換え・再起動・バックアップは通常の実装を通します。

この試験はGitHubへの実HTTP通信、プロキシ環境や転送速度の確認を含みません。2026-10-21時点の公開済み `preview.5` ZIPには現在の更新機構が要求する `portable-release.json` 等が含まれておらず、更新完了までの試験には対応する新しいZIPが必要です。

## 2026-10-21の確認結果

`PortableUpdateTryoutUiE2E.ps1` に両ZIPを渡し、次の6シナリオが成功しました。

- プレビュー版を除外した場合に更新候補が表示されない。
- 旧版から新しいローカルリリースを検出できる。
- ZIPの1バイトを壊すとSHA-256検証で拒否し、起動中の版を置き換えない。
- 正しいZIPを再ダウンロードして、適用待ちへ進める。
- 再起動確認をキャンセルすると、旧版とダウンロード済み状態を維持する。
- 確認後は実際の更新ヘルパーで終了・置き換え・再起動し、言語・テーマ・Data内の確認ファイルを保持する。更新先に試験用通信クラスは含まれない。

ヘルパーの結果は `Success=true`、`RolledBack=false`、`Version=0.1.0-preview.6` で、バックアップも残っています。通常構成と試験構成のReleaseビルド、関連するCoreの48テスト、PowerShell構文検査、差分レビューも成功しました。

画面と試験結果は `%LOCALAPPDATA%/ChatAgentBridge/UpdateTryouts/portable-update-tryout-5de384c99e3445c69f3631d953205f24` のPNGと `ui-results.json`、ヘルパーの結果は `%LOCALAPPDATA%/ChatAgentBridge/Updates/4f847ac6512748ed9daab14184cf5b19/update-result.json` にあります。公開やGitHubへのpushは行っていません。
