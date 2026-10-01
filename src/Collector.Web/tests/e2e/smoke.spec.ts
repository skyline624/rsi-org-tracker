import { test, expect, type Page } from "@playwright/test";

/**
 * Smoke test of the private site. Anonymous checks always run; the signed-in ones
 * need an account: E2E_USERNAME and E2E_PASSWORD (skipped otherwise). Only reads:
 * safe against a deployed site (E2E_BASE_URL).
 */

const PRIVATE_PAGES = ["/", "/orgs", "/users", "/stats", "/changes", "/dashboard", "/orgs/TEST", "/discord", "/discord/multi"];

test.describe("anonymous visitor", () => {
  for (const path of PRIVATE_PAGES) {
    test(`${path} redirects to the login page`, async ({ page }) => {
      await page.goto(path);
      const url = new URL(page.url());
      expect(url.pathname).toBe("/login");
      expect(url.searchParams.get("from")).toBe(path);
    });
  }

  test("a forged session cookie is not enough", async ({ page, context, baseURL }) => {
    await context.addCookies([{ name: "sct_access", value: "x", url: baseURL! }]);
    await page.goto("/orgs");
    expect(new URL(page.url()).pathname).toBe("/login");
  });

  test("the login form renders", async ({ page }) => {
    await page.goto("/login");
    await expect(page.getByText(/SECURE LOGIN/i)).toBeVisible();
    await expect(page.getByLabel(/USERNAME/i)).toBeVisible();
    await expect(page.getByLabel(/PASSWORD/i)).toBeVisible();
  });

  test("public registration no longer exists", async ({ page }) => {
    const response = await page.goto("/register");
    expect(new URL(page.url()).pathname === "/login" || response?.status() === 404).toBe(true);
  });
});

const username = process.env.E2E_USERNAME;
const password = process.env.E2E_PASSWORD;

async function signIn(page: Page, from = "/orgs") {
  await page.goto(`/login?from=${encodeURIComponent(from)}`);
  await page.getByLabel(/USERNAME/i).fill(username!);
  await page.getByLabel(/PASSWORD/i).fill(password!);
  await page.getByRole("button", { name: /CONNECT/i }).click();
  await page.waitForURL((url) => url.pathname === from);
}

test.describe("signed-in user", () => {
  test.skip(!username || !password, "set E2E_USERNAME and E2E_PASSWORD to run");

  test("lands back on the page asked for", async ({ page }) => {
    await signIn(page, "/stats");
    await expect(page.getByText(/UEE::GLOBAL_TELEMETRY/)).toBeVisible();
  });

  test("main pages render their frame", async ({ page }) => {
    await signIn(page);
    await expect(page.getByText(/ORGS INDEXED/)).toBeVisible();
    await page.goto("/users");
    await expect(page.getByText(/UEE::CITIZEN_REGISTRY/)).toBeVisible();
    await page.goto("/changes");
    await expect(page.getByText(/UEE::LIVE_CHANGELOG/)).toBeVisible();
    await page.goto("/discord");
    await expect(page.getByText(/UEE::DISCORD_ROSTERS/)).toBeVisible();
    await page.goto("/discord/multi");
    await expect(page.getByText(/UEE::DISCORD_MULTI/)).toBeVisible();
    await page.goto("/dashboard");
    expect(new URL(page.url()).pathname).toBe("/dashboard");
  });

  test("an unknown Discord server gets the not-found page", async ({ page }) => {
    await signIn(page);
    // Not a snowflake: the page answers without calling the API.
    await page.goto("/discord/NOT_A_SERVER");
    await expect(page.getByRole("heading", { name: /Not found/i })).toBeVisible();
  });

  test("an unknown organization gets the not-found page", async ({ page }) => {
    await signIn(page);
    await page.goto("/orgs/NO_SUCH_ORG_E2E");
    await expect(page.getByRole("heading", { name: /Not found/i })).toBeVisible();
  });
});
