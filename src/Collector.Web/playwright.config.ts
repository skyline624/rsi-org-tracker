import { defineConfig, devices } from "@playwright/test";

// E2E_BASE_URL points the smoke test at a running site (e.g. after a deploy);
// otherwise a local dev server is started.
const baseURL = process.env.E2E_BASE_URL ?? "http://localhost:3000";

export default defineConfig({
  testDir: "./tests/e2e",
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: 1,
  reporter: "list",
  use: {
    baseURL,
    trace: "on-first-retry",
    ignoreHTTPSErrors: true,
  },
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] } },
  ],
  webServer: process.env.E2E_BASE_URL ? undefined : {
    command: "pnpm dev",
    url: "http://localhost:3000",
    reuseExistingServer: true,
    stdout: "ignore",
    stderr: "pipe",
    timeout: 120_000,
  },
});
