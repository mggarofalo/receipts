import { expect, test } from "@playwright/test";
import {
  ADMIN_FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";

for (const width of [375, 1280]) {
  test(`backup guidance supports keyboard disclosure without page overflow (${width}px)`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 900 });
    await installApiMocks(page, {
      user: ADMIN_FIXTURE_USER,
      freezeClock: false,
    });
    await signInAs(page, ADMIN_FIXTURE_USER);
    await page.goto("/admin/backup");
    const summary = page
      .locator("summary")
      .filter({ hasText: "Backup contents" });
    const details = page.locator("details").filter({ has: summary });
    const archivalWarning = page.getByText(
      /Legacy image paths remain in backups/,
    );
    await expect(
      page.getByRole("heading", { name: "Backup & restore" }),
    ).toBeVisible();
    await expect(archivalWarning).toBeHidden();
    await page.getByLabel("Select backup file").focus();
    await page.keyboard.press("Tab");
    await expect(summary).toBeFocused();
    await page.keyboard.press("Space");
    await expect(details).toHaveAttribute("open", "");
    await expect(archivalWarning).toBeVisible();
    await expect(
      page.getByText(/User accounts and authentication settings are excluded/),
    ).toBeVisible();
    await page.keyboard.press("Enter");
    await expect(details).not.toHaveAttribute("open");
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth),
    ).toBeLessThanOrEqual(width);
  });

  test(`empty API keys retain a usable creation dialog (${width}px)`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 900 });
    await installApiMocks(page, {
      user: ADMIN_FIXTURE_USER,
      freezeClock: false,
      overrides: {
        "**/api/apikeys": (route) =>
          route.fulfill({
            json:
              route.request().method() === "POST"
                ? { rawKey: "fixture-secret-key" }
                : [],
          }),
      },
    });
    await signInAs(page, ADMIN_FIXTURE_USER);
    await page.goto("/api-keys");
    await expect(
      page.getByRole("status").filter({ hasText: "No API keys yet." }),
    ).toBeVisible();
    await expect(
      page.getByRole("textbox", { name: "Search API keys" }),
    ).toHaveCount(0);
    const create = page.getByRole("button", { name: "New API key" });
    await create.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog");
    await expect(
      dialog.getByRole("heading", { name: "Create API Key" }),
    ).toBeVisible();
    await expect(dialog.getByRole("textbox", { name: "Name" })).toBeFocused();
    expect(
      await dialog.evaluate((element) => element.getBoundingClientRect().right),
    ).toBeLessThanOrEqual(width);
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(create).toBeFocused();
    await create.press("Enter");
    await page
      .getByRole("textbox", { name: "Name" })
      .fill("Keyboard integration");
    await page.getByRole("button", { name: "Create Key", exact: true }).click();
    const secretDialog = page.getByRole("dialog");
    await expect(
      secretDialog.getByRole("heading", { name: "API Key Created" }),
    ).toBeVisible();
    await expect(secretDialog.getByRole("textbox")).toHaveValue(
      "fixture-secret-key",
    );
    await expect(secretDialog.getByRole("textbox")).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(secretDialog).toBeHidden();
    await expect(create).toBeFocused();
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth),
    ).toBeLessThanOrEqual(width);
  });
}
