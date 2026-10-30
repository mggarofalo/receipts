import { expect, test, type Page } from "@playwright/test";
import { installApiMocks, signInAsFixtureUser } from "../fixtures/api-mocks";
import { itemTypeTaxonomy, nodeId } from "../fixtures/item-type-taxonomy";

async function openPicker(page: Page, palette: "graphite" | "paper") {
  await page.addInitScript(
    ([palette, recent]) => {
      localStorage.setItem("appearance.palette", palette);
      localStorage.setItem("category-picker:recent", JSON.stringify(recent));
    },
    [
      palette,
      [nodeId("Dairy & eggs"), nodeId("Cleaning"), nodeId("Coffee shops")],
    ] as const,
  );
  await installApiMocks(page, {
    overrides: {
      "**/api/taxonomy": (route) =>
        route.fulfill({
          contentType: "application/json",
          body: JSON.stringify(itemTypeTaxonomy),
        }),
    },
  });
  await signInAsFixtureUser(page);
  await page.goto("/search");
  await page.getByRole("combobox", { name: "Category", exact: true }).click();
  await expect(page.getByRole("listbox", { name: "Categories" })).toBeVisible();
}

for (const palette of ["graphite", "paper"] as const) {
  test(`category picker browses into a root (${palette})`, async ({ page }) => {
    await openPicker(page, palette);
    if (palette === "graphite")
      await expect(page).toHaveScreenshot(
        `category-picker-root-${palette}.png`,
      );
    await page.getByRole("option", { name: /^Groceries, / }).click();
    await expect(
      page.getByRole("option", { name: "Groceries", exact: true }),
    ).toBeVisible();
    await expect(page).toHaveScreenshot(`category-picker-level-${palette}.png`);
  });

  test(`category picker searches definitions (${palette})`, async ({
    page,
  }) => {
    await openPicker(page, palette);
    await page
      .getByRole("combobox", { name: "Search category paths" })
      .fill("soap");
    await expect(
      page.getByRole("option", { name: "Household / Cleaning" }),
    ).toBeVisible();
    await expect(page).toHaveScreenshot(
      `category-picker-search-${palette}.png`,
    );
  });
}
