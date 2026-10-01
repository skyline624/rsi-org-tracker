import { test, expect, type Page } from "@playwright/test";

const GUILD = "123456789012345678";
const ROLE = "234567890123456789";
const USER = "345678901234567890";
const fixture = "http://127.0.0.1:3335";

async function signIn(page: Page, username = "admin", from = "/discord") {
  await page.goto(`/login?from=${encodeURIComponent(from)}`);
  await page.getByLabel(/USERNAME/i).fill(username);
  await page.getByLabel(/PASSWORD/i).fill("disposable fixture password");
  await page.getByRole("button", { name: /CONNECT/i }).click();
  await page.waitForURL(url => url.pathname === from);
}

test.beforeEach(async ({ request }) => {
  const response = await request.post(`${fixture}/__fixture/reset`);
  expect(response.status()).toBe(204);
});

test("authenticated roster pages render inert names and every guild tab", async ({ page }) => {
  const browserApiCalls: string[] = [];
  const pageErrors: string[] = [];
  page.on("pageerror", error => pageErrors.push(error.message));
  page.on("request", request => { if (request.url().startsWith(fixture)) browserApiCalls.push(request.url()); });
  await signIn(page);
  await expect(page.getByRole("heading", { name: "Serveurs Discord" })).toBeVisible();
  await page.goto(`/discord/${GUILD}`);
  await expect(page.getByRole("button", { name: "Autoriser un départ massif", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Supprimer et repartir d'une base", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Supprimer et exclure ce serveur", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Supprimer et exclure ce compte", exact: true })).toHaveCount(0);
  await expect(page.getByRole("heading", { name: /Corsaires <img/ })).toBeVisible();
  await expect(page.getByText("Pilote42", { exact: true }).first()).toBeVisible();
  expect(await page.locator('img[src="x"]').count()).toBe(0);
  expect(await page.evaluate(() => (window as unknown as { __discordXss?: number }).__discordXss)).toBeUndefined();
  for (const [tab, label, text] of [["history", "HISTORIQUE", "HISTORIQUE"], ["gaps", "ÉCARTS RSI", "Aucun envoi complet : absences inconnues"],
    ["suggestions", "SUGGESTIONS", "Valider"], ["config", "CONFIG", "RANG RSI"], ["syncs", "ENVOIS", "DÉPARTS MASSIFS"]]) {
    await page.getByRole("navigation", { name: "Onglets du serveur" }).getByRole("link", { name: label, exact: true }).click();
    await expect(page).toHaveURL(`/discord/${GUILD}?tab=${tab}`);
    await expect(page.getByText(text, { exact: false }).first()).toBeVisible();
  }
  expect(pageErrors).toEqual([]);
  await page.goto("/discord/multi");
  await expect(page.getByRole("heading", { name: "Multi-appartenance" })).toBeVisible();
  await expect(page.getByText("Recrutement", { exact: true })).toBeVisible();
  expect(browserApiCalls).toEqual([]);
  expect(await page.evaluate(() => document.cookie)).not.toContain("sct_access");
});

test("the settings panel creates a scoped key, reveals it once, and revokes it", async ({ page, request }) => {
  await signIn(page, "admin", "/settings");
  await expect(page.getByText("https://tracker.example.test", { exact: true })).toBeVisible();
  await page.getByLabel("NOM", { exact: true }).fill("Vencord navigateur");
  await page.getByLabel(/EXPIRATION \(JOURS/).fill("30");
  await page.getByRole("button", { name: "CRÉER LA CLÉ", exact: true }).click();
  await expect(page.getByText("sc_fixture_DISPOSABLE_KEY", { exact: true })).toBeVisible();
  const created = await (await request.get(`${fixture}/__fixture/state`)).json();
  expect(created.mutations[0].body.scope).toBe("discord:ingest");
  expect(Date.parse(created.mutations[0].body.expiresAt) - Date.now()).toBeGreaterThan(29 * 86400000);
  await page.getByRole("button", { name: "J'AI COPIÉ LA CLÉ", exact: true }).click();
  await expect(page.getByText("sc_fixture_DISPOSABLE_KEY", { exact: true })).toHaveCount(0);
  await page.reload();
  await expect(page.getByText("sc_fixture_DISPOSABLE_KEY", { exact: true })).toHaveCount(0);
  page.once("dialog", dialog => dialog.accept());
  await page.getByRole("button", { name: "RÉVOQUER", exact: true }).click();
  await expect(page.getByText("RÉVOQUÉE", { exact: true })).toBeVisible();
});

test("configuration and suggestion actions send the selected values and refresh", async ({ page, request }) => {
  await signIn(page);
  await page.goto(`/discord/${GUILD}?tab=suggestions`);
  await page.getByRole("navigation", { name: "Onglets du serveur" }).getByRole("link", { name: "CONFIG", exact: true }).click();
  await expect(page).toHaveURL(`/discord/${GUILD}?tab=config`);
  await page.getByLabel("Ordre du rang Pilote").fill("42");
  await page.getByLabel("Rang RSI équivalent à Pilote").selectOption("Commandant");
  await page.getByRole("button", { name: "Enregistrer", exact: true }).click();
  await expect(page.getByRole("button", { name: "Enregistrer", exact: true })).toBeDisabled();
  let data = await (await request.get(`${fixture}/__fixture/state`)).json();
  expect(data.mutations.at(-1)).toEqual({ path: `/api/discord/guilds/${GUILD}/roles/${ROLE}`, method: "PUT",
    body: { isRank: true, rankOrder: 42, rsiRankLabel: "Commandant" } });
  await page.getByRole("combobox", { name: "CORPO RSI — NOM OU SID" }).fill("Nouvelle Organisation");
  await page.getByRole("option", { name: "Nouvelle Organisation Interstellaire [NEW]", exact: true }).click();
  await page.getByRole("button", { name: "CHANGER", exact: true }).click();
  await expect(page.getByText("[NEW]", { exact: false }).first()).toBeVisible();
  await page.goto(`/discord/${GUILD}?tab=suggestions`);
  await page.getByRole("button", { name: "Ignorer", exact: true }).click();
  await expect(page.getByRole("button", { name: "Annuler", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Annuler", exact: true }).click();
  await expect(page.getByRole("button", { name: "Valider", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Valider", exact: true }).click();
  await expect(page.getByText("Lien validé : Pilote42 → Pilote42.", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Valider", exact: true })).toHaveCount(0);
  data = await (await request.get(`${fixture}/__fixture/state`)).json();
  expect(data.mutations.at(-1)).toEqual({ path: "/api/discord/links", method: "POST",
    body: { discordUserId: USER, citizenId: 42, handle: "Pilote42" } });
});

test("unmapped guild configuration opens from the notice and after other tabs", async ({ page, request }) => {
  const session = await (await request.post(`${fixture}/api/auth/login`, { data: { username: "admin" } })).json();
  const headers = { Authorization: `Bearer ${session.accessToken}` };
  expect((await request.put(`${fixture}/api/discord/guilds/${GUILD}/org`, { headers, data: { orgSid: null } })).ok()).toBe(true);
  expect((await request.put(`${fixture}/api/discord/guilds/${GUILD}/roles/${ROLE}`, {
    headers, data: { isRank: true, rankOrder: 10, rsiRankLabel: null },
  })).ok()).toBe(true);
  await signIn(page);
  await page.goto(`/discord/${GUILD}`);
  const tabs = page.getByRole("navigation", { name: "Onglets du serveur" });
  await tabs.getByRole("link", { name: "ÉCARTS RSI", exact: true }).click();
  await page.getByRole("link", { name: "(onglet config)", exact: true }).click();
  await expect(page).toHaveURL(`/discord/${GUILD}?tab=config`);
  await expect(page.getByRole("combobox", { name: "CORPO RSI — NOM OU SID" })).toBeEnabled();
  await expect(page.getByLabel("Rang RSI équivalent à Pilote").locator("option")).toHaveCount(1);
  await tabs.getByRole("link", { name: "SUGGESTIONS", exact: true }).click();
  await expect(page.getByRole("button", { name: "Valider", exact: true })).toBeVisible();
  await tabs.getByRole("link", { name: "CONFIG", exact: true }).click();
  await expect(page).toHaveURL(`/discord/${GUILD}?tab=config`);
  await expect(page.getByLabel("Ordre du rang Pilote")).toHaveValue("10");
});

test("unmapped servers propose organizations from their name without linking automatically", async ({ page, request }) => {
  await request.post(`${fixture}/__fixture/reset`, { data: { orgSid: null, guildName: "⭐ LIBERASTRA ⭐" } });
  await signIn(page);
  await expect(page.getByRole("combobox", { name: "CORPO RSI — NOM OU SID" })).toHaveValue("LIBERASTRA");
  await expect(page.getByRole("listbox", { name: "Propositions de corpos RSI" }).getByRole("option")).toHaveCount(2);
  await expect(page.getByRole("button", { name: "RELIER", exact: true })).toBeDisabled();
  expect((await (await request.get(`${fixture}/__fixture/state`)).json()).mutations).toEqual([]);
  await page.goto(`/discord/${GUILD}?tab=config`);
  await expect(page.getByRole("combobox", { name: "CORPO RSI — NOM OU SID" })).toHaveValue("LIBERASTRA");
  await page.getByRole("option", { name: "Libérastra [LIBERASTRA]", exact: true }).click();
  await expect(page.getByText("Corpo sélectionnée : Libérastra [LIBERASTRA].", { exact: true })).toBeVisible();
  expect((await (await request.get(`${fixture}/__fixture/state`)).json()).mutations).toEqual([]);
  await page.getByRole("button", { name: "RELIER", exact: true }).click();
  await expect(page.getByRole("combobox", { name: "CORPO RSI — NOM OU SID" })).toHaveValue("Libérastra [LIBERASTRA]");
  await expect(page.getByRole("button", { name: "CHANGER", exact: true })).toBeDisabled();
  expect((await (await request.get(`${fixture}/__fixture/state`)).json()).mutations.at(-1)).toEqual({
    path: `/api/discord/guilds/${GUILD}/org`, method: "PUT", body: { orgSid: "LIBERASTRA" },
  });
});

test("organization search accepts full names and SIDs and requires a selected result", async ({ page, request }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await request.post(`${fixture}/__fixture/reset`, { data: { orgSid: null, guildName: "" } });
  await signIn(page);
  await page.goto(`/discord/${GUILD}?tab=config`);
  const search = page.getByRole("combobox", { name: "CORPO RSI — NOM OU SID" });
  await search.fill("Nouvelle Organisation Interstellaire");
  await expect(search).toHaveValue("Nouvelle Organisation Interstellaire");
  await expect(page.getByRole("option", { name: "Nouvelle Organisation Interstellaire [NEW]", exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  await page.screenshot({ path: testInfo.outputPath("discord-org-search-mobile.png"), fullPage: true });
  await expect(page.getByRole("button", { name: "RELIER", exact: true })).toBeDisabled();
  await search.press("Enter");
  expect((await (await request.get(`${fixture}/__fixture/state`)).json()).mutations).toEqual([]);
  await search.press("ArrowDown");
  await search.press("Enter");
  await expect(search).toHaveValue("Nouvelle Organisation Interstellaire [NEW]");
  await page.getByRole("button", { name: "RELIER", exact: true }).click();
  await expect(page.getByRole("button", { name: "CHANGER", exact: true })).toBeDisabled();
  await search.fill("Corpo introuvable");
  await expect(page.getByText("Aucune corpo trouvée. Essaie un autre nom ou SID.", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "CHANGER", exact: true })).toBeDisabled();
  await search.fill("LIBER2");
  await page.getByRole("option", { name: "LIBERASTRA Exploration [LIBER2]", exact: true }).click();
  await page.getByRole("button", { name: "CHANGER", exact: true }).click();
  await expect(search).toHaveValue("LIBERASTRA Exploration [LIBER2]");
  const state = await (await request.get(`${fixture}/__fixture/state`)).json();
  expect(state.searches).toContain("nouvelle organisation interstellaire");
  expect(state.mutations.map((mutation: { body: unknown }) => mutation.body)).toEqual([{ orgSid: "NEW" }, { orgSid: "LIBER2" }]);
});

test("a regular reader sees locked configuration and no administrative controls", async ({ page }) => {
  await signIn(page, "reader");
  await page.goto(`/discord/${GUILD}?tab=config`);
  await expect(page.getByRole("combobox", { name: "CORPO RSI — NOM OU SID" })).toBeDisabled();
  await expect(page.getByLabel("Ordre du rang Pilote")).toBeDisabled();
  await expect(page.getByRole("button", { name: "CHANGER", exact: true })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Autoriser un départ massif", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Supprimer et exclure ce serveur", exact: true })).toHaveCount(0);
  await page.goto(`/discord/${GUILD}`);
  await expect(page.getByRole("button", { name: "Supprimer et exclure ce compte", exact: true })).toHaveCount(0);
  await page.goto("/discord/NOT_A_SERVER");
  await expect(page.getByRole("heading", { name: /Not found/i })).toBeVisible();
});

test("member names stay readable on a narrow screen and columns scroll locally", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await signIn(page, "reader", `/discord/${GUILD}`);
  const name = page.getByRole("link", { name: "Pilote42", exact: true }).first();
  await expect(name).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("discord-mobile.png"), fullPage: true });
  expect((await name.boundingBox())!.width).toBeGreaterThan(40);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
});
