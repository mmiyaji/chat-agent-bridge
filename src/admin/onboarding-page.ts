import { escapeHtml, renderAdminFooter, renderAdminFooterScript } from "./server-shared.js";
import { RuntimeSettingsPayload } from "./server-types.js";

/**
 * Step-by-step onboarding wizard. Plain client-side JS switches between steps
 * (no SPA framework), matching the rest of the admin UI's inline-script style.
 * State is kept in JS across steps and only sent to the server on final save.
 */
export function renderOnboardingPage(settings: RuntimeSettingsPayload): string {
  const provider = settings.chatProvider;
  const mattermostActorMode = settings.mattermostActorMode ?? "bot";
  const agentKind = settings.agentKind;
  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>Chat Agent Bridge Onboarding</title>
    <style>
      :root {
        color-scheme: dark;
        font-family: "Segoe UI", sans-serif;
        --bg: #0f172a;
        --panel: rgba(15, 23, 42, 0.9);
        --panel-alt: rgba(30, 41, 59, 0.7);
        --ink: #e2e8f0;
        --muted: #94a3b8;
        --line: #334155;
        --accent: #38bdf8;
        --success: #4ade80;
        --fail: #f87171;
      }
      :root[data-theme="light"] {
        color-scheme: light;
        --bg: #f4f7fb;
        --panel: rgba(255, 255, 255, 0.96);
        --panel-alt: rgba(241, 245, 249, 0.92);
        --ink: #0f172a;
        --muted: #475569;
        --line: #cbd5e1;
        --accent: #0ea5e9;
        --success: #166534;
        --fail: #b91c1c;
      }
      body { margin: 0; background: var(--bg); color: var(--ink); }
      main { max-width: 960px; margin: 0 auto; padding: 32px 20px 48px; }
      .hero { display: flex; justify-content: space-between; gap: 20px; align-items: start; margin-bottom: 24px; }
      .hero-copy { max-width: 640px; }
      .hero h1 { margin: 0 0 8px; font-size: 32px; }
      .hero p { margin: 0; color: var(--muted); line-height: 1.6; }
      .hero-side { display: grid; gap: 12px; justify-items: end; }
      .hero-meta { color: var(--muted); font-size: 12px; }
      .panel { background: var(--panel); border: 1px solid var(--line); border-radius: 16px; padding: 24px; margin-top: 20px; }
      .progress { display: flex; gap: 8px; margin-bottom: 20px; }
      .progress-step { flex: 1; height: 6px; border-radius: 999px; background: var(--line); }
      .progress-step.is-done, .progress-step.is-active { background: var(--accent); }
      .progress-label { color: var(--muted); font-size: 13px; margin-bottom: 18px; }
      .step { display: none; }
      .step.is-active { display: block; }
      .step h2 { margin: 0 0 6px; font-size: 20px; }
      .step > p.step-intro { margin: 0 0 16px; color: var(--muted); }
      .grid { display: grid; gap: 12px; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); }
      label { display: grid; gap: 6px; font-size: 14px; color: var(--muted); }
      input, select { width: 100%; box-sizing: border-box; border-radius: 10px; border: 1px solid var(--line); background: var(--panel-alt); color: var(--ink); padding: 10px 12px; }
      .hint { font-size: 13px; color: var(--muted); margin-top: 6px; line-height: 1.5; }
      .card { border: 1px solid var(--line); border-radius: 14px; padding: 16px; background: var(--panel-alt); }
      .provider-cards { display: grid; gap: 14px; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); margin-bottom: 8px; }
      .provider-card {
        border: 2px solid var(--line);
        border-radius: 16px;
        padding: 18px;
        background: var(--panel-alt);
        cursor: pointer;
        text-align: left;
        display: block;
        width: 100%;
        font: inherit;
        color: var(--ink);
      }
      .provider-card h3 { margin: 0 0 6px; font-size: 17px; }
      .provider-card p { margin: 0; color: var(--muted); font-size: 13px; line-height: 1.5; }
      .provider-card.is-selected { border-color: var(--accent); background: rgba(56, 189, 248, 0.12); }
      .agent-cards { display: grid; gap: 12px; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); margin-bottom: 8px; }
      .agent-card {
        border: 2px solid var(--line);
        border-radius: 14px;
        padding: 14px;
        background: var(--panel-alt);
        cursor: pointer;
        text-align: left;
        display: block;
        width: 100%;
        font: inherit;
        color: var(--ink);
      }
      .agent-card h3 { margin: 0 0 4px; font-size: 15px; }
      .agent-card p { margin: 0; color: var(--muted); font-size: 12px; line-height: 1.4; }
      .agent-card.is-selected { border-color: var(--accent); background: rgba(56, 189, 248, 0.12); }
      details.advanced { margin-top: 14px; border: 1px solid var(--line); border-radius: 12px; padding: 4px 14px; background: var(--panel-alt); }
      details.advanced summary { cursor: pointer; padding: 10px 0; font-weight: 600; }
      details.advanced .grid { margin: 12px 0 14px; }
      .actions { display: flex; gap: 12px; flex-wrap: wrap; margin-top: 24px; align-items: center; }
      button, .link-button { border: 0; border-radius: 999px; padding: 10px 16px; background: var(--accent); color: #082f49; font-weight: 700; cursor: pointer; text-decoration: none; display: inline-flex; align-items: center; }
      button:disabled { opacity: 0.5; cursor: not-allowed; }
      .secondary { background: transparent; border: 1px solid var(--line); color: var(--ink); }
      .flash { margin-top: 16px; min-height: 22px; color: var(--accent); }
      .hidden { display: none !important; }
      code { background: rgba(15, 23, 42, 0.9); padding: 2px 6px; border-radius: 6px; }
      .nav { display: inline-flex; gap: 8px; padding: 4px; border: 1px solid var(--line); border-radius: 999px; background: var(--panel-alt); margin-bottom: 20px; }
      .nav a { color: var(--ink); text-decoration: none; padding: 8px 14px; border-radius: 999px; }
      .nav a.is-active { background: rgba(56, 189, 248, 0.16); border: 1px solid var(--line); }
      .site-footer { margin-top: 18px; display: flex; justify-content: space-between; gap: 16px; flex-wrap: wrap; border-top: 1px solid var(--line); padding-top: 16px; color: var(--muted); }
      .footer-links, .footer-controls { display: flex; gap: 12px; flex-wrap: wrap; align-items: center; }
      .site-footer a { color: var(--accent); }
      .footer-controls label { display: inline-flex; gap: 8px; align-items: center; }
      .footer-controls select { border-radius: 999px; border: 1px solid var(--line); background: var(--panel-alt); color: var(--ink); padding: 6px 10px; }
      .test-result { margin-top: 12px; padding: 10px 14px; border-radius: 12px; border: 1px solid var(--line); display: none; }
      .test-result.is-visible { display: block; }
      .test-result.is-success { border-color: rgba(74, 222, 128, 0.5); background: rgba(74, 222, 128, 0.12); color: var(--success); }
      .test-result.is-failure { border-color: rgba(248, 113, 113, 0.5); background: rgba(248, 113, 113, 0.12); color: var(--fail); }
      .review-list { display: grid; gap: 8px; margin: 0 0 16px; padding: 0; list-style: none; }
      .review-list li { display: flex; justify-content: space-between; gap: 12px; border-bottom: 1px solid var(--line); padding: 8px 0; font-size: 14px; }
      .review-list li span:first-child { color: var(--muted); }
      .restart-note { border: 1px solid rgba(245, 158, 11, 0.5); background: rgba(245, 158, 11, 0.12); color: #fde68a; border-radius: 12px; padding: 12px 14px; margin-top: 14px; }
      .success-panel { border: 1px solid rgba(74, 222, 128, 0.5); background: rgba(74, 222, 128, 0.12); border-radius: 14px; padding: 16px; margin-top: 16px; }
      @media (max-width: 720px) {
        .hero {
          flex-direction: column;
        }
        .hero-side {
          justify-items: start;
        }
      }
    </style>
  </head>
  <body>
    <main>
      <section class="hero">
        <div class="hero-copy">
          <h1>Bridge Onboarding</h1>
          <p>A short guided setup so a first-time run only asks for what it needs. Four quick steps: chat provider, connection details, agent and workspace, then review and save.</p>
        </div>
        <div class="hero-side">
          <nav class="nav" aria-label="Admin navigation">
            <a href="/admin">Dashboard</a>
            <a href="/admin/settings">Settings</a>
            <a href="/admin/onboarding" class="is-active">Onboarding</a>
          </nav>
          <div class="hero-meta">Guided setup</div>
        </div>
      </section>
      <section class="panel">
        <div class="progress" data-testid="onboarding-progress">
          <div class="progress-step" data-progress-step="1"></div>
          <div class="progress-step" data-progress-step="2"></div>
          <div class="progress-step" data-progress-step="3"></div>
          <div class="progress-step" data-progress-step="4"></div>
        </div>
        <div class="progress-label" id="progress-label">Step 1 of 4</div>

        <div class="step is-active" data-step="1" data-testid="onboarding-step-1">
          <h2>1. Choose your chat provider</h2>
          <p class="step-intro">This is the chat app your team already uses. The bridge listens for mentions there and replies in the same thread.</p>
          <div class="provider-cards">
            <button type="button" class="provider-card" id="provider-card-mattermost" data-provider="mattermost" data-testid="provider-card-mattermost">
              <h3>Mattermost</h3>
              <p>Self-hosted or on-prem team chat. Good fit if your org already runs a Mattermost server.</p>
            </button>
            <button type="button" class="provider-card" id="provider-card-slack" data-provider="slack" data-testid="provider-card-slack">
              <h3>Slack</h3>
              <p>Hosted team chat. Good fit if your org already uses Slack workspaces and apps.</p>
            </button>
          </div>
        </div>

        <div class="step" data-step="2" data-testid="onboarding-step-2">
          <h2>2. Connect to <span id="step2-provider-label">your provider</span></h2>
          <p class="step-intro">Only the fields this provider actually needs. Test the connection before saving so you catch typos early.</p>

          <div id="mattermost-connection" class="card">
            <div class="grid">
              <label>
                Server URL
                <input id="mattermost-server-url" type="text" value="${escapeHtml(settings.mattermostServerUrl || "")}" placeholder="https://chat.example.com" data-testid="mattermost-server-url" />
              </label>
              <label>
                Bot token
                <input id="mattermost-bot-token" type="password" placeholder="${settings.hasMattermostBotToken ? "stored" : "paste your bot or personal access token"}" data-testid="mattermost-bot-token" />
              </label>
            </div>
            <details class="advanced" data-testid="mattermost-advanced">
              <summary>Advanced (optional)</summary>
              <div class="grid">
                <label>
                  Mode
                  <select id="mattermost-mode" data-testid="mattermost-mode">
                    <option value="websocket"${settings.mattermostMode === "websocket" ? " selected" : ""}>websocket</option>
                    <option value="http"${settings.mattermostMode === "http" ? " selected" : ""}>http</option>
                  </select>
                </label>
                <label>
                  Actor mode
                  <select id="mattermost-actor-mode" data-testid="mattermost-actor-mode">
                    <option value="bot"${mattermostActorMode === "bot" ? " selected" : ""}>bot</option>
                    <option value="self"${mattermostActorMode === "self" ? " selected" : ""}>self</option>
                  </select>
                </label>
                <label>
                  Webhook token
                  <input id="mattermost-webhook-token" type="password" placeholder="${settings.hasMattermostWebhookToken ? "stored" : "only needed for http mode"}" data-testid="mattermost-webhook-token" />
                </label>
                <label>
                  Action base URL
                  <input id="mattermost-action-base-url" type="text" value="${escapeHtml(settings.mattermostActionBaseUrl || "")}" placeholder="https://bridge.example.com" data-testid="mattermost-action-base-url" />
                </label>
              </div>
              <div class="hint">"websocket" keeps a live connection open to Mattermost and is recommended for most setups. "http" is for webhook-style entry points and needs the webhook token. Actor mode "bot" is for bot or service-account tokens; "self" is for your own account token, and only reacts in your self-DM. Action base URL is only used for button clicks and does not need to be filled in for basic replies.</div>
            </details>
          </div>

          <div id="slack-connection" class="card">
            <div class="grid">
              <label>
                Mode
                <select id="slack-mode" data-testid="slack-mode">
                  <option value="socket"${settings.slackMode === "socket" ? " selected" : ""}>socket</option>
                  <option value="http"${settings.slackMode === "http" ? " selected" : ""}>http</option>
                </select>
              </label>
              <label>
                Bot token
                <input id="slack-bot-token" type="password" placeholder="${settings.hasSlackBotToken ? "stored" : "xoxb-..."}" data-testid="slack-bot-token" />
              </label>
              <label>
                App token
                <input id="slack-app-token" type="password" placeholder="${settings.hasSlackAppToken ? "stored" : "xapp-..."}" data-testid="slack-app-token" />
              </label>
              <label>
                Signing secret
                <input id="slack-signing-secret" type="password" placeholder="${settings.hasSlackSigningSecret ? "stored" : "only needed for http mode"}" data-testid="slack-signing-secret" />
              </label>
            </div>
            <div class="hint">"socket" mode keeps a live connection open to Slack and needs only the bot and app tokens &mdash; no public URL required, and this is recommended for most setups. "http" mode expects Slack to call your bridge over a public URL and needs the signing secret to verify those requests.</div>
          </div>

          <div class="actions">
            <button type="button" id="test-connection" data-testid="test-connection">Test connection</button>
          </div>
          <div id="test-result" class="test-result" data-testid="test-connection-result"></div>
        </div>

        <div class="step" data-step="3" data-testid="onboarding-step-3">
          <h2>3. Agent and workspace</h2>
          <p class="step-intro">Pick which coding agent runs your requests and where it should work by default.</p>
          <div class="agent-cards">
            <button type="button" class="agent-card" id="agent-card-claude-code" data-agent="claude-code" data-testid="agent-card-claude-code">
              <h3>Claude Code</h3>
              <p>Anthropic's CLI coding agent.</p>
            </button>
            <button type="button" class="agent-card" id="agent-card-codex" data-agent="codex" data-testid="agent-card-codex">
              <h3>Codex</h3>
              <p>OpenAI's CLI coding agent.</p>
            </button>
            <button type="button" class="agent-card" id="agent-card-cursor" data-agent="cursor" data-testid="agent-card-cursor">
              <h3>Cursor</h3>
              <p>Cursor's CLI coding agent.</p>
            </button>
            <button type="button" class="agent-card" id="agent-card-gemini" data-agent="gemini" data-testid="agent-card-gemini">
              <h3>Gemini</h3>
              <p>Google's CLI coding agent.</p>
            </button>
          </div>
          <div class="grid">
            <label>
              Default working directory
              <input id="default-workdir" type="text" value="${escapeHtml(settings.defaultWorkingDirectory)}" data-testid="default-workdir" />
            </label>
            <label>
              Approval policy
              <select id="approval" data-testid="approval-policy">
                <option value="never"${settings.defaultApprovalPolicy === "never" ? " selected" : ""}>allow</option>
                <option value="manual"${settings.defaultApprovalPolicy === "manual" ? " selected" : ""}>manual</option>
                <option value="on-failure"${settings.defaultApprovalPolicy === "on-failure" ? " selected" : ""}>on-failure</option>
              </select>
            </label>
          </div>
          <div class="hint">"allow" lets the agent act without asking first. "manual" is a read-only plan mode: the agent proposes changes and waits for your approval before writing anything. "on-failure" behaves like allow, but asks for approval if a step fails.</div>
        </div>

        <div class="step" data-step="4" data-testid="onboarding-step-4">
          <h2>4. Review &amp; apply</h2>
          <p class="step-intro">Confirm what you are about to save. Secrets are never shown back in full.</p>
          <ul class="review-list" id="review-list" data-testid="review-list"></ul>
          <div class="restart-note">Provider, mode, tokens, and ports only take effect after a bridge restart. Approval policy and workspace defaults apply as soon as you save.</div>
          <div class="actions">
            <button type="button" id="save-onboarding" data-testid="save-onboarding">Save settings</button>
          </div>
          <div id="save-result" class="success-panel hidden" data-testid="onboarding-save-result">
            <p>Settings saved.</p>
            <div class="actions">
              <button type="button" id="restart-bridge-onboarding" data-testid="restart-bridge-onboarding">Restart bridge to apply</button>
              <a class="link-button secondary" href="/admin">Back to dashboard</a>
            </div>
          </div>
        </div>

        <div class="actions" id="step-nav">
          <button type="button" class="secondary" id="step-back" data-testid="step-back">Back</button>
          <button type="button" id="step-next" data-testid="step-next">Next</button>
          <a class="link-button secondary" href="/admin/settings">Open full settings</a>
        </div>
        <div id="flash" class="flash" data-testid="onboarding-flash"></div>
      </section>
      ${renderAdminFooter()}
    </main>
    <script>
${renderAdminFooterScript()}
      const state = {
        step: 1,
        provider: "${escapeHtml(provider)}",
        agentKind: "${escapeHtml(agentKind)}",
      };
      const totalSteps = 4;
      const flash = document.getElementById("flash");
      const testResult = document.getElementById("test-result");

      function selectProviderCard() {
        document.getElementById("provider-card-mattermost").classList.toggle("is-selected", state.provider === "mattermost");
        document.getElementById("provider-card-slack").classList.toggle("is-selected", state.provider === "slack");
        document.getElementById("mattermost-connection").classList.toggle("hidden", state.provider !== "mattermost");
        document.getElementById("slack-connection").classList.toggle("hidden", state.provider !== "slack");
        document.getElementById("step2-provider-label").textContent = state.provider === "mattermost" ? "Mattermost" : "Slack";
      }

      function selectAgentCard() {
        for (const agent of ["claude-code", "codex", "cursor", "gemini"]) {
          document.getElementById("agent-card-" + agent).classList.toggle("is-selected", state.agentKind === agent);
        }
      }

      document.getElementById("provider-card-mattermost").addEventListener("click", () => {
        state.provider = "mattermost";
        selectProviderCard();
      });
      document.getElementById("provider-card-slack").addEventListener("click", () => {
        state.provider = "slack";
        selectProviderCard();
      });
      for (const agent of ["claude-code", "codex", "cursor", "gemini"]) {
        document.getElementById("agent-card-" + agent).addEventListener("click", () => {
          state.agentKind = agent;
          selectAgentCard();
        });
      }

      function renderProgress() {
        document.getElementById("progress-label").textContent = "Step " + state.step + " of " + totalSteps;
        for (let i = 1; i <= totalSteps; i++) {
          const el = document.querySelector('[data-progress-step="' + i + '"]');
          if (!el) continue;
          el.classList.toggle("is-done", i < state.step);
          el.classList.toggle("is-active", i === state.step);
        }
        document.querySelectorAll(".step").forEach((stepEl) => {
          const stepNumber = Number.parseInt(stepEl.dataset.step, 10);
          stepEl.classList.toggle("is-active", stepNumber === state.step);
        });
        const backButton = document.getElementById("step-back");
        const nextButton = document.getElementById("step-next");
        backButton.style.visibility = state.step === 1 ? "hidden" : "visible";
        nextButton.classList.toggle("hidden", state.step === totalSteps);
        if (state.step === totalSteps) {
          renderReview();
        }
      }

      function goToStep(step) {
        state.step = Math.min(Math.max(step, 1), totalSteps);
        renderProgress();
      }

      document.getElementById("step-back").addEventListener("click", () => goToStep(state.step - 1));
      document.getElementById("step-next").addEventListener("click", () => goToStep(state.step + 1));

      function maskedValue(hasStored, entered) {
        if (entered) return "(entered)";
        if (hasStored) return "(stored)";
        return "not set";
      }

      function renderReview() {
        const list = document.getElementById("review-list");
        const isMattermost = state.provider === "mattermost";
        const rows = [
          ["Chat provider", isMattermost ? "Mattermost" : "Slack"],
          ["Agent", state.agentKind],
          ["Approval policy", document.getElementById("approval").value],
          ["Default working directory", document.getElementById("default-workdir").value || "(unset)"],
        ];
        if (isMattermost) {
          rows.push(["Server URL", document.getElementById("mattermost-server-url").value || "(unset)"]);
          rows.push(["Mode", document.getElementById("mattermost-mode").value]);
          rows.push(["Actor mode", document.getElementById("mattermost-actor-mode").value]);
          rows.push([
            "Bot token",
            maskedValue(${settings.hasMattermostBotToken ? "true" : "false"}, document.getElementById("mattermost-bot-token").value.trim()),
          ]);
          rows.push([
            "Webhook token",
            maskedValue(${settings.hasMattermostWebhookToken ? "true" : "false"}, document.getElementById("mattermost-webhook-token").value.trim()),
          ]);
        } else {
          rows.push(["Mode", document.getElementById("slack-mode").value]);
          rows.push([
            "Bot token",
            maskedValue(${settings.hasSlackBotToken ? "true" : "false"}, document.getElementById("slack-bot-token").value.trim()),
          ]);
          rows.push([
            "App token",
            maskedValue(${settings.hasSlackAppToken ? "true" : "false"}, document.getElementById("slack-app-token").value.trim()),
          ]);
          rows.push([
            "Signing secret",
            maskedValue(${settings.hasSlackSigningSecret ? "true" : "false"}, document.getElementById("slack-signing-secret").value.trim()),
          ]);
        }
        list.innerHTML = rows
          .map((row) => "<li><span>" + row[0] + "</span><span>" + row[1] + "</span></li>")
          .join("");
      }

      async function testConnection() {
        const button = document.getElementById("test-connection");
        button.disabled = true;
        testResult.className = "test-result";
        testResult.textContent = "Testing...";
        testResult.classList.add("is-visible");
        try {
          const payload = state.provider === "mattermost"
            ? {
                provider: "mattermost",
                serverUrl: document.getElementById("mattermost-server-url").value.trim() || undefined,
                botToken: document.getElementById("mattermost-bot-token").value.trim() || undefined,
              }
            : {
                provider: "slack",
                slackBotToken: document.getElementById("slack-bot-token").value.trim() || undefined,
              };
          const response = await fetch("/admin/api/test-connection", {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify(payload),
          });
          const body = await response.json();
          if (body.ok) {
            const details = body.details || {};
            const label = details.username || details.user || details.team || "connected";
            testResult.textContent = "Connected as " + label + ".";
            testResult.classList.add("is-success");
          } else {
            testResult.textContent = body.error || "Connection failed.";
            testResult.classList.add("is-failure");
          }
        } catch (error) {
          testResult.textContent = error instanceof Error ? error.message : "Connection failed.";
          testResult.classList.add("is-failure");
        } finally {
          button.disabled = false;
        }
      }

      document.getElementById("test-connection").addEventListener("click", () => {
        void testConnection();
      });

      document.getElementById("save-onboarding").addEventListener("click", async () => {
        const isMattermost = state.provider === "mattermost";
        const payload = {
          chatProvider: state.provider,
          agentKind: state.agentKind,
          defaultApprovalPolicy: document.getElementById("approval").value,
          defaultWorkingDirectory: document.getElementById("default-workdir").value.trim(),
          mattermostMode: document.getElementById("mattermost-mode").value,
          mattermostActorMode: document.getElementById("mattermost-actor-mode").value,
          slackMode: document.getElementById("slack-mode").value,
          mattermostServerUrl: document.getElementById("mattermost-server-url").value.trim() || undefined,
          mattermostActionBaseUrl: document.getElementById("mattermost-action-base-url").value.trim() || undefined,
          mattermostBotToken: document.getElementById("mattermost-bot-token").value.trim() || undefined,
          mattermostWebhookToken: document.getElementById("mattermost-webhook-token").value.trim() || undefined,
          slackBotToken: document.getElementById("slack-bot-token").value.trim() || undefined,
          slackAppToken: document.getElementById("slack-app-token").value.trim() || undefined,
          slackSigningSecret: document.getElementById("slack-signing-secret").value.trim() || undefined,
        };
        const saveButton = document.getElementById("save-onboarding");
        saveButton.disabled = true;
        try {
          const response = await fetch("/admin/api/settings", {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify(payload),
          });
          if (!response.ok) {
            flash.textContent = "Failed to save onboarding settings.";
            return;
          }
          flash.textContent = "";
          document.getElementById("save-result").classList.remove("hidden");
        } catch (error) {
          flash.textContent = error instanceof Error ? error.message : "Failed to save onboarding settings.";
        } finally {
          saveButton.disabled = false;
        }
      });

      document.getElementById("restart-bridge-onboarding").addEventListener("click", async () => {
        const confirmed = window.confirm("Restart the bridge process now?");
        if (!confirmed) {
          return;
        }
        try {
          const response = await fetch("/admin/api/bridge/restart", { method: "POST" });
          if (!response.ok) {
            throw new Error(await response.text());
          }
          flash.textContent = "Bridge restart requested. The admin page may disconnect for a few seconds.";
        } catch (error) {
          flash.textContent = error instanceof Error ? error.message : "Bridge restart failed.";
        }
      });

      selectProviderCard();
      selectAgentCard();
      renderProgress();
    </script>
  </body>
</html>`;
}
