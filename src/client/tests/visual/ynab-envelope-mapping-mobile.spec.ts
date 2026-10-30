import { expect, test } from "@playwright/test";
import {
  ADMIN_FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";

test("YNAB envelope links fit a 375px viewport", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== "mobile-chromium", "375px layout check");
  await page.setViewportSize({ width: 375, height: 812 });
  const ok = (json: unknown) => ({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify(json),
  });
  const longName = "Household essentials and weekly groceries for the family";
  await installApiMocks(page, {
    user: ADMIN_FIXTURE_USER,
    ynabConfigured: true,
    overrides: {
      "**/api/ynab/connection-status": (r) =>
        r.fulfill(ok({ isConfigured: true, isConnected: true })),
      "**/api/ynab/budgets": (r) =>
        r.fulfill(ok({ data: [{ id: "budget-a", name: "Budget A" }] })),
      "**/api/ynab/settings/budget": (r) =>
        r.fulfill(ok({ selectedBudgetId: "budget-a" })),
      "**/api/ynab/accounts": (r) => r.fulfill(ok({ data: [] })),
      "**/api/ynab/account-mappings": (r) => r.fulfill(ok({ data: [] })),
      "**/api/ynab/categories": (r) =>
        r.fulfill(
          ok({
            data: [
              { id: "ynab-a", name: "Food A", categoryGroupName: "Living" },
              { id: "ynab-b", name: "Food B", categoryGroupName: "Living" },
            ],
          }),
        ),
      "**/api/envelopes": (r) =>
        r.fulfill(
          ok({
            data: [
              { id: "archived", name: "Old Pantry", isActive: false },
              { id: "long", name: longName, isActive: true },
            ],
          }),
        ),
      "**/api/ynab/envelope-mappings/unmapped": (r) =>
        r.fulfill(ok({ data: [] })),
      "**/api/ynab/envelope-mappings": (r) =>
        r.fulfill(
          ok({
            data: ["archived", "long"].map((envelopeId, index) => ({
              id: `mapping-${index}`,
              envelopeId,
              envelopeName: envelopeId === "long" ? longName : "Old Pantry",
              ynabCategoryId: index === 0 ? "ynab-a" : "ynab-b",
              ynabCategoryName: index === 0 ? "Food A" : "Food B",
              ynabCategoryGroupName: "Living",
              ynabBudgetId: "budget-a",
              createdAt: "2026-10-30T00:00:00Z",
              updatedAt: "2026-10-30T00:00:00Z",
            })),
          }),
        ),
      "**/api/ynab/stale-mappings": (r) =>
        r.fulfill(
          ok({
            staleAccountMappingCount: 0,
            staleCategoryMappingCount: 0,
            currentBudgetId: "budget-a",
          }),
        ),
    },
  });
  await signInAs(page, ADMIN_FIXTURE_USER);
  await page.goto("/settings/ynab", { waitUntil: "domcontentloaded" });
  await expect(page.getByText("Archived", { exact: true })).toBeVisible();
  for (const name of ["Old Pantry", longName]) {
    const selector = page.getByRole("combobox", {
      name: `YNAB category for ${name}`,
    });
    await expect(selector).toBeVisible();
    const row = selector.locator("xpath=..");
    const label = row.locator("span").first();
    await expect(label).toContainText(name);
    const box = await row.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(375);
    expect(await row.evaluate((el) => el.scrollWidth <= el.clientWidth)).toBe(
      true,
    );
    const unlink = page.getByRole("button", { name: `Unlink ${name}` });
    const labelBox = await label.boundingBox();
    const selectorBox = await selector.boundingBox();
    const unlinkBox = await unlink.boundingBox();
    for (const control of [selectorBox, unlinkBox]) {
      expect(control).not.toBeNull();
      expect(control!.x + control!.width).toBeLessThanOrEqual(375);
    }
    // At 375px the row stacks: name, then category, then the unlink button.
    expect(selectorBox!.y).toBeGreaterThanOrEqual(
      labelBox!.y + labelBox!.height,
    );
    expect(unlinkBox!.y).toBeGreaterThanOrEqual(
      selectorBox!.y + selectorBox!.height,
    );
  }
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth),
  ).toBeLessThanOrEqual(375);
});
