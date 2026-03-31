# Chat Agent Bridge

- Languages: [English](README.md) / [日本語](README.ja.md)

Chat Agent Bridge は、チャットスレッドからローカル開発エージェントを継続的に操作しつつ、手元マシンの作業文脈を維持するためのローカル bridge サービスです。

## できること

- チャットスレッドをローカルの agent session に紐付ける
- 同じマシン上で repo の状態、未コミット変更、ローカルツール状態を維持する
- Slack と Mattermost の chat adapter を使う
- Codex と Claude Code の agent adapter を使う
- 承認、監査ログ、repo 切替、admin 画面を提供する
- Mattermost は `bot` モードと `self` モードの両方をサポートする

## クイックスタート

1. 依存をインストールします。

```powershell
npm install
npm run check
```

2. 最低限の環境変数を設定します。

```powershell
$env:CAB_ALLOWED_DIRS="C:\Users\mail\Documents\git"
$env:CAB_DEFAULT_WORKDIR="C:\Users\mail\Documents\git\chat-agent-bridge"
```

3. bridge を起動します。

```powershell
npm run dev
```

または detached service として起動します。

```powershell
npm run service:start
npm run service:status
```

## よく使う service コマンド

```powershell
npm run service:start
npm run service:status
npm run service:restart
npm run service:restart:build
npm run service:stop
```

## ドキュメント

- インストールと基本設定: [docs/install.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/install.md)
- Slack 設定: [docs/slack-setup.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/slack-setup.md)
- Mattermost 設定: [docs/mattermost-setup.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/mattermost-setup.md)
- 運用と service 管理: [docs/operations.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/operations.md)
- テストと E2E: [docs/testing.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/testing.md)
- アーキテクチャ: [docs/architecture.md](/C:/Users/mail/Documents/git/chat-agent-bridge/docs/architecture.md)

## 補足

- bridge は既定で `http://<bridge-host>:8788/admin` に admin サーバーを立てます
- 初期設定は `http://<bridge-host>:8788/admin/onboarding` から進められます
- Codex session はローカルに入っている `codex` CLI を使い、既存のローカルログインを再利用できます
- bridge は Codex 子プロセスに `CHAT_AGENT_BRIDGE=1` を注入するので、外部 hook 側で重複通知を抑制できます
- Mattermost の `MATTERMOST_ACTOR_MODE=self` は本人トークン運用向けで、self-DM のみを受け付け、その self-DM 内でもメンションが必要です
