// Small helpers shared between src/admin/server.ts and src/admin/onboarding-page.ts.
// Split out so the onboarding wizard module does not need to import from
// server.ts (which would create a circular import back into this module).

export function escapeHtml(text: string | number): string {
  return String(text)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

const ADMIN_GITHUB_URL = "https://github.com/mmiyaji/chat-agent-bridge";
const ADMIN_AUTHOR_NAME = "mmiyaji";

export function renderAdminFooter(): string {
  const year = new Date().getFullYear();
  return `
    <footer class="site-footer">
      <div class="footer-links">
        <a href="/admin/terms">Terms</a>
        <a href="/admin/privacy">Privacy</a>
        <span>Author: ${escapeHtml(ADMIN_AUTHOR_NAME)}</span>
        <span>&copy; ${escapeHtml(year)}</span>
        <a href="${escapeHtml(ADMIN_GITHUB_URL)}" target="_blank" rel="noreferrer">GitHub</a>
      </div>
      <div class="footer-controls">
        <label>
          Language
          <select id="footer-language">
            <option value="en">English</option>
            <option value="ja">日本語</option>
          </select>
        </label>
        <label>
          Theme
          <select id="footer-theme">
            <option value="dark">Dark</option>
            <option value="light">Light</option>
            <option value="system">System</option>
          </select>
        </label>
      </div>
    </footer>`;
}

export function renderAdminFooterScript(): string {
  return `
      const footerTheme = document.getElementById("footer-theme");
      const footerLanguage = document.getElementById("footer-language");
      const THEME_STORAGE_KEY = "bridge-admin-theme";
      const LANGUAGE_STORAGE_KEY = "bridge-admin-language";

      function applyTheme(theme) {
        const resolved = theme || "dark";
        document.documentElement.dataset.theme = resolved;
        if (footerTheme) {
          footerTheme.value = resolved;
        }
      }

      function applyLanguage(language) {
        const resolved = language || "en";
        document.documentElement.lang = resolved;
        if (footerLanguage) {
          footerLanguage.value = resolved;
        }
        const jaOption = document.querySelector("#footer-language option[value='ja']");
        if (jaOption) {
          jaOption.textContent = "日本語";
        }
      }

      applyTheme(localStorage.getItem(THEME_STORAGE_KEY) || "dark");
      applyLanguage(localStorage.getItem(LANGUAGE_STORAGE_KEY) || "en");

      footerTheme?.addEventListener("change", (event) => {
        const value = event.target.value;
        localStorage.setItem(THEME_STORAGE_KEY, value);
        applyTheme(value);
      });

      footerLanguage?.addEventListener("change", (event) => {
        const value = event.target.value;
        localStorage.setItem(LANGUAGE_STORAGE_KEY, value);
        applyLanguage(value);
      });
  `;
}
