# 構成図の鮮明化と拡大ビューアー（2026-10-20）

## 変更内容

初期設定ガイドと全般ページの構成図をPNGからSVGへ変更しました。元の図の内容・配置は維持し、文字をアウトライン化したベクター素材を使います。Windows側のSVG描画で扱えるよう、矢印のマーカーも通常のパスへ変換しています。

図全体を標準のボタンにし、「クリックして拡大」を表示しました。クリックやキーボード操作で、アプリ内に拡大ビューアーを開けます。

- 最初は図全体が収まる倍率で表示します。
- 「−」「＋」「全体表示」「100%」と現在の倍率を表示します。
- 拡大した図は縦横にスクロールできます。タッチのピンチ操作にも標準のScrollViewerを使います。
- 全体表示中にウィンドウの大きさを変えると、表示倍率を合わせ直します。
- 「閉じる」は標準ダイアログの右端に置き、閉じた後は元の図のボタンへフォーカスを戻します。
- 初回ガイドで言語を変更すると、プレビューとビューアーの図・操作名も即時に切り替わります。

縮小時も画面に合わせてSVGを描画します。ただし狭い画面で小さな文字を読むには、クリックして100%表示または拡大する操作が必要です。ズームの途中ではSVGの再描画サイズを変更せず、操作が落ち着いてから反映します。

## 素材の再生成

編集元は `docs/assets/overview/OverviewArchitecture.svg` と英語版です。Windows上で次を実行すると、アプリ用のアウトライン済みSVGを再生成できます。生成にはYu Gothic UIを使い、利用先のPCでのフォント解決を不要にしています。

```powershell
pwsh -NoProfile -File desktop/Convert-ArchitectureSvg.ps1
```

SVGの表示にはWinUIの[SvgImageSource](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.media.imaging.svgimagesource)を使い、[RasterizePixelWidthの論理ピクセル単位](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.media.imaging.svgimagesource.rasterizepixelwidth)で拡大率に応じた描画サイズを指定します。

## 検証

```powershell
pwsh -NoProfile -File desktop/ArchitectureViewerUiE2E.ps1 -AppDirectory <desktop/artifacts内のポータブル配置先>
```

専用の未設定データフォルダーから初回ガイドを表示し、日本語／英語、ライト／ダークの4条件で検証します。幅1160・720・500 DIPのガイドと、幅500 DIPの全般ページを対象にします。通常の設定・認証情報・チャット接続・AI実行は使用しません。

倍率の変更、全体表示、100%表示、横スクロール、開いたままのサイズ変更、操作ボタンの重なりと見切れ、閉じる操作とフォーカス復帰、同意状態の保持を確認します。日本語ライトでは、ガイド上で日本語→英語→日本語へ切り替え、再起動せずに英語の図を開けることも確認します。

17件のビューアー操作シナリオが成功しました。検証記録は `desktop/artifacts/architecture-viewer-e14548a8284949a6a1b3ac9de477412f` の `results.json` と各条件のPNGです。Releaseのx64ポータブル構成のビルド、日英リソース検査、PowerShell構文検査、SVG再生成の再現性、差分レビューも成功しました。ピンチ操作そのものの実機タッチ検証は含みません。

公開やGitHubへのpushは行っていません。
