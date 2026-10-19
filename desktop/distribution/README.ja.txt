Chat Agent Bridge
=================

この ZIP は自己完結型 MSIX アプリを Windows にインストールします。Microsoft Store 版ではなく、開発プレビューです。x64 と ARM64 に対応し、Windows 11 を推奨します。manifest の最低要件は Windows 10 version 1809（build 17763）です。ARM64 を選んでパッケージを作成できますが、すべての ARM64 端末で動作確認済みという意味ではありません。

別途 .NET ランタイムや Windows App SDK をインストールする必要はありません。この ZIP には MSIX とインストーラーが含まれ、単体起動できる EXE は含まれません。インストール後、スタートメニューから Chat Agent Bridge を起動してください。個人設定は ZIP に含めません。利用するエージェント CLI は別途インストールしてログインし、アプリで実行ファイル、モデル、作業フォルダーを設定してください。Slack または Mattermost との接続を使う場合も、アプリで別途設定します。

インストール前にリリースファイルを検証します。

    powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -ValidateOnly

スクリプトはパッケージのハッシュ、署名者の拇印、発行者証明書、プレビューの package identity を確認します。証明書を信頼する前に、表示された拇印を[公式 GitHub Releases ページ](https://github.com/mmiyaji/chat-agent-bridge/releases)で公開された値と照合してください。このプレビューは自己署名証明書を使うため、Windows の既定では信頼されません。

照合済みの発行者が信頼されていない場合、同梱の公開 CER を「ローカル コンピューター > 信頼されたユーザー」に登録します。展開した ZIP のフォルダーで管理者として PowerShell を開き、次を実行します。

    Import-Certificate -FilePath .\ChatAgentBridge.WinUI.Preview.cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'

公式リリースの拇印と一致する公開 CER だけを登録してください。秘密鍵を含む PFX は絶対にインポート・配布しないでください。管理者 PowerShell を閉じた後、通常の Windows ユーザーとしてインストールします。

    powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1

インストーラーは昇格せず、証明書ストアも変更しません。スタートメニューからアプリを起動できます。後続版を入手したら、ファイルを検証してそのインストーラーを再実行します。同じ package identity の新しいバージョンが既存のプレビューを更新します。アンインストールするには、Windows の「設定 > アプリ > インストールされているアプリ」で Chat Agent Bridge を選択します。このプレビュー証明書を使わなくなったら、「ローカル コンピューター > 信頼されたユーザー」から削除してください。
