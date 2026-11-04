# v1.0.3 リリース検証記録

更新日: 2026-11-05

## ソースとパッケージ

- 対象ブランチ: `release/v1.0.3`
- 検証対象コミット: `e565491e2faf134fc451d477bf1c881588185788`
- ローカル更新試験用ポータブル x64 ZIP: `ChatAgentBridge.Portable-1.0.3-win-x64.zip`、131,199,971 bytes、SHA-256 `f475719020b0abb9d1178db3ddc8bb03b2c2b8e2635a24ab71a8bf24f8c1c202`
- ローカル検証用ポータブル ARM64 ZIP: `ChatAgentBridge.Portable-1.0.3-win-arm64.zip`、125,005,788 bytes、SHA-256 `0dc6c05c39b633765905b79101e309fa0c9e5a88ace198892026b3b4d3ef2321`
- 両 ZIP の `portable-release.json` は schema 1、releaseVersion `1.0.3`、各アーキテクチャを宣言。`portable-files.json` と `SHA256SUMS.txt` を含み、ポータブル更新の本番アーカイブステージャーによる検証に成功。
- Store 提出用パッケージを x64 / ARM64、version `1.0.3.0` で生成。`.msixupload` とテスト用 `.msix` を含む。
  - x64 `.msixupload`: 66,215,251 bytes、SHA-256 `be192afc325e823fda955ad3e7116db64877ee696f4955ae958efce66e7a3965`
  - ARM64 `.msixupload`: 62,867,939 bytes、SHA-256 `ae996a6f4d78ab68c9e53795e2774b6daa9d28583e7c4bd4761194b02c79ca4a`

## ポータブル更新の実機確認

Windows 上で 1.0.2 のポータブル版から 1.0.3 へ更新し、アプリ再起動まで確認した。記録先は `.artifacts/release-v1.0.3/native-update-test` と、Windows のローカル更新キャッシュにある `update-result.json`。

- 更新結果: `Success=true`、`RolledBack=false`、`Version=1.0.3`
- 再起動後の実行ファイル: FileVersion `1.0.3.0`、ProductVersion `1.0.3+e565491e2faf134fc451d477bf1c881588185788`
- 設定・WorkspaceRoot、ダークテーマ、合成した `history-sentinel.txt` が更新後も保持された。
- 検証データは専用 Data フォルダーを使用。`AutoConnectProvider=none` とし、接続資格情報は含めていない。

## CI と公開状況

- リリース修正に関するローカル回帰テスト: 20/20 成功（`.artifacts/release-v1.0.3/validation/release-ci-fixes.trx`）。CI run `41867141949` では Core 1596/1596、Transports 295/295（合計1891件、失敗・スキップなし）、デスクトップビルド、ライセンス通知、静的検査が成功。UI smoke、両CPUのパッケージ生成、公開成功・失敗経路のモック検証も成功（run 全体が success）。
- Store パッケージ CI run `41867147425`: x64 / ARM64 の両方成功。
- GitHub Release: [v1.0.3](https://github.com/mmiyaji/chat-agent-bridge/releases/tag/v1.0.3) を 2026-11-05 01:54 JST に公開済み。配信 CI [41869268232](https://github.com/mmiyaji/chat-agent-bridge/actions/runs/41869268232) は検査・UI・両CPUパッケージ・検証・公開・Pages 配信まですべて成功。
- Microsoft Store: Chrome から Submission 6（`1152921505702315068`）を提出済み。x64 / ARM64 の `1.0.3.0` が検証済みで、提出後に `Update in certification` を確認。審査通過後の自動公開を選択。

- `main` へ統合・push 済み。タグ `v1.0.3` は検証記録だけを追加した `60352f2c1b52a63929adc116debd69fd52719688` を指す。配信 CI run `41869268232` は成功。ストア提出物とコード差分はなく、GitHub の配布物はタグから改めてビルドした。

## 公開ファイルと更新通知

タグからCIで作成した公開ファイルは、ローカル更新試験用とソースコードが同一で、ビルドコミット情報などが異なるためチェックサムは別になる。

| 公開ZIP | サイズ | SHA-256 |
| --- | ---: | --- |
| x64 | 131,200,120 bytes | `a7cb98f6f1c46132dd65d1eaa864187fb9a27844f8ea046fdcda9711e2552b79` |
| ARM64 | 125,005,104 bytes | `a08288b2cdb1ab4c16e4257f57da6ed04c5ad3f489f67d6501fd65476c0420ee` |

公開後、GitHub APIで非draft・非previewとZIP2件＋チェックサム2件を確認。実際の `updates/stable.json` と `updates/preview.json` を読み、両CPU向けv1.0.3のURL・サイズ・SHA-256がGitHub Releaseの値と一致することを確認した。観測JSONは `.artifacts/release-v1.0.3/evidence` に保存。旧版1.0.2からの適用・再起動試験ではローカル検証用ZIPを使用した。

ストア版は審査提出まで完了している。Microsoftの審査・公開完了は未確認のため、公開済みとは扱わない。提出画面の証跡は `.artifacts/release-v1.0.3/store-submitted.jpg`。
