import { expect, test } from "@playwright/test";
import {
  ADMIN_FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";

for (const width of [375, 1280]) {
  test(`legacy Templates route redirects and navigation omits it (${width}px)`, async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await installApiMocks(page, {
      user: ADMIN_FIXTURE_USER,
      freezeClock: false,
    });
    await signInAs(page, ADMIN_FIXTURE_USER);
    await page.goto("/item-templates");
    await expect(page).toHaveURL(/\/receipts$/);
    await expect(
      page.getByRole("heading", { name: "Receipts", exact: true }),
    ).toBeVisible();
    await expect(page.locator('a[href="/item-templates"]')).toHaveCount(0);
    await expect(
      page.getByRole("button", { name: "New template", exact: true }),
    ).toHaveCount(0);
    const search = page.getByRole("button", { name: "Search or jump to…" });
    await expect(search).toBeVisible();
    await page.screenshot({
      path: testInfo.outputPath("receipts.png"),
      animations: "disabled",
    });

    if (width < 900) {
      await page.getByRole("button", { name: "Open navigation menu" }).click();
      await expect(page.getByRole("dialog")).toBeVisible();
      await expect(
        page
          .getByRole("dialog")
          .getByRole("link", { name: "Receipts", exact: true }),
      ).toBeVisible();
      await expect(page.locator('a[href="/item-templates"]')).toHaveCount(0);
      await page.screenshot({
        path: testInfo.outputPath("navigation.png"),
        animations: "disabled",
      });
      await page.keyboard.press("Escape");
    }
    await search.click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await expect(page.getByRole("option", { name: /templates/i })).toHaveCount(
      0,
    );
  });
}
