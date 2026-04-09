import { test, expect } from "@playwright/test";

/**
 * Smoke test : juste vérifier que chaque page publique répond et affiche
 * ses éléments de cadrage (titre, label HUD, panel). Ne valide pas la
 * donnée — l'API peut être en cours d'indexation.
 */

test.describe("public pages", () => {
  test("/ landing renders HUD tiles", async ({ page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Citizen Intel/);
    await expect(page.getByText(/UEE::CITIZEN_INTEL_NETWORK/)).toBeVisible();
    await expect(
      page.getByRole("heading", { level: 1, name: /Real-time intel/i }),
    ).toBeVisible();
  });

  test("/orgs catalog has filters and data grid", async ({ page }) => {
    await page.goto("/orgs");
    await expect(page.getByText(/UEE::ORG_CATALOG/)).toBeVisible();
    await expect(page.getByText(/SCAN/)).toBeVisible();
    await expect(page.getByText(/ORGS INDEXED/)).toBeVisible();
  });

  test("/users registry renders", async ({ page }) => {
    await page.goto("/users");
    await expect(page.getByText(/UEE::CITIZEN_REGISTRY/)).toBeVisible();
    await expect(page.getByText(/CITIZENS INDEXED/)).toBeVisible();
  });

  test("/stats dashboard renders", async ({ page }) => {
    await page.goto("/stats");
    await expect(page.getByText(/UEE::GLOBAL_TELEMETRY/)).toBeVisible();
  });

  test("/changes feed renders", async ({ page }) => {
    await page.goto("/changes");
    await expect(page.getByText(/UEE::LIVE_CHANGELOG/)).toBeVisible();
  });
});

test.describe("auth pages", () => {
  test("/login form renders", async ({ page }) => {
    await page.goto("/login");
    await expect(page.getByText(/SECURE LOGIN/i)).toBeVisible();
    await expect(page.getByLabel(/USERNAME/i)).toBeVisible();
    await expect(page.getByLabel(/PASSWORD/i)).toBeVisible();
  });

  test("/register form renders", async ({ page }) => {
    await page.goto("/register");
    await expect(page.getByText(/ENLIST/i).first()).toBeVisible();
    await expect(page.getByLabel(/EMAIL/i)).toBeVisible();
  });
});

test.describe("protected routes", () => {
  test("/dashboard redirects to /login when unauthenticated", async ({
    page,
  }) => {
    const res = await page.goto("/dashboard");
    expect(page.url()).toContain("/login");
    expect(res?.status()).toBeLessThan(500);
  });
});
