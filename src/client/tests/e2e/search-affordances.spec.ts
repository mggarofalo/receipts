import { expect, test } from "@playwright/test";
import { installApiMocks, signInAsFixtureUser } from "../fixtures/api-mocks";

for (const palette of ["graphite", "paper"]) {
  test(`keyboard selection is visible and opens the selected purchase (${palette})`, async ({
    page,
  }) => {
    await installApiMocks(page, {
      freezeClock: false,
      overrides: {
        "**/api/receipt-items?*": (route) =>
          route.fulfill({
            json: { data: [], total: 0, offset: 0, limit: 200 },
          }),
        "**/api/search/purchases?*": (route) =>
          route.fulfill({
            json: {
              items: [1, 2].map((n) => ({
                id: `10000000-0000-0000-0000-00000000000${n}`,
                receiptId: `20000000-0000-0000-0000-00000000000${n}`,
                date: `2026-01-0${3 - n}`,
                location: "Market",
                description: `Milk ${n}`,
                receiptItemCode: null,
                normalizedDescriptionId: null,
                normalizedDescription: null,
                quantity: 1,
                unitPrice: 2.5,
                unitPriceCurrency: "USD",
                totalAmount: 2.5,
                totalAmountCurrency: "USD",
              })),
              total: 2,
              offset: 0,
              limit: 5,
            },
          }),
      },
    });
    await signInAsFixtureUser(page);
    await page.goto("/receipts");
    await page.evaluate(
      (value) => (document.documentElement.dataset.palette = value),
      palette,
    );
    await page.getByRole("button", { name: "Search or jump to…" }).click();
    const input = page.getByRole("combobox");
    await input.fill("milk");
    const first = page.getByRole("option").filter({ hasText: "Milk 1" });
    const second = page.getByRole("option").filter({ hasText: "Milk 2" });
    await expect(first).toBeVisible();
    await input.press("Home");
    await expect(first).toHaveAttribute("aria-selected", "true");
    await input.press("ArrowDown");
    await expect(second).toHaveAttribute("aria-selected", "true");
    await expect(first).toHaveAttribute("aria-selected", "false");
    const appearance = await second.evaluate((element) => ({
      background: getComputedStyle(element).backgroundColor,
      container: getComputedStyle(element.closest('[data-slot="command"]')!)
        .backgroundColor,
      marker: getComputedStyle(element).boxShadow,
    }));
    expect(appearance.background).not.toBe(appearance.container);
    expect(appearance.marker).not.toBe("none");
    // The marker, rather than a barely different surface tint, identifies selection.
    const contrast = await second.evaluate((element) => {
      const style = getComputedStyle(element);
      const luminance = (color: string) => {
        const channels = color
          .match(/[\d.]+/g)!
          .slice(0, 3)
          .map(Number)
          .map((value) => {
            const channel = value / 255;
            return channel <= 0.04045
              ? channel / 12.92
              : ((channel + 0.055) / 1.055) ** 2.4;
          });
        return (
          channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722
        );
      };
      const ratio = (a: string, b: string) => {
        const values = [luminance(a), luminance(b)].sort((x, y) => y - x);
        return (values[0] + 0.05) / (values[1] + 0.05);
      };
      const marker = style.boxShadow.match(/rgba?\([^)]+\)/)![0];
      return {
        marker: ratio(marker, style.backgroundColor),
        text: ratio(style.color, style.backgroundColor),
        metadata: ratio(
          getComputedStyle(element.querySelector(".text-muted-foreground")!)
            .color,
          style.backgroundColor,
        ),
      };
    });
    expect(contrast.marker).toBeGreaterThanOrEqual(3);
    expect(contrast.text).toBeGreaterThanOrEqual(4.5);
    expect(contrast.metadata).toBeGreaterThanOrEqual(4.5);
    await input.press("ArrowUp");
    await expect(first).toHaveAttribute("aria-selected", "true");
    await input.press("Enter");
    await expect(page).toHaveURL(
      /\/receipts\/20000000-0000-0000-0000-000000000001$/,
    );
  });
}

for (const [platform, label, key] of [
  ["Win32", "Ctrl", "Control"],
  ["Linux x86_64", "Ctrl", "Control"],
  ["MacIntel", "Command", "Meta"],
]) {
  test(`shortcut labels match ${platform} and open search`, async ({
    page,
  }) => {
    await page.addInitScript((value) => {
      Object.defineProperty(navigator, "platform", { get: () => value });
    }, platform);
    await installApiMocks(page, { freezeClock: false });
    await signInAsFixtureUser(page);
    await page.goto("/receipts");
    const trigger = page.getByRole("button", { name: "Search or jump to…" });
    await expect(trigger.locator("kbd").first()).toHaveText(label);
    await page.keyboard.press(`${key}+k`);
    await expect(
      page.getByRole("dialog", { name: "Command Palette" }),
    ).toBeVisible();
    await page.keyboard.press("Escape");
    await page.keyboard.press("?");
    await expect(
      page
        .getByRole("dialog", { name: "Keyboard Shortcuts" })
        .getByText(`${label}+K`, { exact: true }),
    ).toBeVisible();
  });
}
