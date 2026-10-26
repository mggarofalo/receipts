import { expect, test } from "@playwright/test";
import {
  ADMIN_FIXTURE_USER,
  FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";
import { taxonomyFixture } from "../../src/test/taxonomy-fixture";

test.setTimeout(120_000);
test.use({ navigationTimeout: 60_000 });
const output =
  "C:/Users/mggar/Source/receipts-017-artifacts/1060/navigation-review";
const historicalId = "95000000-0000-4000-8000-000000000099";
const current = { ...taxonomyFixture, previousRevisionId: historicalId };

for (const width of [1280, 375]) {
  for (const admin of [false, true]) {
    test(`categories navigation ${width} ${admin ? "admin" : "reader"}`, async ({
      page,
    }) => {
      await page.setViewportSize({ width, height: width === 375 ? 812 : 900 });
      const user = admin ? ADMIN_FIXTURE_USER : FIXTURE_USER;
      await installApiMocks(page, {
        user,
        freezeClock: false,
        overrides: {
          "**/api/receipt-items?*": (route) =>
            route.fulfill({
              json: { data: [], total: 0, offset: 0, limit: 200 },
            }),
          "**/api/users?*": (route) =>
            route.fulfill({
              json: { data: [], total: 0, offset: 0, limit: 500 },
            }),
          "**/api/search/purchases?*": (route) =>
            route.fulfill({
              json: { items: [], total: 0, offset: 0, limit: 5 },
            }),
          "**/api/taxonomy": (route) => route.fulfill({ json: current }),
          "**/api/taxonomy/revisions/*": (route) =>
            route.fulfill({ json: { ...taxonomyFixture, id: historicalId } }),
          "**/api/category-assistance/status": (route) =>
            route.fulfill({
              json: {
                capability: "unconfigured",
                consentEnabled: false,
                suggestionsAvailable: false,
                provider: null,
                models: [],
              },
            }),
        },
      });
      await signInAs(page, user);
      await page.goto("/categories");
      await expect(
        page.getByRole("heading", { name: "Categories", exact: true }),
      ).toBeVisible();
      await page
        .getByRole("button", { name: "Expand all", exact: true })
        .click();
      await expect(
        page.getByRole("treeitem", { name: "Food / Dairy", exact: true }),
      ).toBeVisible();
      await expect(
        page.getByRole("button", { name: "New category", exact: true }),
      ).toHaveCount(admin ? 1 : 0);
      await expect(
        page.getByRole("button", {
          name: "Rename category: Food",
          exact: true,
        }),
      ).toHaveCount(admin ? 1 : 0);
      await page.screenshot({
        path: `${output}/${width}-${admin ? "admin" : "reader"}-tree.png`,
        fullPage: true,
        animations: "disabled",
      });
      if (width === 375)
        await page
          .getByRole("button", { name: "Open navigation menu" })
          .click();
      const navigation =
        width === 375
          ? page.getByRole("dialog")
          : page.getByRole("navigation", { name: "Primary", exact: true });
      await expect(
        navigation.getByRole("link", { name: "Classification", exact: true }),
      ).toHaveCount(1);
      await expect(
        navigation.getByRole("link", { name: "Categories", exact: true }),
      ).toHaveCount(0);
      await expect(
        navigation.getByRole("link", { name: /Subcategories|Category tree/ }),
      ).toHaveCount(0);
      await page.screenshot({
        path: `${output}/${width}-${admin ? "admin" : "reader"}-navigation.png`,
        fullPage: true,
        animations: "disabled",
      });
      await navigation
        .getByRole("link", { name: "Classification", exact: true })
        .click();
      await expect(page).toHaveURL(/\/classification$/);
      await expect(
        page.getByRole("heading", { name: "Classification", exact: true }),
      ).toBeVisible();
      await expect(page.getByText(/suggestions are unavailable/i)).toBeVisible();
      await page.getByRole("link", { name: "Browse categories" }).click();
      await expect(page).toHaveURL(/\/categories$/);
      await expect(
        page.getByRole("treeitem", { name: "Food", exact: true }),
      ).toBeVisible();
      await page.getByRole("button", { name: "Search or jump to…" }).click();
      await page.getByRole("combobox").fill("dairy");
      await expect(
        page.getByRole("option").filter({ hasText: "Food / Dairy" }),
      ).toBeVisible();
      await page.screenshot({
        path: `${output}/${width}-${admin ? "admin" : "reader"}-palette.png`,
        fullPage: true,
        animations: "disabled",
      });
      await page
        .getByRole("option")
        .filter({ hasText: "Food / Dairy" })
        .click();
      await expect(page).toHaveURL(/\/categories$/);
      await page.goto(`/taxonomy?revision=${historicalId}#details`);
      await expect(page).toHaveURL(
        `/categories?revision=${historicalId}#details`,
      );
      await expect(
        page.getByText("Historical revision", { exact: false }),
      ).toBeVisible();
      await expect(
        page.getByRole("button", { name: "New category", exact: true }),
      ).toHaveCount(0);
      await expect(
        page.getByRole("button", { name: /^Rename category:/ }),
      ).toHaveCount(0);
      await page.screenshot({
        path: `${output}/${width}-${admin ? "admin" : "reader"}-history.png`,
        fullPage: true,
        animations: "disabled",
      });
      await page.getByRole("link", { name: "Back to categories" }).click();
      await expect(page).toHaveURL(/\/categories$/);
      await page.goto("/subcategories?categoryId=old&new=true#old");
      await expect(page).toHaveURL("/categories#old");
      await expect(page.getByRole("dialog")).toHaveCount(0);
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= window.innerWidth,
        ),
      ).toBe(true);
    });
  }
}

test("category routes require authentication", async ({ page }) => {
  await installApiMocks(page, { freezeClock: false });
  for (const path of [
    "/categories",
    "/taxonomy?revision=old#details",
    "/subcategories?new=true",
  ]) {
    await page.goto(path);
    await expect(page).toHaveURL(/\/login$/);
    await expect(
      page.getByRole("heading", { name: "Categories", exact: true }),
    ).toHaveCount(0);
  }
});
