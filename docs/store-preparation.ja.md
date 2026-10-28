# Microsoft Store公開の準備

現行のポータブル版の公開手順は [RELEASE.md](../RELEASE.md) を参照してください。Store用MSIXの提出は別の作業です。

## パッケージの命名方針

希望する名前空間は `ruhenheim.org` です。Storeビルドでは **`org.ruhenheim.ChatAgentBridge`** をパッケージ名として使います。これは希望値であり、Partner Centerで予約・割り当て済みであることは未確認です。提出前に、この製品のIdentityと一致することをPartner Centerで確認してください。

2026-10-28に、隣接するリポジトリのマニフェストを確認しました。

| アプリ | Package/Identity/Name | PublisherDisplayName |
| --- | --- | --- |
| Kiritori | `mmiyaji.kiritori` | `mmiyaji` |
| Clipton | `mmiyaji.Clipton` | `mmiyaji` |

両アプリの `Package/Identity/Publisher` は `CN=663C5D03-791A-4B2C-8509-629FA1CA125A` です。参照元はKiritoriの `KiritoriPackage/Package.appxmanifest` とCliptonの `packaging/Clipton.Package/Package.appxmanifest` です。

既存アプリはドメイン形式を採用していません。今回は新しい命名方針です。Publisherはドメイン名から作らず、Partner Centerでこの製品に表示される正式な値を使用します。既存アプリの値は照合用の参考情報です。

最終的な `Name`・`Publisher`・`PublisherDisplayName` は、[Partner Centerの製品ID](https://learn.microsoft.com/en-us/windows/apps/publish/view-app-identity-details)と一致させます。候補名をマニフェストへ書くだけではStoreへの関連付けは完了しません。

現行の開発用マニフェストは `ChatAgentBridge.WinUI.Development` / `CN=ChatAgentBridgeDevelopment` のままです。ポータブル版のIdentityや更新経路、通知登録、実行ファイル名、`ChatAgentBridge.WinUI` データフォルダーも変更しません。Store用スクリプトは開発用マニフェストを基に一時マニフェストを生成し、Store用のIdentityだけを差し替えます。

## Store提出パッケージのビルド

`desktop/Create-StorePackage.ps1` は `CABStore` を明示したMSIXビルドです。`PackageName` は上記希望値に固定し、`Publisher` と `PublisherDisplayName` はPartner Centerでこの製品に割り当てられた正式値を必須入力にします。隣接アプリで使われている `CN=663C5D03-791A-4B2C-8509-629FA1CA125A` / `mmiyaji` は照合用の参考値にすぎず、この製品の正式値として扱いません。

```powershell
./desktop/Create-StorePackage.ps1 `
  -PackageName org.ruhenheim.ChatAgentBridge `
  -Publisher 'CN=<Partner CenterのPublisher>' `
  -PublisherDisplayName '<Partner CenterのPublisherDisplayName>' `
  -PackageVersion 1.2.3.0 `
  -Architecture x64
```

ARM64は `-Architecture ARM64` を指定します。PackageVersionはStore形式の `major.minor.build.0` で、majorは1以上、最初の3項目は65535以下です（例: `1.2.3.0`）。値のルールは[Microsoftのパッケージバージョン説明](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/package-version-numbering)を参照してください。提出パッケージは `desktop/artifacts/store/<version>/<architecture>/<build-id>` に分離して作成します。署名を無効にしてStoreアップロード形式を生成し、開発用証明書の作成・インストール・信頼変更は行いません。ビルド後、MSIX内のIdentity・CPU・権限・自己完結.NETランタイムを検査します。Partner Centerへアップロードする前にマニフェストのIdentityを確認してください。`-ValidateOnly` は同じ必須入力で一時マニフェストとIdentity変換だけを検証し、ビルドしません。CIは設定ガードと既存の開発用Identityを実行検証します。Storeへの提出・公開はこのビルドやCIでは行いません。

## GitHub Actionsでビルドする

Actionsの **Store package (build only)** を手動実行し、Store用バージョン、Partner CenterのPublisherとPublisherDisplayNameを指定します。パッケージ名の既定値は `org.ruhenheim.ChatAgentBridge` です。

x64とARM64を生成・検査し、`.msixupload` だけをActions artifactとして7日間保持します。GitHub ReleaseやStoreへのアップロード・公開は行いません。実行時の入力は環境変数経由で渡し、PowerShellのコードへ埋め込みません。

ローカルでの検証用Publisherを使ったパッケージは提出しないでください。Store版の実機インストール・更新と通知操作は、正式な製品への関連付け後に限定配布で確認します。ARM64のクロスビルドは実機確認の代わりにはなりません。

## 公開までの確認順序

2026-10-28に検証用Publisherでx64・ARM64の署名なし `.msixupload` を生成しました。内部のパッケージ名・バージョン・CPU、`runFullTrust`、自己完結ランタイムと不要ファイルの混入を検査済みです。設定ガードの正常系・拒否系、ポータブル版のx64ビルド、保存先選択の回帰テストも成功しています。

これはビルド・内容検査の結果です。Storeでの関連付け、認定、インストール後の通知やCLI操作、GitHub上の新しい手動ワークフローの実行確認はまだ完了していません。ローカルではシンボル変換用SDKツール不足の警告が出たため、シンボルパッケージを生成しない構成です。

1. Partner Centerでアプリ名を予約し、この製品の正式な識別子を確認する。
2. Partner Centerの値を確認し、Store用MSIXを生成・検査する。
3. Store版ではStore経由で更新し、ポータブル版のZIP置換や任意の通知パッケージ登録を実行しないことを確認する。
4. 新規インストール・更新・アンインストール、通知の承認／拒否、トレイ、自動起動、外部CLIの実行を実機で検証する。
5. CLIの別途導入・認証が必要なこと、ローカルファイルへのアクセス、`runFullTrust`の必要性を掲載情報と審査手順に記載する。
6. 日英の掲載文・画像・プライバシーポリシーURLを用意し、認定テストと限定配布で確認してから一般公開する。

Store提出用MSIXはMicrosoftによって署名されます。ローカル検証用の署名と、Store外での署名付き配布は別途扱います。[Microsoftの公開ガイド](https://learn.microsoft.com/windows/apps/package-and-deploy/publish-first-app)を参照してください。
