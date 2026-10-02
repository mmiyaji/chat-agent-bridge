import { defineConfig } from "@playwright/test";

const port = Number.parseInt(process.env.ADMIN_E2E_PORT ?? "42188", 10);
const baseURL = `http://127.0.0.1:${port}`;

export default defineConfig({
  testDir: "./e2e",
  timeout: 30_000,
  reporter: "list",
  use: {
    baseURL,
    headless: true,
    ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}),
  },
  webServer: {
    command:
      "node --preserve-symlinks --preserve-symlinks-main ./node_modules/tsx/dist/cli.mjs scripts/admin-e2e-server.ts",
    url: `${baseURL}/admin`,
    timeout: 30_000,
    reuseExistingServer: true,
    env: {
      ADMIN_E2E_PORT: String(port),
    },
  },
});
