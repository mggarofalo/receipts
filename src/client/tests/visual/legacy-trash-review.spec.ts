import { test, expect } from "@playwright/test";
import {
  ADMIN_FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";

test("legacy trash remains usable at desktop and narrow widths", async ({
  page,
}) => {
  const deleted = (id: string, name: string) => ({
    data: [{ id, name }],
    total: 1,
    offset: 0,
    limit: 500,
  });
  await installApiMocks(page, {
    user: ADMIN_FIXTURE_USER,
    overrides: {
      "**/api/receipts/deleted?*": (route) =>
        route.fulfill({ json: { data: [], total: 0 } }),
      "**/api/receipt-items/deleted?*": (route) =>
        route.fulfill({ json: { data: [], total: 0 } }),
      "**/api/transactions/deleted?*": (route) =>
        route.fulfill({ json: { data: [], total: 0 } }),
      "**/api/item-templates/deleted?*": (route) =>
        route.fulfill({ json: { data: [], total: 0 } }),
      "**/api/categories/deleted?*": (route) =>
        route.fulfill({ json: deleted("cat1", "Food") }),
      "**/api/subcategories/deleted?*": (route) =>
        route.fulfill({ json: deleted("sub1", "Dairy") }),
    },
  });
  await signInAs(page, ADMIN_FIXTURE_USER);
  await page.goto("/trash");
  await expect(
    page.getByRole("tab", { name: "Legacy categories (2)" }),
  ).toBeVisible();
  for (const width of [1280, 375]) {
    await page.setViewportSize({ width, height: 812 });
    await expect(page.getByText("Food")).toBeVisible();
    await expect(page.getByText("Dairy")).toBeVisible();
    await expect(page.getByText("Legacy subcategory")).toBeVisible();
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - innerWidth,
    );
    expect(overflow, `${width}px page overflow`).toBeLessThanOrEqual(1);
    await page.getByRole("button", { name: "Empty trash" }).click();
    await expect(page.getByRole("alertdialog")).toContainText(
      "2 items currently listed",
    );
    await page.getByRole("button", { name: "Cancel" }).click();
    await page.getByRole("tab", { name: "Legacy categories (2)" }).click();
    await expect(page.getByText("Food")).toBeVisible();
  }
});
