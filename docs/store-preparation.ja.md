# Microsoft Store公開の準備

現行のポータブル版の公開手順は [RELEASE.md](../RELEASE.md) を参照してください。Store用MSIXの提出は別の作業です。

## パッケージの正式Identity

Store登録で確認した正式Identityに合わせ、パッケージ名はStore版・任意のポータブル通知Identityとも **`mmiyaji.ChatAgentBridge`** に統一します。画面・Storeの表示名は **Chat Agent Bridge** です。以前検討していた `org.ruhenheim.ChatAgentBridge` の命名方針は撤回しました。実行ファイル名、既存のZIP名、保存先フォルダー名は利用者の更新互換性のため維持します。

| Partner Center項目 | 確認済みの値 |
| --- | --- |
| Product/Identity Name | `mmiyaji.ChatAgentBridge` |
| Store package display name | `Chat Agent Bridge` |
| Publisher | `CN=663C5D03-791A-4B2C-8509-629FA1CA125A` |
| PublisherDisplayName | `mmiyaji` |
| Store ID | `9N1LZK25FSGR` |
| Package Family Name | `mmiyaji.ChatAgentBridge_1gej6ajca64sg` |

Storeパッケージの初回提出バージョンは `1.0.0.0` です。

2026-10-28に、隣接するリポジトリのマニフェストも照合しました。

| アプリ | Package/Identity/Name | PublisherDisplayName |
| --- | --- | --- |
| Kiritori | `mmiyaji.kiritori` | `mmiyaji` |
| Clipton | `mmiyaji.Clipton` | `mmiyaji` |

両アプリの `Package/Identity/Publisher` は `CN=663C5D03-791A-4B2C-8509-629FA1CA125A` です。参照元はKiritoriの `KiritoriPackage/Package.appxmanifest` とCliptonの `packaging/Clipton.Package/Package.appxmanifest` です。

Publisherはドメイン名から作らず、この製品に割り当てられたPartner Centerの正式値を使用します。

表の `Name`・`Publisher`・`PublisherDisplayName` は、[Partner Centerの製品ID](https://learn.microsoft.com/en-us/windows/apps/publish/view-app-identity-details)で確認した正式値です。Store IDとPackage Family Nameも登録済みの製品値です。

現行の開発用マニフェストは `ChatAgentBridge.WinUI.Development` / `CN=ChatAgentBridgeDevelopment` のままです。通常のポータブル版の更新経路・通知登録・実行ファイル名・データ保存先は維持します。任意の署名付き通知Identityのパッケージ名は `mmiyaji.ChatAgentBridge` へ変更しますが、Publisherは署名との整合性のため `CN=mmiyaji` のままです（Store版とは別のパッケージファミリーです）。旧通知Identityが登録済みの場合は自動で移行・削除せず、新規登録を止めます。[任意の通知Identityの案内](../desktop/portable-identity/README.md)を参照してください。Store用スクリプトは開発用マニフェストを基に一時マニフェストを生成し、Store用のIdentityだけを差し替えます。

## Store提出パッケージのビルド

`desktop/Create-StorePackage.ps1` は `CABStore` を明示したMSIXビルドです。`PackageName` は上記正式値に固定し、`Publisher` と `PublisherDisplayName` はPartner Centerの正式値を必須入力にします。Storeパッケージの表示名は予約済みの `Chat Agent Bridge` に設定します。

```powershell
./desktop/Create-StorePackage.ps1 `
  -PackageName mmiyaji.ChatAgentBridge `
  -Publisher 'CN=663C5D03-791A-4B2C-8509-629FA1CA125A' `
  -PublisherDisplayName mmiyaji `
  -PackageVersion 1.0.0.0 `
  -Architecture x64
```

ARM64は `-Architecture ARM64` を指定します。PackageVersionはStore形式の `major.minor.build.0` で、majorは1以上、最初の3項目は65535以下です（例: `1.2.3.0`）。値のルールは[Microsoftのパッケージバージョン説明](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/package-version-numbering)を参照してください。提出パッケージは `desktop/artifacts/store/<version>/<architecture>/<build-id>` に分離して作成します。署名を無効にしてStoreアップロード形式を生成し、開発用証明書の作成・インストール・信頼変更は行いません。ビルド後、MSIX内のIdentity・CPU・権限・自己完結.NETランタイムを検査します。Partner Centerへアップロードする前にマニフェストのIdentityを確認してください。`-ValidateOnly` は同じ必須入力で一時マニフェストとIdentity変換だけを検証し、ビルドしません。CIは設定ガードと既存の開発用Identityを実行検証します。Storeへの提出・公開はこのビルドやCIでは行いません。

## Store版のランタイムと利用条件

Store版はWindows App SDKを共有し、.NETランタイムはアプリに同梱します。ポータブル版は両方を同梱する従来の構成を維持します。

- パッケージのOS下限はWindows 10 1809（build 17763）。推奨はサポート期間内のWindows 11です。下限の宣言は、すべての旧OSでの実機検証やMicrosoftのサポート継続を保証するものではありません。
- x64 / ARM64を用意し、Storeが端末に合うパッケージを選択します。利用者が.NET SDKや.NETランタイムを別途導入する必要はありません。
- ビルドされたMSIXの`PackageDependency`が、必要なWindows App Runtimeの名前・最低バージョン・発行元を宣言します。同じCPUで要件を満たす共有パッケージがあれば使用し、なければStore経由で取得します。Windows 11であっても、必要なランタイムが初めから入っているとは限りません。
- Windows App SDK 2.5.1での検証では、依存先は`Microsoft.WindowsAppRuntime.2`、最低バージョンは`2.5.1.0`です。1.8系だけでは条件を満たしません。SDK更新時は、生成されたMSIXの依存宣言を再確認します。
- 初回導入・不足する依存関係の更新にはStoreへの接続が必要です。必要な依存関係が登録済みなら、共有化自体は毎回のオンライン接続を要求しません。AIやチャットサービスの接続要件は別です。
- 組織のポリシーでStoreやMSIX依存パッケージの導入が制限されている環境では、管理者による配布・許可が必要になる場合があります。アプリがその制限を回避することはありません。
- 本体だけをサイドロードしても依存関係はそろいません。Store外の検証では対応する依存パッケージも別途配置する必要があります。

共有フレームワークの更新はStoreが管理します。.NETは同梱するため、その更新はアプリの再ビルド・配布で取り込みます。パッケージ本体は小さくなりますが、ランタイム未導入PCの初回ダウンロード全体が同じ量だけ減るとは限りません。

共有版の検証用`1.0.1.0`では、x64 / ARM64のビルドとMSIX内容検査が成功しました。圧縮済みパッケージはそれぞれ約65.7MB / 62.3MB、展開後は約179.1MB / 201.3MBです。Windows App Runtimeの依存宣言、ネイティブWinUIランタイムの非同梱、.NETランタイムの同梱、OS下限の維持を確認しています。Storeへのアップロード・インストール・旧版からの更新の実機試験は、この検証には含みません。

参考: [共有ランタイム版のMSIX配布](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-packaged-apps)、[.NET 10の対応OS](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。

## GitHub Actionsでビルドする

Actionsの **Store package (build only)** を手動実行し、Store用バージョン `1.0.0.0`、Publisher、PublisherDisplayNameを指定します。パッケージ名の既定値は `mmiyaji.ChatAgentBridge` です。

x64とARM64を生成・検査し、`.msixupload` だけをActions artifactとして7日間保持します。GitHub ReleaseやStoreへのアップロード・公開は行いません。実行時の入力は環境変数経由で渡し、PowerShellのコードへ埋め込みません。

ローカルでの検証用Publisherを使ったパッケージは提出しないでください。Store版の実機インストール・更新と通知操作は、正式な製品への関連付け後に限定配布で確認します。ARM64のクロスビルドは実機確認の代わりにはなりません。

## 公開までの確認順序

2026-10-28の初回ビルド検証では、検証用Publisherでx64・ARM64の署名なし `.msixupload` を生成しました。正式Identityを使った `1.0.0.0` のx64・ARM64パッケージも生成・内容検査し、Partner Centerへアップロードしました。両方の `Validated` を確認済みです。[初回申請の下書きと残作業](store-submission-draft.ja.md)を参照してください。

ビルド・内容検査はStoreへのアップロードや認定、インストール後の通知・CLI操作を確認するものではありません。ローカルではシンボル変換用SDKツール不足の警告が出たため、シンボルパッケージを生成しない構成です。

1. 正式Identityのx64・ARM64 `.msixupload` をPartner Centerへ登録する。
2. Store版ではStore経由で更新し、ポータブル版のZIP置換や任意の通知パッケージ登録を実行しないことを確認する。
3. 新規インストール・更新・アンインストール、通知の承認／拒否、トレイ、自動起動、外部CLIの実行を実機で検証する。
4. CLIの別途導入・認証が必要なこと、ローカルファイルへのアクセス、`runFullTrust`の必要性を掲載情報と審査手順に記載する。
5. 日英の掲載文・画像・プライバシーポリシーURLを用意し、認定テストと限定配布で確認してから一般公開する。

Store提出用MSIXはMicrosoftによって署名されます。ローカル検証用の署名と、Store外での署名付き配布は別途扱います。[Microsoftの公開ガイド](https://learn.microsoft.com/windows/apps/package-and-deploy/publish-first-app)を参照してください。
