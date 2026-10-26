import { expect, test } from "@playwright/test";
import {
  ADMIN_FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";

test("YNAB archived and legacy mappings fit a 375px viewport", async ({
  page,
}, testInfo) => {
  test.skip(testInfo.project.name !== "mobile-chromium", "375px layout check");
  await page.setViewportSize({ width: 375, height: 812 });
  const ok = (json: unknown) => ({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify(json),
  });
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
      "**/api/ynab/category-mappings": (r) =>
        r.fulfill(
          ok({
            data: [
              {
                id: "archived",
                receiptsCategory: "Old Pantry",
                receiptsCategoryId: "old-pantry",
                ynabCategoryId: "ynab-a",
                ynabCategoryName: "Food A",
                ynabCategoryGroupName: "Living",
                ynabBudgetId: "budget-a",
              },
              {
                id: "legacy",
                receiptsCategory: "Groceries",
                receiptsCategoryId: null,
                legacyResolution: "Ambiguous",
                legacyCandidateCategoryIds: ["one", "two"],
                ynabCategoryId: "ynab-b",
                ynabCategoryName: "Food B",
                ynabCategoryGroupName: "Living",
                ynabBudgetId: "budget-a",
              },
            ],
          }),
        ),
      "**/api/taxonomy": (r) =>
        r.fulfill(
          ok({
            id: "revision",
            nodes: [
              {
                id: "old-pantry",
                name: "Old Pantry",
                parentId: null,
                archived: true,
              },
            ],
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
  const legacy = page.getByText(/Legacy “Groceries”/);
  await expect(legacy).toBeVisible();
  await expect(
    page.getByText("Current YNAB mapping: Living / Food B. Budget: Budget A."),
  ).toBeVisible();
  const archived = page.getByText(/Old Pantry — Archived category/);
  await expect(archived).toBeVisible();
  const archivedCard = archived.locator("xpath=..");
  const legacyCard = legacy.locator("xpath=..");
  for (const card of [archivedCard, legacyCard]) {
    const box = await card.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(375);
    expect(await card.evaluate((el) => el.scrollWidth <= el.clientWidth)).toBe(
      true,
    );
  }
  const selector = page.getByRole("combobox", {
    name: "YNAB category for Old Pantry",
  });
  const archivedButton = page.getByRole("button", {
    name: "Remove stable mapping",
  });
  const legacyButton = page.getByRole("button", {
    name: "Remove legacy mapping",
  });
  for (const control of [selector, archivedButton, legacyButton]) {
    const box = await control.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.x + box!.width).toBeLessThanOrEqual(375);
  }
  const selectorBox = await selector.boundingBox();
  const archivedButtonBox = await archivedButton.boundingBox();
  const legacyTextBox = await legacy.boundingBox();
  const legacyButtonBox = await legacyButton.boundingBox();
  expect(archivedButtonBox!.y).toBeGreaterThan(
    selectorBox!.y + selectorBox!.height,
  );
  expect(legacyButtonBox!.y).toBeGreaterThan(
    legacyTextBox!.y + legacyTextBox!.height,
  );
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth),
  ).toBeLessThanOrEqual(375);
});
