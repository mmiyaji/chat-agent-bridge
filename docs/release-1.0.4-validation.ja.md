# v1.0.4-preview.1 検証記録

2026-11-06 時点の公開準備メモです。対象は `release/1.0.4-preview.1` で、Store 配布は行いません。

## 更新後のステージ整理

- 正常起動を確認した後、更新ヘルパーは計画ファイルで検証したステージ内の `.json` / `.txt` を整理します。任意の JSON/TXT、ログ形式、`logs` / `diagnostics` フォルダー、ログ・復旧記録は保護します。
- `dotnet test desktop/ChatAgentBridge.Core.Tests/ChatAgentBridge.Core.Tests.csproj -c Release --filter FullyQualifiedName~PortableUpdateCacheCleanupTests` は17件成功しました。テストには release metadata、file manifest、runtimeconfig、deps、third-party notices、checksums の整理と、ログ・未知ファイル・別のステージディレクトリの保護を含みます。
- 隔離した一時 plan を使う `CleanSuccessfulPayload` の直接呼び出しではステージが削除されました。

## 旧版からの更新試行

- GitHub Releases の `v1.0.3` x64 ZIP を取得し、ローカルSHA-256 `a7cb98f6f1c46132dd65d1eaa864187fb9a27844f8ea046fdcda9711e2552b79` が公開 asset digest と一致することを確認しました。
- その ZIP から作った隔離インストールをローカル `1.0.4-preview.1` x64 ZIP へ更新しました。v1.0.3 の更新ヘルパーで成功・ロールバック両方を実行し、成功時は `1.0.4-preview.1`、ロールバック時は `1.0.3` の起動、設定・履歴ファイルのハッシュ維持、バックアップと管理ファイルの挙動を確認しました。
- 旧ヘルパーは更新後も既知の JSON/TXT キャッシュメタデータを残しますが、その他のステージペイロードは削除します。プレビューZIP同梱の新ヘルパーと `ChatAgentBridge.Core.dll` を使う別の成功試験では、検証済みステージ全体を整理し、更新計画・結果・バックアップ記録を保持しました。新ヘルパーとCore DLLのSHA-256はZIPの `SHA256SUMS.txt` と一致しました。
- 元の `PortableUpdateSmokeTest.ps1` は `-WorkingDirectory` 配下にステージを作りますが、実際の整理ガードは `%LOCALAPPDATA%/ChatAgentBridge/Updates` の所有ルートだけを許可します。検証では `.artifacts/` 内の一時コピーのみを調整し、作業場所を一意なGUIDの所有ルートに移動しました。旧ヘルパー用コピーではステージ assertion を「非ログの旧ペイロードは削除し、旧版が残すJSON/TXTメタデータのみ残る」に変更しました。成功、ロールバック、設定・履歴のハッシュ、復旧確認は維持し、リポジトリの元スクリプトは変更していません。アプリ終了はその試験プロセスだけを停止し、ロールバックダイアログはCUAから読み、閉じました。
- 旧ZIP、ローカルpreview ZIP、試験ログと一時成果物は `.artifacts/release-1.0.4-preview.1/` にあります。旧ZIPの元ファイルは変更していません。CUAのダイアログAXには期待する英語メッセージが表示されましたが、スクリーンショット保存結果はダイアログの証跡にならない画像でした。

## CI

- `workflow_dispatch` CI run `42065799100` は release branch head `aef2237` で成功しました。`npm run check`、`npm test`、日英・設定チェック、Pythonチェック、リリース版数とStore設定チェック、Core/Transportテスト、portable build、third-party notice 検証、UI smoke と証跡アップロードがすべて成功しました。
- 手元の smoke 実行用一時コピーは `.artifacts/` のみで、cleanup guard や product source script は変更していません。実サービス接続・送信、ARM64実機確認は未実施です。

## 公開結果

- `v1.0.4-preview.1` は main commit `d68b4e61627c8379a31660e4ce29af76b2e0617c` から作成し、GitHub prerelease として公開しました。Microsoft Store への提出・公開はしていません。
- [GitHub preview release](https://github.com/mmiyaji/chat-agent-bridge/releases/tag/v1.0.4-preview.1) にある x64 ZIP は 131,244,392 bytes / SHA-256 `71e19c80fb4df4f3a5bf354c42c48f6c272ec8317f7f5145d54cceea63b45d95`、ARM64 ZIP は 125,050,626 bytes / SHA-256 `028d0e1505542d80aa32e7fed3bb9cfbb425ba2b57d25a4c8c34a0313eb4386c` です。x64 のダウンロード済みZIPでは GitHub digest と一致すること、内部の `portable-release.json` が版数・CPUを示すこと、`portable-files.json` / `SHA256SUMS.txt` および更新ヘルパーが含まれ、Core DLL と更新ヘルパーの内部 SHA-256 が一致することを確認しました。ARM64実機での起動は未確認です。
- GitHub Actions の main CI [42068204868](https://github.com/mmiyaji/chat-agent-bridge/actions/runs/42068204868)、Portable release [42068233497](https://github.com/mmiyaji/chat-agent-bridge/actions/runs/42068233497)、説明文更新後の Distribution data [42071406839](https://github.com/mmiyaji/chat-agent-bridge/actions/runs/42071406839) はすべて成功しました。
- 配信フィードの preview は `v1.0.4-preview.1` を先頭に含み、x64 / ARM64 のサイズとdigestが公開Releaseと一致します。stable フィードは変更されず、最新安定版は `v1.0.3` です。フィードはRelease本文を含まず、レビュー済みの日英リリースノートはGitHub Releaseに掲載されています。
