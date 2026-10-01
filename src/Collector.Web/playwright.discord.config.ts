import { defineConfig, devices } from "@playwright/test";

/** Isolated browser acceptance tests with a disposable, loopback-only API fixture. */
export default defineConfig({
  testDir: "./tests/e2e",
  testMatch: "discord.spec.ts",
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  reporter: "list",
  use: {
    baseURL: "http://127.0.0.1:3334",
    channel: process.env.E2E_BROWSER_CHANNEL,
    trace: "retain-on-failure",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: [
    { command: "node tests/e2e/discord-fixture.mjs", url: "http://127.0.0.1:3335/health", reuseExistingServer: false },
    {
      command: process.env.E2E_WEB_COMMAND ?? "node node_modules/next/dist/bin/next start -p 3334 -H 127.0.0.1",
      url: "http://127.0.0.1:3334/login",
      env: { API_BASE_URL: "http://127.0.0.1:3335", NEXT_TELEMETRY_DISABLED: "1", PORT: "3334", HOSTNAME: "127.0.0.1" },
      reuseExistingServer: false,
      timeout: 120_000,
    },
  ],
});
