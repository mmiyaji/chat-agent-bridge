/**
 * Primary operations UI for connecting a chat workspace to a local coding agent.
 * It deliberately uses plain HTML, CSS and browser APIs so the bridge has no
 * additional frontend runtime dependency.
 */
import { workbenchStyle } from "./workbench-style.js";

export function renderControlCenterPage(): string {
  return `<!doctype html>
<html lang="ja">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <meta name="color-scheme" content="light" />
    <title>Chat Agent Bridge | コントロールセンター</title>
    <style>
      :root { font-family: "Yu Gothic UI", "Meiryo", system-ui, sans-serif; color: #182b27; background: #f4f7f5; font-synthesis: none; }
      * { box-sizing: border-box; }
      [hidden] { display: none !important; }
      body { margin: 0; min-width: 320px; background: #f4f7f5; color: #182b27; }
      button, input, select, textarea { font: inherit; }
      button, a, input, select, textarea { accent-color: #13735e; }
      button:focus-visible, a:focus-visible, input:focus-visible, select:focus-visible, textarea:focus-visible { outline: 3px solid #57b89d; outline-offset: 2px; }
      .shell { display: grid; grid-template-columns: 248px minmax(0, 1fr); min-height: 100vh; }
      .sidebar { position: sticky; top: 0; height: 100vh; padding: 26px 18px; background: #fff; border-right: 1px solid #dce7e2; display: flex; flex-direction: column; gap: 26px; }
      .brand { display: flex; gap: 12px; align-items: center; padding: 0 8px; }
      .brand-mark { width: 38px; height: 38px; border-radius: 12px; display: grid; place-items: center; color: #fff; background: #13735e; font-size: 17px; font-weight: 800; }
      .brand strong { display: block; font-size: 14px; letter-spacing: .01em; }
      .brand small { display: block; margin-top: 3px; color: #6d807a; font-size: 11px; }
      .nav-label { padding: 0 12px; color: #80918b; text-transform: uppercase; font-size: 10px; font-weight: 750; letter-spacing: .12em; }
      .nav { display: grid; gap: 5px; }
      .nav button { border: 0; background: transparent; border-radius: 10px; width: 100%; text-align: left; color: #50635d; padding: 11px 12px; cursor: pointer; display: flex; align-items: center; gap: 11px; font-weight: 620; }
      .nav button:hover { background: #f2f7f4; color: #174b3e; }
      .nav button[aria-current="page"] { background: #e8f4ef; color: #11664f; }
      .nav-icon { width: 19px; text-align: center; font-size: 17px; }
      .side-bottom { margin-top: auto; border-top: 1px solid #e6ede9; padding: 18px 8px 0; color: #70817b; font-size: 12px; line-height: 1.6; }
      .side-bottom a { color: #16735c; text-decoration: none; }
      main { min-width: 0; padding: 34px clamp(18px, 4vw, 58px) 56px; }
      .content { max-width: 1240px; margin: 0 auto; }
      .topline { display: flex; align-items: center; justify-content: space-between; gap: 20px; margin-bottom: 26px; }
      .eyebrow { color: #50736a; font-size: 12px; font-weight: 750; letter-spacing: .08em; }
      h1 { margin: 5px 0 0; font-size: clamp(25px, 3vw, 34px); letter-spacing: -.035em; }
      .top-actions { display: flex; align-items: center; gap: 10px; }
      .refresh-note { color: #758780; font-size: 12px; }
      .button { border: 1px solid #c9d8d1; border-radius: 9px; background: #fff; color: #24453b; padding: 9px 14px; cursor: pointer; font-weight: 680; }
      .button:hover { background: #f5faf7; }
      .button.primary { background: #13735e; border-color: #13735e; color: white; }
      .button.primary:hover { background: #0f624f; }
      .button.danger { color: #9c4136; border-color: #e4c7c1; }
      .button:disabled { cursor: wait; opacity: .55; }
      .page { display: none; }
      .page.is-active { display: block; }
      .intro { display: flex; justify-content: space-between; align-items: end; gap: 20px; margin: 0 0 20px; }
      .intro h2 { margin: 0; font-size: 19px; }
      .intro p { max-width: 700px; margin: 7px 0 0; color: #697a74; line-height: 1.65; font-size: 14px; }
      .grid { display: grid; gap: 16px; }
      .status-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .card { background: #fff; border: 1px solid #dce7e2; border-radius: 14px; padding: 20px; min-width: 0; box-shadow: 0 2px 5px rgb(28 68 55 / 3%); }
      .card-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 12px; }
      .card-title { display: flex; align-items: center; gap: 11px; }
      .service-mark { width: 40px; height: 40px; border-radius: 12px; background: #eff6f2; display: grid; place-items: center; color: #16735d; font-size: 18px; font-weight: 800; }
      .card h3 { margin: 0; font-size: 16px; }
      .card-head p { margin: 4px 0 0; color: #70817a; font-size: 12px; }
      .state { border-radius: 999px; padding: 5px 9px; background: #eef2f0; color: #5e7069; font-size: 11px; font-weight: 750; white-space: nowrap; }
      .state[data-state="connected"] { background: #e4f4ec; color: #176b4e; }
      .state[data-state="connecting"] { background: #fff4df; color: #88601b; }
      .state[data-state="error"] { background: #fae9e7; color: #a23d34; }
      .state[data-state="listening"] { background: #e7f1fa; color: #37688d; }
      .card-detail { margin: 17px 0; min-height: 44px; color: #536760; font-size: 13px; line-height: 1.6; overflow-wrap: anywhere; }
      .card-actions { display: flex; flex-wrap: wrap; gap: 8px; }
      .stats { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; margin-top: 16px; }
      .stat { background: #fff; border: 1px solid #dce7e2; border-radius: 12px; padding: 15px 17px; }
      .stat span { display: block; color: #72827c; font-size: 12px; }
      .stat strong { display: block; margin-top: 6px; font-size: 24px; letter-spacing: -.03em; }
      .section { margin-top: 25px; }
      .section-head { display: flex; justify-content: space-between; align-items: center; gap: 16px; margin: 0 0 12px; }
      .section-head h2 { margin: 0; font-size: 16px; }
      .section-head a { color: #13735e; text-decoration: none; font-size: 13px; font-weight: 650; }
      .session-list { display: grid; gap: 9px; }
      .session-row { display: grid; grid-template-columns: minmax(0,1.7fr) minmax(110px,.7fr) minmax(120px,.7fr) auto; align-items: center; gap: 14px; border: 1px solid #dce7e2; border-radius: 11px; background: #fff; padding: 13px 16px; }
      .session-row strong { display: block; font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
      .session-row small { display: block; margin-top: 4px; color: #788982; font-size: 11px; }
      .session-state { color: #53675f; font-size: 12px; }
      .session-state[data-status="completed"] { color: #1c7859; }
      .session-state[data-status="failed"] { color: #a23d34; }
      .session-state[data-status="running"] { color: #8a671c; }
      .session-state[data-status="waiting_for_approval"] { color: #87581a; }
      .form-layout { display: grid; grid-template-columns: minmax(0, 1.25fr) minmax(260px, .75fr); gap: 16px; align-items: start; }
      .form-stack { display: grid; gap: 16px; }
      .field-grid { display: grid; grid-template-columns: repeat(2,minmax(0,1fr)); gap: 14px; margin-top: 17px; }
      .field { display: grid; gap: 6px; min-width: 0; }
      .field.full { grid-column: 1/-1; }
      .field label { font-size: 12px; color: #53675f; font-weight: 700; }
      .field input, .field select, .field textarea { width: 100%; min-width: 0; border: 1px solid #cbd9d2; border-radius: 8px; background: #fff; color: #1e332c; padding: 10px 11px; }
      .field textarea { min-height: 76px; resize: vertical; }
      .field input::placeholder, .field textarea::placeholder { color: #93a19b; }
      .hint { margin: 7px 0 0; color: #788982; font-size: 11px; line-height: 1.55; }
      .subhead { margin: 0; font-size: 14px; }
      .provider-fields[hidden] { display: none; }
      .save-row { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin-top: 18px; }
      .message { min-height: 20px; color: #526b60; font-size: 12px; }
      .sidebar, .form-stack { min-width: 0; }
      .message, .hint, .notice { overflow-wrap: anywhere; }
      .card-title { min-width: 0; }
      .card-head { flex-wrap: wrap; }
      .message[data-kind="error"] { color: #a23d34; }
      .message[data-kind="success"] { color: #176b4e; }
      .notice { border: 1px solid #dce7e2; background: #f7faf8; border-radius: 10px; padding: 13px 14px; color: #61736c; line-height: 1.6; font-size: 12px; }
      .notice strong { display: block; color: #2b4b40; margin-bottom: 4px; }
      .notice.warning { border-color: #ead9b4; background: #fff9ed; }
      .filters { display: flex; gap: 8px; flex-wrap: wrap; }
      .filters select, .filters input { border: 1px solid #cbd9d2; border-radius: 8px; padding: 8px 10px; background: #fff; color: #1e332c; }
      .table-wrap { width: 100%; overflow-x: auto; border: 1px solid #dce7e2; border-radius: 12px; background: #fff; }
      table { border-collapse: collapse; min-width: 680px; width: 100%; text-align: left; }
      th { color: #70817a; background: #f7faf8; font-weight: 700; font-size: 11px; }
      th, td { padding: 12px 14px; border-bottom: 1px solid #e8eeeb; }
      td { font-size: 12px; color: #40574e; }
      tr:last-child td { border-bottom: 0; }
      td strong { color: #203b31; }
      .empty, .load-error { border: 1px dashed #cad9d2; border-radius: 12px; background: #fff; padding: 24px; color: #74857e; text-align: center; font-size: 13px; line-height: 1.6; }
      .load-error { border-color: #e1c3bd; color: #963e35; }
      .load-line { color: #71837b; padding: 18px 2px; font-size: 13px; }
      .footer { border-top: 1px solid #dce7e2; margin-top: 35px; padding-top: 16px; color: #84928d; font-size: 11px; display: flex; gap: 16px; justify-content: space-between; }
      .footer a { color: #4d7467; }
      @media (max-width: 850px) { .shell { grid-template-columns: 1fr; } .sidebar { position: static; width: 100%; height: auto; padding: 12px 16px; border-right: 0; border-bottom: 1px solid #dce7e2; gap: 11px; } .brand { padding: 0 3px; } .nav-label, .side-bottom { display: none; } .nav { display: flex; overflow-x: auto; } .nav button { width: auto; white-space: nowrap; padding: 8px 10px; } main { padding-top: 22px; } }
      @media (max-width: 680px) { .topline { align-items: start; } .top-actions { flex-direction: column; align-items: end; } .refresh-note { display: none; } .status-grid, .form-layout { grid-template-columns: 1fr; } .stats { gap: 8px; } .stat { padding: 12px; } .stat strong { font-size: 20px; } .session-row { grid-template-columns: minmax(0,1fr) auto; gap: 8px 12px; } .session-row .session-meta { grid-column: 1; } .session-row .session-state { grid-column: 2; grid-row: 1; } .session-row .session-open { grid-column: 2; grid-row: 2; } .field-grid { grid-template-columns: 1fr; } .field.full { grid-column: auto; } .intro { display: block; } .filters { margin-top: 12px; } }
      @media (max-width: 680px) { .nav button { flex-shrink: 0; } .topline { flex-wrap: wrap; } }
      @media (prefers-reduced-motion: no-preference) { .button, .nav button { transition: background-color .14s ease, border-color .14s ease; } }
      ${workbenchStyle}
    </style>
  </head>
  <body>
    <div class="shell">
      <aside class="sidebar" aria-label="メインナビゲーション">
        <div class="brand"><div class="brand-mark" aria-hidden="true">橋</div><div><strong>Chat Agent Bridge</strong><small>コントロールセンター</small></div></div>
        <div class="nav-label">ワークスペース</div>
        <nav class="nav" aria-label="画面">
          <button type="button" data-page="overview" aria-current="page"><span class="nav-icon" aria-hidden="true">⌂</span>概要</button>
          <button type="button" data-page="connections"><span class="nav-icon" aria-hidden="true">⇄</span>接続と設定</button>
          <button type="button" data-page="sessions"><span class="nav-icon" aria-hidden="true">▤</span>会話セッション</button>
        </nav>
        <div class="side-bottom">設定はこのPCに保存されます。トークンは画面に再表示しません。<br /><a href="/admin/legacy">旧ダッシュボード</a> · <a href="/admin/onboarding">初期セットアップ</a></div>
      </aside>
      <main><div class="content">
        <header class="topline"><div><div class="eyebrow">LOCAL OPERATIONS</div><h1 id="page-title">概要</h1></div><div class="top-actions"><span class="refresh-note" id="last-updated" aria-live="polite">状態を読み込み中</span><button class="button" id="refresh-button" type="button">更新</button></div></header>
        <div id="global-message" class="message" role="status" aria-live="polite"></div>

        <section class="page is-active" id="page-overview" aria-labelledby="page-title">
          <div class="intro"><div><h2>チャットとエージェントの稼働状況</h2><p>Slack または Mattermost の会話を、ローカルのコーディングエージェントへつなぎます。ここでは接続状態と、会話ごとの実行状況を確認できます。</p></div></div>
          <div class="notice section" id="setup-guide" role="status" aria-live="polite" data-testid="setup-guide"><strong id="setup-title">接続を確認しています</strong><p id="setup-detail"></p><button class="button" type="button" data-goto="connections" id="setup-action">設定画面を開く</button></div>
          <div class="grid status-grid section">
          <article class="card" aria-labelledby="overview-chat-title"><div class="card-head"><div class="card-title"><div class="service-mark" aria-hidden="true">話</div><div><h3 id="overview-chat-title">チャット</h3><p id="overview-chat-name">読み込み中</p></div></div><span class="state" id="overview-chat-state" data-testid="overview-chat-state" data-state="unknown">確認中</span></div><div class="card-detail" id="overview-chat-detail">接続状態を取得しています。</div><div class="card-actions"><button class="button primary" type="button" data-goto="connections">接続を管理</button></div></article>
            <article class="card" aria-labelledby="overview-agent-title"><div class="card-head"><div class="card-title"><div class="service-mark" aria-hidden="true">AI</div><div><h3 id="overview-agent-title">コーディングエージェント</h3><p id="overview-agent-name">読み込み中</p></div></div><span class="state" id="overview-agent-state" data-testid="overview-agent-state" data-state="unknown">確認中</span></div><div class="card-detail" id="overview-agent-detail">エージェントの状態を取得しています。</div><div class="card-actions"><button class="button primary" type="button" data-goto="connections">設定を管理</button></div></article>
          </div>
          <div class="stats" aria-label="セッション数"><div class="stat"><span>すべてのセッション</span><strong id="stat-sessions" data-testid="stat-sessions">—</strong></div><div class="stat"><span>実行中</span><strong id="stat-running" data-testid="stat-running">—</strong></div><div class="stat"><span>承認待ち</span><strong id="stat-approvals" data-testid="stat-approvals">—</strong></div></div>
          <section class="section"><div class="section-head"><h2>最近の会話</h2><button class="button" type="button" data-goto="sessions">セッション一覧</button></div><div id="recent-sessions" class="session-list" data-testid="recent-sessions"><div class="load-line">セッションを読み込み中…</div></div></section>
          <section class="section"><div class="section-head"><h2>確認が必要な項目</h2><a href="#" data-goto="sessions">セッションを確認</a></div><div id="overview-alerts" class="grid"><div class="load-line">状態を読み込み中…</div></div></section>
        </section>

        <section class="page" id="page-connections" aria-labelledby="page-title">
          <div class="intro"><div><h2>接続と実行環境</h2><p>接続先と使うエージェントを選び、設定を保存してからそれぞれ明示的に接続します。保存だけでは接続は開始しません。</p></div></div>
          <div class="form-layout">
            <div class="form-stack">
              <article class="card"><div class="card-head"><div><h3>チャット接続</h3><p>Slack Socket Mode または Mattermost WebSocket を利用できます。</p></div><span class="state" id="chat-state" data-state="unknown">確認中</span></div>
                <div class="field-grid">
                  <div class="field"><label for="chat-provider">チャットサービス</label><select id="chat-provider" data-testid="chat-provider"><option value="slack">Slack</option><option value="mattermost">Mattermost</option></select></div>
                  <div class="field" id="slack-mode-field"><label for="slack-mode">Slack イベント受信方式</label><select id="slack-mode" data-testid="slack-mode"><option value="socket">Socket Mode（推奨・公開URL不要）</option><option value="http">Request URL方式（HTTPS）</option></select><p class="hint" id="slack-mode-hint">Socket Mode は、このPCからSlackへ接続してイベントを受信します。</p></div>
                </div>
                <div class="provider-fields" id="slack-fields">
                  <div class="field-grid">
                    <div class="field"><label for="slack-bot-token">Bot token</label><input id="slack-bot-token" type="password" autocomplete="new-password" placeholder="xoxb-… または保存済み" data-testid="slack-bot-token" /><p class="hint">保存済みのトークンは入力欄に戻しません。空欄なら現在の値を使います。</p></div>
                    <div class="field" id="slack-app-token-field"><label for="slack-app-token">アプリレベルトークン（App-level token）</label><input id="slack-app-token" type="password" autocomplete="new-password" placeholder="xapp-… または保存済み" data-testid="slack-app-token" /><p class="hint">Socket Mode の接続に必要です。</p></div>
                    <div class="field" id="slack-signing-field"><label for="slack-signing-secret">署名検証用シークレット（Signing Secret）</label><input id="slack-signing-secret" type="password" autocomplete="new-password" placeholder="Request URL方式で使用" data-testid="slack-signing-secret" /><p class="hint">Slackから届いたリクエストの署名を検証します。APIトークンとは別の値です。</p></div>
                    <div class="field full"><label for="slack-users">許可するユーザー ID</label><textarea id="slack-users" data-testid="slack-users" placeholder="U01234567 を1行ずつ入力"></textarea><p class="hint">制限する場合はユーザーIDを指定してください。許可リストが空の場合、ユーザーによる制限はありません。</p></div>
                  </div>
                </div>
                <div class="provider-fields" id="mattermost-fields" hidden>
                  <div class="field-grid">
                    <div class="field full"><label for="mm-url">Mattermost サーバー URL</label><input id="mm-url" type="url" placeholder="https://chat.example.com" data-testid="mattermost-url" /><p class="hint">末尾の API パスを含めず、サーバーの URL を入力します。</p></div>
                    <div class="field"><label for="mm-mode">イベント受信方式</label><select id="mm-mode" data-testid="mattermost-mode"><option value="websocket">WebSocket（推奨・常時接続）</option><option value="http">Webhook受信（HTTP/HTTPS）</option></select><p class="hint">WebSocket はこのPCから接続します。Webhook方式はMattermostから到達できる受信URLが必要です。HTTPSは別途リバースプロキシなどで終端します。</p></div>
                    <div class="field"><label for="mm-actor">接続アカウント</label><select id="mm-actor" data-testid="mattermost-actor"><option value="bot">Bot／サービスアカウント</option><option value="self">自分のアカウント（自分宛てDMのみ）</option></select></div>
                    <div class="field full"><label for="mm-token">Bot / Personal access token</label><input id="mm-token" type="password" autocomplete="new-password" placeholder="トークン" data-testid="mattermost-token" /><p class="hint" id="mm-token-hint">保存済みのトークンは入力欄に戻しません。空欄なら現在の値を使います。</p></div>
                    <div class="field full"><label for="mm-users">許可するユーザー ID</label><textarea id="mm-users" data-testid="mattermost-users" placeholder="ユーザー ID を1行ずつ入力"></textarea></div>
                    <div class="field"><label for="mm-webhook">Webhook検証トークン（Webhook方式のみ）</label><input id="mm-webhook" type="password" autocomplete="new-password" placeholder="必要な場合のみ" data-testid="mattermost-webhook-token" /></div>
                    <div class="field"><label for="mm-action-url">ボタン操作のコールバック基底URL（任意）</label><input id="mm-action-url" type="url" placeholder="https://bridge.example.com" data-testid="mattermost-action-url" /><p class="hint">MattermostからこのPCへボタン操作を送るためのURLです。通常のテキスト返信には不要です。</p></div>
                  </div>
                </div>
                <div class="save-row"><button class="button primary" id="save-chat" type="button" data-testid="save-chat">設定を保存</button><button class="button primary" id="connect-chat" type="button" data-testid="connect-chat">チャットに接続</button><button class="button danger" id="disconnect-chat" type="button" data-testid="disconnect-chat">切断</button></div><div class="message" id="chat-message" data-testid="chat-message" role="status" aria-live="polite"></div>
              </article>
              <article class="card"><div class="card-head"><div><h3>エージェントと作業範囲</h3><p>会話から新しい依頼を受けたときに使う設定です。</p></div><span class="state" id="agent-state" data-state="unknown">確認中</span></div>
                <div class="field-grid">
                  <div class="field"><label for="agent-kind">エージェント</label><select id="agent-kind" data-testid="agent-kind"><option value="codex">OpenAI Codex</option><option value="claude-code">Claude Code</option><option value="cursor">Cursor</option><option value="gemini">Gemini CLI</option></select></div>
                  <div class="field"><label for="agent-model">モデル</label><input id="agent-model" type="text" placeholder="CLI の既定モデル" data-testid="agent-model" /></div>
                  <div class="field full"><label for="default-workdir">既定の作業ディレクトリ</label><input id="default-workdir" type="text" placeholder="C:/work/project" data-testid="default-workdir" /><p class="hint">エージェントが作業する既定フォルダーを指定します。</p></div>
                  <div class="field full"><label for="allowed-dirs">許可するディレクトリ</label><textarea id="allowed-dirs" data-testid="allowed-dirs" placeholder="C:/work/project を1行ずつ入力"></textarea><p class="hint">会話から指定できる作業フォルダーを制限します。</p></div>
                  <div class="field"><label for="approval-policy">承認ポリシー</label><select id="approval-policy" data-testid="approval-policy"><option value="manual">手動承認</option><option value="on-failure">失敗時に確認</option><option value="never">自動実行</option></select></div>
                  <div class="field"><label for="network-access">ネットワークアクセス</label><select id="network-access" data-testid="network-access"><option value="disabled">無効</option><option value="enabled">有効</option></select></div>
                </div>
                <div class="save-row"><button class="button primary" id="save-agent" type="button" data-testid="save-agent">設定を保存</button><button class="button primary" id="connect-agent" type="button" data-testid="connect-agent">エージェントを確認</button><button class="button danger" id="disconnect-agent" type="button" data-testid="disconnect-agent">切断</button></div><div class="message" id="agent-message" data-testid="agent-message" role="status" aria-live="polite"></div>
              </article>
            </div>
            <aside class="form-stack"><div class="notice"><strong>安全に接続するために</strong>トークンはローカル設定へ保存されます。保存済みの秘密情報は読み戻して表示しません。設定を変更した後は「設定を保存」を押し、接続ボタンで反映してください。</div><div class="notice warning"><strong>接続と会話の状態</strong>Codex は CLI と認証状態、その他のエージェントは CLI の存在を確認します。依頼の実行状態は会話セッションに表示されます。</div><div class="card"><h3>現在の接続</h3><p id="connection-summary" class="card-detail">接続状態を読み込み中です。</p><div class="card-actions"><button class="button" type="button" id="connection-refresh">状態を更新</button></div></div></aside>
          </div>
        </section>

        <section class="page" id="page-sessions" aria-labelledby="page-title">
          <div class="intro"><div><h2>会話セッション</h2><p>チャット上で継続しているエージェント会話と、直近の実行結果を確認します。会話の返信は元のチャットスレッドに届きます。</p></div><div class="filters"><label class="field"><span class="nav-label">状態</span><select id="session-filter" aria-label="状態で絞り込み"><option value="all">すべて</option><option value="running">実行中</option><option value="completed">完了</option><option value="waiting_for_approval">承認待ち</option><option value="failed">失敗</option></select></label><label class="field"><span class="nav-label">検索</span><input id="session-search" type="search" aria-label="セッションを検索" placeholder="ID、チャンネル、依頼内容" /></label></div></div>
          <div id="sessions-table" data-testid="sessions-container"><div class="load-line">セッションを読み込み中…</div></div>
          <div class="filters section">
            <label class="field">チャット<select id="session-provider" aria-label="チャットで絞り込み"><option value="all">すべて</option><option value="slack">Slack</option><option value="mattermost">Mattermost</option></select></label>
            <span id="session-match-count" class="hint" role="status" aria-live="polite"></span>
          </div>
          <p id="session-no-matches" class="empty" hidden>条件に一致するセッションはありません。状態やチャットの絞り込み、検索語を変更してください。</p>
          <section id="session-detail" class="section card" hidden aria-labelledby="session-detail-title" data-testid="session-detail">
            <div class="card-head"><h2 id="session-detail-title">実行履歴</h2><button class="button" id="close-session-detail" type="button">詳細を閉じる</button></div>
            <div id="session-detail-content"></div>
          </section>
          <div class="section"><div class="section-head"><h2>承認待ち</h2><span class="state" id="approval-count" data-state="unknown">確認中</span></div><div id="approvals-list" data-testid="approvals-list"><div class="load-line">読み込み中…</div></div></div>
          <div class="section"><div class="section-head"><h2>最近のエラー</h2></div><div id="failures-list" data-testid="failures-list"><div class="load-line">読み込み中…</div></div></div>
        </section>
        <footer class="footer"><span>Chat Agent Bridge · ローカル運用コンソール</span><span><a href="/admin/settings">詳細設定</a> · <a href="/admin/onboarding">セットアップガイド</a></span></footer>
      </div></main>
    </div>
    <script>
      (() => {
        const state = { settings: null, settingsLoaded: false, connections: null, sessions: [], approvals: [], failures: [], loadErrors: {}, activePage: 'overview', busy: false, lastUpdated: null };
        const labels = { codex: 'OpenAI Codex', 'claude-code': 'Claude Code', cursor: 'Cursor', gemini: 'Gemini CLI', slack: 'Slack', mattermost: 'Mattermost' };
        const $ = (id) => document.getElementById(id);
        const escapeHtml = (value) => String(value ?? '').replace(/[&<>"']/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]));
        const setMessage = (id, message, kind = '') => { const target = $(id); target.textContent = message || ''; target.dataset.kind = kind; };
        async function fetchJson(url, options = {}) {
          const response = await fetch(url, { ...options, headers: { ...(options.body ? { 'Content-Type': 'application/json' } : {}), ...(options.method && options.method !== 'GET' ? { 'X-CAB-Request': 'admin' } : {}), ...(options.headers || {}) } });
          let payload = {};
          try { payload = await response.json(); } catch { payload = {}; }
          if (!response.ok) throw new Error(typeof payload.error === 'string' ? payload.error : 'リクエストに失敗しました。');
          return payload;
        }
        const statusMeta = (item, isAgent = false) => {
          if (!item) return { state: 'unknown', label: '確認中', detail: '状態を取得できません。' };
          const key = isAgent ? item.state : (item.connected ? 'connected' : item.state);
          const map = { connected: ['connected', '接続中'], listening: ['listening', '待受中'], connecting: ['connecting', '接続中'], disconnected: ['disconnected', '未接続'], error: ['error', 'エラー'] };
          const [stateName, label] = map[key] || ['disconnected', '未接続'];
          return { state: stateName, label, detail: item.statusText || item.lastError || (isAgent ? 'エージェントの状態を確認します。' : 'チャット接続の状態を確認します。') };
        };
        function setPill(id, meta) { const pill = $(id); pill.textContent = meta.label; pill.dataset.state = meta.state; }
        function formatDate(value) { if (!value) return '日時未設定'; const date = new Date(value); return Number.isNaN(date.valueOf()) ? String(value) : new Intl.DateTimeFormat('ja-JP', { dateStyle: 'medium', timeStyle: 'short' }).format(date); }
        function updateSetupGuide() {
          const chat = state.connections?.chat;
          const agent = state.connections?.agent;
          let title, detail;
          if (state.loadErrors.settings || state.loadErrors.connections || !state.settings || !state.connections) {
            title = '接続状態を確認できません'; detail = '「更新」を押して再確認してください。';
          } else if (!state.settings.defaultWorkingDirectory || !state.settings.allowedDirectories) {
            title = '1. 作業フォルダーを設定してください'; detail = '「エージェントと作業範囲」で既定の作業フォルダーと許可するフォルダーを保存してください。';
          } else if (agent?.state !== 'connected' || agent?.kind !== state.settings.agentKind) {
            title = '2. エージェントを確認してください'; detail = 'CLIをインストールしてログインし、「エージェントを確認」を押してください。既存の会話には、その会話で使用したエージェントを接続します。';
          } else if (!((chat?.connected || chat?.state === 'listening') && chat?.provider === state.settings.chatProvider)) {
            title = '3. チャットを接続してください'; detail = 'SlackまたはMattermostの接続情報を保存し、「チャットに接続」を押してください。エラーの場合は接続情報を確認して再接続してください。';
          } else {
            title = 'チャットから依頼できます'; detail = 'チャットでボットにメンションして依頼を送ってください。以後は同じスレッドで会話を続けられます。';
          }
          $('setup-title').textContent = title;
          $('setup-detail').textContent = detail;
        }
        let detailRequest = 0;
        let selectedSessionId = null;
        async function openSessionDetail(sessionId) {
          selectedSessionId = sessionId;
          document.querySelectorAll('.conversation').forEach((button) => button.setAttribute('aria-pressed', String(button.dataset.sessionDetail === sessionId)));
          const request = ++detailRequest;
          $('session-detail').hidden = false;
          const selected = state.sessions.find((session) => session.sessionId === sessionId);
          $('session-detail-title').textContent = selected?.latestExecution?.prompt || selected?.pendingApproval?.prompt || sessionId;
          $('detail-placeholder').hidden = true;
          $('close-session-detail').focus();
          $('session-detail-content').textContent = '読み込み中…';
          try {
            const payload = await fetchJson('/admin/api/sessions/' + encodeURIComponent(sessionId));
            if (request !== detailRequest) return;
            const history = Array.isArray(payload.executionHistory) ? payload.executionHistory : [];
            const pending = state.approvals.filter((entry) => entry.sessionId === sessionId);
            const meta = [labels[selected?.provider], labels[selected?.agent], payload.session?.workingDirectory].filter(Boolean).join(' · ');
            $('session-detail-content').innerHTML = '<p class="card-detail">' + escapeHtml(meta) + '</p>' + pending.map((entry) => '<div class="notice warning"><strong>' + (entry.kind === 'input' ? 'エージェントからの質問' : 'エージェントからの承認要求') + '</strong><p>' + escapeHtml(entry.prompt) + '</p><p>' + escapeHtml(entry.reason || '元のチャットスレッドで返信してください。') + '</p></div>').join('') + '<h3>実行履歴</h3>' + (history.length ? history.map((entry) => '<article class="notice section"><strong>' + escapeHtml(formatDate(entry.updatedAt)) + ' · ' + escapeHtml(statusLabel(entry.status)) + '</strong><p>' + escapeHtml(entry.prompt) + '</p><p style="white-space:pre-wrap">' + escapeHtml(entry.error || entry.summary || '結果はまだありません。') + '</p></article>').join('') : '<p class="empty">実行履歴はまだありません。</p>');
          } catch (error) {
            if (request === detailRequest) $('session-detail-content').textContent = error.message || '実行履歴を読み込めませんでした。詳細を開き直して再試行してください。';
          }
        }
        function sessionStatus(session) {
          const pending = state.approvals.filter((entry) => entry.sessionId === session.sessionId);
          if (pending.some((entry) => entry.kind === 'input')) return 'waiting_for_input';
          if (pending.length || session?.pendingApproval) return 'waiting_for_approval';
          return session?.latestExecution?.status || 'idle';
        }
        const statusLabel = (status) => ({ running: '実行中', completed: '完了', failed: '失敗', waiting_for_approval: '承認待ち', waiting_for_input: '回答待ち', cancelled: 'キャンセル', rejected: '却下', idle: '待機中' }[status] || status || '待機中');
        function renderSessionRows(sessions, compact = false) {
          if (state.loadErrors.sessions) return '<div class="load-error" data-testid="sessions-load-error">セッションを読み込めませんでした。更新してもう一度お試しください。</div>';
          if (!sessions.length) return '<div class="empty" data-testid="sessions-empty">セッションはまだありません。チャットからエージェントにメンションすると、ここに会話が表示されます。</div>';
          return sessions.map((session) => {
            const status = sessionStatus(session);
            const prompt = session.latestExecution?.prompt || session.pendingApproval?.prompt || '会話セッション';
            const channel = (session.provider || '') + (session.channelId ? ' · ' + session.channelId : '');
            return '<article class="session-row" data-status="' + escapeHtml(status) + '" data-search="' + escapeHtml([session.sessionId, channel, prompt, session.agent].join(' ').toLowerCase()) + '">' +
              '<div class="session-meta"><strong title="' + escapeHtml(prompt) + '">' + escapeHtml(prompt) + '</strong><small>' + escapeHtml(labels[session.agent] || session.agent || 'Agent') + ' · ' + escapeHtml(session.sessionId) + '</small></div>' +
              '<div class="session-state" data-status="' + escapeHtml(status) + '">' + escapeHtml(statusLabel(status)) + '</div>' +
              '<div class="session-meta"><strong>' + escapeHtml(channel || 'チャット') + '</strong><small>' + escapeHtml(session.workingDirectory || '') + '</small></div>' +
              '<div class="session-open"><small>' + escapeHtml(formatDate(session.updatedAt)) + '</small></div></article>';
          }).join('');
        }
        function renderTable(sessions) {
          if (state.loadErrors.sessions) return '<div class="load-error" data-testid="sessions-load-error">セッションを読み込めませんでした。更新してもう一度お試しください。</div>';
          if (!sessions.length) return '<div class="empty" data-testid="sessions-table-empty">セッションはまだありません。チャットから最初の依頼を送ると、ここに表示されます。</div>';
          return '<div data-testid="sessions-table">' + sessions.map((session) => {
            const status = sessionStatus(session);
            const title = session.latestExecution?.prompt || session.pendingApproval?.prompt || '継続中の会話';
            const search = [session.sessionId, session.provider, session.channelId, session.workingDirectory, title, session.latestExecution?.summary, session.agent].join(' ').toLowerCase();
            return '<button class="conversation" type="button" aria-pressed="' + String(selectedSessionId === session.sessionId) + '" data-session-detail="' + escapeHtml(session.sessionId) + '" aria-label="実行履歴を開く: ' + escapeHtml(session.sessionId) + '" data-provider="' + escapeHtml(session.provider) + '" data-status="' + escapeHtml(status) + '" data-search="' + escapeHtml(search) + '"><strong>' + escapeHtml(title) + '</strong><span class="row-meta"><span>' + escapeHtml(labels[session.provider] || session.provider) + ' · ' + escapeHtml(session.channelId) + '</span><span class="session-state" data-status="' + escapeHtml(status) + '">' + escapeHtml(statusLabel(status)) + '</span></span><span class="row-meta">' + escapeHtml(formatDate(session.updatedAt)) + '</span></button>';
          }).join('') + '</div>';
        }
        function renderApprovals() {
          if (state.loadErrors.approvals) return '<div class="load-error" data-testid="approvals-load-error">承認待ちを読み込めませんでした。</div>';
          $('approval-count').textContent = state.approvals.length + ' 件'; $('approval-count').dataset.state = state.approvals.length ? 'connecting' : 'connected';
          if (!state.approvals.length) return '<div class="empty" data-testid="approvals-empty">承認待ちはありません。</div>';
          return '<div class="grid">' + state.approvals.map((entry) => '<article class="card"><div class="card-head"><div><h3>' + escapeHtml(entry.prompt || '操作の承認') + '</h3><p>' + escapeHtml(entry.sessionId || '') + ' · ' + escapeHtml(labels[entry.provider] || entry.provider) + '</p></div><span class="state" data-state="connecting">' + (entry.kind === 'input' ? '回答待ち' : '承認待ち') + '</span></div><p class="card-detail">' + escapeHtml(entry.reason || 'エージェントが作業の承認を待っています。') + '<br />' + escapeHtml(entry.expiresAt ? '有効期限: ' + formatDate(entry.expiresAt) : formatDate(entry.requestedAt)) + '</p></article>').join('') + '</div>';
        }
        function renderFailures() {
          if (state.loadErrors.failures) return '<div class="load-error" data-testid="failures-load-error">エラー履歴を読み込めませんでした。</div>';
          if (!state.failures.length) return '<div class="empty" data-testid="failures-empty">最近のエラーはありません。</div>';
          return '<div class="grid">' + state.failures.map((entry) => '<article class="card"><div class="card-head"><div><h3>' + escapeHtml(entry.prompt || entry.sessionId || 'エラー') + '</h3><p>' + escapeHtml(entry.sessionId || '') + ' · ' + escapeHtml(formatDate(entry.updatedAt)) + '</p></div><span class="state" data-state="error">' + escapeHtml(statusLabel(entry.status || 'failed')) + '</span></div><p class="card-detail">' + escapeHtml(entry.error || entry.summary || '実行が完了しませんでした。') + '</p></article>').join('') + '</div>';
        }
        function applySessionFilters() {
          const filter = $('session-filter').value; const query = $('session-search').value.trim().toLowerCase();
          const provider = $('session-provider').value;
          let visible = 0;
          document.querySelectorAll('#sessions-table .conversation').forEach((row) => {
            row.hidden = (filter !== 'all' && row.dataset.status !== filter) || (provider !== 'all' && row.dataset.provider !== provider) || Boolean(query && !row.dataset.search.includes(query));
            if (!row.hidden) visible++;
          });
          $('session-match-count').textContent = visible + ' / ' + state.sessions.length + ' 件';
          $('session-no-matches').hidden = state.loadErrors.sessions || !state.sessions.length || visible > 0;
        }
        function updateOverview() {
          const chat = state.connections?.chat || null; const agent = state.connections?.agent || null;
          const chatMeta = statusMeta(chat); const agentMeta = statusMeta(agent, true);
          document.querySelector('.footer > span').textContent = (labels[chat?.provider] || 'チャット') + ': ' + chatMeta.label + '  /  ' + (labels[agent?.kind] || 'エージェント') + ': ' + agentMeta.label;
          setPill('overview-chat-state', chatMeta); setPill('chat-state', chatMeta); $('overview-chat-name').textContent = labels[state.settings?.chatProvider] || state.settings?.chatProvider || 'チャット'; $('overview-chat-detail').textContent = chatMeta.detail; $('connection-summary').textContent = chatMeta.detail + (agent ? ' / ' + agentMeta.detail : '');
          setPill('overview-agent-state', agentMeta); setPill('agent-state', agentMeta); $('overview-agent-name').textContent = labels[state.settings?.agentKind] || state.settings?.agentKind || 'エージェント'; $('overview-agent-detail').textContent = [agentMeta.detail, agent?.model ? 'モデル: ' + agent.model : '', agent?.authenticated === false ? 'CLI の認証を確認してください。' : ''].filter(Boolean).join(' · ');
          $('stat-sessions').textContent = String(state.sessions.length); $('stat-running').textContent = String(state.sessions.filter((item) => sessionStatus(item) === 'running').length); $('stat-approvals').textContent = String(state.approvals.length);
          $('recent-sessions').innerHTML = renderSessionRows(state.sessions.slice(0, 4), true);
          const alerts = [];
          if (agent?.state === 'error' || agent?.authenticated === false) alerts.push('<div class="notice warning"><strong>エージェントを確認してください</strong>' + escapeHtml(agent.lastError || 'エージェントの CLI または認証状態を確認できません。') + '</div>');
          if (state.approvals.length) alerts.push('<div class="notice warning"><strong>承認待ち ' + state.approvals.length + ' 件</strong>会話セッションから内容を確認してください。</div>');
          if (state.failures.length) alerts.push('<div class="notice"><strong>最近のエラー ' + state.failures.length + ' 件</strong>会話セッションの実行履歴を確認してください。</div>');
          if (!alerts.length) alerts.push('<div class="notice"><strong>対応が必要な項目はありません</strong>接続状態とセッションを定期的に確認しています。</div>');
          $('overview-alerts').innerHTML = alerts.join('');
          $('sessions-table').innerHTML = renderTable(state.sessions); $('approvals-list').innerHTML = renderApprovals(); $('failures-list').innerHTML = renderFailures();
          applySessionFilters();
          updateSetupGuide();
          $('last-updated').textContent = '最終更新 ' + formatDate(state.lastUpdated);
          $('connect-chat').disabled = state.busy || chatMeta.state === 'connected' || chatMeta.state === 'listening'; $('disconnect-chat').disabled = state.busy || (!chat || chatMeta.state === 'disconnected');
          $('connect-agent').disabled = state.busy || agentMeta.state === 'connected'; $('disconnect-agent').disabled = state.busy || (!agent || agentMeta.state === 'disconnected');
        }
        function applySettings(settings) {
          state.settings = settings || {};
          $('chat-provider').value = state.settings.chatProvider || 'slack'; $('slack-mode').value = state.settings.slackMode || 'socket'; $('mm-mode').value = state.settings.mattermostMode || 'websocket'; $('mm-actor').value = state.settings.mattermostActorMode || 'bot';
          $('mm-url').value = state.settings.mattermostServerUrl || ''; $('mm-action-url').value = state.settings.mattermostActionBaseUrl || ''; $('slack-users').value = state.settings.slackAllowedUserIds || ''; $('mm-users').value = state.settings.mattermostAllowedUserIds || '';
          $('agent-kind').value = state.settings.agentKind || 'codex'; $('default-workdir').value = state.settings.defaultWorkingDirectory || ''; $('allowed-dirs').value = state.settings.allowedDirectories || ''; $('approval-policy').value = state.settings.defaultApprovalPolicy || 'manual'; $('network-access').value = state.settings.networkAccess || 'disabled';
          const models = { codex: state.settings.codexModel, 'claude-code': state.settings.claudeModel, cursor: state.settings.cursorModel, gemini: state.settings.geminiModel }; $('agent-model').value = models[$('agent-kind').value] || '';
          $('mm-token-hint').textContent = state.settings.hasMattermostBotToken ? 'トークンは保存済みです。空欄のままなら現在の値を使います。' : 'Mattermost の Bot または Personal access token を入力します。';
          updateProviderFields();
        }
        function updateProviderFields() {
          const isSlack = $('chat-provider').value === 'slack';
          $('slack-fields').hidden = !isSlack; $('mattermost-fields').hidden = isSlack; $('slack-mode-field').hidden = !isSlack;
          $('slack-app-token-field').hidden = !isSlack || $('slack-mode').value !== 'socket'; $('slack-signing-field').hidden = !isSlack || $('slack-mode').value !== 'http';
          $('slack-mode-hint').textContent = $('slack-mode').value === 'socket'
            ? 'このPCからSlackへ接続します。公開URLの設定は不要です。'
            : 'Slackからイベントを受信する公開HTTPS URLが必要です。アプリ内のHTTP待受へ、リバースプロキシやトンネルなどで転送してください。Slack側のRequest URLは /slack/events に対応させます。';
        }
        function settingsPayload(kind) {
          const payload = { agentKind: $('agent-kind').value, defaultWorkingDirectory: $('default-workdir').value.trim(), allowedDirectories: $('allowed-dirs').value.trim(), defaultApprovalPolicy: $('approval-policy').value, networkAccess: $('network-access').value };
          if (kind === 'agent') { const modelKey = { codex: 'codexModel', 'claude-code': 'claudeModel', cursor: 'cursorModel', gemini: 'geminiModel' }[payload.agentKind]; payload[modelKey] = $('agent-model').value.trim(); return payload; }
          payload.chatProvider = $('chat-provider').value; payload.slackMode = $('slack-mode').value; payload.mattermostMode = $('mm-mode').value; payload.mattermostActorMode = $('mm-actor').value; payload.slackAllowedUserIds = $('slack-users').value.trim(); payload.mattermostAllowedUserIds = $('mm-users').value.trim(); payload.mattermostServerUrl = $('mm-url').value.trim(); payload.mattermostActionBaseUrl = $('mm-action-url').value.trim();
          const optional = { slackBotToken: $('slack-bot-token').value.trim(), slackAppToken: $('slack-app-token').value.trim(), slackSigningSecret: $('slack-signing-secret').value.trim(), mattermostBotToken: $('mm-token').value.trim(), mattermostWebhookToken: $('mm-webhook').value.trim() };
          Object.entries(optional).forEach(([key, value]) => { if (value) payload[key] = value; }); return payload;
        }
        async function saveSettings(kind) {
          setMessage(kind === 'chat' ? 'chat-message' : 'agent-message', '設定を保存しています…');
          try { const payload = settingsPayload(kind); const result = await fetchJson('/admin/api/settings', { method: 'POST', body: JSON.stringify(payload) }); if (result.settings) { applySettings(result.settings); state.settingsLoaded = true; } else { const refreshed = await fetchJson('/admin/api/settings'); applySettings(refreshed.settings); state.settingsLoaded = true; } $('slack-bot-token').value = ''; $('slack-app-token').value = ''; $('slack-signing-secret').value = ''; $('mm-token').value = ''; $('mm-webhook').value = ''; setMessage(kind === 'chat' ? 'chat-message' : 'agent-message', '設定を保存しました。接続ボタンで反映してください。', 'success'); await loadStatus(); }
          catch (error) { setMessage(kind === 'chat' ? 'chat-message' : 'agent-message', error.message || '設定を保存できませんでした。', 'error'); }
        }
        async function connectionAction(kind, action) {
          const messageId = kind === 'chat' ? 'chat-message' : 'agent-message'; setMessage(messageId, action === 'connect' ? '接続を確認しています…' : '切断しています…'); state.busy = true; updateOverview();
          try { await fetchJson('/admin/api/connections/' + kind + '/' + action, { method: 'POST', body: '{}' }); await loadStatus(); setMessage(messageId, action === 'connect' ? '接続状態を更新しました。' : '切断しました。', 'success'); }
          catch (error) { setMessage(messageId, error.message || '接続操作に失敗しました。', 'error'); }
          finally { state.busy = false; updateOverview(); }
        }
        async function loadStatus() {
          const requests = await Promise.allSettled([fetchJson('/admin/api/settings'), fetchJson('/admin/api/connections'), fetchJson('/admin/api/sessions'), fetchJson('/admin/api/approvals'), fetchJson('/admin/api/failures?limit=20')]);
          state.loadErrors = { settings: requests[0].status === 'rejected', connections: requests[1].status === 'rejected', sessions: requests[2].status === 'rejected', approvals: requests[3].status === 'rejected', failures: requests[4].status === 'rejected' };
          const failed = requests.filter((result) => result.status === 'rejected');
          if (requests[0].status === 'fulfilled' && requests[0].value.settings && !state.settingsLoaded) { applySettings(requests[0].value.settings); state.settingsLoaded = true; }
          if (requests[1].status === 'fulfilled') state.connections = requests[1].value;
          if (requests[2].status === 'fulfilled') state.sessions = Array.isArray(requests[2].value.sessions) ? requests[2].value.sessions : [];
          if (requests[3].status === 'fulfilled') state.approvals = Array.isArray(requests[3].value.approvals) ? requests[3].value.approvals : [];
          if (requests[4].status === 'fulfilled') state.failures = Array.isArray(requests[4].value.failures) ? requests[4].value.failures : [];
          state.lastUpdated = new Date().toISOString(); updateOverview();
          if (failed.length) { const first = failed[0].reason; $('global-message').textContent = '一部の状態を読み込めませんでした: ' + (first?.message || '通信エラー'); $('global-message').dataset.kind = 'error'; }
          else { $('global-message').textContent = ''; $('global-message').dataset.kind = ''; }
        }
        function showPage(page) { state.activePage = page; document.querySelectorAll('.page').forEach((section) => { section.classList.toggle('is-active', section.id === 'page-' + page); }); document.querySelectorAll('.nav [data-page]').forEach((button) => { if (button.dataset.page === page) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current'); }); $('page-title').textContent = ({ overview: '概要', connections: '接続と設定', sessions: '会話セッション' })[page] || '概要'; }
        document.querySelectorAll('.nav [data-page], [data-goto]').forEach((button) => button.addEventListener('click', (event) => { event.preventDefault(); showPage(button.dataset.page || button.dataset.goto); }));
        $('chat-provider').addEventListener('change', updateProviderFields); $('slack-mode').addEventListener('change', updateProviderFields); $('agent-kind').addEventListener('change', () => { const modelKey = { codex: 'codexModel', 'claude-code': 'claudeModel', cursor: 'cursorModel', gemini: 'geminiModel' }[$('agent-kind').value]; $('agent-model').value = state.settings?.[modelKey] || ''; });
        $('save-chat').addEventListener('click', () => saveSettings('chat')); $('save-agent').addEventListener('click', () => saveSettings('agent'));
        $('connect-chat').addEventListener('click', () => connectionAction('chat', 'connect')); $('disconnect-chat').addEventListener('click', () => connectionAction('chat', 'disconnect')); $('connect-agent').addEventListener('click', () => connectionAction('agent', 'connect')); $('disconnect-agent').addEventListener('click', () => connectionAction('agent', 'disconnect'));
        $('refresh-button').addEventListener('click', loadStatus); $('connection-refresh').addEventListener('click', loadStatus); $('session-filter').addEventListener('change', applySessionFilters); $('session-search').addEventListener('input', applySessionFilters);
        [['waiting_for_input', '回答待ち'], ['idle', '待機中'], ['cancelled', 'キャンセル'], ['rejected', '却下']].forEach(([value, label]) => { const option = document.createElement('option'); option.value = value; option.textContent = label; $('session-filter').append(option); });
        $('session-provider').addEventListener('change', applySessionFilters);
        $('sessions-table').addEventListener('click', (event) => { const button = event.target.closest('[data-session-detail]'); if (button) openSessionDetail(button.dataset.sessionDetail); });
        $('close-session-detail').addEventListener('click', () => { detailRequest++; selectedSessionId = null; $('session-detail').hidden = true; $('detail-placeholder').hidden = false; document.querySelector('.nav [data-page="sessions"]').focus(); });
        const workbench = document.createElement('div'); workbench.className = 'workbench';
        const conversationPane = document.createElement('div'); conversationPane.className = 'conversation-pane';
        const detailPane = document.createElement('div'); detailPane.className = 'detail-pane';
        const intro = document.querySelector('#page-sessions .intro');
        conversationPane.append(intro.querySelector('.filters'), $('session-provider').closest('.filters'), $('sessions-table'), $('session-no-matches'));
        const placeholder = document.createElement('div'); placeholder.id = 'detail-placeholder'; placeholder.className = 'detail-placeholder'; placeholder.textContent = '左の会話を選択すると、実行履歴と確認が必要な項目を表示します。';
        detailPane.append(placeholder, $('session-detail')); workbench.append(conversationPane, detailPane); intro.replaceWith(workbench);
        loadStatus(); window.setInterval(loadStatus, 10000);
      })();
    </script>
  </body>
</html>`;
}
