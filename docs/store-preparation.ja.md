# Microsoft Store公開の準備

現行のポータブル版の公開手順は [RELEASE.md](../RELEASE.md) を参照してください。Store用MSIXの提出は別の作業です。

## パッケージの命名方針

希望する名前空間は `ruhenheim.org` です。逆ドメイン表記の **`org.ruhenheim.ChatAgentBridge`** をパッケージ名の候補とします。これは未確定の候補で、Storeで予約・割り当て済みの識別子ではありません。

2026-10-28に、隣接するリポジトリのマニフェストを確認しました。

| アプリ | Package/Identity/Name | PublisherDisplayName |
| --- | --- | --- |
| Kiritori | `mmiyaji.kiritori` | `mmiyaji` |
| Clipton | `mmiyaji.Clipton` | `mmiyaji` |

両アプリの `Package/Identity/Publisher` は `CN=663C5D03-791A-4B2C-8509-629FA1CA125A` です。参照元はKiritoriの `KiritoriPackage/Package.appxmanifest` とCliptonの `packaging/Clipton.Package/Package.appxmanifest` です。

既存アプリはドメイン形式を採用していません。今回の候補は新しい命名方針です。Publisherはドメイン名から作らず、Partner Centerでこの製品に表示される正式な値を使用します。既存アプリの値は照合用の参考情報です。

最終的な `Name`・`Publisher`・`PublisherDisplayName` は、[Partner Centerの製品ID](https://learn.microsoft.com/en-us/windows/apps/publish/view-app-identity-details)と一致させます。候補名をマニフェストへ書くだけではStoreへの関連付けは完了しません。

現行の開発用・ポータブル版のIdentity、通知登録、データ保存先は、この方針の記録だけでは変更しません。

## 公開までの確認順序

1. Partner Centerでアプリ名を予約し、この製品の正式な識別子を確認する。
2. Store用MSIXのマニフェストとビルド・CIを整備する。
3. Store版ではStore経由で更新し、ポータブル版のZIP置換や任意の通知パッケージ登録を実行しないことを確認する。
4. 新規インストール・更新・アンインストール、通知の承認／拒否、トレイ、自動起動、外部CLIの実行を実機で検証する。
5. CLIの別途導入・認証が必要なこと、ローカルファイルへのアクセス、`runFullTrust`の必要性を掲載情報と審査手順に記載する。
6. 日英の掲載文・画像・プライバシーポリシーURLを用意し、認定テストと限定配布で確認してから一般公開する。

Store提出用MSIXはMicrosoftによって署名されます。ローカル検証用の署名と、Store外での署名付き配布は別途扱います。[Microsoftの公開ガイド](https://learn.microsoft.com/windows/apps/package-and-deploy/publish-first-app)を参照してください。
