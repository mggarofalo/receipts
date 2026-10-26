import { expect, test } from "@playwright/test";
import { installApiMocks, signInAs } from "../fixtures/api-mocks";

const group = {
  group: { store: "Market", rawDescription: "MILK 2% GAL", itemCode: null },
  purchaseCount: 12,
  conflict: false,
  migratedBroad: true,
  unresolved: false,
  unassigned: false,
  currentCategories: [
    { categoryId: "dairy", path: "Groceries / Dairy", purchaseCount: 12 },
  ],
  disposition: null,
  dispositionVersion: 0,
};

for (const width of [375, 1280]) {
  test(`cleanup link, keyboard choice and mobile width (${width}px)`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 900 });
    await installApiMocks(page, { freezeClock: false });
    await page.route("**/api/historical-reclassifications/groups?*", (route) =>
      route.fulfill({
        json: { items: [group], totalGroups: 1, nextOffset: null },
      }),
    );
    await page.route("**/api/historical-reclassifications?*", (route) =>
      route.fulfill({ json: { items: [], nextOffset: null } }),
    );
    await signInAs(page);
    await page.goto("/categories", {
      waitUntil: "domcontentloaded",
      timeout: 30_000,
    });
    await page.getByRole("link", { name: "Clean up history" }).click();
    await expect(page).toHaveURL(/\/categories\/cleanup$/);
    await expect(page.getByText("MILK 2% GAL")).toBeVisible();
    const checkbox = page.getByRole("checkbox", {
      name: "Select MILK 2% GAL at Market",
    });
    await checkbox.focus();
    await page.keyboard.press("Space");
    await expect(checkbox).toBeChecked();
    await expect(page.getByText("Selected groups (1/25)")).toBeVisible();
    await page.getByRole("combobox", { name: "Bulk destination" }).click();
    await page.getByRole("option", { name: "Unclassified" }).click();
    await expect(page.getByPlaceholder("Search category paths…")).toBeHidden();
    await expect(
      page.getByRole("button", { name: "Capture server preview" }),
    ).toBeDisabled();
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth),
    ).toBeLessThanOrEqual(width);
    await page.waitForTimeout(250);
    await page.screenshot({
      path: `C:/Users/mggar/Source/receipts-017-artifacts/1053/cleanup-${width}.png`,
      fullPage: true,
    });
  });
}
